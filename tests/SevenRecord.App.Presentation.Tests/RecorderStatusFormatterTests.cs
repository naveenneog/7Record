using SevenRecord.App.Presentation;
using SevenRecord.Capture.Abstractions;
using SevenRecord.Domain.Audio;

namespace SevenRecord.App.Presentation.Tests;

[TestClass]
public sealed class RecorderStatusFormatterTests
{
    [TestMethod]
    public void AMutedTrackReadsAsMutedRatherThanAsZeroGain()
    {
        // "0.0 dB" and "muted" mean opposite things to a user checking their mix.
        Assert.AreEqual(
            "muted",
            RecorderStatusFormatter.DescribeMix(new AudioMixSettings(0, true)));
    }

    [TestMethod]
    public void GainIsSignedSoBoostAndCutAreDistinguishable()
    {
        Assert.AreEqual(
            "+6.0 dB",
            RecorderStatusFormatter.DescribeMix(new AudioMixSettings(6, false)));
        Assert.AreEqual(
            "-3.0 dB",
            RecorderStatusFormatter.DescribeMix(new AudioMixSettings(-3, false)));
        Assert.AreEqual(
            "0.0 dB",
            RecorderStatusFormatter.DescribeMix(new AudioMixSettings(0, false)));
    }

    [TestMethod]
    public void ErrorBeatsReady()
    {
        // Reporting Ready while a source is in error is how a user records for an hour
        // and only then discovers the microphone captured nothing.
        Assert.AreEqual(
            CaptureSourceState.Error,
            RecorderStatusFormatter.WorstState(
                CaptureSourceState.Ready,
                CaptureSourceState.Error));
        Assert.AreEqual(
            CaptureSourceState.Error,
            RecorderStatusFormatter.WorstState(
                CaptureSourceState.Error,
                CaptureSourceState.Ready));
    }

    [TestMethod]
    public void WarningBeatsReadyButLosesToError()
    {
        Assert.AreEqual(
            CaptureSourceState.Warning,
            RecorderStatusFormatter.WorstState(
                CaptureSourceState.Ready,
                CaptureSourceState.Warning));
        Assert.AreEqual(
            CaptureSourceState.Unavailable,
            RecorderStatusFormatter.WorstState(
                CaptureSourceState.Warning,
                CaptureSourceState.Unavailable));
    }

    [TestMethod]
    public void ErrorAndUnavailableRankEquallyAndTheFirstIsKept()
    {
        // Both mean the source produced nothing, so neither should mask the other; the
        // tie-break is deterministic rather than arbitrary.
        Assert.AreEqual(
            CaptureSourceState.Error,
            RecorderStatusFormatter.WorstState(
                CaptureSourceState.Error,
                CaptureSourceState.Unavailable));
        Assert.AreEqual(
            RecorderStatusFormatter.Severity(CaptureSourceState.Error),
            RecorderStatusFormatter.Severity(CaptureSourceState.Unavailable));
    }

    [TestMethod]
    public void CombiningAStateWithItselfIsStable()
    {
        foreach (CaptureSourceState state in AllStates)
        {
            Assert.AreEqual(
                state,
                RecorderStatusFormatter.WorstState(state, state));
        }
    }

    [TestMethod]
    public void AProjectRootIsSortableUniqueAndReadableBack()
    {
        string first = RecorderStatusFormatter.CreateProjectRoot();
        string second = RecorderStatusFormatter.CreateProjectRoot();

        Assert.AreNotEqual(
            first,
            second,
            "two recordings started in the same second must not collide");

        string name = Path.GetFileName(first);
        StringAssert.Contains(
            RecorderTextFormatter.FormatProjectDisplayName(name),
            "Recording",
            "the generated name must be readable by the formatter that displays it");
        StringAssert.Contains(first, "7Record");
        StringAssert.Contains(first, "Projects");
    }

    private static readonly CaptureSourceState[] AllStates =
    [
        CaptureSourceState.Ready,
        CaptureSourceState.Warning,
        CaptureSourceState.Error,
        CaptureSourceState.Unavailable,
    ];
}
