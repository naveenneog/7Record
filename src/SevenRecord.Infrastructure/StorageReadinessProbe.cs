using SevenRecord.Capture.Abstractions;

namespace SevenRecord.Infrastructure;

public sealed class StorageReadinessProbe : ICaptureReadinessProbe
{
    private const long MinimumBytes = 5L * 1024 * 1024 * 1024;
    private const long BlockingBytes = 2L * 1024 * 1024 * 1024;

    public ValueTask<IReadOnlyList<CaptureReadinessItem>> CheckAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string videosPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            string root = Path.GetPathRoot(videosPath)
                ?? throw new IOException("The Videos folder does not have a storage root.");
            DriveInfo drive = new(root);
            long availableBytes = drive.AvailableFreeSpace;
            double availableGigabytes = availableBytes / 1024d / 1024d / 1024d;

            CaptureSourceState state = availableBytes switch
            {
                < BlockingBytes => CaptureSourceState.Error,
                < MinimumBytes => CaptureSourceState.Warning,
                _ => CaptureSourceState.Ready,
            };

            string message = $"{availableGigabytes:F1} GB available on {drive.Name}.";
            return ValueTask.FromResult<IReadOnlyList<CaptureReadinessItem>>(
            [
                new("storage", "Storage", state, true, message)
            ]);
        }
        catch (UnauthorizedAccessException)
        {
            return ValueTask.FromResult<IReadOnlyList<CaptureReadinessItem>>(
            [
                new(
                    "storage",
                    "Storage",
                    CaptureSourceState.Error,
                    true,
                    "Windows denied access to the Videos storage location.")
            ]);
        }
        catch (IOException exception)
        {
            return ValueTask.FromResult<IReadOnlyList<CaptureReadinessItem>>(
            [
                new(
                    "storage",
                    "Storage",
                    CaptureSourceState.Error,
                    true,
                    exception.Message)
            ]);
        }
    }
}
