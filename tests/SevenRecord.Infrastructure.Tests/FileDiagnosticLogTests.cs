using SevenRecord.Infrastructure.Diagnostics;

namespace SevenRecord.Infrastructure.Tests;

[TestClass]
public sealed class FileDiagnosticLogTests
{
    // Budgets must be at or above the clamp floor, or the log silently raises them and
    // the test measures something other than the number written in it.
    private const long SmallBudget = 1024;

    [TestMethod]
    public void WriteRecordsSeveritySourceMessageAndException()
    {
        using TemporaryDirectory directory = new();
        FileDiagnosticLog log = new(directory.Path);

        log.Write(
            DiagnosticSeverity.Fault,
            "OnExportMp4Clicked",
            "Export failed",
            new InvalidOperationException("disk is full"));

        string content = File.ReadAllText(log.CurrentFilePath);
        StringAssert.Contains(content, "Fault");
        StringAssert.Contains(content, "OnExportMp4Clicked");
        StringAssert.Contains(content, "Export failed");
        StringAssert.Contains(content, nameof(InvalidOperationException));
        StringAssert.Contains(content, "disk is full");
    }

    [TestMethod]
    public void WriteCapturesInnerExceptionDetail()
    {
        using TemporaryDirectory directory = new();
        FileDiagnosticLog log = new(directory.Path);

        log.Write(
            DiagnosticSeverity.Fault,
            "Worker",
            "Nested failure",
            new InvalidOperationException(
                "outer",
                new UnauthorizedAccessException("the real cause")));

        StringAssert.Contains(File.ReadAllText(log.CurrentFilePath), "the real cause");
    }

    [TestMethod]
    public void WriteKeepsEachEntryOnASingleLine()
    {
        using TemporaryDirectory directory = new();
        FileDiagnosticLog log = new(directory.Path);

        log.Write(
            DiagnosticSeverity.Warning,
            "Multi",
            "first line\r\nsecond line\nthird line");

        string[] lines = File.ReadAllLines(log.CurrentFilePath);
        Assert.HasCount(1, lines);
        StringAssert.Contains(lines[0], "second line");
    }

    [TestMethod]
    public void WriteNeverThrowsWhenTheDirectoryPathIsAFile()
    {
        using TemporaryDirectory directory = new();
        string blocker = Path.Combine(directory.Path, "blocked");
        File.WriteAllText(blocker, "this is a file, not a directory");

        FileDiagnosticLog log = new(blocker);

        // A crash reporter that crashes is worse than no crash reporter.
        log.Write(DiagnosticSeverity.Fault, "Source", "message");

        Assert.AreEqual(1, log.SuppressedFailureCount);
    }

    [TestMethod]
    public void WriteNeverThrowsOnAnInvalidDirectoryName()
    {
        FileDiagnosticLog log = new("\0:invalid|path?");

        log.Write(DiagnosticSeverity.Fault, "Source", "message");

        Assert.AreEqual(1, log.SuppressedFailureCount);
    }

    [TestMethod]
    public void WriteNeverThrowsWhenTheExceptionItselfMisbehaves()
    {
        using TemporaryDirectory directory = new();
        FileDiagnosticLog log = new(directory.Path);

        // exception.ToString() runs arbitrary user code, on exactly the path that is
        // only ever taken while something has already gone wrong.
        log.Write(
            DiagnosticSeverity.Fault,
            "Source",
            "message",
            new HostileException());

        Assert.AreEqual(1, log.SuppressedFailureCount);
    }

    [TestMethod]
    public void WriteNeverThrowsOnNullSourceOrMessage()
    {
        using TemporaryDirectory directory = new();
        FileDiagnosticLog log = new(directory.Path);

        // Nullable annotations are a compile-time promise, and these values arrive from
        // another assembly across a WinRT boundary.
        log.Write(DiagnosticSeverity.Fault, null!, null!);

        Assert.AreEqual(0, log.SuppressedFailureCount);
        Assert.HasCount(1, File.ReadAllLines(log.CurrentFilePath));
    }

    [TestMethod]
    public void BudgetsBelowTheFloorAreClampedUpward()
    {
        using TemporaryDirectory directory = new();
        FileDiagnosticLog log = new(directory.Path, maximumFileBytes: 16);

        Assert.AreEqual(
            FileDiagnosticLog.MinimumMaximumFileBytes,
            log.MaximumFileBytes);
    }

    [TestMethod]
    public void RotatesWhenTheCurrentFileExceedsItsBudget()
    {
        using TemporaryDirectory directory = new();
        FileDiagnosticLog log = new(
            directory.Path,
            maximumFileBytes: SmallBudget,
            retainedFileCount: 50);

        for (int index = 0; index < 60; index++)
        {
            log.Write(DiagnosticSeverity.Info, "Rotation", Padded(index));
        }

        string[] files = Directory.GetFiles(directory.Path, "*.log");
        Assert.IsGreaterThan(1, files.Length);

        // Rotating is only worth anything if it actually holds the bound.
        foreach (string file in files)
        {
            Assert.IsLessThanOrEqualTo(
                SmallBudget,
                new FileInfo(file).Length,
                $"{Path.GetFileName(file)} exceeded the byte budget");
        }
    }

