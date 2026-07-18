namespace SevenRecord.Recording;

public sealed class RecordingProjectWriter : IDisposable
{
    private readonly RecordingJournal _journal;
    private readonly SemaphoreSlim _publicationLock = new(1, 1);
    private readonly RecordingSegmentPublisher _publisher;
    private int _lastSequence;
    private bool _disposed;

    private RecordingProjectWriter(
        string projectRoot,
        RecordingJournal journal,
        int lastSequence)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
        _journal = journal;
        _publisher = new RecordingSegmentPublisher(ProjectRoot, journal);
        _lastSequence = lastSequence;
    }

    public string ProjectRoot { get; }

    public static async Task<RecordingProjectWriter> OpenAsync(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string fullProjectRoot = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(fullProjectRoot);
        RecordingJournal journal = new(
            Path.Combine(fullProjectRoot, "recording.journal"));

        try
        {
            RecordingJournalReplay replay =
                await journal.ReplayAsync(cancellationToken);
            if (replay.CorruptLineNumber is not null)
            {
                throw new InvalidDataException(
                    "The recording journal has an incomplete or corrupt tail.");
            }

            int lastSequence = replay.Entries.Count == 0
                ? 0
                : replay.Entries.Max(entry => entry.Sequence);
            return new RecordingProjectWriter(
                fullProjectRoot,
                journal,
                lastSequence);
        }
        catch
        {
            journal.Dispose();
            throw;
        }
    }

    public async Task<RecordingSegmentEntry> PublishAsync(
        string temporaryPath,
        string sourceId,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _publicationLock.WaitAsync(cancellationToken);
        try
        {
            int sequence = checked(_lastSequence + 1);
            RecordingSegmentEntry entry = await _publisher.PublishAsync(
                temporaryPath,
                sequence,
                sourceId,
                start,
                duration,
                cancellationToken);
            _lastSequence = sequence;
            return entry;
        }
        finally
        {
            _publicationLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _journal.Dispose();
        _publicationLock.Dispose();
    }
}
