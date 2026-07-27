using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SevenRecord.Media;

public sealed record SegmentConcatenationResult(
    bool Succeeded,
    string OutputPath,
    string? Error);

public static class FfmpegSegmentConcatenator
{
    public static async Task<SegmentConcatenationResult> ConcatenateAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentOutOfRangeException.ThrowIfLessThan(inputPaths.Count, 2);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string? ffmpegPath = FfmpegLocator.FindExecutable();
        if (ffmpegPath is null)
        {
            return new SegmentConcatenationResult(
                false,
                outputPath,
                "FFmpeg is unavailable.");
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        string manifestPath = Path.Combine(
            Path.GetDirectoryName(fullOutputPath)!,
            $".{Path.GetFileName(fullOutputPath)}-{Guid.NewGuid():N}.concat.txt");
        try
        {
            await File.WriteAllTextAsync(
                manifestPath,
                BuildManifest(inputPaths),
                new UTF8Encoding(false),
                cancellationToken);
            ProcessStartInfo startInfo = new()
            {
                FileName = ffmpegPath,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (string argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y",
                         "-f", "concat", "-safe", "0",
                         "-i", manifestPath,
                         "-c", "copy",
                         fullOutputPath
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                return new SegmentConcatenationResult(
                    false,
                    outputPath,
                    "FFmpeg concatenation could not be started.");
            }
            Task<string> error =
                process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> output =
                process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await output;
            string standardError = await error;
            return process.ExitCode == 0 &&
                File.Exists(fullOutputPath) &&
                new FileInfo(fullOutputPath).Length > 0
                ? new SegmentConcatenationResult(
                    true,
                    fullOutputPath,
                    null)
                : new SegmentConcatenationResult(
                    false,
                    outputPath,
                    string.IsNullOrWhiteSpace(standardError)
                        ? $"FFmpeg concatenation exited with code {process.ExitCode}."
                        : standardError.Trim());
        }
        catch (Win32Exception exception)
        {
            return new SegmentConcatenationResult(
                false,
                outputPath,
                exception.Message);
        }
        finally
        {
            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
        }
    }

    public static string BuildManifest(IReadOnlyList<string> inputPaths)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        StringBuilder builder = new();
        foreach (string inputPath in inputPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
            string escaped = Path.GetFullPath(inputPath)
                .Replace('\\', '/')
                .Replace("'", "'\\''", StringComparison.Ordinal);
            builder.Append("file '");
            builder.Append(escaped);
            builder.AppendLine("'");
        }
        return builder.ToString();
    }
}
