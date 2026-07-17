using NAudio.CoreAudioApi;
using NAudio.Wave;
using SevenRecord.Capture.Abstractions;

namespace SevenRecord.Audio.Windows;

public enum AudioCaptureSource
{
    Microphone,
    SystemAudio,
}

public sealed record AudioCapturePacket(
    AudioCaptureSource Source,
    byte[] Data,
    TimeSpan ProjectTime,
    long SamplePosition,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    ClockDriftEstimate Drift,
    bool HasDiscontinuity);

public sealed record AudioCaptureHealth(
    AudioCaptureSource Source,
    long Packets,
    long Bytes,
    long SamplePosition,
    long Discontinuities,
    TimeSpan LastProjectTime,
    ClockDriftEstimate Drift);

public sealed class SynchronizedAudioCaptureSession : IAsyncDisposable
{
    private readonly SourceCapture _microphone;
    private readonly ProjectClock _projectClock;
    private readonly SourceCapture _systemAudio;
    private bool _started;
    private bool _stopped;

    public SynchronizedAudioCaptureSession(ProjectClock projectClock)
    {
        ArgumentNullException.ThrowIfNull(projectClock);
        _projectClock = projectClock;

        MMDeviceEnumerator enumerator = new();
        try
        {
            MMDevice microphoneDevice = enumerator.GetDefaultAudioEndpoint(
                DataFlow.Capture,
                Role.Multimedia);
            MMDevice renderDevice = enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia);

            _microphone = new SourceCapture(
                AudioCaptureSource.Microphone,
                microphoneDevice,
                new WasapiCapture(microphoneDevice));
            _systemAudio = new SourceCapture(
                AudioCaptureSource.SystemAudio,
                renderDevice,
                new WasapiLoopbackCapture(renderDevice));
        }
        finally
        {
            enumerator.Dispose();
        }

        _microphone.Capture.DataAvailable += OnMicrophoneData;
        _systemAudio.Capture.DataAvailable += OnSystemAudioData;
    }

    public event Action<AudioCapturePacket>? PacketCaptured;

    public event Action<AudioCaptureHealth>? HealthChanged;

    public string MicrophoneName => _microphone.Device.FriendlyName;

    public string SystemAudioName => _systemAudio.Device.FriendlyName;

    public WaveFormat MicrophoneFormat => _microphone.Capture.WaveFormat;

    public WaveFormat SystemAudioFormat => _systemAudio.Capture.WaveFormat;

    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("Audio capture has already started.");
        }

        _started = true;
        _microphone.Capture.StartRecording();
        try
        {
            _systemAudio.Capture.StartRecording();
        }
        catch
        {
            _microphone.Capture.StopRecording();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            throw new InvalidOperationException("Audio capture has not started.");
        }

        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _microphone.Capture.StopRecording();
        _systemAudio.Capture.StopRecording();
        await Task.WhenAll(
            _microphone.WaitForStoppedAsync(cancellationToken),
            _systemAudio.WaitForStoppedAsync(cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        if (_started && !_stopped)
        {
            await StopAsync();
        }

        _microphone.Capture.DataAvailable -= OnMicrophoneData;
        _systemAudio.Capture.DataAvailable -= OnSystemAudioData;
        _microphone.Dispose();
        _systemAudio.Dispose();
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs args) =>
        PublishPacket(_microphone, args);

    private void OnSystemAudioData(object? sender, WaveInEventArgs args) =>
        PublishPacket(_systemAudio, args);

    private void PublishPacket(SourceCapture source, WaveInEventArgs args)
    {
        if (args.BytesRecorded == 0)
        {
            return;
        }

        WaveFormat format = source.Capture.WaveFormat;
        long frames = args.BytesRecorded / format.BlockAlign;
        TimeSpan projectTime = _projectClock.Normalize(QpcTimestamp.Now());
        AudioPacketTiming timing = source.Timeline.AddPacket(
            projectTime,
            frames,
            format.SampleRate);
        if (timing.HasDiscontinuity)
        {
            Interlocked.Increment(ref source.Discontinuities);
        }

        long packets = Interlocked.Increment(ref source.Packets);
        long bytes = Interlocked.Add(ref source.Bytes, args.BytesRecorded);

        PacketCaptured?.Invoke(
            new AudioCapturePacket(
                source.Source,
                args.Buffer.AsSpan(0, args.BytesRecorded).ToArray(),
                projectTime,
                timing.SamplePosition,
                format.SampleRate,
                format.Channels,
                format.BitsPerSample,
                timing.Drift,
                timing.HasDiscontinuity));

        if (timing.HasDiscontinuity || packets % 16 == 0)
        {
            HealthChanged?.Invoke(
                new AudioCaptureHealth(
                    source.Source,
                    packets,
                    bytes,
                    timing.SamplePosition,
                    Interlocked.Read(ref source.Discontinuities),
                    projectTime,
                    timing.Drift));
        }
    }

    private sealed class SourceCapture : IDisposable
    {
        public SourceCapture(
            AudioCaptureSource source,
            MMDevice device,
            WasapiCapture capture)
        {
            Source = source;
            Device = device;
            Capture = capture;
            Capture.RecordingStopped += OnRecordingStopped;
        }

        public WasapiCapture Capture { get; }

        public MMDevice Device { get; }

        public long Bytes;

        public long Discontinuities;

        public long Packets;

        public AudioCaptureSource Source { get; }

        public AudioPacketTimeline Timeline { get; } = new();

        private TaskCompletionSource Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForStoppedAsync(CancellationToken cancellationToken) =>
            Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        public void Dispose()
        {
            Capture.RecordingStopped -= OnRecordingStopped;
            Capture.Dispose();
            Device.Dispose();
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs args)
        {
            if (args.Exception is null)
            {
                Stopped.TrySetResult();
            }
            else
            {
                Stopped.TrySetException(args.Exception);
            }
        }
    }
}
