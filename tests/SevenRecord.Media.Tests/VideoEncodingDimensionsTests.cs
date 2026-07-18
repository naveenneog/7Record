using SevenRecord.Media;

namespace SevenRecord.Media.Tests;

[TestClass]
public sealed class VideoEncodingDimensionsTests
{
    [TestMethod]
    public void EvenDimensionsRemainUnchanged()
    {
        Assert.AreEqual(1920, VideoEncodingDimensions.NormalizeEven(1920));
    }

    [TestMethod]
    public void OddDimensionsRoundUpForVideoCodecs()
    {
        Assert.AreEqual(1080, VideoEncodingDimensions.NormalizeEven(1079));
    }

    [TestMethod]
    public void NonPositiveDimensionsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => VideoEncodingDimensions.NormalizeEven(0));
    }
}
