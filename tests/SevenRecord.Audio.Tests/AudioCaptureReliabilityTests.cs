using SevenRecord.Capture.Abstractions;
using SevenRecord.Audio.Windows;

namespace SevenRecord.Audio.Tests;

[TestClass]
public sealed class AudioCaptureReliabilityTests
{
    [TestMethod]
    public void StableHealthIsAccepted()
    {
        Assert.IsFalse(
            AudioCaptureReliability.IsAtRisk(
                Health(
                    discontinuities: 0,
                    missing: TimeSpan.Zero,
                    queueOverflows: 0)));
    }

    [TestMethod]
    public void MissingAudioOrQueueOverflowIsRisk()
    {
        Assert.IsTrue(
            AudioCaptureReliability.IsAtRisk(
                Health(
                    discontinuities: 1,
                    missing: TimeSpan.FromMilliseconds(120),
                    queueOverflows: 0)));
        Assert.IsTrue(
            AudioCaptureReliability.IsAtRisk(
                Health(
                    discontinuities: 0,
                    missing: TimeSpan.Zero,
                    queueOverflows: 1)));
    }

    private static AudioCaptureHealth Health(
        long discontinuities,
        TimeSpan missing,
        long queueOverflows) =>
        new(
            AudioCaptureSource.Microphone,
            10,
            100,
            25,
            discontinuities,
            missing,
            TimeSpan.FromSeconds(1),
            new ClockDriftEstimate(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                0))
        {
            QueueOverflows = queueOverflows,
        };
}
