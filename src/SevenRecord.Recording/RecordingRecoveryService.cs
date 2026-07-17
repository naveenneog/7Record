namespace SevenRecord.Recording;

public sealed record RecordingRecoveryReport(
    RecordingJournalReplay Journal,
    IReadOnlyList<RecordingSegmentEntry> RecoveredSegments,
    IReadOnlyList<RecordingSegmentEntry> MissingSegments,
    IReadOnlyList<RecordingSegmentEntry> CorruptSegments,
    IReadOnlyList<string> OrphanFiles,
    IReadOnlyList<string> TemporaryFiles);

public sealed class RecordingRecoveryService
{
    private readonly RecordingJournal _journal;
    private readonly string _projectRoot;

    public RecordingRecoveryService(string projectRoot, RecordingJournal journal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(journal);

        _projectRoot = Path.GetFullPath(projectRoot);
        _journal = journal;
    }

    public async Task<RecordingRecoveryReport> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        RecordingJournalReplay replay = await _journal.ReplayAsync(cancellationToken);
        List<RecordingSegmentEntry> recovered = [];
        List<RecordingSegmentEntry> missing = [];
        List<RecordingSegmentEntry> corrupt = [];
        HashSet<string> referencedPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (RecordingSegmentEntry entry in replay.Entries)
        {
            string fullPath;
            try
            {
                fullPath = RecordingPathGuard.ResolveWithinRoot(_projectRoot, entry.RelativePath);
            }
            catch (InvalidDataException)
            {
                corrupt.Add(entry);
                continue;
            }

            referencedPaths.Add(fullPath);
            if (!File.Exists(fullPath))
            {
                missing.Add(entry);
                continue;
            }

            FileInfo file = new(fullPath);
            string actualHash = await RecordingSegmentPublisher.ComputeSha256Async(
                fullPath,
                cancellationToken);
            if (file.Length != entry.Length ||
                !string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                corrupt.Add(entry);
                continue;
            }

            recovered.Add(entry);
        }

        string segmentsRoot = Path.Combine(_projectRoot, "segments");
        string[] segmentFiles = Directory.Exists(segmentsRoot)
            ? Directory.GetFiles(segmentsRoot, "*", SearchOption.AllDirectories)
            : [];
        string[] orphanFiles = segmentFiles
            .Where(path => !referencedPaths.Contains(Path.GetFullPath(path)))
            .Select(path => Path.GetRelativePath(_projectRoot, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] temporaryFiles = Directory.Exists(_projectRoot)
            ? Directory.GetFiles(_projectRoot, "*.partial*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_projectRoot, path))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        return new RecordingRecoveryReport(
            replay,
            recovered,
            missing,
            corrupt,
            orphanFiles,
            temporaryFiles);
    }
}
