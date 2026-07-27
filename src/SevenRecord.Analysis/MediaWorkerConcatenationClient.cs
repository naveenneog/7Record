using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using SevenRecord.Media;

namespace SevenRecord.Analysis;

public static class MediaWorkerConcatenationClient
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<SegmentConcatenationResult> ConcatenateAsync(
        string workerPath,
        IReadOnlyList<string> inputPaths,
        string outputPath,
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
        startInfo.ArgumentList.Add("concat-media");
        startInfo.ArgumentList.Add(Path.GetFullPath(outputPath));
        foreach (string inputPath in inputPaths)
        {
            startInfo.ArgumentList.Add(Path.GetFullPath(inputPath));
        }

        try
        {
            using Process worker = new() { StartInfo = startInfo };
            if (!worker.Start())
            {
                return new SegmentConcatenationResult(
                    false,
                    outputPath,
                    "The media worker could not be started.");
            }

            Task<string> output =
                worker.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error =
                worker.StandardError.ReadToEndAsync(cancellationToken);
            await worker.WaitForExitAsync(cancellationToken);
            string json = await output;
            string standardError = await error;
            return JsonSerializer.Deserialize<SegmentConcatenationResult>(
                    json,
                    SerializerOptions) ??
                new SegmentConcatenationResult(
                    false,
                    outputPath,
                    string.IsNullOrWhiteSpace(standardError)
                        ? "The media worker returned an invalid result."
                        : standardError.Trim());
        }
        catch (Win32Exception exception)
        {
            return new SegmentConcatenationResult(
                false,
                outputPath,
                exception.Message);
        }
    }
}
