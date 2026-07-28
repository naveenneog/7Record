using SevenRecord.Domain.Video;

namespace SevenRecord.Domain.Tests;

[TestClass]
public sealed class CameraEffectSettingsTests
{
    [TestMethod]
    public void BlurModeAndExposureArePreserved()
    {
        CameraEffectSettings effects =
            (CameraEffectSettings.Default with
            {
                Exposure = 0.4,
                BackgroundBlur = BackgroundBlurMode.Portrait,
            }).Constrain();

        Assert.AreEqual(0.4, effects.Exposure);
        Assert.AreEqual(
            BackgroundBlurMode.Portrait,
            effects.BackgroundBlur);
    }
}
