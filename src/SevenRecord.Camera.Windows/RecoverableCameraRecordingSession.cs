using System.Diagnostics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Domain.Video;
using SevenRecord.Media;
using SevenRecord.Media.Windows;
using SevenRecord.Recording;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media;
using Windows.Media.MediaProperties;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;

namespace SevenRecord.Camera.Windows;

public sealed record CameraRecordingResult(
    RecordingSegmentEntry Segment,
    string DeviceName,
    int Width,
    int Height,
    long Frames,
    long DroppedFrames,
    PresenterLayoutSettings Layout,
    string LayoutPath);

public sealed class RecoverableCameraRecordingSession : IAsyncDisposable
{
    private const int PreviewFramesPerSecond = 6;
    private const int PreviewMaximumHeight = 360;
    private const int PreviewMaximumWidth = 640;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly MediaCapture _capture;
    private readonly CanvasDevice _device;
    private readonly MediaFrameReader _reader;
    private readonly string _layoutPath;
    private readonly object _layoutGate = new();
    private PresenterLayoutSettings _layout;
    private bool _ownsProjectWriter;
    private BackgroundEffectSupport? _previousBackgroundEffects;
    private BackgroundEffectSupport? _appliedBackgroundEffects;
    private readonly RecordingPauseController _pauseController;
    private readonly RecordingSegmentPolicy _segmentPolicy;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private readonly ProjectClock _projectClock;
    private readonly RecordingProjectWriter _projectWriter;
    private readonly string _projectRoot;
    private readonly CanvasRenderTarget _renderTarget;
    private readonly CancellationTokenSource _shutdown = new();
    private Direct3DSurfaceVideoEncoder _encoder;
    private string _temporaryPath;
    private Exception? _failure;
    private readonly TaskCompletionSource _firstFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _frames;
    private long _droppedFrames;
    private long _lastPreviewTimestamp;
    private bool _completed;
    private bool _disposed;
    private TimeSpan _duration;
    private int _previewInFlight;
    private CanvasRenderTarget? _previewTarget;
    private Task _previewTask = Task.CompletedTask;
    private Task _publicationTail = Task.CompletedTask;
    private readonly List<Exception> _rolloverFailures = [];
    private int _segmentNumber = 1;
    private TimeSpan _segmentStart;
    private int _stopping;
    private bool _currentSegmentHasFrame;
    private RecordingSegmentEntry? _lastPublishedSegment;
    private bool _stopped;

    private RecoverableCameraRecordingSession(
        string projectRoot,
        string deviceName,
        int width,
        int height,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        MediaCapture capture,
        MediaFrameReader reader,
        CanvasDevice device,
        CanvasRenderTarget renderTarget,
        Direct3DSurfaceVideoEncoder encoder,
        RecordingProjectWriter projectWriter,
        bool ownsProjectWriter,
        PresenterLayoutSettings layout,
        RecordingSegmentPolicy segmentPolicy,
        string temporaryPath)
    {
        DeviceName = deviceName;
        Width = width;
        Height = height;
        _projectClock = projectClock;
        _pauseController = pauseController;
        _capture = capture;
        _reader = reader;
        _device = device;
        _renderTarget = renderTarget;
        _encoder = encoder;
        _projectWriter = projectWriter;
        _ownsProjectWriter = ownsProjectWriter;
        _projectRoot = projectRoot;
        _segmentPolicy = segmentPolicy;
        _layout = layout.ConstrainToFrame();
        _temporaryPath = temporaryPath;
        _layoutPath = Path.Combine(projectRoot, "presenter-layout.json");
        _reader.FrameArrived += OnFrameArrived;
    }

    public string DeviceName { get; }

    public BackgroundEffectSupport BackgroundEffects { get; private init; } =
        new(false, false, BackgroundBlurMode.Off, null);

    public int Height { get; }

    public event Action<Exception>? PreviewFailed;

    public event Action<SoftwareBitmapPreviewFrame>? PreviewFrameReady;

    public int Width { get; }

    public PresenterLayoutSettings Layout
    {
        get
        {
            lock (_layoutGate)
            {
                return _layout;
            }
        }
    }

