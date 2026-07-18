using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SevenRecord.Input.Windows;

public enum GlobalHotKeyAction
{
    StartStopRecording,
    PauseResumeRecording,
}

public sealed class GlobalHotKeyService : IDisposable
{
    private const int StartStopId = 0x7A01;
    private const int PauseResumeId = 0x7A02;
    private const uint Control = 0x0002;
    private const uint Shift = 0x0004;
    private const uint NoRepeat = 0x4000;
    private const uint RecordingKey = 0x52;
    private const uint PauseKey = 0x50;
    private const uint HotKeyMessage = 0x0312;
    private readonly nint _window;
    private readonly SubclassProcedure _windowProcedure;
    private bool _disposed;
    private bool _pauseRegistered;
    private bool _recordingRegistered;

    public GlobalHotKeyService(nint window)
    {
        ArgumentOutOfRangeException.ThrowIfZero(window);

        _window = window;
        _windowProcedure = WindowProcedure;
        if (!SetWindowSubclass(
                _window,
                _windowProcedure,
                (nuint)GetHashCode(),
                0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "7Record could not attach its hotkey message handler.");
        }

        try
        {
            _recordingRegistered = RegisterHotKey(
                _window,
                StartStopId,
                Control | Shift | NoRepeat,
                RecordingKey);
            if (!_recordingRegistered)
            {
                throw Conflict("Ctrl+Shift+R");
            }

            _pauseRegistered = RegisterHotKey(
                _window,
                PauseResumeId,
                Control | Shift | NoRepeat,
                PauseKey);
            if (!_pauseRegistered)
            {
                throw Conflict("Ctrl+Shift+P");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public event Action<GlobalHotKeyAction>? Triggered;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_recordingRegistered)
        {
            UnregisterHotKey(_window, StartStopId);
            _recordingRegistered = false;
        }

        if (_pauseRegistered)
        {
            UnregisterHotKey(_window, PauseResumeId);
            _pauseRegistered = false;
        }

        RemoveWindowSubclass(
            _window,
            _windowProcedure,
            (nuint)GetHashCode());
    }

    private nint WindowProcedure(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == HotKeyMessage)
        {
            GlobalHotKeyAction? action = (int)wordParameter switch
            {
                StartStopId => GlobalHotKeyAction.StartStopRecording,
                PauseResumeId => GlobalHotKeyAction.PauseResumeRecording,
                _ => null,
            };
            if (action is not null)
            {
                try
                {
                    Triggered?.Invoke(action.Value);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                }

                return 0;
            }
        }

        return DefSubclassProc(window, message, wordParameter, longParameter);
    }

    private static Win32Exception Conflict(string shortcut) =>
        new(
            Marshal.GetLastWin32Error(),
            $"The global shortcut {shortcut} is already in use.");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter,
        nuint subclassId,
        nuint referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(
        nint window,
        int id,
        uint modifiers,
        uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint window,
        SubclassProcedure procedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint window,
        SubclassProcedure procedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint window,
        uint message,
        nuint wordParameter,
        nint longParameter);
}
