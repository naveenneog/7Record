using SevenRecord.Infrastructure.Diagnostics;

namespace SevenRecord.Infrastructure.Tests;

[TestClass]
public sealed class FaultBarrierTests
{
    [TestMethod]
    public async Task GuardAsyncSwallowsTheExceptionSoAsyncVoidCannotKillTheProcess()
    {
        RecordingDiagnosticLog log = new();
        FaultBarrier barrier = new(log);

        await barrier.GuardAsync(
            "OnExportMp4Clicked",
            () => throw new InvalidOperationException("boom"));

        Assert.AreEqual(1, barrier.FaultCount);
    }

    [TestMethod]
    public async Task GuardAsyncRecordsTheOperationAndTheException()
    {
        RecordingDiagnosticLog log = new();
        FaultBarrier barrier = new(log);

        await barrier.GuardAsync(
            "OnStartRecordingClicked",
            () => throw new InvalidOperationException("recorder was busy"));

        Assert.HasCount(1, log.Entries);
        Assert.AreEqual(DiagnosticSeverity.Fault, log.Entries[0].Severity);
        Assert.AreEqual("OnStartRecordingClicked", log.Entries[0].Source);
        StringAssert.Contains(log.Entries[0].ExceptionDetail!, "recorder was busy");
    }

    [TestMethod]
    public async Task GuardAsyncNotifiesTheUserWithAnActionableReport()
    {
        RecordingDiagnosticLog log = new();
        List<FaultReport> reports = [];
        FaultBarrier barrier = new(log, reports.Add);

        await barrier.GuardAsync(
            "OnGenerateCaptionsClicked",
            () => throw new IOException("the model file is locked"));

        Assert.HasCount(1, reports);
        Assert.AreEqual("OnGenerateCaptionsClicked", reports[0].Operation);
        StringAssert.Contains(reports[0].UserMessage, "the model file is locked");
    }

    [TestMethod]
    public async Task GuardAsyncDoesNothingWhenTheWorkSucceeds()
    {
        RecordingDiagnosticLog log = new();
        List<FaultReport> reports = [];
        FaultBarrier barrier = new(log, reports.Add);
        bool ran = false;

        await barrier.GuardAsync(
            "Healthy",
            () =>
            {
                ran = true;
                return Task.CompletedTask;
            });

        Assert.IsTrue(ran);
        Assert.AreEqual(0, barrier.FaultCount);
        Assert.IsEmpty(reports);
        Assert.IsEmpty(log.Entries);
    }

    [TestMethod]
    public async Task CancellationIsNotAFaultAndIsNotShownToTheUser()
    {
        RecordingDiagnosticLog log = new();
        List<FaultReport> reports = [];
        FaultBarrier barrier = new(log, reports.Add);

        await barrier.GuardAsync(
            "OnRefreshReadinessClicked",
            () => throw new OperationCanceledException());

        Assert.AreEqual(0, barrier.FaultCount);
        Assert.IsEmpty(reports);
        Assert.HasCount(1, log.Entries);
        Assert.AreEqual(DiagnosticSeverity.Info, log.Entries[0].Severity);
    }

    [TestMethod]
    public async Task AFailingNotifierCannotEscapeTheBarrier()
    {
        RecordingDiagnosticLog log = new();
        FaultBarrier barrier = new(
            log,
            _ => throw new InvalidOperationException("the UI is already torn down"));

        // If reporting a fault could itself throw, the barrier would become the
        // very crash it exists to prevent.
        await barrier.GuardAsync(
            "OnUnloaded",
            () => throw new IOException("primary failure"));

        Assert.AreEqual(1, barrier.FaultCount);
        Assert.HasCount(2, log.Entries);
        StringAssert.Contains(log.Entries[1].Message, "notifier");
    }

    [TestMethod]
    public async Task AFailingLogCannotEscapeTheBarrier()
    {
        FaultBarrier barrier = new(new ThrowingDiagnosticLog());

        await barrier.GuardAsync(
            "OnLoaded",
            () => throw new IOException("primary failure"));

        Assert.AreEqual(1, barrier.FaultCount);
    }

    [TestMethod]
    public void GuardSwallowsSynchronousExceptions()
    {
        RecordingDiagnosticLog log = new();
        FaultBarrier barrier = new(log);

        barrier.Guard(
            "OnCaptionSelectionChanged",
            () => throw new InvalidOperationException("boom"));

        Assert.AreEqual(1, barrier.FaultCount);
    }