    public static async Task<RecoverableCameraRecordingSession> CreateAsync(
        string projectRoot,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        PresenterLayoutSettings? layout = null,
        RecordingSegmentPolicy? segmentPolicy = null,
        CancellationToken cancellationToken = default)
    {
        RecordingProjectWriter projectWriter =
            await RecordingProjectWriter.OpenAsync(
                projectRoot,
                cancellationToken);
        try
        {
            return await CreateAsync(
                projectRoot,
                projectClock,
                pauseController,
                projectWriter,
                ownsProjectWriter: true,
                layout: layout,
                segmentPolicy: segmentPolicy,
                cancellationToken: cancellationToken);
        }
        catch
        {
            projectWriter.Dispose();
            throw;
        }
    }

    public static Task<RecoverableCameraRecordingSession> CreateAsync(
        string projectRoot,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        PresenterLayoutSettings? layout = null,
        RecordingSegmentPolicy? segmentPolicy = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(
            projectRoot,
            projectClock,
            pauseController,
            projectWriter,
            ownsProjectWriter: false,
            layout: layout,
            segmentPolicy: segmentPolicy,
            cancellationToken: cancellationToken);

    private static async Task<RecoverableCameraRecordingSession> CreateAsync(
        string projectRoot,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        bool ownsProjectWriter,
        PresenterLayoutSettings? layout,
        RecordingSegmentPolicy? segmentPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(projectClock);
        ArgumentNullException.ThrowIfNull(pauseController);
        ArgumentNullException.ThrowIfNull(projectWriter);
        Directory.CreateDirectory(projectRoot);

        IReadOnlyList<MediaFrameSourceGroup> groups =
            await MediaFrameSourceGroup.FindAllAsync();
        MediaFrameSourceGroup[] colorGroups = groups
            .Where(candidate =>
                candidate.SourceInfos.Any(info =>
                    info.SourceKind is MediaFrameSourceKind.Color))
            .ToArray();
        if (colorGroups.Length == 0)
        {
            throw new CameraBackgroundEffectRestoreException(
                "No color camera source is available.");
        }

        List<Exception> failures = [];
        foreach (MediaFrameSourceGroup group in colorGroups)
        {
            foreach (MediaCaptureSharingMode sharingMode in new[]
                     {
                         MediaCaptureSharingMode.SharedReadOnly,
                         MediaCaptureSharingMode.ExclusiveControl,
                     })
            {
                try
                {
                    return await CreateFromGroupAsync(
                        projectRoot,
                        projectClock,
                        pauseController,
                        projectWriter,
                        ownsProjectWriter,
                        layout,
                        segmentPolicy,
                        group,
                        sharingMode,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (exception.Data.Contains(
                            "BackgroundEffectRestoreFailure"))
                    {
                        throw new InvalidOperationException(
                            "Windows could not restore the previous camera background effect.",
                            exception);
                    }
                    failures.Add(exception);
                }
            }
        }

        throw new AggregateException(
            "No camera delivered a processable frame.",
            failures);
    }

    private static async Task<RecoverableCameraRecordingSession>
        CreateFromGroupAsync(
            string projectRoot,
            ProjectClock projectClock,
            RecordingPauseController pauseController,
            RecordingProjectWriter projectWriter,
            bool ownsProjectWriter,
            PresenterLayoutSettings? layout,
            RecordingSegmentPolicy? segmentPolicy,
            MediaFrameSourceGroup group,
            MediaCaptureSharingMode sharingMode,
            CancellationToken cancellationToken)
    {
        MediaCapture capture = new();
        BackgroundEffectSupport? previousBackgroundEffects = null;
        BackgroundEffectSupport? appliedBackgroundEffects = null;
        try
        {
            PresenterLayoutSettings requestedLayout =
                (layout ?? PresenterLayoutSettings.DefaultOverlay)
                    .ConstrainToFrame();
            await capture.InitializeAsync(
                new MediaCaptureInitializationSettings
                {
                    MemoryPreference = MediaCaptureMemoryPreference.Auto,
                    SharingMode = sharingMode,
                    SourceGroup = group,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                });
            previousBackgroundEffects =
                WindowsStudioBackgroundEffectController.Query(
                    capture.VideoDeviceController);
            if (!previousBackgroundEffects.OperationSucceeded &&
                !previousBackgroundEffects.DefinitelyUnsupported &&
                requestedLayout.Effects.BackgroundBlur is
                    BackgroundBlurMode.Off)
            {
                throw new InvalidOperationException(
                    "Windows could not verify that background blur is off.");
            }
            if (!previousBackgroundEffects.OperationSucceeded &&
                requestedLayout.Effects.BackgroundBlur is not
                    BackgroundBlurMode.Off)
            {
                throw new InvalidOperationException(
                    "Windows could not safely inspect the current camera background effect.");
            }
            if (sharingMode is MediaCaptureSharingMode.SharedReadOnly &&
                previousBackgroundEffects.OperationSucceeded &&
                previousBackgroundEffects.IsSupported &&
                requestedLayout.Effects.BackgroundBlur !=
                    previousBackgroundEffects.ActiveMode)
            {
                throw new CameraEffectControlRequiredException();
            }
            BackgroundEffectSupport backgroundEffects =
                sharingMode is MediaCaptureSharingMode.ExclusiveControl &&
                previousBackgroundEffects.OperationSucceeded
                    ? WindowsStudioBackgroundEffectController.Apply(
                        capture.VideoDeviceController,
                        requestedLayout.Effects.BackgroundBlur)
                    : previousBackgroundEffects with
                    {
                        Message = requestedLayout.Effects.BackgroundBlur ==
                            previousBackgroundEffects.ActiveMode
                            ? null
                            : "Camera is shared; using the current Windows background effect.",
                    };
            if (sharingMode is MediaCaptureSharingMode.ExclusiveControl &&
                !backgroundEffects.OperationSucceeded)
            {
                appliedBackgroundEffects = backgroundEffects;
                throw new InvalidOperationException(
                    backgroundEffects.Message ??
                    "Windows did not confirm the requested background effect.");
            }
            appliedBackgroundEffects = backgroundEffects;
            if (requestedLayout.Effects.BackgroundBlur is not
                    BackgroundBlurMode.Off &&
                backgroundEffects.ActiveMode !=
                    requestedLayout.Effects.BackgroundBlur)
            {
                throw new InvalidOperationException(
                    backgroundEffects.Message ??
                    "The requested person-aware background effect could not be enabled.");
            }
            PresenterLayoutSettings effectiveLayout =
                requestedLayout with
                {
                    Effects = requestedLayout.Effects with
                    {
                        BackgroundBlur =
                            backgroundEffects.ActiveMode,
                    },
                };

            MediaFrameSource source = capture.FrameSources.Values
                .First(candidate =>
                    candidate.Info.SourceKind is MediaFrameSourceKind.Color);
            MediaFrameFormat format = source.SupportedFormats
                .Where(candidate => candidate.VideoFormat is not null)
                .OrderByDescending(candidate =>
                    (long)candidate.VideoFormat.Width * candidate.VideoFormat.Height)
                .First();
            await source.SetFormatAsync(format);

            int width = (int)source.CurrentFormat.VideoFormat.Width;
            int height = (int)source.CurrentFormat.VideoFormat.Height;
            MediaFrameReader reader = await capture.CreateFrameReaderAsync(
                source,
                MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            string temporaryPath = TemporaryPath(projectRoot, 1);
            int encoderWidth = VideoEncodingDimensions.NormalizeEven(width);
            int encoderHeight = VideoEncodingDimensions.NormalizeEven(height);
            Direct3DSurfaceVideoEncoder encoder =
                await Direct3DSurfaceVideoEncoder.CreateAsync(
                    temporaryPath,
                    encoderWidth,
                    encoderHeight,
                    framesPerSecond: 30,
                    bitrate: 4_000_000,
                    cancellationToken);
            CanvasDevice device = new();
            CanvasRenderTarget renderTarget = new(
                device,
                encoderWidth,
                encoderHeight,
                96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                CanvasAlphaMode.Ignore);
            RecoverableCameraRecordingSession session = new(
                projectRoot,
                group.DisplayName,
                width,
                height,
                projectClock,
                pauseController,
                capture,
                reader,
                device,
                renderTarget,
                encoder,
                projectWriter,
                ownsProjectWriter: false,
                effectiveLayout,
                segmentPolicy ?? RecordingSegmentPolicy.Default,
                temporaryPath)
            {
                BackgroundEffects = backgroundEffects,
                _previousBackgroundEffects =
                    sharingMode is
                        MediaCaptureSharingMode.ExclusiveControl &&
                    previousBackgroundEffects.OperationSucceeded
                        ? previousBackgroundEffects
                        : null,
                _appliedBackgroundEffects = backgroundEffects,
            };

            MediaFrameReaderStartStatus status = await reader.StartAsync();
            if (status is not MediaFrameReaderStartStatus.Success)
            {
                await session.DisposeAsync();
                throw new InvalidOperationException($"Camera reader failed to start: {status}.");
            }

            try
            {
                await session._firstFrame.Task.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
            }
            catch (OperationCanceledException cancellationException)
                when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    cancellationException.Data["CameraCleanupFailure"] =
                        cleanupException.ToString();
                }
                throw;
            }
            catch
            {
                await session.DisposeAsync();
                throw new InvalidOperationException(
                    $"Camera '{group.DisplayName}' did not deliver a processable frame within five seconds.");
            }

            session._ownsProjectWriter = ownsProjectWriter;
            return session;
        }
        catch (Exception initializationException)
        {
            if (previousBackgroundEffects is
                    { OperationSucceeded: true } &&
                sharingMode is MediaCaptureSharingMode.ExclusiveControl)
            {
                try
                {
                    if (appliedBackgroundEffects is not null)
                    {
                        await WindowsStudioBackgroundEffectController
                            .RestoreWithRetryAsync(
                                capture.VideoDeviceController,
                                previousBackgroundEffects,
                                appliedBackgroundEffects,
                                CancellationToken.None);
                    }
                    else
                    {
                        BackgroundEffectSupport restored =
                            WindowsStudioBackgroundEffectController.Restore(
                                capture.VideoDeviceController,
                                previousBackgroundEffects);
                        if (!restored.OperationSucceeded)
                        {
                            throw new CameraBackgroundEffectRestoreException(
                                restored.Message ??
                                "The previous camera effect could not be restored.");
                        }
                    }
                }
                catch (Exception restorationException)
                {
                    initializationException.Data[
                        "BackgroundEffectRestoreFailure"] =
                        restorationException.ToString();
                }
            }
            capture.Dispose();
            throw;
        }
    }

    public async Task<CameraRecordingResult> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The camera recording is already complete.");
        }

