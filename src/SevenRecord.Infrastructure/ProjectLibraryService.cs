using SevenRecord.Domain.Projects;
using SevenRecord.Recording;

namespace SevenRecord.Infrastructure;

public sealed class ProjectLibraryService
{
    public static async Task<IReadOnlyList<ProjectSummary>> ListAsync(
        string projectsRoot,
        int maximumProjects = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumProjects);

        if (!Directory.Exists(projectsRoot))
        {
            return [];
        }

        DirectoryInfo[] directories = new DirectoryInfo(projectsRoot)
            .GetDirectories()
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .Take(maximumProjects)
            .ToArray();
        List<ProjectSummary> projects = [];
        foreach (DirectoryInfo directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projects.Add(await InspectProjectAsync(directory, cancellationToken));
        }

        return projects;
    }

    private static async Task<ProjectSummary> InspectProjectAsync(
        DirectoryInfo directory,
        CancellationToken cancellationToken)
    {
        string journalPath = System.IO.Path.Combine(
            directory.FullName,
            "recording.journal");
        using RecordingJournal journal = new(journalPath);
        RecordingRecoveryService recovery = new(directory.FullName, journal);

        try
        {
            RecordingRecoveryReport report = await recovery.InspectAsync(cancellationToken);
            TimeSpan duration = report.Journal.Entries.Count == 0
                ? TimeSpan.Zero
                : TimeSpan.FromTicks(
                    report.Journal.Entries.Max(entry =>
                        checked(entry.StartTicks + entry.DurationTicks)));
            ProjectRecoveryState state;
            string message;
            if (report.MissingSegments.Count > 0 || report.CorruptSegments.Count > 0)
            {
                state = ProjectRecoveryState.NeedsAttention;
                message =
                    $"{report.MissingSegments.Count} missing, " +
                    $"{report.CorruptSegments.Count} damaged segment(s).";
            }
            else if (report.Journal.HasCorruptTail ||
                     report.OrphanFiles.Count > 0 ||
                     report.TemporaryFiles.Count > 0)
            {
                state = ProjectRecoveryState.Recoverable;
                message =
                    $"{report.OrphanFiles.Count} orphaned, " +
                    $"{report.TemporaryFiles.Count} partial file(s).";
            }
            else
            {
                state = ProjectRecoveryState.Ready;
                message = "All journaled sources are healthy.";
            }

            return new ProjectSummary(
                directory.Name,
                directory.FullName,
                directory.LastWriteTimeUtc,
                duration,
                report.RecoveredSegments.Count,
                state,
                message);
        }
        catch (InvalidDataException exception)
        {
            return new ProjectSummary(
                directory.Name,
                directory.FullName,
                directory.LastWriteTimeUtc,
                TimeSpan.Zero,
                0,
                ProjectRecoveryState.Corrupt,
                exception.Message);
        }
        catch (IOException exception)
        {
            return new ProjectSummary(
                directory.Name,
                directory.FullName,
                directory.LastWriteTimeUtc,
                TimeSpan.Zero,
                0,
                ProjectRecoveryState.NeedsAttention,
                exception.Message);
        }
    }
}
