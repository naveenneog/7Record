namespace SevenRecord.Domain.Audio;

public enum AudioTrackKind
{
    Microphone,
    SystemAudio,
}

public sealed record AudioGapMetadata(
    TimeSpan Start,
    TimeSpan Duration);

public sealed record AudioClockMetadata(
    TimeSpan Drift,
    TimeSpan ObservedDuration,
    double PartsPerMillion);

public sealed record AudioTrackTimingMetadata(
    AudioTrackKind Track,
    IReadOnlyList<AudioGapMetadata> Gaps,
    AudioClockMetadata Clock);

public sealed record AudioTimingManifest(
    int SchemaVersion,
    IReadOnlyList<AudioTrackTimingMetadata> Tracks);

public enum AudioRepairEventKind
{
    InsertSilence,
    AdjustPlaybackRate,
}

public sealed record AudioRepairEvent(
    AudioTrackKind Track,
    AudioRepairEventKind Kind,
    TimeSpan Start,
    TimeSpan Duration,
    double PlaybackRate);
