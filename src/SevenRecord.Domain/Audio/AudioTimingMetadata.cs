namespace SevenRecord.Domain.Audio;

public enum AudioTrackKind
{
    Microphone,
    SystemAudio,
}

public sealed record AudioMixSettings(
    double GainDecibels,
    bool IsMuted)
{
    public static AudioMixSettings Default { get; } = new(0, false);

    public AudioMixSettings Constrain() =>
        this with
        {
            GainDecibels = Math.Clamp(
                double.IsFinite(GainDecibels)
                    ? GainDecibels
                    : 0,
                -24,
                12),
        };
}

public sealed record ProjectAudioMixSettings(
    AudioMixSettings Microphone,
    AudioMixSettings SystemAudio)
{
    public static ProjectAudioMixSettings Default { get; } =
        new(AudioMixSettings.Default, AudioMixSettings.Default);

    public ProjectAudioMixSettings Constrain() =>
        new(
            (Microphone ?? AudioMixSettings.Default).Constrain(),
            (SystemAudio ?? AudioMixSettings.Default).Constrain());
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
