using System.Text.Json;
using SevenRecord.Analysis;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Domain.Input;
using SevenRecord.Input.Windows;

string projectRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(
        Path.GetTempPath(),
        "SevenRecord.Input.Probe",
        Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(projectRoot);

ProjectClock clock = ProjectClock.StartNew();
try
{
    await using CursorMetadataRecorder recorder = CursorMetadataRecorder.Start(
        clock,
        new RecordingPauseController());
    await Task.Delay(TimeSpan.FromSeconds(2));
    CursorMetadataDocument document = await recorder.CompleteAsync(projectRoot);
    IReadOnlyList<CursorZoomEvent> zooms = CursorZoomPlanner.CreatePlan(document);
    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            succeeded = true,
            projectRoot,
            events = document.Events.Count,
            moves = document.Events.Count(item => item.Kind is CursorEventKind.Move),
            clicks = document.Events.Count(item => item.Kind is CursorEventKind.Click),
            zooms = zooms.Count,
        },
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
}
catch (InvalidOperationException exception)
{
    Console.WriteLine(JsonSerializer.Serialize(
        new
        {
            succeeded = false,
            exception.Message,
        },
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
}
