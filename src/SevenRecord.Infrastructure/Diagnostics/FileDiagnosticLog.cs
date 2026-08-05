using System.Globalization;
using System.Text;

namespace SevenRecord.Infrastructure.Diagnostics;

/// <summary>
/// Appends diagnostics to a rotating set of plain-text files under
/// <c>%LOCALAPPDATA%\7Record\Diagnostics</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two properties matter more than anything else here, and both are covered by tests:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>It never throws.</b> This log is written from crash paths. If it could fail it would
/// turn a recoverable fault into an unrecoverable one, which is the exact outcome it exists
/// to prevent. Every failure is counted in <see cref="SuppressedFailureCount"/> instead.
/// Formatting happens inside the guarded region too: <c>exception.ToString()</c> runs
/// arbitrary user code, and it arrives on exactly this path.
/// </description></item>
/// <item><description>
/// <b>It is bounded, across process restarts.</b> 7Record runs for hours while writing
/// multi-gigabyte video. An unbounded log on a machine that is already filling its disk is a
/// data-loss bug, not a debugging aid. Because the scenario this feature exists for is a
/// crash-restart loop, both the size bound and the retention order must survive a restart.
/// </description></item>
/// </list>
/// <para>
/// Entries are one line each, so a log stays greppable even when a stack trace is involved.
/// </para>
/// </remarks>
public sealed class FileDiagnosticLog : IDiagnosticLog
{
    private const long MinimumFileBytes = 1024;
    private const long DefaultMaximumFileBytes = 1024 * 1024;
    private const int DefaultRetainedFileCount = 5;
    private const string FilePrefix = "7record-";
    private const string FileExtension = ".log";

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly long _maximumFileBytes;
    private readonly int _retainedFileCount;

    private string? _currentFilePath;
    private long _currentFileBytes;
    private int _sequence;
    private bool _sequenceSeeded;
    private int _suppressedFailureCount;

    public FileDiagnosticLog(
        string? directory = null,
        long maximumFileBytes = DefaultMaximumFileBytes,
        int retainedFileCount = DefaultRetainedFileCount)
    {
        // Deliberately no I/O and no path validation in the constructor: a diagnostic
        // sink must be constructible even when the environment it is diagnosing is broken.
        _directory = directory ?? DefaultDirectory;
        _maximumFileBytes = Math.Max(MinimumFileBytes, maximumFileBytes);
        _retainedFileCount = Math.Max(1, retainedFileCount);
    }

