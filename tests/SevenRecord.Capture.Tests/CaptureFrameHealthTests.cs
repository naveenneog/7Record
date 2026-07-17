using SevenRecord.Capture.Abstractions;

namespace SevenRecord.Capture.Tests;

[TestClass]
public sealed class CaptureFrameHealthTests
{
    [TestMethod]
    public void SnapshotTracksFramesDropsAndProjectTime()
    {
        CaptureFrameHealthCounter counter = new();

        counter.ReportReceived(TimeSpan.FromSeconds(1));
        counter.ReportReceived(TimeSpan.FromSeconds(2));
        CaptureFrameHealthSnapshot snapshot = counter.ReportDropped();

        Assert.AreEqual(2, snapshot.FramesReceived);
        Assert.AreEqual(1, snapshot.FramesDropped);
        Assert.AreEqual(TimeSpan.FromSeconds(2), snapshot.LastProjectTime);
        Assert.AreEqual(0.5d, snapshot.DropRate);
    }
}
