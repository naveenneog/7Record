using System.Text.Json;
using SevenRecord.Camera.Windows;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Domain.Video;
using SevenRecord.Media.Windows;
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

if (args.Length > 0 &&
    string.Equals(args[0], "preview", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        PresenterLayoutSettings layout =
            PresenterLayoutSettings.DefaultOverlay with
            {
                Framing = new CameraFramingSettings(1.5, 0.4, 0.6),
                Effects = new CameraEffectSettings(0.2),
            };
        int frames = 0;
        await using CameraPreviewSession preview =
            await CameraPreviewSession.CreateAsync(layout);
        preview.FrameReady += frame =>
        {
            Interlocked.Increment(ref frames);
            frame.Dispose();
        };
        await Task.Delay(TimeSpan.FromSeconds(3));
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                succeeded = frames > 0,
                preview.DeviceName,
                preview.Width,
                preview.Height,
                frames,
                outputFiles = 0,
                backgroundEffects = preview.BackgroundEffects,
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
                exception.Message,
            },
            serializerOptions));
        Environment.ExitCode = 1;
    }
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
    TaskCompletionSource<SoftwareBitmapPreviewFrame> firstPreview =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    int previewFrames = 0;
    camera.PreviewFrameReady += frame =>
    {
        Interlocked.Increment(ref previewFrames);
        firstPreview.TrySetResult(frame);
    };
    camera.PreviewFailed += exception =>
        firstPreview.TrySetException(exception);
    using SoftwareBitmapPreviewFrame preview = await firstPreview.Task.WaitAsync(
        TimeSpan.FromSeconds(5));
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
            previewFrames,
            previewWidth = preview.Bitmap.PixelWidth,
            previewHeight = preview.Bitmap.PixelHeight,
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
