using System.Text.Json;
using SevenRecord.Camera.Windows;
using SevenRecord.Capture.Abstractions;
using Windows.Media.Capture.Frames;

JsonSerializerOptions serializerOptions =
    new(JsonSerializerDefaults.Web) { WriteIndented = true };

if (args.Length > 0 && string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
{
    IReadOnlyList<MediaFrameSourceGroup> groups = await MediaFrameSourceGroup.FindAllAsync();
    Console.WriteLine(JsonSerializer.Serialize(
        groups.Select(group => new
        {
            group.Id,
            group.DisplayName,
            sources = group.SourceInfos.Select(source => new
            {
                source.Id,
                source.DeviceInformation?.Name,
                source.SourceKind,
                source.MediaStreamType,
            })
        }),
        serializerOptions));
    return;
}

int durationSeconds = args.Length > 0 && int.TryParse(args[0], out int parsed)
    ? parsed
    : 10;
string projectRoot = args.Length > 1
    ? Path.GetFullPath(args[1])
    : Path.Combine(
        Path.GetTempPath(),
        "SevenRecord.Camera.Probe",
        Guid.NewGuid().ToString("N"));

try
{
    ProjectClock clock = ProjectClock.StartNew();
    await using RecoverableCameraRecordingSession camera =
        await RecoverableCameraRecordingSession.CreateAsync(
            projectRoot,
            clock,
            new RecordingPauseController());
    await Task.Delay(TimeSpan.FromSeconds(durationSeconds));
    CameraRecordingResult result = await camera.CompleteAsync();

    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            succeeded = true,
            durationSeconds,
            projectRoot,
            result.DeviceName,
            result.Width,
            result.Height,
            result.Frames,
            result.DroppedFrames,
            segment = result.Segment.RelativePath,
            result.Layout,
            result.LayoutPath,
        },
        serializerOptions));
}
catch (Exception exception)
{
    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            succeeded = false,
            errorType = exception.GetType().Name,
            hresult = $"0x{exception.HResult:X8}",
            exception.Message,
        },
        serializerOptions));
    Environment.ExitCode = 1;
}
