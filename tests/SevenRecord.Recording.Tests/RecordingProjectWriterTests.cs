using SevenRecord.Recording;

namespace SevenRecord.Recording.Tests;

[TestClass]
public sealed class RecordingProjectWriterTests
{
    private static readonly int[] FirstTwoSequences = [1, 2];

    [TestMethod]
    public async Task ConcurrentPublicationsReceiveUniqueOrderedSequences()
    {
        using TemporaryProjectDirectory project = new();
        using RecordingProjectWriter writer =
            await RecordingProjectWriter.OpenAsync(project.Path);
        string screen = await CreateTemporarySegmentAsync(project.Path, "screen");
        string audio = await CreateTemporarySegmentAsync(project.Path, "audio");

        RecordingSegmentEntry[] entries = await Task.WhenAll(
            writer.PublishAsync(
                screen,
                "screen",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1)),
            writer.PublishAsync(
                audio,
                "microphone",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1)));

        using RecordingJournal journal = new(
            Path.Combine(project.Path, "recording.journal"));
        RecordingJournalReplay replay = await journal.ReplayAsync();

        CollectionAssert.AreEqual(
            FirstTwoSequences,
            replay.Entries.Select(entry => entry.Sequence).ToArray());
        Assert.AreEqual(
            2,
            entries.Select(entry => entry.Sequence).Distinct().Count());
    }

    [TestMethod]
    public async Task ReopenedWriterContinuesAfterExistingSequence()
    {
        using TemporaryProjectDirectory project = new();
        using (RecordingJournal journal = new(
            Path.Combine(project.Path, "recording.journal")))
        {
            await journal.AppendAsync(
                new RecordingSegmentEntry(
                    7,
                    Guid.NewGuid().ToString("N"),
                    "screen",
                    "segments/screen/existing.mp4",
                    0,
                    TimeSpan.FromSeconds(1).Ticks,
                    1,
                    new string('A', 64)));
        }

        using RecordingProjectWriter writer =
            await RecordingProjectWriter.OpenAsync(project.Path);
        string temporaryPath =
            await CreateTemporarySegmentAsync(project.Path, "camera");

        RecordingSegmentEntry entry = await writer.PublishAsync(
            temporaryPath,
            "camera",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));

        Assert.AreEqual(8, entry.Sequence);
    }

    [TestMethod]
    public async Task CanceledPublicationLeavesTemporaryFileUntouched()
    {
        using TemporaryProjectDirectory project = new();
        using RecordingProjectWriter writer =
            await RecordingProjectWriter.OpenAsync(project.Path);
        string temporaryPath =
            await CreateTemporarySegmentAsync(project.Path, "screen");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => writer.PublishAsync(
                temporaryPath,
                "screen",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(1),
                cancellation.Token));

        Assert.IsTrue(File.Exists(temporaryPath));
    }

    private static async Task<string> CreateTemporarySegmentAsync(
        string projectRoot,
        string name)
    {
        string path = Path.Combine(
            projectRoot,
            "temp",
            $"{name}.partial.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        return path;
    }
}
