using System.ComponentModel;
using System.Diagnostics;

namespace SevenRecord.Media;

public sealed record FfmpegEncoderProbeResult(
    bool Succeeded,
    string? Error,
    string? ExecutablePath,
    string? ProductVersion,
    FfmpegEncoderCapabilities? Capabilities,
    EncoderSelection? Selection,
    IReadOnlyList<EncoderValidationAttempt> ValidationAttempts);

public static class FfmpegEncoderProbe
{
    public static async Task<FfmpegEncoderProbeResult> ProbeAsync(
        string? executablePath = null,
        EncoderPreference preference = EncoderPreference.Auto,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        executablePath ??= FfmpegLocator.FindExecutable();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Failure("FFmpeg is not installed or is not available on PATH.");
        }

        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);

        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "-hide_banner -encoders",
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };

            if (!process.Start())
            {
                return Failure("FFmpeg could not be started.", executablePath);
            }

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
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
                return Failure("FFmpeg encoder discovery timed out.", executablePath);
            }

            string output = await standardOutput;
            string errors = await standardError;
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(errors)
                    ? $"FFmpeg exited with code {process.ExitCode}."
                    : errors.Trim().Split(Environment.NewLine)[0];
                return Failure(detail, executablePath);
            }

            FfmpegEncoderCapabilities capabilities = FfmpegEncoderCapabilities.Parse(output);
            IReadOnlyList<EncoderSelection> candidates =
                EncoderSelectionPolicy.GetCandidates(capabilities, preference);
            if (candidates.Count == 0)
            {
                return Failure(
                    "FFmpeg does not expose a compatible H.264 encoder.",
                    executablePath,
                    capabilities);
            }

            List<EncoderValidationAttempt> attempts = [];
            EncoderSelection? selected = null;
            foreach (EncoderSelection candidate in candidates)
            {
                EncoderValidationAttempt attempt = await FfmpegEncoderValidator.ValidateAsync(
                    executablePath,
                    candidate,
                    timeout,
                    cancellationToken);
                attempts.Add(attempt);
                if (attempt.Succeeded)
                {
                    selected = candidate with
                    {
                        IsFallback = candidate.IsFallback || attempts.Count > 1,
                    };
                    break;
                }
            }

            if (selected is null)
            {
                return Failure(
                    "All discovered H.264 encoders failed validation.",
                    executablePath,
                    capabilities,
                    attempts);
            }

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(executablePath);
            return new FfmpegEncoderProbeResult(
                true,
                null,
                executablePath,
                version.ProductVersion,
                capabilities,
                selected,
                attempts);
        }
        catch (Win32Exception)
        {
            return Failure("FFmpeg could not be executed.", executablePath);
        }
    }

    private static FfmpegEncoderProbeResult Failure(
        string error,
        string? executablePath = null,
        FfmpegEncoderCapabilities? capabilities = null,
        IReadOnlyList<EncoderValidationAttempt>? validationAttempts = null) =>
        new(false, error, executablePath, null, capabilities, null, validationAttempts ?? []);
}
