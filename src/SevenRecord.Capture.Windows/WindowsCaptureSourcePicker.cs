using Windows.Graphics.Capture;

namespace SevenRecord.Capture.Windows;

public sealed record WindowsCaptureTarget(GraphicsCaptureItem Item)
{
    public string DisplayName => Item.DisplayName;

    public int Width => Item.Size.Width;

    public int Height => Item.Size.Height;
}

public sealed class WindowsCaptureSourcePicker
{
    public static async Task<WindowsCaptureTarget?> PickAsync(nint ownerWindow)
    {
        GraphicsCapturePicker picker = new();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);

        GraphicsCaptureItem? item = await picker.PickSingleItemAsync();
        return item is null ? null : new WindowsCaptureTarget(item);
    }
}
