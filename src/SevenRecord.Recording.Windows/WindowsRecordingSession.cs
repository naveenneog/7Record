using System.Runtime.InteropServices;
using SevenRecord.Audio.Windows;
using SevenRecord.Camera.Windows;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Capture.Windows;
using SevenRecord.Domain.Input;
using SevenRecord.Input.Windows;

namespace SevenRecord.Recording.Windows;

public enum RecordingStopReason
{
    User,
    CaptureClosed,
    CaptureFailed,
    ApplicationExit,
}

public enum RecordingIssueSeverity
{
    Warning,
    Error,
}

public sealed record WindowsRecordingIssue(
    string Component,
    RecordingIssueSeverity Severity,
    string Message);

public sealed record WindowsRecordingRequest(
    string ProjectRoot,
    WindowsCaptureTarget Target,
    bool IncludeCamera);

public sealed record WindowsRecordingStartResult(
    WindowsRecordingSession Session,
    IReadOnlyList<WindowsRecordingIssue> Issues);

public sealed record WindowsRecordingFinalizationResult(
    Guid SessionId,
    string ProjectRoot,
    RecordingStopReason StopReason,
    CaptureFrameHealthSnapshot ScreenHealth,
    RecordingSegmentEntry? Screen,
    AudioRecordingResult? Audio,
    CameraRecordingResult? Camera,
    CursorMetadataDocument? Cursor,
    IReadOnlyList<WindowsRecordingIssue> Issues)
{
    public bool Succeeded =>
        Screen is not null &&
        Issues.All(issue => issue.Severity is not RecordingIssueSeverity.Error);
}

public sealed class WindowsRecordingSession : IAsyncDisposable
{
    private readonly RecoverableAudioRecordingSession? _audio;
    private readonly RecoverableCameraRecordingSession? _camera;
    private readonly WindowsScreenCaptureSession _capture;
    private readonly CursorMetadataRecorder? _cursor;
    private readonly IReadOnlyList<WindowsRecordingIssue> _startIssues;
    private readonly RecordingPauseController _pauseController;
    private readonly ProjectClock _projectClock;
    private readonly RecordingProjectWriter _projectWriter;
    private readonly SurfaceScreenSegmentRecorder _screen;
    private readonly object _stopGate = new();
    private int _captureClosed;
    private Exception? _captureFailure;
    private Task<WindowsRecordingFinalizationResult>? _stopTask;

    private WindowsRecordingSession(
        WindowsRecordingRequest request,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        SurfaceScreenSegmentRecorder screen,
        RecoverableAudioRecordingSession? audio,
        RecoverableCameraRecordingSession? camera,
        CursorMetadataRecorder? cursor,
        WindowsScreenCaptureSession capture,
        IReadOnlyList<WindowsRecordingIssue> startIssues)
    {
        SessionId = Guid.NewGuid();
        Request = request;
        _projectClock = projectClock;
        _pauseController = pauseController;
        _projectWriter = projectWriter;
        _screen = screen;
        _audio = audio;
        _camera = camera;
        _cursor = cursor;
        _capture = capture;
        _startIssues = startIssues;
        _capture.HealthChanged += OnScreenHealthChanged;
        _capture.CaptureClosed += OnCaptureClosed;
        _capture.CaptureFailed += OnCaptureFailed;
        _screen.Failed += OnScreenFailed;
        if (_audio is not null)
        {
            _audio.HealthChanged += OnAudioHealthChanged;
        }
    }

    public event Action<AudioCaptureHealth>? AudioHealthChanged;

    public event Action? CaptureClosed;

    public event Action<Exception>? CaptureFailed;

    public event Action<CaptureFrameHealthSnapshot>? ScreenHealthChanged;

    public string? CameraDeviceName => _camera?.DeviceName;

    public Exception? CaptureFailure => Volatile.Read(ref _captureFailure);

    public bool HasAudio => _audio is not null;

    public bool IsCaptureClosed => Volatile.Read(ref _captureClosed) != 0;

    public bool IsPaused => _pauseController.IsPaused;

    public WindowsRecordingRequest Request { get; }

    public Guid SessionId { get; }

