using SevenRecord.Capture.Abstractions;

namespace SevenRecord.Capture.Tests;

[TestClass]
public sealed class CaptureReadinessSnapshotTests
{
    [TestMethod]
    public void RequiredUnavailableSourceBlocksRecording()
    {
        CaptureReadinessSnapshot snapshot = new(
        [
            Item("screen", CaptureSourceState.Ready, true),
            Item("microphone", CaptureSourceState.Unavailable, true),
        ]);

        Assert.IsFalse(snapshot.CanRecord);
        Assert.AreEqual("microphone", snapshot.BlockingItems.Single().Key);
    }

    [TestMethod]
    public void OptionalUnavailableSourceDoesNotBlockRecording()
    {
        CaptureReadinessSnapshot snapshot = new(
        [
            Item("screen", CaptureSourceState.Ready, true),
            Item("camera", CaptureSourceState.Unavailable, false),
        ]);

        Assert.IsTrue(snapshot.CanRecord);
        Assert.IsEmpty(snapshot.BlockingItems);
    }

    [TestMethod]
    public void WarningAllowsRecordingWithVisibleDegradation()
    {
        CaptureReadinessSnapshot snapshot = new(
        [
            Item("screen", CaptureSourceState.Ready, true),
            Item("encoder", CaptureSourceState.Warning, true),
        ]);

        Assert.IsTrue(snapshot.CanRecord);
    }

    private static CaptureReadinessItem Item(
        string key,
        CaptureSourceState state,
        bool isRequired) =>
        new(key, key, state, isRequired, key);
}
