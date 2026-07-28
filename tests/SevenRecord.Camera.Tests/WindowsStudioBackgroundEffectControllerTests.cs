using SevenRecord.Domain.Video;
using SevenRecord.Camera.Windows;

namespace SevenRecord.Camera.Tests;

[TestClass]
public sealed class WindowsStudioBackgroundEffectControllerTests
{
    [TestMethod]
    public void MapsBlurModesToDriverFlags()
    {
        Assert.AreEqual(
            0UL,
            WindowsStudioBackgroundEffectController.FlagsFor(
                BackgroundBlurMode.Off));
        Assert.AreEqual(
            1UL,
            WindowsStudioBackgroundEffectController.FlagsFor(
                BackgroundBlurMode.Standard));
        Assert.AreEqual(
            5UL,
            WindowsStudioBackgroundEffectController.FlagsFor(
                BackgroundBlurMode.Portrait));
    }

    [TestMethod]
    public void MapsDriverFlagsToStrongestMode()
    {
        Assert.AreEqual(
            BackgroundBlurMode.Portrait,
            WindowsStudioBackgroundEffectController.ModeFromFlags(5));
        Assert.AreEqual(
            BackgroundBlurMode.Standard,
            WindowsStudioBackgroundEffectController.ModeFromFlags(1));
        Assert.AreEqual(
            BackgroundBlurMode.Off,
            WindowsStudioBackgroundEffectController.ModeFromFlags(0));
    }
}
