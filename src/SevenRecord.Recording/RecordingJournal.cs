using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SevenRecord.Recording;

public sealed record RecordingJournalReplay(
    IReadOnlyList<RecordingSegmentEntry> Entries,
    bool HasCorruptTail,
    int? CorruptLineNumber);

public sealed class RecordingJournal : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _appendLock = new(1, 1);
    private bool _disposed;

    public RecordingJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    public async Task AppendAsync(
        RecordingSegmentEntry entry,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        ValidateEntry(entry);

        string entryJson = JsonSerializer.Serialize(entry, SerializerOptions);
        JournalEnvelope envelope = new(entry, ComputeChecksum(entryJson));
        string line = JsonSerializer.Serialize(envelope, SerializerOptions);

        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await _appendLock.WaitAsync(cancellationToken);
        try
        {
            await using FileStream stream = new(
                Path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public async Task<RecordingJournalReplay> ReplayAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(Path))
        {
            return new RecordingJournalReplay([], false, null);
        }

        string[] lines = await File.ReadAllLinesAsync(Path, cancellationToken);
        List<RecordingSegmentEntry> entries = [];

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int lineNumber = index + 1;
            if (!TryReadEnvelope(line, out RecordingSegmentEntry? entry))
            {
                if (index == lines.Length - 1)
                {
                    return new RecordingJournalReplay(entries, true, lineNumber);
                }

                throw new InvalidDataException($"Recording journal line {lineNumber} is corrupt.");
            }

            entries.Add(entry);
        }

        return new RecordingJournalReplay(entries, false, null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _appendLock.Dispose();
        _disposed = true;
    }

    private static bool TryReadEnvelope(string line, out RecordingSegmentEntry entry)
    {
        try
        {
            JournalEnvelope? envelope = JsonSerializer.Deserialize<JournalEnvelope>(line, SerializerOptions);
            if (envelope?.Entry is null || string.IsNullOrWhiteSpace(envelope.Checksum))
            {
                entry = null!;
                return false;
            }

            string entryJson = JsonSerializer.Serialize(envelope.Entry, SerializerOptions);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(envelope.Checksum),
                    Convert.FromHexString(ComputeChecksum(entryJson))))
            {
                entry = null!;
                return false;
            }

            ValidateEntry(envelope.Entry);
            entry = envelope.Entry;
            return true;
        }
        catch (FormatException)
        {
            entry = null!;
            return false;
        }
        catch (JsonException)
        {
            entry = null!;
            return false;
        }
        catch (ArgumentException)
        {
            entry = null!;
            return false;
        }
    }

    private static string ComputeChecksum(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateEntry(RecordingSegmentEntry entry)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entry.Sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SegmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.RelativePath);
        ArgumentOutOfRangeException.ThrowIfNegative(entry.StartTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entry.DurationTicks);
        ArgumentOutOfRangeException.ThrowIfNegative(entry.Length);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Sha256);
    }

    private sealed record JournalEnvelope(RecordingSegmentEntry Entry, string Checksum);
}
