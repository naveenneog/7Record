using SevenRecord.Audio.Windows;

namespace SevenRecord.Audio.Tests;

[TestClass]
public sealed class AudioPacketTimelineTests
{
    [TestMethod]
    public void ContinuousPacketsAdvanceWithoutADiscontinuity()
    {
        AudioPacketTimeline timeline = new();

        AudioPacketTiming first = timeline.AddPacket(
            TimeSpan.FromMilliseconds(20),
            frames: 960,
            sampleRate: 48_000);
        AudioPacketTiming second = timeline.AddPacket(
            TimeSpan.FromMilliseconds(40),
            frames: 960,
            sampleRate: 48_000);

        Assert.AreEqual(960, first.SamplePosition);
        Assert.AreEqual(1_920, second.SamplePosition);
        Assert.IsFalse(second.HasDiscontinuity);
    }

    [TestMethod]
    public void LargeCallbackGapIsReported()
    {
        AudioPacketTimeline timeline = new();
        timeline.AddPacket(
            TimeSpan.FromMilliseconds(20),
            frames: 960,
            sampleRate: 48_000);

        AudioPacketTiming timing = timeline.AddPacket(
            TimeSpan.FromMilliseconds(120),
            frames: 960,
            sampleRate: 48_000);

        Assert.IsTrue(timing.HasDiscontinuity);
        Assert.AreEqual(TimeSpan.FromMilliseconds(40), timing.GapStart);
        Assert.AreEqual(TimeSpan.FromMilliseconds(80), timing.MissingDuration);
    }
}