    /// <summary>
    /// Sits alongside the existing <c>%LOCALAPPDATA%\7Record\Settings</c> folder.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> is a plain known-folder
    /// lookup, so unlike <c>ApplicationData.Current.LocalFolder</c> it resolves in both
    /// packaged (MSIX) and unpackaged runs. See docs/UNKNOWNS.md U-2.
    /// </remarks>
    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "7Record",
            "Diagnostics");

    /// <summary>The smallest per-file budget this log will honour, whatever the caller asks for.</summary>
    public static long MinimumMaximumFileBytes => MinimumFileBytes;

    /// <summary>The effective per-file byte budget, after clamping.</summary>
    public long MaximumFileBytes => _maximumFileBytes;

    /// <summary>The file entries are currently appended to, or empty before the first write.</summary>
    public string CurrentFilePath
    {
        get
        {
            lock (_gate)
            {
                return _currentFilePath ?? string.Empty;
            }
        }
    }

    /// <summary>
    /// How many entries were dropped because the log itself could not be written.
    /// Non-zero means diagnostics are unreliable on this machine.
    /// </summary>
    public int SuppressedFailureCount
    {
        get
        {
            lock (_gate)
            {
                return _suppressedFailureCount;
            }
        }
    }

    public void Write(
        DiagnosticSeverity severity,
        string source,
        string message,
        Exception? exception = null)
    {
        lock (_gate)
        {
            bool rotated;
            try
            {
                // Formatting is inside the guard on purpose. exception.ToString() runs
                // arbitrary user code, and a huge aggregate stack trace can fail to
                // allocate during the very low-memory crash we are trying to record.
                byte[] bytes = Encoding.UTF8.GetBytes(
                    Format(severity, source, message, exception));

                Directory.CreateDirectory(_directory);
                rotated = EnsureCurrentFile(bytes.LongLength);

                using (FileStream stream = new(
                    _currentFilePath!,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                _currentFileBytes += bytes.LongLength;
            }
            catch (Exception)
            {
                // Intentionally total. Anything at all that goes wrong while recording a
                // fault must be absorbed here - see the class remarks. The counter is the
                // only evidence, and it is asserted by FileDiagnosticLogTests.
                _suppressedFailureCount++;
                return;
            }

            if (!rotated)
            {
                return;
            }

            try
            {
                // Pruning happens after the write, never before: until the first byte is
                // written the new current file does not exist on disk, so pruning early
                // would not count it and would leave retainedFileCount + 1 files behind.
                Prune();
            }
            catch (Exception)
            {
                // A pruning failure is not a dropped entry - the write above succeeded -
                // so it deliberately does not touch SuppressedFailureCount.
            }
        }
    }

    /// <summary>
    /// Resolves the file to append to, adopting or rotating as required.
    /// </summary>
    /// <returns><see langword="true"/> when the log rolled over to a new file.</returns>
    private bool EnsureCurrentFile(long incomingBytes)
    {
        if (_currentFilePath is null)
        {
            SeedSequence();
            _currentFilePath = NextFilePath();
        }

        // Checked even on the first write of a process, so that adopting a nearly-full
        // file from a previous run rotates immediately instead of overshooting the budget.
        if (_currentFileBytes > 0 &&
            _currentFileBytes + incomingBytes > _maximumFileBytes)
        {
            // Reset first: NextFilePath owns the counter from here, because it may adopt
            // a partially-filled file and seed it with the real length.
            _currentFileBytes = 0;
            _currentFilePath = NextFilePath();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Starts numbering where the previous process run left off.
    /// </summary>
    /// <remarks>
    /// Without this, every restart begins at sequence 0 again, so a new run's filenames
    /// sort <i>before</i> the files the previous run left behind. Retention would then keep
    /// the stale files and delete the fresh ones - and the scenario that produces repeated
    /// restarts is exactly the crash loop these logs exist to explain.
    /// </remarks>
    private void SeedSequence()
    {
        if (_sequenceSeeded)
        {
            return;
        }

        _sequenceSeeded = true;
        try
        {
            string prefix = $"{FilePrefix}{TodayStamp()}-";
            int highest = -1;
            foreach (string file in Directory.GetFiles(
                _directory,
                $"{prefix}*{FileExtension}"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length > prefix.Length &&
                    int.TryParse(
                        name.AsSpan(prefix.Length),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int parsed))
                {
                    highest = Math.Max(highest, parsed);
                }
            }

            _sequence = Math.Max(0, highest);
        }
        catch (Exception)
        {
            // An unreadable directory just means we start at 0 and may reuse a name.
            _sequence = 0;
        }
    }

    private string NextFilePath()
    {
        string stamp = TodayStamp();
        while (true)
        {
            string path = Path.Combine(
                _directory,
                $"{FilePrefix}{stamp}-{_sequence:D4}{FileExtension}");
            _sequence++;

            FileInfo info = new(path);
            if (!info.Exists)
            {
                return path;
            }

            // A file left by an earlier process run is adopted, not ignored. Seeding the
            // byte counter from its real length is what keeps the size bound a property of
            // the file rather than of a single process lifetime.
            if (info.Length < _maximumFileBytes)
            {
                _currentFileBytes = info.Length;
                return path;
            }
        }
    }

    private void Prune()
    {
        string[] files = Directory.GetFiles(
            _directory,
            $"{FilePrefix}*{FileExtension}");
        if (files.Length <= _retainedFileCount)
        {
            return;
        }

        // Ordered by last-write time rather than by filename. Filenames restart their
        // numbering every day and every process, so name order does not reliably mean
        // "oldest first" - and deleting in the wrong order throws away the newest
        // evidence, which is the only evidence anyone wants after a crash.
        Array.Sort(
            files,
            (left, right) => File.GetLastWriteTimeUtc(left)
                .CompareTo(File.GetLastWriteTimeUtc(right)));

        int surplus = files.Length - _retainedFileCount;
        foreach (string file in files)
        {
            if (surplus <= 0)
            {
                break;
            }

            // Never delete the file being appended to. FileMode.Append would silently
            // recreate it and the byte counter would stop matching reality.
            if (string.Equals(file, _currentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
                surplus--;
            }
            catch (IOException)
            {
                // A log file held open by a tail viewer must not break logging.
            }
            catch (UnauthorizedAccessException)
            {
                // Likewise for a file locked down by policy.
            }
        }
    }

    private static string TodayStamp() =>
        DateTimeOffset.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Format(
        DiagnosticSeverity severity,
        string source,
        string message,
        Exception? exception)
    {
        StringBuilder builder = new();
        builder
            .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
            .Append('\t')
            .Append(severity)
            .Append('\t')
            .Append(Flatten(source))
            .Append('\t')
            .Append(Flatten(message));

        if (exception is not null)
        {
            builder.Append('\t').Append(Flatten(exception.ToString()));
        }

        return builder.Append(Environment.NewLine).ToString();
    }

    /// <summary>
    /// Collapses newlines so one entry stays one line. Stack traces are multi-line by
    /// nature, and a log you cannot grep line-by-line is a log nobody reads.
    /// </summary>
    /// <remarks>
    /// Null-tolerant on purpose: nullable annotations are a compile-time promise, and these
    /// values cross an assembly boundary from code this class does not control.
    /// </remarks>
    private static string Flatten(string? value) =>
        (value ?? string.Empty)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\n", StringComparison.Ordinal)
            .Replace("\t", "    ", StringComparison.Ordinal);
}
