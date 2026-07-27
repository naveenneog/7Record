using System.Threading.Channels;
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
    bool HasDiscontinuity,
    TimeSpan? GapStart,
    TimeSpan MissingDuration);

public sealed record AudioCaptureHealth(
    AudioCaptureSource Source,
    long Packets,
    long Bytes,
    long SamplePosition,
    long Discontinuities,
    TimeSpan TotalMissingDuration,
    TimeSpan LastProjectTime,
    ClockDriftEstimate Drift)
{
    public long QueueOverflows { get; init; }
}

public sealed class SynchronizedAudioCaptureSession : IAsyncDisposable
{
    private const int PacketQueueCapacity = 512;
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

    public Exception? MicrophoneFailure => _microphone.ConsumerFailure;

    public string MicrophoneName => _microphone.Device.FriendlyName;

    public string SystemAudioName => _systemAudio.Device.FriendlyName;

    public Exception? SystemAudioFailure => _systemAudio.ConsumerFailure;

    public WaveFormat MicrophoneFormat => _microphone.Capture.WaveFormat;

    public WaveFormat SystemAudioFormat => _systemAudio.Capture.WaveFormat;

    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("Audio capture has already started.");
        }

        _started = true;
        _microphone.StartConsumer(ProcessPacketsAsync);
        _systemAudio.StartConsumer(ProcessPacketsAsync);
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
        _microphone.CompleteQueue();
        _systemAudio.CompleteQueue();
        await Task.WhenAll(
            _microphone.WaitForConsumerAsync(cancellationToken),
            _systemAudio.WaitForConsumerAsync(cancellationToken));
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
        EnqueuePacket(_microphone, args);

    private void OnSystemAudioData(object? sender, WaveInEventArgs args) =>
        EnqueuePacket(_systemAudio, args);

    private void EnqueuePacket(SourceCapture source, WaveInEventArgs args)
    {
        if (args.BytesRecorded == 0)
        {
            return;
        }

        byte[] buffer = GC.AllocateUninitializedArray<byte>(args.BytesRecorded);
        args.Buffer.AsSpan(0, args.BytesRecorded).CopyTo(buffer);
        CapturedAudioPacket packet = new(
            buffer,
            args.BytesRecorded,
            _projectClock.Normalize(QpcTimestamp.Now()));
        if (!source.TryEnqueue(packet))
        {
            Interlocked.Increment(ref source.QueueOverflows);
        }
    }

    private async Task ProcessPacketsAsync(SourceCapture source)
    {
        await foreach (CapturedAudioPacket captured in source.ReadPacketsAsync())
        {
            if (source.ConsumerFailure is not null)
            {
                continue;
            }

            try
            {
                PublishPacket(source, captured);
            }
            catch (Exception exception)
            {
                source.SetConsumerFailure(exception);
            }
        }
    }

    private void PublishPacket(
        SourceCapture source,
        CapturedAudioPacket captured)
    {
        WaveFormat format = source.Capture.WaveFormat;
        long frames = captured.Length / format.BlockAlign;
        TimeSpan projectTime = captured.ProjectTime;
        AudioPacketTiming timing = source.Timeline.AddPacket(
            projectTime,
            frames,
            format.SampleRate);
        if (timing.HasDiscontinuity)
        {
            Interlocked.Increment(ref source.Discontinuities);
            Interlocked.Add(ref source.MissingTicks, timing.MissingDuration.Ticks);
        }

        long packets = Interlocked.Increment(ref source.Packets);
        long bytes = Interlocked.Add(ref source.Bytes, captured.Length);

        PacketCaptured?.Invoke(
            new AudioCapturePacket(
                source.Source,
                captured.Buffer,
                projectTime,
                timing.SamplePosition,
                format.SampleRate,
                format.Channels,
                format.BitsPerSample,
                timing.Drift,
                timing.HasDiscontinuity,
                timing.GapStart,
                timing.MissingDuration));

        if (timing.HasDiscontinuity || packets % 16 == 0)
        {
            HealthChanged?.Invoke(
                new AudioCaptureHealth(
                    source.Source,
                    packets,
                    bytes,
                    timing.SamplePosition,
                    Interlocked.Read(ref source.Discontinuities),
                    TimeSpan.FromTicks(Interlocked.Read(ref source.MissingTicks)),
                    projectTime,
                    timing.Drift)
                {
                    QueueOverflows = Interlocked.Read(ref source.QueueOverflows),
                });
        }
    }

    private readonly record struct CapturedAudioPacket(
        byte[] Buffer,
        int Length,
        TimeSpan ProjectTime);

    private sealed class SourceCapture : IDisposable
    {
        private readonly Channel<CapturedAudioPacket> _packets =
            Channel.CreateBounded<CapturedAudioPacket>(
                new BoundedChannelOptions(PacketQueueCapacity)
                {
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                });
        private Task _consumerTask = Task.CompletedTask;

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

        private Exception? _consumerFailure;

        public Exception? ConsumerFailure =>
            Volatile.Read(ref _consumerFailure);

        public long Discontinuities;

        public long MissingTicks;

        public long Packets;

        public long QueueOverflows;

        public AudioCaptureSource Source { get; }

        public AudioPacketTimeline Timeline { get; } = new();

        private TaskCompletionSource Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForStoppedAsync(CancellationToken cancellationToken) =>
            Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        public void CompleteQueue() =>
            _packets.Writer.TryComplete();

        public IAsyncEnumerable<CapturedAudioPacket> ReadPacketsAsync() =>
            _packets.Reader.ReadAllAsync();

        public void StartConsumer(
            Func<SourceCapture, Task> consumer) =>
            _consumerTask = Task.Run(() => consumer(this));

        public void SetConsumerFailure(Exception exception) =>
            Interlocked.CompareExchange(
                ref _consumerFailure,
                exception,
                null);

        public bool TryEnqueue(CapturedAudioPacket packet) =>
            _packets.Writer.TryWrite(packet);

        public Task WaitForConsumerAsync(CancellationToken cancellationToken) =>
            _consumerTask.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

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