    [TestMethod]
    public void PrunesRotatedFilesToTheRetentionLimit()
    {
        using TemporaryDirectory directory = new();
        FileDiagnosticLog log = new(
            directory.Path,
            maximumFileBytes: SmallBudget,
            retainedFileCount: 3);

        for (int index = 0; index < 200; index++)
        {
            log.Write(DiagnosticSeverity.Info, "Retention", Padded(index));
        }

        string[] files = Directory.GetFiles(directory.Path, "*.log");

        // Exactly three, not "at most three" - zero surviving files would also satisfy
        // an upper-bound-only assertion while being a total loss of diagnostics.
        Assert.HasCount(3, files);
        Assert.IsTrue(
            File.Exists(log.CurrentFilePath),
            "the file being appended to must never be pruned");
    }

    [TestMethod]
    public void RetentionKeepsTheNEWESTEntriesAfterASameDayRestart()
    {
        using TemporaryDirectory directory = new();

        // A first process run fills the retention limit.
        FileDiagnosticLog first = new(
            directory.Path,
            maximumFileBytes: SmallBudget,
            retainedFileCount: 3);
        for (int index = 0; index < 80; index++)
        {
            first.Write(DiagnosticSeverity.Info, "FirstRun", Padded(index));
        }

        // The app crashes and restarts on the same day - the exact scenario these logs
        // exist to explain. The restarted run's entries must survive; the old ones may not.
        FileDiagnosticLog second = new(
            directory.Path,
            maximumFileBytes: SmallBudget,
            retainedFileCount: 3);
        for (int index = 0; index < 80; index++)
        {
            second.Write(DiagnosticSeverity.Fault, "AFTERRESTART", Padded(index));
        }

        string[] files = Directory.GetFiles(directory.Path, "*.log");
        Assert.HasCount(3, files);

        bool anyRestartEvidence = files.Any(
            file => File.ReadAllText(file).Contains(
                "AFTERRESTART",
                StringComparison.Ordinal));
        Assert.IsTrue(
            anyRestartEvidence,
            "retention deleted the newest logs and kept the stale ones");
    }

    [TestMethod]
    public void ResumingIntoAnExistingFileStillRespectsTheByteBudget()
    {
        using TemporaryDirectory directory = new();

        FileDiagnosticLog first = new(directory.Path, maximumFileBytes: SmallBudget);
        for (int index = 0; index < 6; index++)
        {
            first.Write(DiagnosticSeverity.Info, "Resume", Padded(index));
        }

        Assert.IsGreaterThan(0, new FileInfo(first.CurrentFilePath).Length);

        // A second process starts against the same directory. It must not treat the
        // existing file as empty, or the bound becomes "budget per process run".
        FileDiagnosticLog second = new(directory.Path, maximumFileBytes: SmallBudget);
        for (int index = 0; index < 6; index++)
        {
            second.Write(DiagnosticSeverity.Info, "Resume", Padded(index));
        }

        // Tolerance is one entry, not one whole budget: the failure mode being guarded
        // against overshoots by up to a full budget, so a 2x tolerance could not catch it.
        long tolerance = SmallBudget + 256;
        foreach (string file in Directory.GetFiles(directory.Path, "*.log"))
        {
            Assert.IsLessThanOrEqualTo(
                tolerance,
                new FileInfo(file).Length,
                $"{Path.GetFileName(file)} grew past its budget after a restart");
        }
    }

    [TestMethod]
    public void ConcurrentWritesDoNotThrowAndDoNotLoseEntries()
    {
        using TemporaryDirectory directory = new();
        FileDiagnosticLog log = new(directory.Path, maximumFileBytes: 1024 * 1024);
        const int WriterCount = 8;
        const int PerWriter = 50;
        using Barrier start = new(WriterCount);

        // The barrier forces the writers to contend for real; without it the thread pool
        // is free to run them one after another and the test proves nothing.
        Parallel.For(0, WriterCount, writer =>
        {
            start.SignalAndWait();
            for (int index = 0; index < PerWriter; index++)
            {
                log.Write(
                    DiagnosticSeverity.Info,
                    "Concurrency",
                    $"writer {writer} entry {index}");
            }
        });

        Assert.HasCount(
            WriterCount * PerWriter,
            File.ReadAllLines(log.CurrentFilePath));
        Assert.AreEqual(0, log.SuppressedFailureCount);
    }

    [TestMethod]
    public void DefaultDirectoryLivesUnderLocalApplicationData()
    {
        string expectedRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "7Record");

        StringAssert.StartsWith(FileDiagnosticLog.DefaultDirectory, expectedRoot);
    }

    private static string Padded(int index) =>
        $"entry {index} padded out with enough text to move the file toward its budget";

    private sealed class HostileException : Exception
    {
        public override string ToString() =>
            throw new InvalidOperationException("ToString blew up");

        public override string Message =>
            throw new InvalidOperationException("Message blew up");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SevenRecord.Diagnostics.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leaked temp directory must never fail a test run.
            }
        }
    }
}
