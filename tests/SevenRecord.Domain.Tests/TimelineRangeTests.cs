using SevenRecord.Domain.Timeline;

namespace SevenRecord.Domain.Tests;

[TestClass]
public sealed class TimelineRangeTests
{
    [TestMethod]
    public void FromStartAndDurationCalculatesEnd()
    {
        TimelineRange range = TimelineRange.FromStartAndDuration(
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(TimeSpan.FromSeconds(3), range.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(8), range.End);
        Assert.AreEqual(TimeSpan.FromSeconds(5), range.Duration);
    }

    [TestMethod]
    public void ContainsUsesAnExclusiveEnd()
    {
        TimelineRange range = TimelineRange.FromStartAndDuration(TimeSpan.Zero, TimeSpan.FromSeconds(2));

        Assert.IsTrue(range.Contains(TimeSpan.FromSeconds(1)));
        Assert.IsFalse(range.Contains(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void ShiftRejectsAResultBeforeTheTimeline()
    {
        TimelineRange range = TimelineRange.FromStartAndDuration(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => range.Shift(TimeSpan.FromSeconds(-2)));
    }
}