    public static async Task<WindowsRecordingStartResult> StartAsync(
        WindowsRecordingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
        ArgumentNullException.ThrowIfNull(request.Target);

        RecordingProjectWriter? projectWriter = null;
        SurfaceScreenSegmentRecorder? screen = null;
        RecoverableAudioRecordingSession? audio = null;
        RecoverableCameraRecordingSession? camera = null;
        CursorMetadataRecorder? cursor = null;
        WindowsScreenCaptureSession? capture = null;
        List<WindowsRecordingIssue> issues = [];

        try
        {
            projectWriter = await RecordingProjectWriter.OpenAsync(
                request.ProjectRoot,
                cancellationToken);
            ProjectClock projectClock = ProjectClock.StartNew();
            RecordingPauseController pauseController = new();
            pauseController.Pause(TimeSpan.Zero);

            try
            {
                cursor = CursorMetadataRecorder.Start(
                    projectClock,
                    pauseController);
            }
            catch (InvalidOperationException exception)
            {
                issues.Add(
                    Warning("cursor", exception));
            }

            screen = await SurfaceScreenSegmentRecorder.CreateAsync(
                request.ProjectRoot,
                request.Target.Width,
                request.Target.Height,
                projectClock,
                pauseController,
                projectWriter,
                cancellationToken);

            AudioRecordingStartResult audioStart =
                RecoverableAudioRecordingSession.TryStart(
                    request.ProjectRoot,
                    projectClock,
                    pauseController,
                    projectWriter);
            audio = audioStart.Session;
            if (!audioStart.Succeeded)
            {
                issues.Add(
                    new WindowsRecordingIssue(
                        "audio",
                        RecordingIssueSeverity.Warning,
                        audioStart.Error ?? "Audio capture could not start."));
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (request.IncludeCamera)
            {
                try
                {
                    camera = await RecoverableCameraRecordingSession.CreateAsync(
                        request.ProjectRoot,
                        projectClock,
                        pauseController,
                        projectWriter,
                        cancellationToken: cancellationToken);
                }
                catch (Exception exception) when (
                    exception is COMException or
                        InvalidOperationException or
                        UnauthorizedAccessException)
                {
                    issues.Add(
                        Warning("camera", exception));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();

            capture = WindowsScreenCaptureSession.Create(
                request.Target.Item,
                projectClock,
                screen.ProcessFrameAsync);
            WindowsRecordingSession session = new(
                request,
                projectClock,
                pauseController,
                projectWriter,
                screen,
                audio,
                camera,
                cursor,
                capture,
                issues.ToArray());
            TimeSpan startTime =
                projectClock.Normalize(QpcTimestamp.Now());
            pauseController.Resume(startTime);
            capture.Start();
            return new WindowsRecordingStartResult(
                session,
                session._startIssues);
        }
        catch (Exception startException)
        {
            List<Exception> cleanupErrors = await CleanupFailedStartAsync(
                capture,
                screen,
                audio,
                camera,
                cursor,
                projectWriter);
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException(
                    "Recording start failed and one or more resources also failed to clean up.",
                    [startException, .. cleanupErrors]);
            }

            throw;
        }
    }

    public TimeSpan MapActiveTime(TimeSpan rawProjectTime) =>
        _pauseController.Map(rawProjectTime);

    public void Pause()
    {
        TimeSpan rawTime = _projectClock.Normalize(QpcTimestamp.Now());
        _pauseController.Pause(rawTime);
    }

    public void Resume()
    {
        TimeSpan rawTime = _projectClock.Normalize(QpcTimestamp.Now());
        _pauseController.Resume(rawTime);
    }

    public async Task<WindowsRecordingFinalizationResult> StopAsync(
        RecordingStopReason reason,
        CancellationToken cancellationToken = default)
    {
        Task<WindowsRecordingFinalizationResult> stopTask;
        lock (_stopGate)
        {
            _stopTask ??= StopCoreAsync(reason);
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? await stopTask.WaitAsync(cancellationToken)
            : await stopTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(RecordingStopReason.ApplicationExit);
    }

    private async Task<WindowsRecordingFinalizationResult> StopCoreAsync(
        RecordingStopReason reason)
    {
        List<WindowsRecordingIssue> issues = [.. _startIssues];
        RecordingSegmentEntry? screenResult = null;
        AudioRecordingResult? audioResult = null;
        CameraRecordingResult? cameraResult = null;
        CursorMetadataDocument? cursorResult = null;
        CaptureFrameHealthSnapshot screenHealth = _capture.Health;
        TimeSpan stopTime =
            _projectClock.Normalize(QpcTimestamp.Now());
        if (!_pauseController.IsPaused)
        {
            _pauseController.Pause(stopTime);
        }
        TimeSpan activeDuration = _pauseController.Map(stopTime);

        _capture.HealthChanged -= OnScreenHealthChanged;
        _capture.CaptureClosed -= OnCaptureClosed;
        _capture.CaptureFailed -= OnCaptureFailed;
        _screen.Failed -= OnScreenFailed;
        if (_audio is not null)
        {
            _audio.HealthChanged -= OnAudioHealthChanged;
        }

        try
        {
            await _capture.StopAsync();
            Exception? captureFailure =
                _capture.TerminalFailure ?? _captureFailure;
            if (captureFailure is not null)
            {
                issues.Add(Error("screen-capture", captureFailure));
            }
        }
        catch (Exception exception)
        {
            issues.Add(Error("screen-capture", exception));
        }

        bool audioStopped = _audio is null;
        if (_audio is not null)
        {
            try
            {
                await _audio.StopAsync(CancellationToken.None);
                audioStopped = true;
            }
            catch (Exception exception)
            {
                issues.Add(Warning("audio-stop", exception));
            }
        }

        bool cameraStopped = _camera is null;
        if (_camera is not null)
        {
            try
            {
                await _camera.StopAsync(
                    activeDuration,
                    CancellationToken.None);
                cameraStopped = true;
            }
            catch (Exception exception)
            {
                issues.Add(Warning("camera-stop", exception));
            }
        }

        if (_cursor is not null)
        {
            try
            {
                cursorResult = await _cursor.CompleteAsync(
                    Request.ProjectRoot,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                issues.Add(Warning("cursor", exception));
            }
        }

        try
        {
            screenResult = await _screen.CompleteAsync(activeDuration);
        }
        catch (Exception exception)
        {
            issues.Add(Error("screen-publish", exception));
        }

        if (_audio is not null && audioStopped)
        {
            try
            {
                audioResult = await _audio.PublishAsync(
                    activeDuration,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                issues.Add(Warning("audio-publish", exception));
            }
        }

        if (_camera is not null && cameraStopped)
        {
            try
            {
                cameraResult = await _camera.PublishAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                issues.Add(Warning("camera-publish", exception));
            }
        }

        await DisposeComponentAsync(
            "screen",
            _screen.DisposeAsync,
            issues);
        await DisposeComponentAsync(
            "screen-capture",
            _capture.DisposeAsync,
            issues);
        if (_audio is not null)
        {
            await DisposeComponentAsync(
                "audio",
                _audio.DisposeAsync,
                issues);
        }
        if (_camera is not null)
        {
            await DisposeComponentAsync(
                "camera",
                _camera.DisposeAsync,
                issues);
        }
        if (_cursor is not null)
        {
            await DisposeComponentAsync(
                "cursor",
                _cursor.DisposeAsync,
                issues);
        }
        _projectWriter.Dispose();

        return new WindowsRecordingFinalizationResult(
            SessionId,
            Request.ProjectRoot,
            reason,
            screenHealth,
            screenResult,
            audioResult,
            cameraResult,
            cursorResult,
            issues);
    }

    private static async Task<List<Exception>> CleanupFailedStartAsync(
        WindowsScreenCaptureSession? capture,
        SurfaceScreenSegmentRecorder? screen,
        RecoverableAudioRecordingSession? audio,
        RecoverableCameraRecordingSession? camera,
        CursorMetadataRecorder? cursor,
        RecordingProjectWriter? projectWriter)
    {
        List<Exception> errors = [];
        if (capture is not null)
        {
            await TryCleanupAsync(capture.DisposeAsync, errors);
        }
        if (screen is not null)
        {
            await TryCleanupAsync(screen.DisposeAsync, errors);
        }
        if (audio is not null)
        {
            await TryCleanupAsync(audio.DisposeAsync, errors);
        }
        if (camera is not null)
        {
            await TryCleanupAsync(camera.DisposeAsync, errors);
        }
        if (cursor is not null)
        {
            await TryCleanupAsync(cursor.DisposeAsync, errors);
        }

        try
        {
            projectWriter?.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        return errors;
    }

    private static async Task TryCleanupAsync(
        Func<ValueTask> cleanup,
        List<Exception> errors)
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static async Task DisposeComponentAsync(
        string component,
        Func<ValueTask> dispose,
        List<WindowsRecordingIssue> issues)
    {
        try
        {
            await dispose();
        }
        catch (Exception exception)
        {
            issues.Add(Warning($"{component}-dispose", exception));
        }
    }

    private void OnAudioHealthChanged(AudioCaptureHealth health) =>
        AudioHealthChanged?.Invoke(health);

    private void OnCaptureClosed()
    {
        Interlocked.Exchange(ref _captureClosed, 1);
        CaptureClosed?.Invoke();
    }

    private void OnCaptureFailed(Exception exception)
    {
        _captureFailure = exception;
        CaptureFailed?.Invoke(exception);
    }

    private void OnScreenHealthChanged(CaptureFrameHealthSnapshot health) =>
        ScreenHealthChanged?.Invoke(health);

    private void OnScreenFailed(Exception exception)
    {
        _captureFailure = exception;
        CaptureFailed?.Invoke(exception);
    }

    private static WindowsRecordingIssue Error(
        string component,
        Exception exception) =>
        new(
            component,
            RecordingIssueSeverity.Error,
            exception.Message);

    private static WindowsRecordingIssue Warning(
        string component,
        Exception exception) =>
        new(
            component,
            RecordingIssueSeverity.Warning,
            exception.Message);
}
