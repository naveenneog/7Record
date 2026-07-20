using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SevenRecord.Domain.Video;
using SevenRecord.Media;

namespace SevenRecord.Analysis;

public static partial class FfmpegLoadingDetector
{
    public static async Task<IReadOnlyList<LoadingSpeedEvent>> DetectAsync(
        string screenMediaPath,
        TimeSpan? minimumDuration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenMediaPath);
        string? ffmpegPath = FfmpegLocator.FindExecutable();
        if (ffmpegPath is null)
        {
            throw new InvalidOperationException(
                "FFmpeg is required for loading-interval detection.");
        }

        TimeSpan minimum = minimumDuration ?? TimeSpan.FromSeconds(2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minimum, TimeSpan.Zero);
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
                     "-hide_banner", "-i", Path.GetFullPath(screenMediaPath),
                     "-vf", $"freezedetect=n=-50dB:d={minimum.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)}",
                     "-an", "-f", "null", "-"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "FFmpeg loading detection could not be started.");
            }

            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await output;
            string log = await error;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg loading detection exited with code {process.ExitCode}.");
            }

            TimeSpan mediaDuration = await ProbeDurationAsync(
                screenMediaPath,
                cancellationToken);
            return Parse(log, minimum, mediaDuration);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "FFmpeg loading detection could not be executed.",
                exception);
        }
    }

    public static IReadOnlyList<LoadingSpeedEvent> Parse(
        string log,
        TimeSpan minimumDuration,
        TimeSpan? mediaDuration = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        List<LoadingSpeedEvent> events = [];
        double? start = null;
        foreach (string line in log.Split(Environment.NewLine))
        {
            Match startMatch = FreezeStartRegex().Match(line);
            if (startMatch.Success)
            {
                start = ParseSeconds(startMatch.Groups["value"].Value);
                continue;
            }

            Match endMatch = FreezeEndRegex().Match(line);
            if (!endMatch.Success || start is null)
            {
                continue;
            }

            double end = ParseSeconds(endMatch.Groups["value"].Value);
            TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, end - start.Value));
            if (duration >= minimumDuration)
            {
                events.Add(
                    new LoadingSpeedEvent(
                        CreateEventId(events.Count, TimeSpan.FromSeconds(start.Value)),
                        TimeSpan.FromSeconds(start.Value),
                        duration,
                        Speed: 4,
                        Confidence: 0.65));
            }

            start = null;
        }

        if (start is not null && mediaDuration is TimeSpan endOfMedia)
        {
            TimeSpan duration = endOfMedia - TimeSpan.FromSeconds(start.Value);
            if (duration >= minimumDuration)
            {
                events.Add(
                    new LoadingSpeedEvent(
                        CreateEventId(events.Count, TimeSpan.FromSeconds(start.Value)),
                        TimeSpan.FromSeconds(start.Value),
                        duration,
                        Speed: 4,
                        Confidence: 0.6));
            }
        }

        return events;
    }

    private static async Task<TimeSpan> ProbeDurationAsync(
        string mediaPath,
        CancellationToken cancellationToken)
    {
        string? ffprobePath = FfmpegLocator.FindExecutable("ffprobe.exe");
        if (ffprobePath is null)
        {
            throw new InvalidOperationException(
                "FFprobe is required for loading-interval detection.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = ffprobePath,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
                 {
                     "-v", "error",
                     "-show_entries", "format=duration",
                     "-of", "default=noprint_wrappers=1:nokey=1",
                     Path.GetFullPath(mediaPath)
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("FFprobe could not be started.");
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string value = (await output).Trim();
        string standardError = await error;
        if (process.ExitCode != 0 ||
            !double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double seconds))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(standardError)
                    ? "FFprobe did not return a valid duration."
                    : standardError.Trim());
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static double ParseSeconds(string value) =>
        double.Parse(value, CultureInfo.InvariantCulture);

    private static string CreateEventId(int index, TimeSpan start) =>
        $"loading-{index:D4}-{start.Ticks:x16}";

    [GeneratedRegex(@"freeze_start:\s*(?<value>-?\d+(?:\.\d+)?)")]
    private static partial Regex FreezeStartRegex();

    [GeneratedRegex(@"freeze_end:\s*(?<value>-?\d+(?:\.\d+)?)")]
    private static partial Regex FreezeEndRegex();
}
