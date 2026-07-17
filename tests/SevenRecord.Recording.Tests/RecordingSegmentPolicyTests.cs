namespace SevenRecord.Recording.Tests;

[TestClass]
public sealed class RecordingSegmentPolicyTests
{
    [TestMethod]
    public void DefaultLimitsCrashLossToFiveSeconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(5), RecordingSegmentPolicy.Default.TargetDuration);
    }

    [DataRow(1)]
    [DataRow(11)]
    [TestMethod]
    public void ConstructorRejectsUnsafeDurations(int seconds)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RecordingSegmentPolicy(TimeSpan.FromSeconds(seconds)));
    }
}
