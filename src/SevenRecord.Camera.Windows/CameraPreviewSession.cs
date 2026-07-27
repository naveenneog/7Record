using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using SevenRecord.Domain.Video;
using SevenRecord.Media.Windows;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace SevenRecord.Camera.Windows;

public sealed class CameraPreviewSession : IAsyncDisposable
{
    private const int PreviewFramesPerSecond = 12;
    private const int PreviewMaximumHeight = 360;
    private const int PreviewMaximumWidth = 640;
    private readonly MediaCapture _capture;
    private readonly CanvasDevice _device;
    private readonly MediaFrameReader _reader;
    private readonly CanvasRenderTarget _previewTarget;
    private readonly CanvasRenderTarget _renderTarget;
    private readonly SemaphoreSlim _processingGate = new(1, 1);
    private readonly TaskCompletionSource _firstFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _layoutGate = new();
    private PresenterLayoutSettings _layout;
    private long _lastPreviewTimestamp;
    private bool _disposed;

    private CameraPreviewSession(
        string deviceName,
        int width,
        int height,
        MediaCapture capture,
        MediaFrameReader reader,
        CanvasDevice device,
        CanvasRenderTarget renderTarget,
        PresenterLayoutSettings layout)
    {
        DeviceName = deviceName;
        Width = width;
        Height = height;
        _capture = capture;
        _reader = reader;
        _device = device;
        _renderTarget = renderTarget;
        (int previewWidth, int previewHeight) = ScaleToFit(
            width,
            height,
            PreviewMaximumWidth,
            PreviewMaximumHeight);
        _previewTarget = new CanvasRenderTarget(
            device,
            previewWidth,
            previewHeight,
            96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            CanvasAlphaMode.Ignore);
        _layout = layout.ConstrainToFrame();
        _reader.FrameArrived += OnFrameArrived;
    }

    public string DeviceName { get; }

    public event Action<Exception>? Failed;

    public event Action<SoftwareBitmapPreviewFrame>? FrameReady;

    public int Height { get; }

    public int Width { get; }

