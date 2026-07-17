using System.Text.Json;
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

public sealed class RecoverableAudioRecordingSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SynchronizedAudioCaptureSession _capture;
    private readonly RecordingJournal _journal;
    private readonly List<AudioGapMetadata> _microphoneGaps = [];
    private readonly object _microphoneGate = new();
    private readonly string _microphoneTemporaryPath;
    private readonly WaveFileWriter _microphoneWriter;
    private readonly RecordingSegmentPublisher _publisher;
    private readonly string _projectRoot;
    private readonly ProjectClock _projectClock;
    private readonly RecordingPauseController _pauseController;
    private readonly object _systemAudioGate = new();
    private readonly string _systemAudioTemporaryPath;
    private readonly WaveFileWriter _systemAudioWriter;
    private readonly List<AudioGapMetadata> _systemAudioGaps = [];
    private bool _completed;
    private bool _stopped;
    private AudioCaptureHealth? _microphoneHealth;
    private AudioCaptureHealth? _systemAudioHealth;

    private RecoverableAudioRecordingSession(
        string projectRoot,
        SynchronizedAudioCaptureSession capture,
        ProjectClock projectClock,
        RecordingPauseController pauseController)
    {
        _projectRoot = projectRoot;
        _projectClock = projectClock;
        _pauseController = pauseController;
        _capture = capture;
        _journal = new RecordingJournal(Path.Combine(projectRoot, "recording.journal"));
        _publisher = new RecordingSegmentPublisher(projectRoot, _journal);
        _microphoneTemporaryPath = Path.Combine(projectRoot, "temp", "microphone.partial.wav");
        _systemAudioTemporaryPath = Path.Combine(projectRoot, "temp", "system-audio.partial.wav");
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

    public static RecoverableAudioRecordingSession Start(
        string projectRoot,
        ProjectClock projectClock,
        RecordingPauseController pauseController)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(projectClock);
        ArgumentNullException.ThrowIfNull(pauseController);
        Directory.CreateDirectory(projectRoot);

        SynchronizedAudioCaptureSession capture = new(projectClock);
        RecoverableAudioRecordingSession? session = null;
        try
        {
            session = new RecoverableAudioRecordingSession(
                projectRoot,
                capture,
                projectClock,
                pauseController);
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
        await _capture.StopAsync(cancellationToken);
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

    public async Task<AudioRecordingResult> PublishAsync(
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
        TimeSpan duration = _pauseController.Map(
            _projectClock.Normalize(QpcTimestamp.Now()));
        RecordingSegmentEntry microphone = await _publisher.PublishAsync(
            _microphoneTemporaryPath,
            sequence: 2,
            sourceId: "microphone",
            start: TimeSpan.Zero,
            duration,
            cancellationToken);
        RecordingSegmentEntry systemAudio = await _publisher.PublishAsync(
            _systemAudioTemporaryPath,
            sequence: 3,
            sourceId: "system-audio",
            start: TimeSpan.Zero,
            duration,
            cancellationToken);
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
        _journal.Dispose();
    }

    private void OnPacketCaptured(AudioCapturePacket packet)
    {
        if (_pauseController.IsPaused)
        {
            return;
        }

        if (packet.Source is AudioCaptureSource.Microphone)
        {
            lock (_microphoneGate)
            {
                AddGap(_microphoneGaps, packet);
                _microphoneWriter.Write(packet.Data, 0, packet.Data.Length);
            }
        }
        else
        {
            lock (_systemAudioGate)
            {
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

    private static void AddGap(
        List<AudioGapMetadata> gaps,
        AudioCapturePacket packet)
    {
        if (packet.HasDiscontinuity &&
            packet.GapStart is TimeSpan gapStart &&
            packet.MissingDuration > TimeSpan.Zero)
        {
            gaps.Add(new AudioGapMetadata(gapStart, packet.MissingDuration));
        }
    }
}