        await StopAsync(cancellationToken);
        return await PublishAsync(cancellationToken);
    }

    public void UpdateLayout(PresenterLayoutSettings layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        lock (_layoutGate)
        {
            _layout = layout.ConstrainToFrame();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        StopAsync(
            _pauseController.Map(
                _projectClock.Normalize(QpcTimestamp.Now())),
            cancellationToken);

    public async Task StopAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        Interlocked.Exchange(ref _stopping, 1);
        _reader.FrameArrived -= OnFrameArrived;
        await _reader.StopAsync();
        _shutdown.Cancel();
        await _processingGate.WaitAsync(cancellationToken);
        _processingGate.Release();
        await _previewTask.WaitAsync(cancellationToken);

        if (_failure is not null)
        {
            throw new InvalidOperationException(
                $"Camera frame processing failed: {_failure.GetType().Name} " +
                $"0x{_failure.HResult:X8}: {_failure.Message}",
                _failure);
        }

        if (_currentSegmentHasFrame)
        {
            await _encoder.CompleteAsync();
        }
        _duration = duration;
    }

    public async Task<CameraRecordingResult> PublishAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_stopped)
        {
            throw new InvalidOperationException("Camera capture must stop before publication.");
        }

        if (_completed)
        {
            throw new InvalidOperationException("The camera recording is already complete.");
        }

        _completed = true;
        await _encoder.DisposeAsync();
        await _publicationTail;
        if (_currentSegmentHasFrame)
        {
            _lastPublishedSegment =
                await _projectWriter.PublishAsync(
                    _temporaryPath,
                    sourceId: "camera",
                    start: _segmentStart,
                    _duration - _segmentStart,
                    cancellationToken);
        }
        else if (File.Exists(_temporaryPath))
        {
            File.Delete(_temporaryPath);
        }
        ThrowRolloverFailures();
        RecordingSegmentEntry segment = _lastPublishedSegment ??
            throw new InvalidOperationException(
                "No camera segment received an encodable frame.");

        PresenterLayoutSettings layout = Layout;
        string temporaryLayoutPath = _layoutPath + ".tmp";
        string layoutJson = JsonSerializer.Serialize(layout, SerializerOptions);
        await File.WriteAllTextAsync(
            temporaryLayoutPath,
            layoutJson,
            cancellationToken);
        File.Move(temporaryLayoutPath, _layoutPath, overwrite: true);

        return new CameraRecordingResult(
            segment,
            DeviceName,
            Width,
            Height,
            Interlocked.Read(ref _frames),
            Interlocked.Read(ref _droppedFrames),
            layout,
            Path.GetFileName(_layoutPath));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _stopping, 1);
        Exception? failure = null;
        _reader.FrameArrived -= OnFrameArrived;
        if (!_stopped)
        {
            try
            {
                await _reader.StopAsync();
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }
        }

        _shutdown.Cancel();
        try
        {
            if (_previousBackgroundEffects is not null)
            {
                if (_appliedBackgroundEffects is not null)
                {
                    await WindowsStudioBackgroundEffectController
                        .RestoreWithRetryAsync(
                            _capture.VideoDeviceController,
                            _previousBackgroundEffects,
                            _appliedBackgroundEffects);
                }
                else
                {
                    BackgroundEffectSupport restored =
                        WindowsStudioBackgroundEffectController.Restore(
                            _capture.VideoDeviceController,
                            _previousBackgroundEffects);
                    if (!restored.OperationSucceeded)
                    {
                        throw new CameraBackgroundEffectRestoreException(
                            restored.Message ??
                            "The previous camera background effect could not be restored.");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        try
        {
            await _processingGate.WaitAsync();
            _processingGate.Release();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        try
        {
            await _previewTask;
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        try
        {
            await _encoder.DisposeAsync();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        try
        {
            await _publicationTail;
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }

        try
        {
            _previewTarget?.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        try
        {
            _renderTarget.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        try
        {
            _device.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        try
        {
            _reader.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        try
        {
            _capture.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        if (_ownsProjectWriter)
        {
            try
            {
                _projectWriter.Dispose();
            }
            catch (Exception exception)
            {
                failure = Combine(failure, exception);
            }
        }
        try
        {
            _processingGate.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }
        try
        {
            _shutdown.Dispose();
        }
        catch (Exception exception)
        {
            failure = Combine(failure, exception);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static Exception Combine(
        Exception? existing,
        Exception next) =>
        existing is null
            ? next
            : new AggregateException(existing, next);

    private async void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!_processingGate.Wait(0))
        {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }

        try
        {
            using MediaFrameReference? frame = sender.TryAcquireLatestFrame();
            if (frame?.VideoMediaFrame?.Direct3DSurface is not { } surface)
            {
                return;
            }
            _firstFrame.TrySetResult();
            if (_pauseController.IsPaused)
            {
                return;
            }

            TimeSpan systemRelativeTime =
                frame.SystemRelativeTime ?? QpcTimestamp.Now().SystemRelativeTime;
            TimeSpan projectTime = _pauseController.Map(
                _projectClock.NormalizeSystemRelativeTime(systemRelativeTime));
            using (CanvasDrawingSession drawing = _renderTarget.CreateDrawingSession())
            {
                drawing.Clear(global::Windows.UI.Color.FromArgb(255, 0, 0, 0));
            }
            using VideoFrame sourceFrame =
                VideoFrame.CreateWithDirect3D11Surface(surface);
            using VideoFrame destinationFrame =
                VideoFrame.CreateWithDirect3D11Surface(_renderTarget);
            global::Windows.Graphics.DirectX.Direct3D11.Direct3DSurfaceDescription
                surfaceDescription = surface.Description;
            BitmapBounds sourceBounds = new()
            {
                Width = (uint)surfaceDescription.Width,
                Height = (uint)surfaceDescription.Height,
            };
            BitmapBounds destinationBounds = new()
            {
                Width = (uint)_renderTarget.SizeInPixels.Width,
                Height = (uint)_renderTarget.SizeInPixels.Height,
            };
            await sourceFrame.CopyToAsync(
                destinationFrame,
                sourceBounds,
                destinationBounds);
            if (Volatile.Read(ref _stopping) == 0 &&
                _segmentPolicy.ShouldRollover(
                    _segmentStart,
                    projectTime))
            {
                await RotateSegmentAsync(projectTime);
            }
            await _encoder.ProcessSurfaceAsync(
                _renderTarget,
                projectTime,
                _shutdown.Token);
            _currentSegmentHasFrame = true;
            QueuePreviewFrame(projectTime);
            Interlocked.Increment(ref _frames);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _failure ??= exception;
            _firstFrame.TrySetException(exception);
        }
        finally
        {
            _processingGate.Release();
        }
    }

    private async Task RotateSegmentAsync(TimeSpan boundary)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        Direct3DSurfaceVideoEncoder previousEncoder = _encoder;
        string previousPath = _temporaryPath;
        TimeSpan previousStart = _segmentStart;
        await previousEncoder.CompleteAsync();
        await previousEncoder.DisposeAsync();
        QueueSegmentFinalization(
            previousPath,
            previousStart,
            boundary - previousStart);
        _segmentNumber++;
        _temporaryPath = TemporaryPath(_projectRoot, _segmentNumber);
        _encoder = await Direct3DSurfaceVideoEncoder.CreateAsync(
            _temporaryPath,
            VideoEncodingDimensions.NormalizeEven(Width),
            VideoEncodingDimensions.NormalizeEven(Height),
            framesPerSecond: 30,
            bitrate: 4_000_000,
            CancellationToken.None);
        _segmentStart = boundary;
        _currentSegmentHasFrame = false;
    }

    private void QueueSegmentFinalization(
        string temporaryPath,
        TimeSpan start,
        TimeSpan duration)
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
                _lastPublishedSegment =
                    await _projectWriter.PublishAsync(
                    temporaryPath,
                    "camera",
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

    private void ThrowRolloverFailures()
    {
        lock (_rolloverFailures)
        {
            if (_rolloverFailures.Count > 0)
            {
                throw new AggregateException(
                    "One or more camera segments could not be published.",
                    _rolloverFailures);
            }
        }
    }

    private static string TemporaryPath(
        string projectRoot,
        int segmentNumber) =>
        Path.Combine(
            projectRoot,
            "temp",
            $"camera-{segmentNumber:D8}.partial.mp4");


    private void QueuePreviewFrame(TimeSpan projectTime)
    {
        if (PreviewFrameReady is null || !TryBeginPreview())
        {
            return;
        }

        try
        {
            (int width, int height) = ScaleToFit(
                Width,
                Height,
                PreviewMaximumWidth,
                PreviewMaximumHeight);
            EnsurePreviewTarget(width, height);
            PresenterLayoutSettings layout = Layout;
            using ExposureEffect exposure = new()
            {
                Source = _renderTarget,
                Exposure = (float)layout.Effects.Exposure,
            };
            using CanvasDrawingSession drawing =
                _previewTarget!.CreateDrawingSession();
            drawing.Clear(global::Windows.UI.Color.FromArgb(255, 0, 0, 0));
            drawing.DrawImage(
                exposure,
                new Rect(0, 0, width, height),
                new Rect(0, 0, Width, Height));
            _previewTask = PublishPreviewFrameAsync(
                _previewTarget,
                projectTime);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _previewInFlight, 0);
            PreviewFailed?.Invoke(exception);
        }
    }

    private async Task PublishPreviewFrameAsync(
        CanvasRenderTarget snapshot,
        TimeSpan projectTime)
    {
        SoftwareBitmap? bitmap = null;
        try
        {
            using SoftwareBitmap surfaceCopy =
                await SoftwareBitmap.CreateCopyFromSurfaceAsync(snapshot);
            bitmap = SoftwareBitmap.Convert(
                surfaceCopy,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            if (!_shutdown.IsCancellationRequested)
            {
                Action<SoftwareBitmapPreviewFrame>? handler = PreviewFrameReady;
                if (handler is not null)
                {
                    SoftwareBitmapPreviewFrame frame = new(bitmap, projectTime);
                    bitmap = null;
                    handler(frame);
                }
            }
        }
        catch (Exception exception)
        {
            PreviewFailed?.Invoke(exception);
        }
        finally
        {
            bitmap?.Dispose();
            Interlocked.Exchange(ref _previewInFlight, 0);
        }
    }

    private void EnsurePreviewTarget(int width, int height)
    {
        if (_previewTarget is not null &&
            ((int)_previewTarget.SizeInPixels.Width != width ||
             (int)_previewTarget.SizeInPixels.Height != height))
        {
            _previewTarget.Dispose();
            _previewTarget = null;
        }
        _previewTarget ??= new CanvasRenderTarget(
            _device,
            width,
            height,
            96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Ignore);
    }

    private bool TryBeginPreview()
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Volatile.Read(ref _lastPreviewTimestamp);
        if (previous != 0 &&
            Stopwatch.GetElapsedTime(previous, now) <
            TimeSpan.FromSeconds(1d / PreviewFramesPerSecond))
        {
            return false;
        }
        if (Interlocked.CompareExchange(ref _previewInFlight, 1, 0) != 0)
        {
            return false;
        }

        Volatile.Write(ref _lastPreviewTimestamp, now);
        return true;
    }

    private static (int Width, int Height) ScaleToFit(
        int sourceWidth,
        int sourceHeight,
        int maximumWidth,
        int maximumHeight)
    {
        double scale = Math.Min(
            1,
            Math.Min(
                (double)maximumWidth / sourceWidth,
                (double)maximumHeight / sourceHeight));
        return (
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }
}
