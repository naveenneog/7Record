using Windows.Graphics.Imaging;

namespace SevenRecord.Media.Windows;

public sealed class SoftwareBitmapPreviewFrame : IDisposable
{
    private SoftwareBitmap? _bitmap;

    public SoftwareBitmapPreviewFrame(
        SoftwareBitmap bitmap,
        TimeSpan projectTime)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        _bitmap = bitmap;
        ProjectTime = projectTime;
    }

    public SoftwareBitmap Bitmap =>
        _bitmap ??
        throw new ObjectDisposedException(nameof(SoftwareBitmapPreviewFrame));

    public TimeSpan ProjectTime { get; }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
