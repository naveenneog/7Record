namespace SevenRecord.Recording.Tests;

[TestClass]
public sealed class RecordingJournalTests
{
    [TestMethod]
    public async Task AppendAndReplayPreserveSegmentMetadata()
    {
        using TemporaryProjectDirectory project = new();
        using RecordingJournal journal = new(System.IO.Path.Combine(project.Path, "recording.journal"));
        RecordingSegmentEntry expected = Entry(1, "segments/screen/one.mkv");

        await journal.AppendAsync(expected);
        RecordingJournalReplay replay = await journal.ReplayAsync();

        Assert.IsFalse(replay.HasCorruptTail);
        Assert.AreEqual(expected, replay.Entries.Single());
    }

    [TestMethod]
    public async Task ReplayIgnoresAnIncompleteFinalLine()
    {
        using TemporaryProjectDirectory project = new();
        string journalPath = System.IO.Path.Combine(project.Path, "recording.journal");
        using RecordingJournal journal = new(journalPath);
        await journal.AppendAsync(Entry(1, "segments/screen/one.mkv"));
        await File.AppendAllTextAsync(journalPath, "{\"entry\":");

        RecordingJournalReplay replay = await journal.ReplayAsync();

        Assert.IsTrue(replay.HasCorruptTail);
        Assert.AreEqual(2, replay.CorruptLineNumber);
        Assert.HasCount(1, replay.Entries);
    }

    [TestMethod]
    public async Task ReplayRejectsCorruptionBeforeTheTail()
    {
        using TemporaryProjectDirectory project = new();
        string journalPath = System.IO.Path.Combine(project.Path, "recording.journal");
        using RecordingJournal journal = new(journalPath);
        await journal.AppendAsync(Entry(1, "segments/screen/one.mkv"));
        await File.AppendAllTextAsync(journalPath, "{\"invalid\":true}" + Environment.NewLine);
        await journal.AppendAsync(Entry(2, "segments/screen/two.mkv"));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => journal.ReplayAsync());
    }

    private static RecordingSegmentEntry Entry(int sequence, string path) =>
        new(
            sequence,
            Guid.NewGuid().ToString("N"),
            "screen",
            path,
            TimeSpan.FromSeconds(sequence - 1).Ticks,
            TimeSpan.FromSeconds(5).Ticks,
            3,
            new string('A', 64));
}
