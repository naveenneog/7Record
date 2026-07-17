using SevenRecord.Domain.Video;

namespace SevenRecord.Domain.Tests;

[TestClass]
public sealed class PresenterLayoutSettingsTests
{
    [TestMethod]
    public void DefaultOverlayIsAReversibleNormalizedLayout()
    {
        PresenterLayoutSettings layout = PresenterLayoutSettings.DefaultOverlay;

        Assert.AreEqual(PresenterLayoutMode.RoundedOverlay, layout.Mode);
        Assert.IsTrue(layout.X is >= 0 and <= 1);
        Assert.IsTrue(layout.Y is >= 0 and <= 1);
        Assert.IsTrue(layout.Width is > 0 and <= 1);
        Assert.IsTrue(layout.Height is > 0 and <= 1);
        Assert.AreEqual(0.5d, layout.CornerRadius);
    }
}
