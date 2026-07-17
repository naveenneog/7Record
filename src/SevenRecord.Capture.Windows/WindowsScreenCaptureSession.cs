using System.Collections.Concurrent;
using Microsoft.Graphics.Canvas;
using SevenRecord.Capture.Abstractions;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace SevenRecord.Capture.Windows;

public sealed class ScreenCaptureFrameLease : IDisposable
{
    private readonly CanvasDevice _device;
    private Direct3D11CaptureFrame? _frame;

    internal ScreenCaptureFrameLease(
        Direct3D11CaptureFrame frame,
        TimeSpan projectTime,
        CanvasDevice device)
    {
        _frame = frame;
        ProjectTime = projectTime;
        _device = device;
    }

    public TimeSpan ProjectTime { get; }

    public SizeInt32 ContentSize => Frame.ContentSize;

    public IDirect3DSurface Surface => Frame.Surface;

    public byte[] CopyBgra8()
    {
        using CanvasBitmap bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device, Surface);
        return bitmap.GetPixelBytes();
    }

    private Direct3D11CaptureFrame Frame =>
        _frame ?? throw new ObjectDisposedException(nameof(ScreenCaptureFrameLease));

    public void Dispose()
    {
        _frame?.Dispose();
        _frame = null;
    }
}

public sealed class WindowsScreenCaptureSession : IAsyncDisposable
{
    private const int FramePoolSize = 3;
    private const int MaximumQueuedFrames = 3;

    private readonly SemaphoreSlim _availableFrames = new(0);
    private readonly CanvasDevice _device;
    private readonly CaptureFrameHealthCounter _health = new();
    private readonly GraphicsCaptureItem _item;
    private readonly Func<ScreenCaptureFrameLease, CancellationToken, ValueTask> _processor;
    private readonly ProjectClock _projectClock;
    private readonly ConcurrentQueue<ScreenCaptureFrameLease> _queue = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Direct3D11CaptureFramePool _framePool;
    private GraphicsCaptureSession _session;
    private Task _consumerTask;
    private SizeInt32 _frameSize;
    private int _failureReported;
    private int _queuedFrames;
    private bool _disposed;

    private WindowsScreenCaptureSession(
        GraphicsCaptureItem item,
        ProjectClock projectClock,
        Func<ScreenCaptureFrameLease, CancellationToken, ValueTask> processor)
    {
        _item = item;
        _projectClock = projectClock;
        _processor = processor;
        _frameSize = item.Size;
        _device = new CanvasDevice();
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolSize,
            _frameSize);
        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        _framePool.FrameArrived += OnFrameArrived;
        _item.Closed += OnCaptureItemClosed;
        _consumerTask = Task.Run(ProcessFramesAsync);
    }

    public event Action? CaptureClosed;

    public event Action<Exception>? CaptureFailed;

    public event Action<CaptureFrameHealthSnapshot>? HealthChanged;

    public CaptureFrameHealthSnapshot Health => _health.Snapshot();

    public static WindowsScreenCaptureSession Start(
        GraphicsCaptureItem item,
        ProjectClock projectClock,
        Func<ScreenCaptureFrameLease, CancellationToken, ValueTask> processor)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(projectClock);
        ArgumentNullException.ThrowIfNull(processor);

        WindowsScreenCaptureSession capture = new(item, projectClock, processor);
        capture._session.StartCapture();
        return capture;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _framePool.FrameArrived -= OnFrameArrived;
        _item.Closed -= OnCaptureItemClosed;
        _session.Dispose();
        _shutdown.Cancel();
        _availableFrames.Release();

        await _consumerTask;

        while (_queue.TryDequeue(out ScreenCaptureFrameLease? frame))
        {
            frame.Dispose();
        }

        _framePool.Dispose();
        _device.Dispose();
        _availableFrames.Dispose();
        _shutdown.Dispose();
    }

    private void OnFrameArrived(
        Direct3D11CaptureFramePool sender,
        object args)
    {
        try
        {
            ProcessFrameArrived(sender);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _failureReported, 1) == 0)
            {
                CaptureFailed?.Invoke(exception);
            }
        }
    }

    private void ProcessFrameArrived(Direct3D11CaptureFramePool sender)
    {
        Direct3D11CaptureFrame frame = sender.TryGetNextFrame();
        TimeSpan systemRelativeTime = frame.SystemRelativeTime;
        TimeSpan projectTime = _projectClock.NormalizeSystemRelativeTime(systemRelativeTime);
        CaptureFrameHealthSnapshot snapshot = _health.ReportReceived(projectTime);
        SizeInt32 contentSize = frame.ContentSize;
        if (contentSize.Width != _frameSize.Width || contentSize.Height != _frameSize.Height)
        {
            frame.Dispose();
            snapshot = _health.ReportDropped();

            if (Volatile.Read(ref _queuedFrames) == 0)
            {
                _frameSize = contentSize;
                sender.Recreate(
                    _device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    FramePoolSize,
                    _frameSize);
            }

            HealthChanged?.Invoke(snapshot);
            return;
        }

        ScreenCaptureFrameLease lease = new(frame, projectTime, _device);

        int queuedFrames = Interlocked.Increment(ref _queuedFrames);
        if (queuedFrames > MaximumQueuedFrames)
        {
            Interlocked.Decrement(ref _queuedFrames);
            lease.Dispose();
            snapshot = _health.ReportDropped();
            HealthChanged?.Invoke(snapshot);
            return;
        }

        _queue.Enqueue(lease);
        _availableFrames.Release();

        if (snapshot.FramesReceived == 1 || snapshot.FramesReceived % 30 == 0)
        {
            HealthChanged?.Invoke(snapshot);
        }
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args) =>
        CaptureClosed?.Invoke();

    private async Task ProcessFramesAsync()
    {
        try
        {
            while (true)
            {
                await _availableFrames.WaitAsync(_shutdown.Token);
                while (_queue.TryDequeue(out ScreenCaptureFrameLease? frame))
                {
                    Interlocked.Decrement(ref _queuedFrames);
                    using (frame)
                    {
                        await _processor(frame, _shutdown.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }
}
