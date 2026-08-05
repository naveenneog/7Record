using SevenRecord.App.Presentation;

namespace SevenRecord.App.Presentation.Tests;

/// <summary>
/// Covers the one extracted member whose signature and body actually changed.
/// </summary>
/// <remarks>
/// <see cref="RecorderTextFormatter.IssueMessage"/> was rewritten to take plain
/// component/message pairs instead of the recorder's own issue type, so that this assembly
/// does not drag Win2D and the Windows App SDK into anything that references it. A rewrite
/// is exactly where a silent behaviour change hides, so the original semantics are pinned
/// here: first case-insensitive prefix match wins, and the result is the fallback followed
/// by the issue message - not the issue message alone.
/// </remarks>
[TestClass]
public sealed class RecorderTextFormatterTests
{
    [TestMethod]
    public void IssueMessageAppendsTheReasonToTheFallback()
    {
        string message = RecorderTextFormatter.IssueMessage(
            [("audio", "The microphone is in use by another app.")],
            "audio",
            "Audio is unavailable.");

        // Not just the reason: the fallback carries the "what", the issue carries the "why".
        Assert.AreEqual(
            "Audio is unavailable. The microphone is in use by another app.",
            message);
    }

    [TestMethod]
    public void IssueMessageFallsBackWhenNothingMatches()
    {
        string message = RecorderTextFormatter.IssueMessage(
            [("camera", "No camera found.")],
            "audio",
            "Audio is unavailable.");

        Assert.AreEqual("Audio is unavailable.", message);
    }

    [TestMethod]
    public void IssueMessageFallsBackOnAnEmptyIssueList()
    {
        string message = RecorderTextFormatter.IssueMessage(
            [],
            "audio",
            "Audio is unavailable.");

        Assert.AreEqual("Audio is unavailable.", message);
    }

    [TestMethod]
    public void IssueMessageMatchesOnAPrefixNotAnExactName()
    {
        // Real components are named things like "audio.microphone".
        string message = RecorderTextFormatter.IssueMessage(
            [("audio.microphone", "Device removed.")],
            "audio",
            "Audio is unavailable.");

        StringAssert.Contains(message, "Device removed.");
    }

    [TestMethod]
    public void IssueMessageIgnoresCaseWhenMatching()
    {
        string message = RecorderTextFormatter.IssueMessage(
            [("AUDIO", "Device removed.")],
            "audio",
            "Audio is unavailable.");

        StringAssert.Contains(message, "Device removed.");
    }

    [TestMethod]
    public void IssueMessageUsesTheFirstMatchWhenSeveralApply()
    {
        string message = RecorderTextFormatter.IssueMessage(
            [
                ("audio", "First reason."),
                ("audio", "Second reason."),
            ],
            "audio",
            "Audio is unavailable.");

        StringAssert.Contains(message, "First reason.");
        Assert.IsFalse(
            message.Contains("Second reason.", StringComparison.Ordinal),
            "only the first matching issue should be reported");
    }

    [TestMethod]
    public void ATimestampedProjectNameBecomesReadable()
    {
        string display = RecorderTextFormatter.FormatProjectDisplayName(
            "20260805-143000");

        StringAssert.Contains(display, "Recording");
        StringAssert.Contains(display, "2026");
        StringAssert.Contains(display, "Aug");
    }

    [TestMethod]
    public void ATimestampedNameWithASuffixIsStillRecognised()
    {
        string display = RecorderTextFormatter.FormatProjectDisplayName(
            "20260805-143000-take2");

        StringAssert.Contains(display, "Recording");
    }

    [TestMethod]
    public void ANameThatIsNotATimestampIsLeftAlone()
    {
        Assert.AreEqual(
            "my-demo-project",
            RecorderTextFormatter.FormatProjectDisplayName("my-demo-project"));
    }

    [TestMethod]
    public void ANameShorterThanATimestampIsLeftAlone()
    {
        Assert.AreEqual(
            "short",
            RecorderTextFormatter.FormatProjectDisplayName("short"));
    }

    [TestMethod]
    public void ANameThatLooksLikeATimestampButIsNotAValidDateIsLeftAlone()
    {
        // Right shape, impossible month. Must not be reported as a recording date.
        Assert.AreEqual(
            "20261305-143000",
            RecorderTextFormatter.FormatProjectDisplayName("20261305-143000"));
    }
}
