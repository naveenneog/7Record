using System.Text.Json;
using SevenRecord.Domain.Audio;
using SevenRecord.Domain.Timeline;
using SevenRecord.Domain.Video;
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
        RecordingJournalReplay replay = await journal.ReplayAsync(cancellationToken);

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
        string repairPath = Path.Combine(fullProjectPath, "audio-repair-plan.json");
        if (File.Exists(repairPath))
        {
            string json = await File.ReadAllTextAsync(repairPath, cancellationToken);
            AudioRepairEvent[] repairs =
                JsonSerializer.Deserialize<AudioRepairEvent[]>(json, SerializerOptions) ?? [];
            automation.AddRange(
                repairs.Select(repair => new TimelineAutomationEvent(
                    Guid.NewGuid().ToString("N"),
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
                        Guid.NewGuid().ToString("N"),
                        "PresenterLayout",
                        TimelineTrackKind.Camera,
                        TimelineRange.FromStartAndDuration(TimeSpan.Zero, duration),
                        $"{layout.Mode} at {layout.X:P0}, {layout.Y:P0}",
                        true));
            }
        }

        return new TimelineDocument(
            fullProjectPath,
            duration,
            clips,
            automation
                .OrderBy(item => item.Range.Start)
                .ThenBy(item => item.Kind)
                .ToArray());
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
}
