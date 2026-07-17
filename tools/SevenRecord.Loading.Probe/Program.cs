using System.Text.Json;
using SevenRecord.Analysis;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: SevenRecord.Loading.Probe <screen-media> [output-json]");
    return 2;
}

IReadOnlyList<SevenRecord.Domain.Video.LoadingSpeedEvent> events =
    await FfmpegLoadingDetector.DetectAsync(args[0]);
if (args.Length > 1)
{
    await File.WriteAllTextAsync(
        args[1],
        JsonSerializer.Serialize(
            events,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
}

Console.WriteLine(JsonSerializer.Serialize(
    new
    {
        intervals = events.Count,
        events,
    },
    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
return 0;
