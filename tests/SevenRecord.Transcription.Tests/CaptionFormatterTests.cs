using SevenRecord.Domain.Captions;

namespace SevenRecord.Transcription.Tests;

[TestClass]
public sealed class CaptionFormatterTests
{
    private static readonly CaptionDocument Document = new(
        1,
        "en",
        [
            new CaptionSegment(
                "one",
                TimeSpan.FromSeconds(1.25),
                TimeSpan.FromSeconds(3.5),
                "Hello 7Record")
        ]);

    [TestMethod]
    public void SrtUsesCommaMillisecondsAndIndexes()
    {
        string srt = CaptionFormatter.ToSrt(Document);

        StringAssert.Contains(srt, "1\r\n");
        StringAssert.Contains(srt, "00:00:01,250 --> 00:00:03,500");
        StringAssert.Contains(srt, "Hello 7Record");
    }

    [TestMethod]
    public void VttUsesHeaderAndDotMilliseconds()
    {
        string vtt = CaptionFormatter.ToVtt(Document);

        StringAssert.StartsWith(vtt, "WEBVTT");
        StringAssert.Contains(vtt, "00:00:01.250 --> 00:00:03.500");
    }

    [TestMethod]
    public void OutputIsSortedByCaptionStart()
    {
        CaptionDocument unsorted = Document with
        {
            Segments =
            [
                new CaptionSegment(
                    "later",
                    TimeSpan.FromSeconds(4),
                    TimeSpan.FromSeconds(5),
                    "Later"),
                Document.Segments.Single(),
            ],
        };

        string srt = CaptionFormatter.ToSrt(unsorted);

        Assert.IsTrue(
            srt.IndexOf("Hello 7Record", StringComparison.Ordinal) <
            srt.IndexOf("Later", StringComparison.Ordinal));
    }
}
