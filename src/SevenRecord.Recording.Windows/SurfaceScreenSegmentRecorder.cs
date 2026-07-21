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
    private readonly Direct3DSurfaceVideoEncoder _encoder;
    private readonly RecordingPauseController _pauseController;
    private readonly RecordingProjectWriter _projectWriter;
    private readonly ProjectClock _projectClock;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _surfaceGate = new(1, 1);
    private readonly string _temporaryPath;
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
        string temporaryPath)
    {
        _encoder = encoder;
        _encoderWidth = encoderWidth;
        _encoderHeight = encoderHeight;
        _projectWriter = projectWriter;
        _projectClock = projectClock;
        _pauseController = pauseController;
        _temporaryPath = temporaryPath;
    }

    public static async Task<SurfaceScreenSegmentRecorder> CreateAsync(
        string projectRoot,
        int width,
        int height,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(projectClock);
        ArgumentNullException.ThrowIfNull(pauseController);
        ArgumentNullException.ThrowIfNull(projectWriter);
        Directory.CreateDirectory(projectRoot);

        string temporaryPath = Path.Combine(
            projectRoot,
            "temp",
            "screen.partial.mp4");
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

        await _encoder.CompleteAsync();
        if (pacingFailure is not null)
        {
            throw new InvalidOperationException(
                "The screen frame pacer failed.",
                pacingFailure);
        }

        return await _projectWriter.PublishAsync(
            _temporaryPath,
            sourceId: "screen",
            start: TimeSpan.Zero,
            duration);
    }

    public async ValueTask DisposeAsync()
    {
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
        finally
        {
            _encodeFrame?.Dispose();
            _latestFrame?.Dispose();
            _previewFrame?.Dispose();
            _surfaceGate.Dispose();
            _shutdown.Dispose();
        }

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

                await _encoder.ProcessSurfaceAsync(
                    _encodeFrame!,
                    projectTime,
                    _shutdown.Token);
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
}
