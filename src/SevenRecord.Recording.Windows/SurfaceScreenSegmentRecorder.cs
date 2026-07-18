using Microsoft.Graphics.Canvas;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Capture.Windows;
using SevenRecord.Media;
using SevenRecord.Media.Windows;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace SevenRecord.Recording.Windows;

public sealed class SurfaceScreenSegmentRecorder : IAsyncDisposable
{
    private const int FramesPerSecond = 60;
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
    private CanvasRenderTarget? _encodeFrame;
    private bool _hasFrame;
    private CanvasRenderTarget? _latestFrame;
    private bool _pacingStarted;
    private Task _pacingTask = Task.CompletedTask;

    public event Action<Exception>? Failed;

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
                await _surfaceGate.WaitAsync(_shutdown.Token);
                try
                {
                    hasFrame = _hasFrame;
                    if (hasFrame)
                    {
                        using CanvasDrawingSession drawing =
                            _encodeFrame!.CreateDrawingSession();
                        drawing.DrawImage(_latestFrame);
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

                TimeSpan projectTime = _pauseController.Map(
                    _projectClock.Normalize(QpcTimestamp.Now()));
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
