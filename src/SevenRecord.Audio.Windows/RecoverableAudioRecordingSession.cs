using System.Text.Json;
using System.Runtime.InteropServices;
using NAudio;
using NAudio.Wave;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Domain.Audio;
using SevenRecord.Recording;

namespace SevenRecord.Audio.Windows;

public sealed record AudioRecordingResult(
    RecordingSegmentEntry Microphone,
    RecordingSegmentEntry SystemAudio,
    AudioTimingManifest Timing,
    string TimingManifestPath,
    AudioCaptureHealth? MicrophoneHealth,
    AudioCaptureHealth? SystemAudioHealth);

public sealed record AudioRecordingStartResult(
    RecoverableAudioRecordingSession? Session,
    string? Error)
{
    public bool Succeeded => Session is not null;
}

public sealed class RecoverableAudioRecordingSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SynchronizedAudioCaptureSession _capture;
    private readonly List<AudioGapMetadata> _microphoneGaps = [];
    private readonly object _microphoneGate = new();
    private string _microphoneTemporaryPath;
    private WaveFileWriter _microphoneWriter;
    private readonly bool _ownsProjectWriter;
    private readonly string _projectRoot;
    private readonly RecordingProjectWriter _projectWriter;
    private readonly ProjectClock _projectClock;
    private readonly RecordingPauseController _pauseController;
    private readonly RecordingSegmentPolicy _segmentPolicy;
    private readonly object _systemAudioGate = new();
    private string _systemAudioTemporaryPath;
    private WaveFileWriter _systemAudioWriter;
    private readonly List<AudioGapMetadata> _systemAudioGaps = [];
    private Task _publicationTail = Task.CompletedTask;
    private readonly object _publicationGate = new();
    private readonly List<Exception> _rolloverFailures = [];
    private bool _completed;
    private bool _stopped;
    private int _microphoneSegmentNumber = 1;
    private int _systemAudioSegmentNumber = 1;
    private TimeSpan _microphoneSegmentStart;
    private TimeSpan _systemAudioSegmentStart;
    private AudioCaptureHealth? _microphoneHealth;
    private AudioCaptureHealth? _systemAudioHealth;

    private RecoverableAudioRecordingSession(
        string projectRoot,
        SynchronizedAudioCaptureSession capture,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        bool ownsProjectWriter,
        RecordingSegmentPolicy segmentPolicy)
    {
        _projectRoot = projectRoot;
        _projectClock = projectClock;
        _pauseController = pauseController;
        _capture = capture;
        _projectWriter = projectWriter;
        _ownsProjectWriter = ownsProjectWriter;
        _segmentPolicy = segmentPolicy;
        _microphoneTemporaryPath = TemporaryPath(
            projectRoot,
            "microphone",
            _microphoneSegmentNumber);
        _systemAudioTemporaryPath = TemporaryPath(
            projectRoot,
            "system-audio",
            _systemAudioSegmentNumber);
        Directory.CreateDirectory(Path.Combine(projectRoot, "temp"));
        _microphoneWriter = new WaveFileWriter(
            _microphoneTemporaryPath,
            capture.MicrophoneFormat);
        _systemAudioWriter = new WaveFileWriter(
            _systemAudioTemporaryPath,
            capture.SystemAudioFormat);
        _capture.PacketCaptured += OnPacketCaptured;
        _capture.HealthChanged += OnHealthChanged;
    }

    public event Action<AudioCaptureHealth>? HealthChanged;

    public AudioCaptureHealth? MicrophoneHealth => _microphoneHealth;

    public Exception? MicrophoneFailure => _capture.MicrophoneFailure;

    public AudioCaptureHealth? SystemAudioHealth => _systemAudioHealth;

    public Exception? SystemAudioFailure => _capture.SystemAudioFailure;

    public static RecoverableAudioRecordingSession Start(
        string projectRoot,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingSegmentPolicy? segmentPolicy = null)
    {
        RecordingProjectWriter projectWriter =
            RecordingProjectWriter.OpenAsync(projectRoot)
                .GetAwaiter()
                .GetResult();
        try
        {
            return Start(
                projectRoot,
                projectClock,
                pauseController,
                projectWriter,
                ownsProjectWriter: true,
                segmentPolicy ?? RecordingSegmentPolicy.Default);
        }
        catch
        {
            projectWriter.Dispose();
            throw;
        }
    }

    public static RecoverableAudioRecordingSession Start(
        string projectRoot,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        RecordingSegmentPolicy? segmentPolicy = null) =>
        Start(
            projectRoot,
            projectClock,
            pauseController,
            projectWriter,
            ownsProjectWriter: false,
            segmentPolicy ?? RecordingSegmentPolicy.Default);

    public static AudioRecordingStartResult TryStart(
        string projectRoot,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        RecordingSegmentPolicy? segmentPolicy = null)
    {
        try
        {
            return new AudioRecordingStartResult(
                Start(
                    projectRoot,
                    projectClock,
                    pauseController,
                    projectWriter,
                    segmentPolicy),
                null);
        }
        catch (Exception exception) when (
            exception is COMException or
                InvalidOperationException or
                MmException or
                UnauthorizedAccessException)
        {
            return new AudioRecordingStartResult(null, exception.Message);
        }
    }

    private static RecoverableAudioRecordingSession Start(
        string projectRoot,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        bool ownsProjectWriter,
        RecordingSegmentPolicy segmentPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(projectClock);
        ArgumentNullException.ThrowIfNull(pauseController);
        ArgumentNullException.ThrowIfNull(projectWriter);
        Directory.CreateDirectory(projectRoot);

        SynchronizedAudioCaptureSession capture = new(projectClock);
        RecoverableAudioRecordingSession? session = null;
        try
        {
            session = new RecoverableAudioRecordingSession(
                projectRoot,
                capture,
                projectClock,
                pauseController,
                projectWriter,
                ownsProjectWriter,
                segmentPolicy);
            capture.Start();
            return session;
        }
        catch
        {
            if (session is not null)
            {
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else
            {
                capture.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            throw;
        }
    }

    public async Task<AudioRecordingResult> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The audio recording is already complete.");
        }

        await StopAsync(cancellationToken);
        return await PublishAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        try
        {
            await _capture.StopAsync(cancellationToken);
        }
        finally
        {
            _capture.PacketCaptured -= OnPacketCaptured;
            _capture.HealthChanged -= OnHealthChanged;
            lock (_microphoneGate)
            {
                _microphoneWriter.Dispose();
            }

            lock (_systemAudioGate)
            {
                _systemAudioWriter.Dispose();
            }
        }
    }

    public async Task<AudioRecordingResult> PublishAsync(
        CancellationToken cancellationToken = default)
    {
        TimeSpan duration = _pauseController.Map(
            _projectClock.Normalize(QpcTimestamp.Now()));
        return await PublishAsync(duration, cancellationToken);
    }

    public async Task<AudioRecordingResult> PublishAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (!_stopped)
        {
            throw new InvalidOperationException("Audio capture must stop before publication.");
        }

        if (_completed)
        {
            throw new InvalidOperationException("The audio recording is already complete.");
        }

        _completed = true;
        await _publicationTail;
        RecordingSegmentEntry microphone = await _projectWriter.PublishAsync(
            _microphoneTemporaryPath,
            sourceId: "microphone",
            start: _microphoneSegmentStart,
            duration - _microphoneSegmentStart,
            cancellationToken);
        RecordingSegmentEntry systemAudio = await _projectWriter.PublishAsync(
            _systemAudioTemporaryPath,
            sourceId: "system-audio",
            start: _systemAudioSegmentStart,
            duration - _systemAudioSegmentStart,
            cancellationToken);
        ThrowRolloverFailures();
        AudioTimingManifest timing = CreateTimingManifest();
        string timingManifestPath = Path.Combine(_projectRoot, "audio-timing.json");
        string temporaryManifestPath = timingManifestPath + ".tmp";
        string json = JsonSerializer.Serialize(
            timing,
            SerializerOptions);
        await File.WriteAllTextAsync(
            temporaryManifestPath,
            json,
            cancellationToken);
        File.Move(temporaryManifestPath, timingManifestPath, overwrite: true);

        return new AudioRecordingResult(
            microphone,
            systemAudio,
            timing,
            Path.GetRelativePath(_projectRoot, timingManifestPath),
            _microphoneHealth,
            _systemAudioHealth);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            _capture.PacketCaptured -= OnPacketCaptured;
            _capture.HealthChanged -= OnHealthChanged;
            if (!_stopped)
            {
                _microphoneWriter.Dispose();
                _systemAudioWriter.Dispose();
            }
        }

        await _capture.DisposeAsync();
        await _publicationTail;
        if (_ownsProjectWriter)
        {
            _projectWriter.Dispose();
        }
    }

    private void OnPacketCaptured(AudioCapturePacket packet)
    {
        if (_pauseController.IsPaused)
        {
            return;
        }

        TimeSpan activeTime = _pauseController.Map(packet.ProjectTime);
        if (packet.Source is AudioCaptureSource.Microphone)
        {
            lock (_microphoneGate)
            {
                if (!Volatile.Read(ref _stopped) &&
                    _segmentPolicy.ShouldRollover(
                        _microphoneSegmentStart,
                        activeTime))
                {
                    RotateMicrophone(activeTime);
                }
                AddGap(_microphoneGaps, packet);
                _microphoneWriter.Write(packet.Data, 0, packet.Data.Length);
            }
        }
        else
        {
            lock (_systemAudioGate)
            {
                if (!Volatile.Read(ref _stopped) &&
                    _segmentPolicy.ShouldRollover(
                        _systemAudioSegmentStart,
                        activeTime))
                {
                    RotateSystemAudio(activeTime);
                }
                AddGap(_systemAudioGaps, packet);
                _systemAudioWriter.Write(packet.Data, 0, packet.Data.Length);
            }
        }
    }

    private void OnHealthChanged(AudioCaptureHealth health)
    {
        if (health.Source is AudioCaptureSource.Microphone)
        {
            _microphoneHealth = health;
        }
        else
        {
            _systemAudioHealth = health;
        }

        HealthChanged?.Invoke(health);
    }

    private AudioTimingManifest CreateTimingManifest() =>
        new(
            1,
            [
                CreateTrackTiming(
                    AudioTrackKind.Microphone,
                    _microphoneGaps,
                    _microphoneHealth),
                CreateTrackTiming(
                    AudioTrackKind.SystemAudio,
                    _systemAudioGaps,
                    _systemAudioHealth)
            ]);

    private static AudioTrackTimingMetadata CreateTrackTiming(
        AudioTrackKind track,
        IReadOnlyList<AudioGapMetadata> gaps,
        AudioCaptureHealth? health)
    {
        ClockDriftEstimate drift = health?.Drift ?? default;
        return new AudioTrackTimingMetadata(
            track,
            gaps.ToArray(),
            new AudioClockMetadata(
                drift.Drift,
                drift.ObservedDuration,
                drift.PartsPerMillion));
    }

    private void AddGap(
        List<AudioGapMetadata> gaps,
        AudioCapturePacket packet)
    {
        if (packet.HasDiscontinuity &&
            packet.GapStart is TimeSpan gapStart &&
            packet.MissingDuration > TimeSpan.Zero)
        {
            gaps.Add(
                new AudioGapMetadata(
                    _pauseController.Map(gapStart),
                    packet.MissingDuration));
        }
    }

    private void RotateMicrophone(TimeSpan boundary)
    {
        if (Volatile.Read(ref _stopped))
        {
            return;
        }
        _microphoneWriter.Dispose();
        QueueSegmentPublication(
            _microphoneTemporaryPath,
            "microphone",
            _microphoneSegmentStart,
            boundary - _microphoneSegmentStart);
        _microphoneSegmentStart = boundary;
        _microphoneSegmentNumber++;
        _microphoneTemporaryPath = TemporaryPath(
            _projectRoot,
            "microphone",
            _microphoneSegmentNumber);
        _microphoneWriter = new WaveFileWriter(
            _microphoneTemporaryPath,
            _capture.MicrophoneFormat);
    }

    private void RotateSystemAudio(TimeSpan boundary)
    {
        if (Volatile.Read(ref _stopped))
        {
            return;
        }
        _systemAudioWriter.Dispose();
        QueueSegmentPublication(
            _systemAudioTemporaryPath,
            "system-audio",
            _systemAudioSegmentStart,
            boundary - _systemAudioSegmentStart);
        _systemAudioSegmentStart = boundary;
        _systemAudioSegmentNumber++;
        _systemAudioTemporaryPath = TemporaryPath(
            _projectRoot,
            "system-audio",
            _systemAudioSegmentNumber);
        _systemAudioWriter = new WaveFileWriter(
            _systemAudioTemporaryPath,
            _capture.SystemAudioFormat);
    }

    private void QueueSegmentPublication(
        string temporaryPath,
        string sourceId,
        TimeSpan start,
        TimeSpan duration)
    {
        lock (_publicationGate)
        {
            Task predecessor = _publicationTail;
            _publicationTail = Task.Run(async () =>
            {
                try
                {
                    await predecessor;
                }
                catch (Exception exception)
                {
                    lock (_rolloverFailures)
                    {
                        _rolloverFailures.Add(exception);
                    }
                }

                try
                {
                    await _projectWriter.PublishAsync(
                        temporaryPath,
                        sourceId,
                        start,
                        duration,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    lock (_rolloverFailures)
                    {
                        _rolloverFailures.Add(exception);
                    }
                }
            });
        }
    }

    private void ThrowRolloverFailures()
    {
        lock (_rolloverFailures)
        {
            if (_rolloverFailures.Count > 0)
            {
                throw new AggregateException(
                    "One or more audio segments could not be published.",
                    _rolloverFailures);
            }
        }
    }

    private static string TemporaryPath(
        string projectRoot,
        string sourceId,
        int segmentNumber) =>
        Path.Combine(
            projectRoot,
            "temp",
            $"{sourceId}-{segmentNumber:D8}.partial.wav");
}
