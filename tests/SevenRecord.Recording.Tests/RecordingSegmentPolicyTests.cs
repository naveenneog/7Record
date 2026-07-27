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

    [TestMethod]
    public void RolloverBeginsAtTargetDuration()
    {
        RecordingSegmentPolicy policy = RecordingSegmentPolicy.Default;

        Assert.IsFalse(
            policy.ShouldRollover(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(9.999)));
        Assert.IsTrue(
            policy.ShouldRollover(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10)));
    }
}
