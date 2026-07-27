using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Capture.Windows;
using SevenRecord.Media;
using SevenRecord.Media.Windows;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace SevenRecord.Recording.Windows;

public sealed class SurfaceScreenSegmentRecorder : IAsyncDisposable
{
    private const int FramesPerSecond = 60;
    private const int PreviewFramesPerSecond = 6;
    private const int PreviewMaximumHeight = 540;
    private const int PreviewMaximumWidth = 960;
    private const uint Bitrate = 12_000_000;

    private readonly int _encoderHeight;
    private readonly int _encoderWidth;
    private readonly RecordingPauseController _pauseController;
    private readonly RecordingSegmentPolicy _segmentPolicy;
    private readonly RecordingProjectWriter _projectWriter;
    private readonly ProjectClock _projectClock;
    private readonly string _projectRoot;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _surfaceGate = new(1, 1);
    private Direct3DSurfaceVideoEncoder _encoder;
    private string _temporaryPath;
    private bool _completed;
    private CanvasDevice? _device;
    private CanvasRenderTarget? _encodeFrame;
    private bool _hasFrame;
    private CanvasRenderTarget? _latestFrame;
    private long _lastPreviewTimestamp;
    private bool _pacingStarted;
    private Task _pacingTask = Task.CompletedTask;
    private CanvasRenderTarget? _previewFrame;
    private int _previewInFlight;
    private Task _previewTask = Task.CompletedTask;
    private Task _publicationTail = Task.CompletedTask;
    private readonly List<Exception> _rolloverFailures = [];
    private int _segmentNumber = 1;
    private TimeSpan _segmentStart;
    private int _stopping;
    private bool _currentSegmentHasFrame;
    private RecordingSegmentEntry? _lastPublishedSegment;

    public event Action<Exception>? Failed;

    public event Action<Exception>? PreviewFailed;

    public event Action<SoftwareBitmapPreviewFrame>? PreviewFrameReady;

    private SurfaceScreenSegmentRecorder(
        Direct3DSurfaceVideoEncoder encoder,
        int encoderWidth,
        int encoderHeight,
        RecordingProjectWriter projectWriter,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        string projectRoot,
        RecordingSegmentPolicy segmentPolicy,
        string temporaryPath)
    {
        _encoder = encoder;
        _encoderWidth = encoderWidth;
        _encoderHeight = encoderHeight;
        _projectWriter = projectWriter;
        _projectClock = projectClock;
        _pauseController = pauseController;
        _projectRoot = projectRoot;
        _segmentPolicy = segmentPolicy;
        _temporaryPath = temporaryPath;
    }

    public static async Task<SurfaceScreenSegmentRecorder> CreateAsync(
        string projectRoot,
        int width,
        int height,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        RecordingSegmentPolicy? segmentPolicy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(projectClock);
        ArgumentNullException.ThrowIfNull(pauseController);
        ArgumentNullException.ThrowIfNull(projectWriter);
        Directory.CreateDirectory(projectRoot);

        RecordingSegmentPolicy policy =
            segmentPolicy ?? RecordingSegmentPolicy.Default;
        string temporaryPath = TemporaryPath(projectRoot, 1);
        int encoderWidth = VideoEncodingDimensions.NormalizeEven(width);
        int encoderHeight = VideoEncodingDimensions.NormalizeEven(height);
        Direct3DSurfaceVideoEncoder encoder =
            await Direct3DSurfaceVideoEncoder.CreateAsync(
                temporaryPath,
                encoderWidth,
                encoderHeight,
                FramesPerSecond,
                Bitrate,
                cancellationToken);
        return new SurfaceScreenSegmentRecorder(
            encoder,
            encoderWidth,
            encoderHeight,
            projectWriter,
            projectClock,
            pauseController,
            Path.GetFullPath(projectRoot),
            policy,
            temporaryPath);
    }

    public async ValueTask ProcessFrameAsync(
        ScreenCaptureFrameLease frame,
        CancellationToken cancellationToken)
    {
        if (_pauseController.IsPaused)
        {
            return;
        }

        await _surfaceGate.WaitAsync(cancellationToken);
        try
        {
            EnsureFrameTargets(frame.Device);
            using CanvasBitmap bitmap =
                CanvasBitmap.CreateFromDirect3D11Surface(
                    frame.Device,
                    frame.Surface);
            using CanvasDrawingSession drawing =
                _latestFrame!.CreateDrawingSession();
            drawing.Clear(Color.FromArgb(255, 0, 0, 0));
            drawing.DrawImage(bitmap);
            _hasFrame = true;
            if (!_pacingStarted)
            {
                _pacingStarted = true;
                _pacingTask = Task.Run(
                    PaceFramesAsync,
                    CancellationToken.None);
            }
        }
        finally
        {
            _surfaceGate.Release();
        }
    }

    public Task<RecordingSegmentEntry> CompleteAsync() =>
        CompleteAsync(
            _pauseController.Map(
                _projectClock.Normalize(QpcTimestamp.Now())));

