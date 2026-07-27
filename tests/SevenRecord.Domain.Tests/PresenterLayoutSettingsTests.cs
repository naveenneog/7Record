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

    [TestMethod]
    public void ConstrainToFrameKeepsOverlayFullyVisible()
    {
        PresenterLayoutSettings layout = new(
            PresenterLayoutMode.RoundedOverlay,
            X: 0.95,
            Y: -0.2,
            Width: 0.4,
            Height: 0.3,
            CornerRadius: 4);

        PresenterLayoutSettings constrained = layout.ConstrainToFrame();

        Assert.AreEqual(0.6d, constrained.X, 0.0001);
        Assert.AreEqual(0d, constrained.Y);
        Assert.AreEqual(0.4d, constrained.Width);
        Assert.AreEqual(0.3d, constrained.Height);
        Assert.AreEqual(0.5d, constrained.CornerRadius);
    }

    [TestMethod]
    public void CameraCropCentersAndZoomsForOverlayAspect()
    {
        NormalizedCameraCrop crop = CameraCropGeometry.Calculate(
            1920,
            1080,
            viewportAspectRatio: 1,
            new CameraFramingSettings(2, 0.5, 0.5));

        Assert.AreEqual(0.28125, crop.Width, 0.0001);
        Assert.AreEqual(0.5, crop.Height, 0.0001);
        Assert.AreEqual(0.359375, crop.X, 0.0001);
        Assert.AreEqual(0.25, crop.Y, 0.0001);
    }

    [TestMethod]
    public void CameraFramingAndEffectsAreConstrained()
    {
        PresenterLayoutSettings layout =
            (PresenterLayoutSettings.DefaultOverlay with
            {
                Framing = new CameraFramingSettings(9, -1, 2),
                Effects = new CameraEffectSettings(4),
            }).ConstrainToFrame();

        Assert.AreEqual(4d, layout.Framing.Zoom);
        Assert.AreEqual(0d, layout.Framing.CenterX);
        Assert.AreEqual(1d, layout.Framing.CenterY);
        Assert.AreEqual(1d, layout.Effects.Exposure);
    }
}
