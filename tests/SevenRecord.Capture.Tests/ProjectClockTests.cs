using SevenRecord.Capture.Abstractions;

namespace SevenRecord.Capture.Tests;

[TestClass]
public sealed class ProjectClockTests
{
    private const long Frequency = 10_000_000;

    [TestMethod]
    public void NormalizeUsesTheProjectOrigin()
    {
        ProjectClock clock = new(new QpcTimestamp(50 * Frequency, Frequency));

        TimeSpan projectTime = clock.Normalize(new QpcTimestamp(53 * Frequency, Frequency));

        Assert.AreEqual(TimeSpan.FromSeconds(3), projectTime);
    }

    [TestMethod]
    public void NormalizeSupportsAOneHourCaptureWithoutClockLoss()
    {
        ProjectClock clock = new(new QpcTimestamp(100 * Frequency, Frequency));

        TimeSpan projectTime = clock.Normalize(new QpcTimestamp(3_700 * Frequency, Frequency));

        Assert.AreEqual(TimeSpan.FromHours(1), projectTime);
    }

    [TestMethod]
    public void NormalizeSystemRelativeTimeSharesTheQpcEpoch()
    {
        ProjectClock clock = new(new QpcTimestamp(25 * Frequency, Frequency));

        TimeSpan projectTime = clock.NormalizeSystemRelativeTime(TimeSpan.FromSeconds(27.5));

        Assert.AreEqual(TimeSpan.FromSeconds(2.5), projectTime);
    }

    [TestMethod]
    public void NormalizeConvertsASecondQpcFrequencyThroughSystemTime()
    {
        ProjectClock clock = new(new QpcTimestamp(25 * Frequency, Frequency));

        TimeSpan projectTime = clock.Normalize(new QpcTimestamp(55_000, 2_000));

        Assert.AreEqual(TimeSpan.FromSeconds(2.5), projectTime);
    }

    [TestMethod]
    public void NormalizeRejectsTimestampsBeforeRecordingStart()
    {
        ProjectClock clock = new(new QpcTimestamp(50 * Frequency, Frequency));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => clock.Normalize(new QpcTimestamp(49 * Frequency, Frequency)));
    }
}
