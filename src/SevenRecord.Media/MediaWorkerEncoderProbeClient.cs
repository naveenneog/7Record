using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SevenRecord.Media;

public static class MediaWorkerEncoderProbeClient
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static async Task<FfmpegEncoderProbeResult> ProbeAsync(
        string workerPath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);

        ProcessStartInfo startInfo = new()
        {
            FileName = workerPath,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("probe-encoders");

        try
        {
            using Process worker = new() { StartInfo = startInfo };
            if (!worker.Start())
            {
                return Failure("The media worker could not be started.");
            }

            Task<string> output = worker.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = worker.StandardError.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(effectiveTimeout);

            try
            {
                await worker.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                worker.Kill(entireProcessTree: true);
                return Failure("The media worker encoder probe timed out.");
            }

            string json = await output;
            string standardError = await error;
            FfmpegEncoderProbeResult? result =
                JsonSerializer.Deserialize<FfmpegEncoderProbeResult>(json, SerializerOptions);
            if (result is null)
            {
                return Failure(
                    string.IsNullOrWhiteSpace(standardError)
                        ? "The media worker returned an empty encoder report."
                        : standardError.Trim());
            }

            return result;
        }
        catch (Win32Exception)
        {
            return Failure("The media worker could not be executed.");
        }
        catch (JsonException exception)
        {
            return Failure($"The media worker returned invalid JSON: {exception.Message}");
        }
    }

    private static FfmpegEncoderProbeResult Failure(string error) =>
        new(false, error, null, null, null, null, []);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
