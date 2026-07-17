using SevenRecord.Capture.Abstractions;

namespace SevenRecord.Capture.Tests;

[TestClass]
public sealed class ClockDriftEstimatorTests
{
    [TestMethod]
    public void InitialDeviceOffsetDoesNotCountAsDrift()
    {
        ClockDriftEstimator estimator = new();
        estimator.AddSample(TimeSpan.FromSeconds(10), 48_000 * 3, 48_000);

        ClockDriftEstimate estimate = estimator.AddSample(
            TimeSpan.FromSeconds(20),
            48_000 * 13,
            48_000);

        Assert.AreEqual(TimeSpan.Zero, estimate.Drift);
        Assert.AreEqual(0d, estimate.PartsPerMillion);
    }

    [TestMethod]
    public void DetectsOneHundredPartsPerMillionOverAnHour()
    {
        ClockDriftEstimator estimator = new();
        estimator.AddSample(TimeSpan.Zero, 0, 48_000);

        long samplesAfterOneHour = 48_000L * 3_600L + 17_280L;
        ClockDriftEstimate estimate = estimator.AddSample(
            TimeSpan.FromHours(1),
            samplesAfterOneHour,
            48_000);

        Assert.AreEqual(TimeSpan.FromMilliseconds(360), estimate.Drift);
        Assert.AreEqual(100d, estimate.PartsPerMillion, 0.001d);
        Assert.IsTrue(estimate.Exceeds(TimeSpan.FromMilliseconds(40)));
    }

    [TestMethod]
    public void AlignedClockRemainsWithinSyncThreshold()
    {
        ClockDriftEstimator estimator = new();
        estimator.AddSample(TimeSpan.Zero, 0, 48_000);

        ClockDriftEstimate estimate = estimator.AddSample(
            TimeSpan.FromHours(1),
            48_000L * 3_600L,
            48_000);

        Assert.IsFalse(estimate.Exceeds(TimeSpan.FromMilliseconds(40)));
    }
}
