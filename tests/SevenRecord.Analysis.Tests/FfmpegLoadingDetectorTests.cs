namespace SevenRecord.Analysis.Tests;

[TestClass]
public sealed class FfmpegLoadingDetectorTests
{
    [TestMethod]
    public void ParsesSustainedFreezeIntoSpeedEvent()
    {
        const string log = """
            [freezedetect] lavfi.freezedetect.freeze_start: 1.250
            [freezedetect] lavfi.freezedetect.freeze_duration: 3.500
            [freezedetect] lavfi.freezedetect.freeze_end: 4.750
            """;

        SevenRecord.Domain.Video.LoadingSpeedEvent result =
            FfmpegLoadingDetector.Parse(
                log,
                TimeSpan.FromSeconds(2)).Single();

        Assert.AreEqual(TimeSpan.FromSeconds(1.25), result.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(3.5), result.Duration);
        Assert.AreEqual(4d, result.Speed);
    }

    [TestMethod]
    public void OpenFreezeAtEndUsesMediaDuration()
    {
        const string log =
            "[freezedetect] lavfi.freezedetect.freeze_start: 1.000";

        SevenRecord.Domain.Video.LoadingSpeedEvent result =
            FfmpegLoadingDetector.Parse(
                log,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5)).Single();

        Assert.AreEqual(TimeSpan.FromSeconds(4), result.Duration);
    }
}
