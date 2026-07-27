using System.Text.Json;
using SevenRecord.Domain.Audio;
using SevenRecord.Domain.Captions;
using SevenRecord.Domain.Input;
using SevenRecord.Domain.Video;
using SevenRecord.Domain.Timeline;
using SevenRecord.Recording;

namespace SevenRecord.Editor;

public static class ProjectTimelineLoader
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static async Task<TimelineDocument> LoadAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string fullProjectPath = Path.GetFullPath(projectPath);
        using RecordingJournal journal = new(
            Path.Combine(fullProjectPath, "recording.journal"));
        RecordingRecoveryReport recovery =
            await new RecordingRecoveryService(fullProjectPath, journal)
                .InspectAsync(cancellationToken);
        if (recovery.MissingSegments.Count > 0 ||
            recovery.CorruptSegments.Count > 0)
        {
            throw new InvalidDataException(
                "The project cannot be opened because " +
                $"{recovery.MissingSegments.Count} source file(s) are missing and " +
                $"{recovery.CorruptSegments.Count} are damaged.");
        }
        RecordingJournalReplay replay = recovery.Journal;

        TimelineClip[] clips = replay.Entries
            .Select(entry => new TimelineClip(
                entry.SegmentId,
                TrackForSource(entry.SourceId),
                entry.RelativePath,
                TimelineRange.FromStartAndDuration(
                    TimeSpan.FromTicks(entry.StartTicks),
                    TimeSpan.FromTicks(entry.DurationTicks))))
            .OrderBy(clip => clip.Range.Start)
            .ThenBy(clip => clip.Track)
            .ToArray();
        TimeSpan duration = clips.Length == 0
            ? TimeSpan.Zero
            : clips.Max(clip => clip.Range.End);

        List<TimelineAutomationEvent> automation = [];
        TimelineCaption[] captions = [];
        string captionsPath = Path.Combine(fullProjectPath, "captions.json");
        if (File.Exists(captionsPath))
        {
            string json = await File.ReadAllTextAsync(captionsPath, cancellationToken);
            CaptionDocument? document =
                JsonSerializer.Deserialize<CaptionDocument>(json, SerializerOptions);
            captions = document?.Segments
                .Select(segment => new TimelineCaption(
                    segment.Id,
                    TimelineRange.FromStartAndDuration(
                        segment.Start,
                        segment.End - segment.Start),
                    segment.Text))
                .ToArray() ?? [];
            if (captions.Length > 0)
            {
                duration = TimeSpan.FromTicks(
                    Math.Max(duration.Ticks, captions.Max(item => item.Range.End.Ticks)));
            }
        }
        string repairPath = Path.Combine(fullProjectPath, "audio-repair-plan.json");
        if (File.Exists(repairPath))
        {
            string json = await File.ReadAllTextAsync(repairPath, cancellationToken);
            AudioRepairEvent[] repairs =
                JsonSerializer.Deserialize<AudioRepairEvent[]>(json, SerializerOptions) ?? [];
            automation.AddRange(
                repairs.Select(repair => new TimelineAutomationEvent(
                    AudioRepairId(repair),
                    repair.Kind.ToString(),
                    TrackForAudio(repair.Track),
                    TimelineRange.FromStartAndDuration(
                        repair.Start,
                        repair.Duration),
                    repair.Kind is AudioRepairEventKind.InsertSilence
                        ? $"Insert {repair.Duration.TotalMilliseconds:F0} ms silence"
                        : $"Playback rate {repair.PlaybackRate:F6}",
                    true)));
        }

        string layoutPath = Path.Combine(fullProjectPath, "presenter-layout.json");
        if (File.Exists(layoutPath))
        {
            string json = await File.ReadAllTextAsync(layoutPath, cancellationToken);
            PresenterLayoutSettings? layout =
                JsonSerializer.Deserialize<PresenterLayoutSettings>(json, SerializerOptions);
            if (layout is not null)
            {
                automation.Add(
                    new TimelineAutomationEvent(
                        "presenter-layout",
                        "PresenterLayout",
                        TimelineTrackKind.Camera,
                        TimelineRange.FromStartAndDuration(TimeSpan.Zero, duration),
                        $"{layout.Mode} at {layout.X:P0}, {layout.Y:P0}",
                        true)
                    {
                        NumericData = new Dictionary<string, double>
                        {
                            ["mode"] = (double)layout.Mode,
                            ["x"] = layout.X,
                            ["y"] = layout.Y,
                            ["width"] = layout.Width,
                            ["height"] = layout.Height,
                            ["cornerRadius"] = layout.CornerRadius,
                        },
                    });
            }
        }

        string cursorZoomPath = Path.Combine(
            fullProjectPath,
            "cursor-zoom-plan.json");
        if (File.Exists(cursorZoomPath))
        {
            string cursorJson = await File.ReadAllTextAsync(
                cursorZoomPath,
                cancellationToken);
            CursorZoomEvent[] zooms =
                JsonSerializer.Deserialize<CursorZoomEvent[]>(
                    cursorJson,
                    SerializerOptions) ?? [];
            automation.AddRange(
                zooms.Select(zoom => new TimelineAutomationEvent(
                    zoom.Id,
                    "CursorZoom",
                    TimelineTrackKind.Screen,
                    TimelineRange.FromStartAndDuration(
                        zoom.Start,
                        zoom.Duration),
                    $"Zoom {zoom.Scale:F1}× at {zoom.CenterX:P0}, {zoom.CenterY:P0}",
                    true)
                {
                    NumericData = new Dictionary<string, double>
                    {
                        ["centerX"] = zoom.CenterX,
                        ["centerY"] = zoom.CenterY,
                        ["scale"] = zoom.Scale,
                    },
                }));
        }

        string loadingPath = Path.Combine(
            fullProjectPath,
            "loading-speed-plan.json");
        if (File.Exists(loadingPath))
        {
            string loadingJson = await File.ReadAllTextAsync(
                loadingPath,
                cancellationToken);
            LoadingSpeedEvent[] loadingEvents =
                JsonSerializer.Deserialize<LoadingSpeedEvent[]>(
                    loadingJson,
                    SerializerOptions) ?? [];
            automation.AddRange(
                loadingEvents.Select(item => new TimelineAutomationEvent(
                    item.Id,
                    "LoadingSpeed",
                    TimelineTrackKind.Screen,
                    TimelineRange.FromStartAndDuration(
                        item.Start,
                        item.Duration),
                    $"Speed up waiting to {item.Speed:F1}×",
                    true)
                {
                    NumericData = new Dictionary<string, double>
                    {
                        ["speed"] = item.Speed,
                        ["confidence"] = item.Confidence,
                    },
                }));
        }

        return new TimelineDocument(
            fullProjectPath,
            duration,
            clips,
            automation
                .OrderBy(item => item.Range.Start)
                .ThenBy(item => item.Kind)
                .ToArray())
        {
            Captions = captions,
        };
    }

    private static TimelineTrackKind TrackForSource(string sourceId) =>
        sourceId switch
        {
            "screen" => TimelineTrackKind.Screen,
            "camera" => TimelineTrackKind.Camera,
            "microphone" => TimelineTrackKind.Microphone,
            "system-audio" => TimelineTrackKind.SystemAudio,
            _ => TimelineTrackKind.Automation,
        };

    private static TimelineTrackKind TrackForAudio(AudioTrackKind track) =>
        track is AudioTrackKind.Microphone
            ? TimelineTrackKind.Microphone
            : TimelineTrackKind.SystemAudio;

    private static string AudioRepairId(AudioRepairEvent repair) =>
        $"audio-{repair.Track}-{repair.Kind}-" +
        $"{repair.Start.Ticks:x16}-{repair.Duration.Ticks:x16}";
}
