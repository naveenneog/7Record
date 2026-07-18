using System.Runtime.InteropServices;
using Microsoft.UI;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace SevenRecord.Capture.Windows;

public sealed record WindowsCaptureTarget(GraphicsCaptureItem Item)
{
    public string DisplayName => Item.DisplayName;

    public int Width => Item.Size.Width;

    public int Height => Item.Size.Height;
}

public sealed class WindowsCaptureSourcePicker
{
    private const int AppModelErrorNoPackage = 15700;
    private const uint MonitorDefaultToPrimary = 1;

    public static async Task<WindowsCaptureTarget> GetPrimaryDisplayAsync()
    {
        nint monitor = MonitorFromPoint(default, MonitorDefaultToPrimary);
        if (monitor == 0)
        {
            throw new InvalidOperationException("Windows could not resolve the primary display.");
        }

        if (!HasPackageIdentity())
        {
            throw new InvalidOperationException(
                "Automatic primary-display capture requires the installed 7Record package.");
        }

        AppCapabilityAccessStatus access =
            await GraphicsCaptureAccess.RequestAccessAsync(
                GraphicsCaptureAccessKind.Programmatic);
        if (access is not AppCapabilityAccessStatus.Allowed)
        {
            throw new InvalidOperationException(
                $"Windows denied automatic display capture access: {access}.");
        }

        Microsoft.UI.DisplayId uiDisplayId =
            Win32Interop.GetDisplayIdFromMonitor(monitor);
        GraphicsCaptureItem item =
            GraphicsCaptureItem.TryCreateFromDisplayId(
                new global::Windows.Graphics.DisplayId(uiDisplayId.Value))
            ?? throw new InvalidOperationException(
                "Windows could not create a capture source for the primary display.");
        return new WindowsCaptureTarget(item);
    }

    public static async Task<WindowsCaptureTarget?> PickAsync(nint ownerWindow)
    {
        GraphicsCapturePicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);

        GraphicsCaptureItem? item = await picker.PickSingleItemAsync();
        return item is null ? null : new WindowsCaptureTarget(item);
    }

    private static bool HasPackageIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, 0) != AppModelErrorNoPackage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Point(int X, int Y);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(
        Point point,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        nint packageFullName);
}
