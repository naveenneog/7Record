using System.ComponentModel;
using System.Diagnostics;

namespace SevenRecord.Media;

public sealed record EncoderValidationAttempt(
    EncoderSelection Selection,
    bool Succeeded,
    double ElapsedMilliseconds,
    string? Error);

public static class FfmpegEncoderValidator
{
    public static async Task<EncoderValidationAttempt> ValidateAsync(
        string executablePath,
        EncoderSelection selection,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(selection);

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments =
                        "-hide_banner -loglevel error -f lavfi " +
                        "-i color=c=black:s=1280x720:r=60:d=0.25 -an " +
                        $"-c:v {selection.FfmpegName} -frames:v 15 -f null -",
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };

            if (!process.Start())
            {
                return Failed(selection, stopwatch, "FFmpeg could not be started.");
            }

            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(effectiveTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return Failed(selection, stopwatch, "Encoder validation timed out.");
            }

            _ = await standardOutput;
            string error = await standardError;
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error)
                    ? $"Encoder exited with code {process.ExitCode}."
                    : error.Trim().Split(Environment.NewLine)[0];
                return Failed(selection, stopwatch, detail);
            }

            stopwatch.Stop();
            return new EncoderValidationAttempt(
                selection,
                true,
                stopwatch.Elapsed.TotalMilliseconds,
                null);
        }
        catch (Win32Exception)
        {
            return Failed(selection, stopwatch, "FFmpeg could not be executed.");
        }
    }

    private static EncoderValidationAttempt Failed(
        EncoderSelection selection,
        Stopwatch stopwatch,
        string error)
    {
        stopwatch.Stop();
        return new EncoderValidationAttempt(
            selection,
            false,
            stopwatch.Elapsed.TotalMilliseconds,
            error);
    }
}
