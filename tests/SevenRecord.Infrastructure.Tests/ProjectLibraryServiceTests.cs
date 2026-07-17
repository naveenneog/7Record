using SevenRecord.Domain.Projects;
using SevenRecord.Infrastructure;
using SevenRecord.Recording;

namespace SevenRecord.Infrastructure.Tests;

[TestClass]
public sealed class ProjectLibraryServiceTests
{
    [TestMethod]
    public async Task HealthyJournaledProjectIsReady()
    {
        using TestProjectsRoot root = new();
        string project = root.CreateProject("ready");
        string temporary = System.IO.Path.Combine(project, "temp", "screen.partial");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(temporary)!);
        await File.WriteAllTextAsync(temporary, "screen");
        using RecordingJournal journal =
            new(System.IO.Path.Combine(project, "recording.journal"));
        RecordingSegmentPublisher publisher = new(project, journal);
        await publisher.PublishAsync(
            temporary,
            1,
            "screen",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5));

        ProjectSummary summary =
            (await ProjectLibraryService.ListAsync(root.Path)).Single();

        Assert.AreEqual(ProjectRecoveryState.Ready, summary.RecoveryState);
        Assert.AreEqual(TimeSpan.FromSeconds(5), summary.Duration);
        Assert.AreEqual(1, summary.MediaSegments);
    }

    [TestMethod]
    public async Task IncompleteJournalTailIsRecoverable()
    {
        using TestProjectsRoot root = new();
        string project = root.CreateProject("recoverable");
        string journalPath = System.IO.Path.Combine(project, "recording.journal");
        await File.WriteAllTextAsync(journalPath, "{\"entry\":");

        ProjectSummary summary =
            (await ProjectLibraryService.ListAsync(root.Path)).Single();

        Assert.AreEqual(ProjectRecoveryState.Recoverable, summary.RecoveryState);
    }

    private sealed class TestProjectsRoot : IDisposable
    {
        public TestProjectsRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SevenRecord.Library.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateProject(string name)
        {
            string project = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(project);
            return project;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
