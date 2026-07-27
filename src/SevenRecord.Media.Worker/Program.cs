using System.Text.Json;
using System.Text.Json.Serialization;
using SevenRecord.Analysis;
using SevenRecord.Export;
using SevenRecord.Media;

JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
};
serializerOptions.Converters.Add(new JsonStringEnumConverter());

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

if (string.Equals(args[0], "probe-encoders", StringComparison.OrdinalIgnoreCase))
{
    string? executablePath = args.Length > 1 ? args[1] : null;
    FfmpegEncoderProbeResult result = await FfmpegEncoderProbe.ProbeAsync(executablePath);

    Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));

    return result.Succeeded ? 0 : 1;
}

if (string.Equals(args[0], "encode-bgra", StringComparison.OrdinalIgnoreCase) &&
    args.Length == 6 &&
    int.TryParse(args[1], out int width) &&
    int.TryParse(args[2], out int height) &&
    int.TryParse(args[3], out int framesPerSecond))
{
    string? ffmpegPath = FfmpegLocator.FindExecutable();
    if (ffmpegPath is null)
    {
        Console.Error.WriteLine("FFmpeg is not installed or is not available on PATH.");
        return 1;
    }

    RawVideoEncoderSettings settings = new(
        width,
        height,
        framesPerSecond,
        args[4],
        args[5]);
    RawVideoEncoderResult result = await FfmpegRawVideoEncoder.EncodeAsync(
        Console.OpenStandardInput(),
        ffmpegPath,
        settings);
    if (!result.Succeeded)
    {
        Console.Error.WriteLine(result.Error);
        return 1;
    }

    return 0;
}

if (string.Equals(args[0], "export-plan", StringComparison.OrdinalIgnoreCase) &&
    args.Length == 3)
{
    string json = await File.ReadAllTextAsync(args[1]);
    RenderPlan? plan = JsonSerializer.Deserialize<RenderPlan>(json, serializerOptions);
    if (plan is null)
    {
        Console.Error.WriteLine("Render plan is empty or invalid.");
        return 1;
    }

    RenderPlanExportResult result = await FfmpegRenderPlanExporter.ExportAsync(
        plan,
        args[2]);
    Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));
    return result.Succeeded ? 0 : 1;
}

if (string.Equals(args[0], "detect-loading", StringComparison.OrdinalIgnoreCase) &&
    args.Length == 3)
{
    try
    {
        IReadOnlyList<SevenRecord.Domain.Video.LoadingSpeedEvent> events =
            await FfmpegLoadingDetector.DetectAsync(args[1]);
        await File.WriteAllTextAsync(
            args[2],
            JsonSerializer.Serialize(events, serializerOptions));
        LoadingDetectionWorkerResult result = new(true, events.Count, null);
        Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));
        return 0;
    }

    catch (Exception exception)
    {
        LoadingDetectionWorkerResult result = new(false, 0, exception.Message);
        Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));
        return 1;
    }
}

if (string.Equals(args[0], "concat-media", StringComparison.OrdinalIgnoreCase) &&
    args.Length >= 4)
{
    SegmentConcatenationResult result =
        await FfmpegSegmentConcatenator.ConcatenateAsync(
            args.Skip(2).ToArray(),
            args[1]);
    Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));
    return result.Succeeded ? 0 : 1;
}

if (string.Equals(args[0], "detect-silence", StringComparison.OrdinalIgnoreCase) &&
    args.Length == 3)
{
    try
    {
        IReadOnlyList<AudioSilenceInterval> intervals =
            await FfmpegSilenceDetector.DetectAsync(args[1]);
        await File.WriteAllTextAsync(
            args[2],
            JsonSerializer.Serialize(intervals, serializerOptions));
        SilenceDetectionWorkerResult result =
            new(true, intervals.Count, null);
        Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));
        return 0;
    }
    catch (Exception exception)
    {
        SilenceDetectionWorkerResult result =
            new(false, 0, exception.Message);
        Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));
        return 1;
    }
}

PrintUsage();
return 2;

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  SevenRecord.Media.Worker probe-encoders [ffmpeg-path]");
    Console.Error.WriteLine(
        "  SevenRecord.Media.Worker encode-bgra <width> <height> <fps> <encoder> <output>");
    Console.Error.WriteLine(
        "  SevenRecord.Media.Worker export-plan <render-plan.json> <output.mp4>");
    Console.Error.WriteLine(
        "  SevenRecord.Media.Worker detect-loading <screen-media> <output-json>");
    Console.Error.WriteLine(
        "  SevenRecord.Media.Worker concat-media <output-media> <input-media>...");
    Console.Error.WriteLine(
        "  SevenRecord.Media.Worker detect-silence <audio-media> <output-json>");
}
