using System.Runtime.InteropServices;
using System.Text.Json;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Domain.Input;

namespace SevenRecord.Input.Windows;

public sealed class CursorMetadataRecorder : IAsyncDisposable
{
    private const int LeftButton = 0x01;
    private const int RightButton = 0x02;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<CursorMetadataEvent> _events = [];
    private readonly object _gate = new();
    private readonly RecordingPauseController _pauseController;
    private readonly ProjectClock _projectClock;
    private readonly Task _samplingTask;
    private bool _leftWasDown;
    private POINT _lastPoint;
    private TimeSpan _lastMoveTime;
    private bool _rightWasDown;

    private CursorMetadataRecorder(
        ProjectClock projectClock,
        RecordingPauseController pauseController)
    {
        _projectClock = projectClock;
        _pauseController = pauseController;
        _samplingTask = Task.Run(SampleAsync);
    }

    public static CursorMetadataRecorder Start(
        ProjectClock projectClock,
        RecordingPauseController pauseController)
    {
        ArgumentNullException.ThrowIfNull(projectClock);
        ArgumentNullException.ThrowIfNull(pauseController);
        if (!GetCursorPos(out POINT initialPoint))
        {
            throw new InvalidOperationException(
                "Windows cursor position is unavailable in the current desktop session.");
        }

        CursorMetadataRecorder recorder = new(projectClock, pauseController)
        {
            _lastPoint = initialPoint,
        };
        return recorder;
    }

    public async Task<CursorMetadataDocument> CompleteAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _shutdown.Cancel();
        await _samplingTask.WaitAsync(cancellationToken);

        CursorMetadataDocument document;
        lock (_gate)
        {
            document = new CursorMetadataDocument(1, _events.ToArray());
        }

        string path = Path.Combine(projectRoot, "cursor-events.json");
        string temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(document, SerializerOptions),
            cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
        return document;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _samplingTask;
        _shutdown.Dispose();
    }

    private async Task SampleAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(16));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                Sample();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void Sample()
    {
        if (_pauseController.IsPaused)
        {
            return;
        }

        if (!GetCursorPos(out POINT point))
        {
            return;
        }

        IntPtr foregroundWindow = GetForegroundWindow();
        RECT bounds = default;
        bool hasBounds =
            foregroundWindow != IntPtr.Zero &&
            GetWindowRect(foregroundWindow, out bounds) &&
            bounds.Right > bounds.Left &&
            bounds.Bottom > bounds.Top;
        double normalizedX = hasBounds
            ? Math.Clamp(
                (point.X - bounds.Left) / (double)(bounds.Right - bounds.Left),
                0,
                1)
            : 0;
        double normalizedY = hasBounds
            ? Math.Clamp(
                (point.Y - bounds.Top) / (double)(bounds.Bottom - bounds.Top),
                0,
                1)
            : 0;
        TimeSpan projectTime = _pauseController.Map(
            _projectClock.Normalize(QpcTimestamp.Now()));

        bool leftDown = (GetAsyncKeyState(LeftButton) & 0x8000) != 0;
        bool rightDown = (GetAsyncKeyState(RightButton) & 0x8000) != 0;
        if (leftDown && !_leftWasDown)
        {
            AddEvent(
                projectTime,
                point,
                normalizedX,
                normalizedY,
                CursorEventKind.Click,
                CursorButton.Left);
        }

        if (rightDown && !_rightWasDown)
        {
            AddEvent(
                projectTime,
                point,
                normalizedX,
                normalizedY,
                CursorEventKind.Click,
                CursorButton.Right);
        }

        if (point.X != _lastPoint.X ||
            point.Y != _lastPoint.Y ||
            projectTime - _lastMoveTime >= TimeSpan.FromMilliseconds(100))
        {
            AddEvent(
                projectTime,
                point,
                normalizedX,
                normalizedY,
                CursorEventKind.Move,
                CursorButton.None);
            _lastPoint = point;
            _lastMoveTime = projectTime;
        }

        _leftWasDown = leftDown;
        _rightWasDown = rightDown;
    }

    private void AddEvent(
        TimeSpan projectTime,
        POINT point,
        double normalizedX,
        double normalizedY,
        CursorEventKind kind,
        CursorButton button)
    {
        lock (_gate)
        {
            _events.Add(
                new CursorMetadataEvent(
                    projectTime,
                    point.X,
                    point.Y,
                    normalizedX,
                    normalizedY,
                    kind,
                    button));
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out RECT rectangle);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
