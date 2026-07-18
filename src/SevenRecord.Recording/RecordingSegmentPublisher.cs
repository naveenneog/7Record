using System.Security.Cryptography;

namespace SevenRecord.Recording;

public sealed class RecordingSegmentPublisher
{
    private readonly RecordingJournal _journal;
    private readonly string _projectRoot;

    public RecordingSegmentPublisher(string projectRoot, RecordingJournal journal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(journal);

        _projectRoot = Path.GetFullPath(projectRoot);
        _journal = journal;
    }

    public async Task<RecordingSegmentEntry> PublishAsync(
        string temporaryPath,
        int sequence,
        string sourceId,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(start, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        string fullTemporaryPath = Path.GetFullPath(temporaryPath);
        _ = RecordingPathGuard.ResolveWithinRoot(
            _projectRoot,
            Path.GetRelativePath(_projectRoot, fullTemporaryPath));

        if (!File.Exists(fullTemporaryPath))
        {
            throw new FileNotFoundException("The temporary segment does not exist.", fullTemporaryPath);
        }

        string segmentId = Guid.NewGuid().ToString("N");
        string extension = Path.GetExtension(fullTemporaryPath);
        if (string.Equals(extension, ".partial", StringComparison.OrdinalIgnoreCase))
        {
            extension = Path.GetExtension(Path.GetFileNameWithoutExtension(fullTemporaryPath));
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".segment";
        }

        string relativePath = Path.Combine(
            "segments",
            SanitizePathPart(sourceId),
            $"{sequence:D8}-{segmentId}{extension}");
        string finalPath = RecordingPathGuard.ResolveWithinRoot(_projectRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        long length = new FileInfo(fullTemporaryPath).Length;
        string sha256 = await ComputeSha256Async(fullTemporaryPath, cancellationToken);

        File.Move(fullTemporaryPath, finalPath);

        RecordingSegmentEntry entry = new(
            sequence,
            segmentId,
            sourceId,
            relativePath,
            start.Ticks,
            duration.Ticks,
            length,
            sha256);
        // Cancellation is honored before the file move. Once the commit begins,
        // the journal append must finish so a published segment is never left
        // unreferenced solely because the caller canceled its wait.
        await _journal.AppendAsync(entry, CancellationToken.None);

        return entry;
    }

    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string SanitizePathPart(string value)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string sanitized = string.Concat(
            value.Select(character => invalidCharacters.Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(sanitized) ? "source" : sanitized;
    }
}