    [TestMethod]
    public async Task ASynchronouslyThrowingFactoryIsStillGuarded()
    {
        RecordingDiagnosticLog log = new();
        FaultBarrier barrier = new(log);

        // The delegate throws before it ever returns a Task, which is a
        // different code path from a faulted Task.
        await barrier.GuardAsync(
            "ThrowsBeforeAwait",
            () => throw new InvalidOperationException("thrown synchronously"));

        Assert.AreEqual(1, barrier.FaultCount);
        Assert.HasCount(1, log.Entries);
    }

    [TestMethod]
    public async Task ATaskThatFaultsAFTERAnAwaitIsContained()
    {
        RecordingDiagnosticLog log = new();
        FaultBarrier barrier = new(log);

        // This is the case the class actually exists for: the delegate returns a Task
        // successfully and only then faults, so GuardAsync genuinely suspends. Every
        // other test throws before returning a Task, which never suspends at all.
        await barrier.GuardAsync(
            "FaultsAfterAwait",
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("faulted after suspending");
            });

        Assert.AreEqual(1, barrier.FaultCount);
        StringAssert.Contains(
            log.Entries[0].ExceptionDetail!,
            "faulted after suspending");
    }

    [TestMethod]
    public async Task CancellationAfterAnAwaitIsStillNotAFault()
    {
        RecordingDiagnosticLog log = new();
        List<FaultReport> reports = [];
        FaultBarrier barrier = new(log, reports.Add);

        await barrier.GuardAsync(
            "CancelledAfterAwait",
            async () =>
            {
                await Task.Yield();
                throw new OperationCanceledException();
            });

        Assert.AreEqual(0, barrier.FaultCount);
        Assert.IsEmpty(reports);
    }

    [TestMethod]
    public async Task FaultsFromManyThreadsAreAllCounted()
    {
        RecordingDiagnosticLog log = new();
        FaultBarrier barrier = new(log);
        const int WorkerCount = 8;
        const int PerWorker = 5;
        using Barrier start = new(WorkerCount);

        // Worker count is deliberately small and fixed. A rendezvous barrier with more
        // participants than the thread pool has threads blocks until the pool injects
        // more, roughly one per second - which turns this into a 20-second test that can
        // hang outright on a constrained machine.
        await Task.WhenAll(
            Enumerable.Range(0, WorkerCount).Select(worker =>
                Task.Run(async () =>
                {
                    start.SignalAndWait();
                    for (int index = 0; index < PerWorker; index++)
                    {
                        await barrier.GuardAsync(
                            $"Worker{worker}Operation{index}",
                            async () =>
                            {
                                await Task.Yield();
                                throw new InvalidOperationException("boom");
                            });
                    }
                })));

        Assert.AreEqual(WorkerCount * PerWorker, barrier.FaultCount);
        Assert.HasCount(WorkerCount * PerWorker, log.Entries);
    }

    [TestMethod]
    public async Task ABlankOperationNameCannotEscapeTheBarrier()
    {
        RecordingDiagnosticLog log = new();
        FaultBarrier barrier = new(log);

        // Validation that throws out of the barrier would, in an async void handler, be
        // the very process kill this class promises is structurally impossible.
        await barrier.GuardAsync(
            "   ",
            () => throw new InvalidOperationException("boom"));

        Assert.AreEqual(1, barrier.FaultCount);
        Assert.AreEqual("UnnamedOperation", log.Entries[0].Source);
    }

    [TestMethod]
    public async Task ANullWorkDelegateCannotEscapeTheBarrier()
    {
        RecordingDiagnosticLog log = new();
        FaultBarrier barrier = new(log);

        await barrier.GuardAsync("NullWork", null!);

        Assert.AreEqual(1, barrier.FaultCount);
    }

    [TestMethod]
    public void GuardWithANullActionCannotEscapeTheBarrier()
    {
        RecordingDiagnosticLog log = new();
        FaultBarrier barrier = new(log);

        barrier.Guard("NullAction", null!);

        Assert.AreEqual(1, barrier.FaultCount);
    }

    private sealed class RecordingDiagnosticLog : IDiagnosticLog
    {
        private readonly List<DiagnosticEntry> _entries = [];

        public IReadOnlyList<DiagnosticEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public void Write(
            DiagnosticSeverity severity,
            string source,
            string message,
            Exception? exception = null)
        {
            lock (_entries)
            {
                _entries.Add(
                    new DiagnosticEntry(
                        DateTimeOffset.UtcNow,
                        severity,
                        source,
                        message,
                        exception?.ToString()));
            }
        }
    }

    private sealed class ThrowingDiagnosticLog : IDiagnosticLog
    {
        public void Write(
            DiagnosticSeverity severity,
            string source,
            string message,
            Exception? exception = null) =>
            throw new IOException("the log itself is broken");
    }
}
