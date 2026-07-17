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
    bool IsEnabled)
{
    public IReadOnlyDictionary<string, double> NumericData { get; init; } =
        new Dictionary<string, double>();
}

public sealed record TimelineCaption(
    string Id,
    TimelineRange Range,
    string Text);

public sealed record TimelineDocument(
    string ProjectPath,
    TimeSpan Duration,
    IReadOnlyList<TimelineClip> Clips,
    IReadOnlyList<TimelineAutomationEvent> Automation)
{
    public IReadOnlyList<TimelineCaption> Captions { get; init; } = [];
}