    public async Task<RecordingSegmentEntry> CompleteAsync(
        TimeSpan duration)
    {
        if (_completed)
        {
            throw new InvalidOperationException(
                "The surface segment is already complete.");
        }

        _completed = true;
        Interlocked.Exchange(ref _stopping, 1);
        _shutdown.Cancel();
        Exception? pacingFailure = null;
        try
        {
            await _pacingTask;
        }
        catch (Exception exception)
        {
            pacingFailure = exception;
        }
        try
        {
            await _previewTask;
        }
        catch (Exception exception)
        {
            pacingFailure = pacingFailure is null
                ? exception
                : new AggregateException(pacingFailure, exception);
        }

        if (_currentSegmentHasFrame)
        {
            await _encoder.CompleteAsync();
        }
        await _encoder.DisposeAsync();
        await _publicationTail;
        if (pacingFailure is not null)
        {
            throw new InvalidOperationException(
                "The screen frame pacer failed.",
                pacingFailure);
        }

        if (_currentSegmentHasFrame)
        {
            _lastPublishedSegment =
                await _projectWriter.PublishAsync(
                    _temporaryPath,
                    sourceId: "screen",
                    start: _segmentStart,
                    duration - _segmentStart);
        }
        else if (File.Exists(_temporaryPath))
        {
            File.Delete(_temporaryPath);
        }
        ThrowRolloverFailures();
        return _lastPublishedSegment ??
            throw new InvalidOperationException(
                "No screen segment received an encodable frame.");
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _shutdown.Cancel();
        Exception? failure = null;
        try
        {
            await _pacingTask;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            await _previewTask;
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }

        try
        {
            await _encoder.DisposeAsync();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }
        try
        {
            await _publicationTail;
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }
        _encodeFrame?.Dispose();
        _latestFrame?.Dispose();
        _previewFrame?.Dispose();
        _surfaceGate.Dispose();
        _shutdown.Dispose();

        if (failure is not null)
        {
            throw failure;
        }
    }

    private void EnsureFrameTargets(CanvasDevice device)
    {
        _device ??= device;
        _latestFrame ??= CreateFrameTarget(device);
        _encodeFrame ??= CreateFrameTarget(device);
    }

    private CanvasRenderTarget CreateFrameTarget(CanvasDevice device) =>
        new(
            device,
            _encoderWidth,
            _encoderHeight,
            96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Ignore);

    private void QueuePreviewFrame(
        CanvasBitmap source,
        CanvasDevice device,
        int sourceWidth,
        int sourceHeight,
        TimeSpan projectTime)
    {
        if (PreviewFrameReady is null || !TryBeginPreview())
        {
            return;
        }

        try
        {
            (int width, int height) = ScaleToFit(
                sourceWidth,
                sourceHeight,
                PreviewMaximumWidth,
                PreviewMaximumHeight);
            EnsurePreviewTarget(device, width, height);
            using CanvasDrawingSession drawing =
                _previewFrame!.CreateDrawingSession();
            drawing.Clear(Color.FromArgb(255, 0, 0, 0));
            drawing.DrawImage(
                source,
                new Rect(0, 0, width, height),
                new Rect(0, 0, sourceWidth, sourceHeight));
            _previewTask = PublishPreviewFrameAsync(
                _previewFrame,
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

    private void EnsurePreviewTarget(
        CanvasDevice device,
        int width,
        int height)
    {
        if (_previewFrame is not null &&
            ((int)_previewFrame.SizeInPixels.Width != width ||
             (int)_previewFrame.SizeInPixels.Height != height))
        {
            _previewFrame.Dispose();
            _previewFrame = null;
        }
        _previewFrame ??= new CanvasRenderTarget(
            device,
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

    private async Task PaceFramesAsync()
    {
        using PeriodicTimer timer = new(
            TimeSpan.FromSeconds(1d / FramesPerSecond));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                if (_pauseController.IsPaused)
                {
                    continue;
                }

                bool hasFrame;
                TimeSpan projectTime = _pauseController.Map(
                    _projectClock.Normalize(QpcTimestamp.Now()));
                await _surfaceGate.WaitAsync(_shutdown.Token);
                try
                {
                    hasFrame = _hasFrame;
                    if (hasFrame)
                    {
                        using CanvasDrawingSession drawing =
                            _encodeFrame!.CreateDrawingSession();
                        drawing.DrawImage(_latestFrame);
                        QueuePreviewFrame(
                            _latestFrame!,
                            _device!,
                            _encoderWidth,
                            _encoderHeight,
                            projectTime);
                    }
                }
                finally
                {
                    _surfaceGate.Release();
                }

                if (!hasFrame)
                {
                    continue;
                }

                if (Volatile.Read(ref _stopping) == 0 &&
                    _segmentPolicy.ShouldRollover(
                        _segmentStart,
                        projectTime))
                {
                    await RotateSegmentAsync(projectTime);
                }

                await _encoder.ProcessSurfaceAsync(
                    _encodeFrame!,
                    projectTime,
                    _shutdown.Token);
                _currentSegmentHasFrame = true;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Failed?.Invoke(exception);
            throw;
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
            _encoderWidth,
            _encoderHeight,
            FramesPerSecond,
            Bitrate,
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
                    "screen",
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
                    "One or more screen segments could not be published.",
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
            $"screen-{segmentNumber:D8}.partial.mp4");

}
