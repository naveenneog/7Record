using SevenRecord.Domain.Timeline;

namespace SevenRecord.Domain.Tests;

[TestClass]
public sealed class TimelineEditMapperTests
{
    [TestMethod]
    public void MapsSourceRangeIntoReorderedOutput()
    {
        TimelineEditDocument edits = new(
            1,
            [
                new TimelineEditSlice(
                    "later",
                    new TimelineRange(
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(8))),
                new TimelineEditSlice(
                    "earlier",
                    new TimelineRange(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(3)))
            ]);

        TimelineMappedRange mapped = TimelineEditMapper.MapRange(
            new TimelineRange(
                TimeSpan.FromSeconds(6),
                TimeSpan.FromSeconds(7)),
            edits).Single();

        Assert.AreEqual(TimeSpan.FromSeconds(1), mapped.OutputRange.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(2), mapped.OutputRange.End);
    }
}
