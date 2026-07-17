namespace SevenRecord.Domain.Timeline;

public enum TimelineTrackKind
{
    Screen,
    Camera,
    Microphone,
    SystemAudio,
    Captions,
    Automation,
}

public sealed record TimelineClip(
    string Id,
    TimelineTrackKind Track,
    string SourcePath,
    TimelineRange Range);

public sealed record TimelineAutomationEvent(
    string Id,
    string Kind,
    TimelineTrackKind TargetTrack,
    TimelineRange Range,
    string Description,
    bool IsEnabled);

public sealed record TimelineDocument(
    string ProjectPath,
    TimeSpan Duration,
    IReadOnlyList<TimelineClip> Clips,
    IReadOnlyList<TimelineAutomationEvent> Automation);
