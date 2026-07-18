using System.Text.Json;
using Microsoft.Graphics.Canvas;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Domain.Video;
using SevenRecord.Media.Windows;
using SevenRecord.Recording;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Graphics.DirectX;

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
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly MediaCapture _capture;
    private readonly CanvasDevice _device;
    private readonly Direct3DSurfaceVideoEncoder _encoder;
    private readonly MediaFrameReader _reader;
    private readonly string _layoutPath;
    private readonly PresenterLayoutSettings _layout;
    private readonly bool _ownsProjectWriter;
    private readonly RecordingPauseController _pauseController;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private readonly ProjectClock _projectClock;
    private readonly RecordingProjectWriter _projectWriter;
    private readonly CanvasRenderTarget _renderTarget;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _temporaryPath;
    private Exception? _failure;
    private readonly TaskCompletionSource _firstFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _frames;
    private long _droppedFrames;
    private bool _completed;
    private bool _disposed;
    private TimeSpan _duration;
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
        _layout = layout;
        _temporaryPath = temporaryPath;
        _layoutPath = Path.Combine(projectRoot, "presenter-layout.json");
        _reader.FrameArrived += OnFrameArrived;
    }

    public string DeviceName { get; }

    public int Height { get; }

    public int Width { get; }

    public static async Task<RecoverableCameraRecordingSession> CreateAsync(
        string projectRoot,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        PresenterLayoutSettings? layout = null,
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
        CancellationToken cancellationToken = default) =>
        CreateAsync(
            projectRoot,
            projectClock,
            pauseController,
            projectWriter,
            ownsProjectWriter: false,
            layout: layout,
            cancellationToken: cancellationToken);

    private static async Task<RecoverableCameraRecordingSession> CreateAsync(
        string projectRoot,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        bool ownsProjectWriter,
        PresenterLayoutSettings? layout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(projectClock);
        ArgumentNullException.ThrowIfNull(pauseController);
        ArgumentNullException.ThrowIfNull(projectWriter);
        Directory.CreateDirectory(projectRoot);

        IReadOnlyList<MediaFrameSourceGroup> groups =
            await MediaFrameSourceGroup.FindAllAsync();
        MediaFrameSourceGroup group = groups
            .FirstOrDefault(candidate =>
                candidate.SourceInfos.Any(info =>
                    info.SourceKind is MediaFrameSourceKind.Color))
            ?? throw new InvalidOperationException("No color camera source is available.");

        MediaCapture capture = new();
        try
        {
            await capture.InitializeAsync(
                new MediaCaptureInitializationSettings
                {
                    MemoryPreference = MediaCaptureMemoryPreference.Auto,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    SourceGroup = group,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                });

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
            string temporaryPath = Path.Combine(
                projectRoot,
                "temp",
                "camera.partial.mp4");
            Direct3DSurfaceVideoEncoder encoder =
                await Direct3DSurfaceVideoEncoder.CreateAsync(
                    temporaryPath,
                    width,
                    height,
                    framesPerSecond: 30,
                    bitrate: 4_000_000,
                    cancellationToken);
            CanvasDevice device = new();
            CanvasRenderTarget renderTarget = new(
                device,
                width,
                height,
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
                ownsProjectWriter,
                layout ?? PresenterLayoutSettings.DefaultOverlay,
                temporaryPath);

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

            return session;
        }
        catch
        {
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
        _reader.FrameArrived -= OnFrameArrived;
        await _reader.StopAsync();
        _shutdown.Cancel();
        await _processingGate.WaitAsync(cancellationToken);
        _processingGate.Release();

        if (_failure is not null)
        {
            throw new InvalidOperationException("Camera frame processing failed.", _failure);
        }

        await _encoder.CompleteAsync();
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
        RecordingSegmentEntry segment = await _projectWriter.PublishAsync(
            _temporaryPath,
            sourceId: "camera",
            start: TimeSpan.Zero,
            _duration,
            cancellationToken);

        string temporaryLayoutPath = _layoutPath + ".tmp";
        string layoutJson = JsonSerializer.Serialize(_layout, SerializerOptions);
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
            _layout,
            Path.GetFileName(_layoutPath));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
            await _encoder.DisposeAsync();
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
            using CanvasBitmap cameraBitmap =
                CanvasBitmap.CreateFromDirect3D11Surface(_device, surface);
            using (CanvasDrawingSession drawing = _renderTarget.CreateDrawingSession())
            {
                drawing.DrawImage(cameraBitmap);
            }
            await _encoder.ProcessSurfaceAsync(
                _renderTarget,
                projectTime,
                _shutdown.Token);
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
}
