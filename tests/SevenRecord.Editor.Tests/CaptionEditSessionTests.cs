using SevenRecord.Domain.Captions;

namespace SevenRecord.Editor.Tests;

[TestClass]
public sealed class CaptionEditSessionTests
{
    [TestMethod]
    public void UpdateUndoAndRedoPreserveCaptionHistory()
    {
        CaptionEditSession session = new(
            new CaptionDocument(
                1,
                "en",
                [
                    new CaptionSegment(
                        "caption",
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        "Original")
                ]));

        session.UpdateCaption(
            "caption",
            "Edited",
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromSeconds(2.5));

        Assert.AreEqual("Edited", session.Current.Segments.Single().Text);
        Assert.IsTrue(session.Undo());
        Assert.AreEqual("Original", session.Current.Segments.Single().Text);
        Assert.IsTrue(session.Redo());
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), session.Current.Segments.Single().Start);
    }

    [TestMethod]
    public void RejectsOverlappingCaptionEdit()
    {
        CaptionEditSession session = new(
            new CaptionDocument(
                1,
                "en",
                [
                    new CaptionSegment(
                        "one",
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(1),
                        "One"),
                    new CaptionSegment(
                        "two",
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2),
                        "Two")
                ]),
            TimeSpan.FromSeconds(2));

        Assert.ThrowsExactly<InvalidDataException>(
            () => session.UpdateCaption(
                "two",
                "Overlap",
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(1.5)));
    }

    [TestMethod]
    public void RejectsCaptionPastRecordingEnd()
    {
        CaptionEditSession session = new(
            new CaptionDocument(
                1,
                "en",
                [
                    new CaptionSegment(
                        "caption",
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(1),
                        "Caption")
                ]),
            TimeSpan.FromSeconds(2));

        Assert.ThrowsExactly<InvalidDataException>(
            () => session.UpdateCaption(
                "caption",
                "Too late",
                TimeSpan.FromSeconds(1.5),
                TimeSpan.FromSeconds(2.5)));
    }
}
