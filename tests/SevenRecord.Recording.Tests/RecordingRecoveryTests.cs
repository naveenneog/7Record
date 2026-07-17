namespace SevenRecord.Recording.Tests;

[TestClass]
public sealed class RecordingRecoveryTests
{
    [TestMethod]
    public async Task PublisherMovesSegmentAndJournalsIt()
    {
        using TemporaryProjectDirectory project = new();
        string temporaryPath = System.IO.Path.Combine(project.Path, "temp", "screen.partial");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(temporaryPath)!);
        await File.WriteAllTextAsync(temporaryPath, "segment-data");

        using RecordingJournal journal = new(System.IO.Path.Combine(project.Path, "recording.journal"));
        RecordingSegmentPublisher publisher = new(project.Path, journal);

        RecordingSegmentEntry entry = await publisher.PublishAsync(
            temporaryPath,
            1,
            "screen",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5));

        Assert.IsFalse(File.Exists(temporaryPath));
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(project.Path, entry.RelativePath)));
        Assert.AreEqual(entry, (await journal.ReplayAsync()).Entries.Single());
    }

    [TestMethod]
    public async Task RecoveryReportsOrphansTemporaryFilesAndTampering()
    {
        using TemporaryProjectDirectory project = new();
        string temporaryPath = System.IO.Path.Combine(project.Path, "temp", "screen.partial");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(temporaryPath)!);
        await File.WriteAllTextAsync(temporaryPath, "segment-data");

        using RecordingJournal journal = new(System.IO.Path.Combine(project.Path, "recording.journal"));
        RecordingSegmentPublisher publisher = new(project.Path, journal);
        RecordingSegmentEntry entry = await publisher.PublishAsync(
            temporaryPath,
            1,
            "screen",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5));

        string publishedPath = System.IO.Path.Combine(project.Path, entry.RelativePath);
        await File.AppendAllTextAsync(publishedPath, "-tampered");

        string orphanPath = System.IO.Path.Combine(project.Path, "segments", "camera", "orphan.mkv");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(orphanPath)!);
        await File.WriteAllTextAsync(orphanPath, "orphan");

        string partialPath = System.IO.Path.Combine(project.Path, "temp", "unfinished.partial");
        await File.WriteAllTextAsync(partialPath, "unfinished");

        RecordingRecoveryService recovery = new(project.Path, journal);
        RecordingRecoveryReport report = await recovery.InspectAsync();

        Assert.AreEqual(entry, report.CorruptSegments.Single());
        Assert.IsEmpty(report.RecoveredSegments);
        Assert.HasCount(1, report.OrphanFiles);
        Assert.HasCount(1, report.TemporaryFiles);
    }
}
