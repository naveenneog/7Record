using SevenRecord.Domain.Audio;

namespace SevenRecord.Analysis;

public static class AudioRepairPlanner
{
    private const double MinimumRate = 0.995;
    private const double MaximumRate = 1.005;
    private const double MinimumDriftPartsPerMillion = 50;
    private static readonly TimeSpan MinimumDriftObservation = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<AudioRepairEvent> CreatePlan(AudioTimingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        List<AudioRepairEvent> events = [];

        foreach (AudioTrackTimingMetadata track in manifest.Tracks)
        {
            events.AddRange(
                track.Gaps
                    .Where(gap => gap.Duration > TimeSpan.Zero)
                    .Select(gap => new AudioRepairEvent(
                        track.Track,
                        AudioRepairEventKind.InsertSilence,
                        gap.Start,
                        gap.Duration,
                        1)));

            if (track.Clock.ObservedDuration >= MinimumDriftObservation &&
                Math.Abs(track.Clock.PartsPerMillion) >= MinimumDriftPartsPerMillion)
            {
                double playbackRate = Math.Clamp(
                    1d + track.Clock.PartsPerMillion / 1_000_000d,
                    MinimumRate,
                    MaximumRate);
                events.Add(
                    new AudioRepairEvent(
                        track.Track,
                        AudioRepairEventKind.AdjustPlaybackRate,
                        TimeSpan.Zero,
                        track.Clock.ObservedDuration,
                        playbackRate));
            }
        }

        return events
            .OrderBy(repair => repair.Start)
            .ThenBy(repair => repair.Track)
            .ToArray();
    }
}
