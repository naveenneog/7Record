namespace SevenRecord.Analysis.Tests;

[TestClass]
public sealed class FfmpegSilenceDetectorTests
{
    [TestMethod]
    public void ParsesSilenceInterval()
    {
        const string log = """
            [silencedetect] silence_start: 1.500
            [silencedetect] silence_end: 4.000 | silence_duration: 2.500
            """;

        AudioSilenceInterval interval =
            FfmpegSilenceDetector.Parse(log).Single();

        Assert.AreEqual(TimeSpan.FromSeconds(1.5), interval.Start);
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), interval.Duration);
    }
}
