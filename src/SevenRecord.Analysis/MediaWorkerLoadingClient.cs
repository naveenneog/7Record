using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace SevenRecord.Analysis;

public sealed record LoadingDetectionWorkerResult(
    bool Succeeded,
    int Intervals,
    string? Error);

public static class MediaWorkerLoadingClient
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<LoadingDetectionWorkerResult> DetectAsync(
        string workerPath,
        string screenMediaPath,
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
        startInfo.ArgumentList.Add("detect-loading");
        startInfo.ArgumentList.Add(Path.GetFullPath(screenMediaPath));
        startInfo.ArgumentList.Add(Path.GetFullPath(outputJsonPath));

        try
        {
            using Process worker = new() { StartInfo = startInfo };
            if (!worker.Start())
            {
                return new LoadingDetectionWorkerResult(
                    false,
                    0,
                    "The media worker could not be started.");
            }

            Task<string> output = worker.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = worker.StandardError.ReadToEndAsync(cancellationToken);
            await worker.WaitForExitAsync(cancellationToken);
            string json = await output;
            string standardError = await error;
            LoadingDetectionWorkerResult? result =
                JsonSerializer.Deserialize<LoadingDetectionWorkerResult>(
                    json,
                    SerializerOptions);
            return result ??
                new LoadingDetectionWorkerResult(
                    false,
                    0,
                    string.IsNullOrWhiteSpace(standardError)
                        ? "The media worker returned an invalid loading result."
                        : standardError.Trim());
        }
        catch (Win32Exception)
        {
            return new LoadingDetectionWorkerResult(
                false,
                0,
                "The media worker could not be executed.");
        }
        catch (JsonException exception)
        {
            return new LoadingDetectionWorkerResult(
                false,
                0,
                $"The media worker returned invalid JSON: {exception.Message}");
        }
    }
}
