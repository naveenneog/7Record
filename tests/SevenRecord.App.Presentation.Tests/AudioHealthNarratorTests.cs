using SevenRecord.App.Presentation;
using SevenRecord.Audio.Windows;
using SevenRecord.Capture.Abstractions;

namespace SevenRecord.App.Presentation.Tests;

/// <summary>
/// Covers the audio warning thresholds, which decide whether a user is told their
/// recording has an audio problem while there is still time to react.
/// </summary>
/// <remarks>
/// These were private statics inside a 3,900-line page, so nothing could reach them and
/// no test could prove a threshold was right. Extracting them was what made this testable.
/// </remarks>
[TestClass]
public sealed class AudioHealthNarratorTests
{
    [TestMethod]
    public void AMissingSourceReadsAsWaitingRatherThanHealthy()
    {
        string described = AudioHealthNarrator.Describe("Mic", null);

        StringAssert.Contains(described, "waiting for samples");
    }

    [TestMethod]
    public void ANullSourceIsNotTreatedAsASyncRisk()
    {
        // "No data yet" and "data is wrong" must not look the same, or every recording
        // would open with a spurious warning.
        Assert.IsFalse(AudioHealthNarrator.HasSyncRisk(null));
    }

    [TestMethod]
    public void AHealthySourceIsNotASyncRisk()
    {
        Assert.IsFalse(AudioHealthNarrator.HasSyncRisk(Health()));
    }

    [TestMethod]
    public void DriftBeyondTheThresholdIsASyncRisk()
    {
        AudioCaptureHealth justOver = Health(
            drift: AudioHealthNarrator.DriftWarningThreshold +
                TimeSpan.FromMilliseconds(1));

        Assert.IsTrue(AudioHealthNarrator.HasSyncRisk(justOver));
    }

    [TestMethod]
    public void DriftExactlyAtTheThresholdIsNotYetARisk()
    {
        // Exceeds() is strictly greater-than. Pinning the boundary stops a later
        // "tidy-up" from silently turning this into >= and warning on every recording.
        AudioCaptureHealth exactly = Health(
            drift: AudioHealthNarrator.DriftWarningThreshold);

        Assert.IsFalse(AudioHealthNarrator.HasSyncRisk(exactly));
    }

    [TestMethod]
    public void NegativeDriftCountsJustAsMuchAsPositiveDrift()
    {
        // Audio running early is exactly as broken as audio running late.
        AudioCaptureHealth early = Health(
            drift: -(AudioHealthNarrator.DriftWarningThreshold +
                TimeSpan.FromMilliseconds(1)));

        Assert.IsTrue(AudioHealthNarrator.HasSyncRisk(early));
    }

    [TestMethod]
    public void ASingleDiscontinuityIsAlreadyARisk()
    {
        Assert.IsTrue(AudioHealthNarrator.HasSyncRisk(Health(discontinuities: 1)));
    }

    [TestMethod]
    public void ASingleQueueOverflowIsAlreadyARisk()
    {
        Assert.IsTrue(AudioHealthNarrator.HasSyncRisk(Health(queueOverflows: 1)));
    }

    [TestMethod]
    public void MissingAudioAtTheThresholdIsARisk()
    {
        // Unlike drift this boundary is inclusive, because missing audio is lost data
        // rather than a measurement that might settle.
        AudioCaptureHealth atLimit = Health(
            missing: AudioHealthNarrator.MissingWarningThreshold);

        Assert.IsTrue(AudioHealthNarrator.HasSyncRisk(atLimit));
    }

    [TestMethod]
    public void AWarningNamesOnlyTheSourcesActuallyAtRisk()
    {
        string warning = AudioHealthNarrator.BuildWarning(
            Health(discontinuities: 3),
            Health());

        StringAssert.Contains(warning, "Mic");
        Assert.IsFalse(
            warning.Contains("System", StringComparison.Ordinal),
            "a healthy system-audio track must not be named in the warning");
    }

    [TestMethod]
    public void AWarningNamesBothSourcesWhenBothAreAtRisk()
    {
        string warning = AudioHealthNarrator.BuildWarning(
            Health(discontinuities: 2),
            Health(queueOverflows: 5));

        StringAssert.Contains(warning, "Mic");
        StringAssert.Contains(warning, "System");
        StringAssert.Contains(warning, ";");
    }

    [TestMethod]
    public void AWarningWithNoRiskySourceStillReadsAsASentence()
    {
        string warning = AudioHealthNarrator.BuildWarning(null, null);

        StringAssert.Contains(warning, "Audio sync risk detected");
        Assert.IsFalse(
            warning.Contains(':', StringComparison.Ordinal),
            "with no details there must be no dangling colon");
    }

    [TestMethod]
    public void DescriptionOmitsMissingAndOverflowWhenThereAreNone()
    {
        string described = AudioHealthNarrator.Describe("Mic", Health());

        Assert.IsFalse(
            described.Contains("missing", StringComparison.Ordinal),
            "a clean track must not mention missing audio");
        Assert.IsFalse(
            described.Contains("queue overflows", StringComparison.Ordinal),
            "a clean track must not mention queue overflows");
    }

    [TestMethod]
    public void DescriptionReportsMissingAudioAndOverflowsWhenPresent()
    {
        string described = AudioHealthNarrator.Describe(
            "System",
            Health(missing: TimeSpan.FromMilliseconds(250), queueOverflows: 4));

        StringAssert.Contains(described, "250 ms missing");
        StringAssert.Contains(described, "4 queue overflows");
    }

    private static AudioCaptureHealth Health(
        TimeSpan? drift = null,
        long discontinuities = 0,
        TimeSpan? missing = null,
        long queueOverflows = 0) =>
        new(
            AudioCaptureSource.Microphone,
            Packets: 100,
            Bytes: 100000,
            SamplePosition: 48000,
            Discontinuities: discontinuities,
            TotalMissingDuration: missing ?? TimeSpan.Zero,
            LastProjectTime: TimeSpan.FromSeconds(1),
            Drift: new ClockDriftEstimate(
                drift ?? TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                0))
        {
            QueueOverflows = queueOverflows,
        };
}
