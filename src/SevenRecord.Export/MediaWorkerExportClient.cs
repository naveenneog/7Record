using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace SevenRecord.Export;

public static class MediaWorkerExportClient
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<RenderPlanExportResult> ExportAsync(
        string workerPath,
        string renderPlanPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderPlanPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        ProcessStartInfo startInfo = new()
        {
            FileName = workerPath,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("export-plan");
        startInfo.ArgumentList.Add(Path.GetFullPath(renderPlanPath));
        startInfo.ArgumentList.Add(Path.GetFullPath(outputPath));

        try
        {
            using Process worker = new() { StartInfo = startInfo };
            if (!worker.Start())
            {
                return new RenderPlanExportResult(
                    false,
                    outputPath,
                    "The media worker could not be started.");
            }

            Task<string> output = worker.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = worker.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await worker.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await TerminateWorkerAsync(worker).ConfigureAwait(false);
                throw;
            }
            string json = await output;
            string standardError = await error;
            RenderPlanExportResult? result =
                JsonSerializer.Deserialize<RenderPlanExportResult>(
                    json,
                    SerializerOptions);
            return result ??
                new RenderPlanExportResult(
                    false,
                    outputPath,
                    string.IsNullOrWhiteSpace(standardError)
                        ? "The media worker returned an invalid export result."
                        : standardError.Trim());
        }
        catch (Win32Exception)
        {
            return new RenderPlanExportResult(
                false,
                outputPath,
                "The media worker could not be executed.");
        }
        catch (JsonException exception)
        {
            return new RenderPlanExportResult(
                false,
                outputPath,
                $"The media worker returned invalid JSON: {exception.Message}");
        }
    }

    private static async Task TerminateWorkerAsync(Process worker)
    {
        try
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (TimeoutException)
        {
        }
    }
}
