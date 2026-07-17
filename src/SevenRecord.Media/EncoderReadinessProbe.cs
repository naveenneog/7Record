using System.Diagnostics;
using SevenRecord.Capture.Abstractions;

namespace SevenRecord.Media;

public sealed class EncoderReadinessProbe : ICaptureReadinessProbe
{
    public ValueTask<IReadOnlyList<CaptureReadinessItem>> CheckAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? executablePath = FindExecutableOnPath("ffmpeg.exe");
        if (executablePath is null)
        {
            return ValueTask.FromResult<IReadOnlyList<CaptureReadinessItem>>(
            [
                CreateError("FFmpeg is not installed or is not available on PATH.")
            ]);
        }

        FileVersionInfo version = FileVersionInfo.GetVersionInfo(executablePath);
        string versionLabel = string.IsNullOrWhiteSpace(version.ProductVersion)
            ? "version unavailable"
            : version.ProductVersion;

        return ValueTask.FromResult<IReadOnlyList<CaptureReadinessItem>>(
        [
            new(
                "encoder",
                "Encoder",
                CaptureSourceState.Warning,
                true,
                $"FFmpeg {versionLabel} found. Hardware encoder enumeration will run in the isolated media worker.")
        ]);
    }

    private static string? FindExecutableOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim(), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static CaptureReadinessItem CreateError(string message) =>
        new("encoder", "Encoder", CaptureSourceState.Error, true, message);
}
