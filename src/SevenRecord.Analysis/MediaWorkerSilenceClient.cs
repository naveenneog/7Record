using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace SevenRecord.Analysis;

public sealed record SilenceDetectionWorkerResult(
    bool Succeeded,
    int Intervals,
    string? Error);

public static class MediaWorkerSilenceClient
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<SilenceDetectionWorkerResult> DetectAsync(
        string workerPath,
        string audioMediaPath,
        string outputJsonPath,
        CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = workerPath,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("detect-silence");
        startInfo.ArgumentList.Add(Path.GetFullPath(audioMediaPath));
        startInfo.ArgumentList.Add(Path.GetFullPath(outputJsonPath));
        try
        {
            using Process worker = new() { StartInfo = startInfo };
            if (!worker.Start())
            {
                return new SilenceDetectionWorkerResult(
                    false,
                    0,
                    "The media worker could not be started.");
            }
            Task<string> output =
                worker.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error =
                worker.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await worker.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TerminateWorker(worker);
                throw;
            }
            string json = await output;
            string standardError = await error;
            return JsonSerializer.Deserialize<SilenceDetectionWorkerResult>(
                    json,
                    SerializerOptions) ??
                new SilenceDetectionWorkerResult(
                    false,
                    0,
                    string.IsNullOrWhiteSpace(standardError)
                        ? "The media worker returned an invalid silence result."
                        : standardError.Trim());
        }
        catch (Win32Exception exception)
        {
            return new SilenceDetectionWorkerResult(
                false,
                0,
                exception.Message);
        }
    }

    private static void TerminateWorker(Process worker)
    {
        try
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                worker.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
