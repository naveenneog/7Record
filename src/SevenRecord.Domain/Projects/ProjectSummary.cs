namespace SevenRecord.Domain.Projects;

public enum ProjectRecoveryState
{
    Ready,
    Recoverable,
    NeedsAttention,
    Corrupt,
}

public sealed record ProjectSummary(
    string Name,
    string Path,
    DateTimeOffset UpdatedAt,
    TimeSpan Duration,
    int MediaSegments,
    ProjectRecoveryState RecoveryState,
    string StatusMessage);
