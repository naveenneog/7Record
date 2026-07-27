namespace SevenRecord.Media;

public static class MediaWorkerLocator
{
    private const string WorkerExecutable = "SevenRecord.Media.Worker.exe";

    public static string? FindExecutable(string? applicationBaseDirectory = null)
    {
        string baseDirectory = Path.GetFullPath(
            applicationBaseDirectory ?? AppContext.BaseDirectory);
        string[] candidates =
        [
            Path.Combine(baseDirectory, "MediaWorker", WorkerExecutable),
            Path.Combine(baseDirectory, "AppX", "MediaWorker", WorkerExecutable),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
