using System.Text.Json;
using System.Text.Json.Serialization;
using SevenRecord.Media;

if (args.Length == 0 || !string.Equals(args[0], "probe-encoders", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: SevenRecord.Media.Worker probe-encoders [ffmpeg-path]");
    return 2;
}

string? executablePath = args.Length > 1 ? args[1] : null;
FfmpegEncoderProbeResult result = await FfmpegEncoderProbe.ProbeAsync(executablePath);

JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
};
serializerOptions.Converters.Add(new JsonStringEnumConverter());
Console.WriteLine(JsonSerializer.Serialize(result, serializerOptions));

return result.Succeeded ? 0 : 1;
