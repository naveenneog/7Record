using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SevenRecord.Media;

namespace SevenRecord.Analysis;

public sealed record AudioSilenceInterval(
    TimeSpan Start,
    TimeSpan Duration)
{
    public TimeSpan End => Start + Duration;
}

public static partial class FfmpegSilenceDetector
{
    public static async Task<IReadOnlyList<AudioSilenceInterval>> DetectAsync(
        string audioMediaPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioMediaPath);
        string? ffmpegPath = FfmpegLocator.FindExecutable();
        if (ffmpegPath is null)
        {
            throw new InvalidOperationException(
                "FFmpeg is required for silence detection.");
        }

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
                     "-hide_banner", "-i", Path.GetFullPath(audioMediaPath),
                     "-af", "silencedetect=n=-45dB:d=0.5",
                     "-f", "null", "-"
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
                    "FFmpeg silence detection could not be started.");
            }
            Task<string> error =
                process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> output =
                process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await output;
            string log = await error;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg silence detection exited with code {process.ExitCode}.");
            }
            return Parse(log);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "FFmpeg silence detection could not be executed.",
                exception);
        }
    }

    public static IReadOnlyList<AudioSilenceInterval> Parse(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        List<AudioSilenceInterval> intervals = [];
        double? start = null;
        foreach (string line in log.Split(Environment.NewLine))
        {
            Match startMatch = SilenceStartRegex().Match(line);
            if (startMatch.Success)
            {
                start = ParseSeconds(startMatch.Groups["value"].Value);
                continue;
            }
            Match endMatch = SilenceEndRegex().Match(line);
            if (!endMatch.Success || start is null)
            {
                continue;
            }
            double end = ParseSeconds(endMatch.Groups["value"].Value);
            intervals.Add(
                new AudioSilenceInterval(
                    TimeSpan.FromSeconds(start.Value),
                    TimeSpan.FromSeconds(Math.Max(0, end - start.Value))));
            start = null;
        }
        return intervals;
    }

    private static double ParseSeconds(string value) =>
        double.Parse(value, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"silence_start:\s*(?<value>-?\d+(?:\.\d+)?)")]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end:\s*(?<value>-?\d+(?:\.\d+)?)")]
    private static partial Regex SilenceEndRegex();
}
