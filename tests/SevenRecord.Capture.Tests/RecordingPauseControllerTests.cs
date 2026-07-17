using SevenRecord.Capture.Abstractions;

namespace SevenRecord.Capture.Tests;

[TestClass]
public sealed class RecordingPauseControllerTests
{
    [TestMethod]
    public void ResumeClosesPausedTimeGap()
    {
        RecordingPauseController controller = new();
        controller.Pause(TimeSpan.FromSeconds(3));

        Assert.AreEqual(
            TimeSpan.FromSeconds(3),
            controller.Map(TimeSpan.FromSeconds(8)));

        controller.Resume(TimeSpan.FromSeconds(8));

        Assert.AreEqual(
            TimeSpan.FromSeconds(4),
            controller.Map(TimeSpan.FromSeconds(9)));
    }
}
