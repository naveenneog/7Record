using SevenRecord.Domain.Audio;

namespace SevenRecord.Analysis.Tests;

[TestClass]
public sealed class AudioRepairPlannerTests
{
    [TestMethod]
    public void GapsBecomeNonDestructiveSilenceEvents()
    {
        AudioTimingManifest manifest = Manifest(
            gaps:
            [
                new AudioGapMetadata(
                    TimeSpan.FromSeconds(4),
                    TimeSpan.FromMilliseconds(180))
            ],
            driftPartsPerMillion: 0,
            observedDuration: TimeSpan.FromSeconds(10));

        AudioRepairEvent repair = AudioRepairPlanner.CreatePlan(manifest).Single();

        Assert.AreEqual(AudioRepairEventKind.InsertSilence, repair.Kind);
        Assert.AreEqual(TimeSpan.FromSeconds(4), repair.Start);
        Assert.AreEqual(TimeSpan.FromMilliseconds(180), repair.Duration);
        Assert.AreEqual(1d, repair.PlaybackRate);
    }

    [TestMethod]
    public void SustainedDriftBecomesABoundedRateEvent()
    {
        AudioTimingManifest manifest = Manifest(
            gaps: [],
            driftPartsPerMillion: -19_000,
            observedDuration: TimeSpan.FromMinutes(1));

        AudioRepairEvent repair = AudioRepairPlanner.CreatePlan(manifest).Single();

        Assert.AreEqual(AudioRepairEventKind.AdjustPlaybackRate, repair.Kind);
        Assert.AreEqual(0.995d, repair.PlaybackRate);
        Assert.AreEqual(TimeSpan.FromMinutes(1), repair.Duration);
    }

    [TestMethod]
    public void ShortOrNegligibleDriftDoesNotCreateAnEdit()
    {
        AudioTimingManifest manifest = Manifest(
            gaps: [],
            driftPartsPerMillion: 40,
            observedDuration: TimeSpan.FromMinutes(1));

        Assert.IsEmpty(AudioRepairPlanner.CreatePlan(manifest));
    }

    private static AudioTimingManifest Manifest(
        IReadOnlyList<AudioGapMetadata> gaps,
        double driftPartsPerMillion,
        TimeSpan observedDuration) =>
        new(
            1,
            [
                new AudioTrackTimingMetadata(
                    AudioTrackKind.Microphone,
                    gaps,
                    new AudioClockMetadata(
                        TimeSpan.FromSeconds(
                            driftPartsPerMillion / 1_000_000d *
                            observedDuration.TotalSeconds),
                        observedDuration,
                        driftPartsPerMillion))
            ]);
}