    public static async Task<CameraPreviewSession> CreateAsync(
        PresenterLayoutSettings layout,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MediaFrameSourceGroup> groups =
            await MediaFrameSourceGroup.FindAllAsync();
        List<Exception> failures = [];
        foreach (MediaFrameSourceGroup group in groups.Where(candidate =>
                     candidate.SourceInfos.Any(info =>
                         info.SourceKind is MediaFrameSourceKind.Color)))
        {
            try
            {
                return await CreateFromGroupAsync(
                    group,
                    layout,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        throw new AggregateException(
            "No camera delivered a preview frame.",
            failures);
    }

    public void UpdateLayout(PresenterLayoutSettings layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        lock (_layoutGate)
        {
            _layout = layout.ConstrainToFrame();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _reader.FrameArrived -= OnFrameArrived;
        Exception? failure = null;
        try
        {
            await _reader.StopAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            await _processingGate.WaitAsync();
            _processingGate.Release();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }
        foreach (Action dispose in new Action[]
                 {
                     _reader.Dispose,
                     _capture.Dispose,
                     _previewTarget.Dispose,
                     _renderTarget.Dispose,
                     _device.Dispose,
                     _processingGate.Dispose,
                 })
        {
            try
            {
                dispose();
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }
        }
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static async Task<CameraPreviewSession> CreateFromGroupAsync(
        MediaFrameSourceGroup group,
        PresenterLayoutSettings layout,
        CancellationToken cancellationToken)
    {
        MediaCapture capture = new();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await capture.InitializeAsync(
                new MediaCaptureInitializationSettings
                {
                    MemoryPreference = MediaCaptureMemoryPreference.Auto,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    SourceGroup = group,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                });
            cancellationToken.ThrowIfCancellationRequested();
            MediaFrameSource source = capture.FrameSources.Values
                .First(candidate =>
                    candidate.Info.SourceKind is MediaFrameSourceKind.Color);
            MediaFrameFormat format = source.SupportedFormats
                .Where(candidate => candidate.VideoFormat is not null)
                .OrderByDescending(candidate =>
                    (long)candidate.VideoFormat.Width *
                    candidate.VideoFormat.Height)
                .First();
            await source.SetFormatAsync(format);
            cancellationToken.ThrowIfCancellationRequested();
            int width = (int)source.CurrentFormat.VideoFormat.Width;
            int height = (int)source.CurrentFormat.VideoFormat.Height;
            MediaFrameReader reader = await capture.CreateFrameReaderAsync(
                source,
                MediaEncodingSubtypes.Bgra8);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            CanvasDevice device = new();
            CanvasRenderTarget renderTarget = new(
                device,
                width,
                height,
                96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                CanvasAlphaMode.Ignore);
            CameraPreviewSession session = new(
                group.DisplayName,
                width,
                height,
                capture,
                reader,
                device,
                renderTarget,
                layout);
            cancellationToken.ThrowIfCancellationRequested();
            MediaFrameReaderStartStatus status = await reader.StartAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (status is not MediaFrameReaderStartStatus.Success)
            {
                await session.DisposeAsync();
                throw new InvalidOperationException(
                    $"Camera reader failed to start: {status}.");
            }
            try
            {
                await session._firstFrame.Task.WaitAsync(
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private async void OnFrameArrived(
        MediaFrameReader sender,
        MediaFrameArrivedEventArgs args)
    {
        if (!_processingGate.Wait(0))
        {
            return;
        }
        try
        {
            using MediaFrameReference? frame = sender.TryAcquireLatestFrame();
            if (frame?.VideoMediaFrame?.Direct3DSurface is not { } surface)
            {
                return;
            }
            using VideoFrame source =
                VideoFrame.CreateWithDirect3D11Surface(surface);
            using VideoFrame destination =
                VideoFrame.CreateWithDirect3D11Surface(_renderTarget);
            var description = surface.Description;
            await source.CopyToAsync(
                destination,
                new BitmapBounds
                {
                    Width = (uint)description.Width,
                    Height = (uint)description.Height,
                },
                new BitmapBounds
                {
                    Width = (uint)_renderTarget.SizeInPixels.Width,
                    Height = (uint)_renderTarget.SizeInPixels.Height,
                });
            _firstFrame.TrySetResult();
            if (!ShouldPublishPreview())
            {
                return;
            }

            PresenterLayoutSettings layout;
            lock (_layoutGate)
            {
                layout = _layout;
            }
            using ExposureEffect exposure = new()
            {
                Source = _renderTarget,
                Exposure = (float)layout.Effects.Exposure,
            };
            using (CanvasDrawingSession drawing =
                   _previewTarget.CreateDrawingSession())
            {
                drawing.DrawImage(
                    exposure,
                    new Rect(
                        0,
                        0,
                        _previewTarget.SizeInPixels.Width,
                        _previewTarget.SizeInPixels.Height),
                    new Rect(0, 0, Width, Height));
            }
            using SoftwareBitmap surfaceCopy =
                await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                    _previewTarget);
            SoftwareBitmap bitmap = SoftwareBitmap.Convert(
                surfaceCopy,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            Action<SoftwareBitmapPreviewFrame>? handler = FrameReady;
            if (handler is null)
            {
                bitmap.Dispose();
                return;
            }
            handler(new SoftwareBitmapPreviewFrame(bitmap, TimeSpan.Zero));
        }
        catch (Exception exception)
        {
            _firstFrame.TrySetException(exception);
            Failed?.Invoke(exception);
        }
        finally
        {
            _processingGate.Release();
        }
    }

    private bool ShouldPublishPreview()
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Volatile.Read(ref _lastPreviewTimestamp);
        if (previous != 0 &&
            Stopwatch.GetElapsedTime(previous, now) <
            TimeSpan.FromSeconds(1d / PreviewFramesPerSecond))
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
                maximumWidth / (double)sourceWidth,
                maximumHeight / (double)sourceHeight));
        return (
            Math.Max(1, (int)Math.Round(sourceWidth * scale)),
            Math.Max(1, (int)Math.Round(sourceHeight * scale)));
    }
}
