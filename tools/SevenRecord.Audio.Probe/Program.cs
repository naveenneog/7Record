using System.Collections.Concurrent;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SevenRecord.Audio.Windows;
using SevenRecord.Capture.Abstractions;

int durationSeconds = args.Length > 0 && int.TryParse(args[0], out int parsed)
    ? parsed
    : 10;
string? projectRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : null;

ProjectClock clock = ProjectClock.StartNew();
using WaveOutEvent toneOutput = CreateTone();

if (projectRoot is not null)
{
    ConcurrentDictionary<AudioCaptureSource, AudioCaptureHealth> health = new();
    await using RecoverableAudioRecordingSession recording =
        RecoverableAudioRecordingSession.Start(projectRoot, clock);
    recording.HealthChanged += snapshot => health[snapshot.Source] = snapshot;

    toneOutput.Play();
    await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
    toneOutput.Stop();
    AudioRecordingResult recordingResult = await recording.CompleteAsync();

    var persistedResult = new
    {
        durationSeconds,
        projectRoot,
        microphone = CreateHealthResult(
            recordingResult.Microphone.RelativePath,
            health.GetValueOrDefault(AudioCaptureSource.Microphone)),
        systemAudio = CreateHealthResult(
            recordingResult.SystemAudio.RelativePath,
            health.GetValueOrDefault(AudioCaptureSource.SystemAudio)),
    };
    WriteJson(persistedResult);
    return;
}

await using SynchronizedAudioCaptureSession capture = new(clock);
ConcurrentDictionary<AudioCaptureSource, ProbeSourceState> states = new();
capture.PacketCaptured += packet =>
{
    ProbeSourceState state = states.GetOrAdd(packet.Source, static _ => new ProbeSourceState());
    Interlocked.Increment(ref state.Packets);
    Interlocked.Add(ref state.Bytes, packet.Data.Length);
    state.LastDrift = packet.Drift;
    state.SampleRate = packet.SampleRate;
    state.Channels = packet.Channels;
    state.BitsPerSample = packet.BitsPerSample;
    if (packet.HasDiscontinuity)
    {
        Interlocked.Increment(ref state.Discontinuities);
    }
};

capture.Start();
toneOutput.Play();
await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
toneOutput.Stop();
await capture.StopAsync();

var result = new
{
    durationSeconds,
    microphone = CreateResult(
        capture.MicrophoneName,
        states.GetValueOrDefault(AudioCaptureSource.Microphone)),
    systemAudio = CreateResult(
        capture.SystemAudioName,
        states.GetValueOrDefault(AudioCaptureSource.SystemAudio)),
};
WriteJson(result);

static object CreateResult(string deviceName, ProbeSourceState? state) => new
{
    deviceName,
    packets = state?.Packets ?? 0,
    bytes = state?.Bytes ?? 0,
    sampleRate = state?.SampleRate ?? 0,
    channels = state?.Channels ?? 0,
    bitsPerSample = state?.BitsPerSample ?? 0,
    driftMilliseconds = state?.LastDrift.Drift.TotalMilliseconds ?? 0,
    driftPartsPerMillion = state?.LastDrift.PartsPerMillion ?? 0,
    observedSeconds = state?.LastDrift.ObservedDuration.TotalSeconds ?? 0,
    discontinuities = state?.Discontinuities ?? 0,
};

static object CreateHealthResult(string relativePath, AudioCaptureHealth? health) => new
{
    relativePath,
    packets = health?.Packets ?? 0,
    bytes = health?.Bytes ?? 0,
    samplePosition = health?.SamplePosition ?? 0,
    driftMilliseconds = health?.Drift.Drift.TotalMilliseconds ?? 0,
    driftPartsPerMillion = health?.Drift.PartsPerMillion ?? 0,
    observedSeconds = health?.Drift.ObservedDuration.TotalSeconds ?? 0,
    discontinuities = health?.Discontinuities ?? 0,
};

static WaveOutEvent CreateTone()
{
    WaveOutEvent output = new();
    SignalGenerator tone = new(48_000, 2)
    {
        Frequency = 440,
        Gain = 0.04,
        Type = SignalGeneratorType.Sin,
    };
    output.Init(tone);
    return output;
}

static void WriteJson<T>(T value) =>
    Console.WriteLine(JsonSerializer.Serialize(
        value,
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

file sealed class ProbeSourceState
{
    public long Bytes;
    public int BitsPerSample;
    public int Channels;
    public long Discontinuities;
    public ClockDriftEstimate LastDrift;
    public long Packets;
    public int SampleRate;
}
