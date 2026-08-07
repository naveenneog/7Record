using SevenRecord.Infrastructure.Jobs;

namespace SevenRecord.Infrastructure.Tests;

/// <summary>
/// Covers the registry that makes shutdown own every background job.
/// </summary>
/// <remarks>
/// The defect this exists for: export, transcription, edited-preview rendering and project
/// post-processing were all started fire-and-forget with no cancellation token and no
/// record of them anywhere. Closing the window neither cancelled nor awaited them, so an
/// FFmpeg worker process could outlive the app that started it and keep writing to a file
/// the user believed was finished.
/// </remarks>
[TestClass]
public sealed class BackgroundJobRegistryTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ARunningJobIsTracked()
    {
        using BackgroundJobRegistry registry = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task job = registry.RunAsync("export", _ => release.Task);

        Assert.AreEqual(1, registry.ActiveCount);
        release.SetResult();
        await job;
        Assert.AreEqual(0, registry.ActiveCount);
    }

    [TestMethod]
    public async Task AFaultedJobStopsBeingTrackedAndSurfacesToItsCaller()
    {
        using BackgroundJobRegistry registry = new();

        Task job = registry.RunAsync(
            "export",
            _ => throw new InvalidOperationException("render failed"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => job);
        Assert.AreEqual(0, registry.ActiveCount);
    }

    [TestMethod]
    public async Task AJobResultIsReturnedToItsCaller()
    {
        using BackgroundJobRegistry registry = new();

        string result = await registry.RunAsync(
            "captions",
            _ => Task.FromResult("done"));

        Assert.AreEqual("done", result);
    }

    [TestMethod]
    public async Task DrainingWithNoJobsCompletesImmediately()
    {
        using BackgroundJobRegistry registry = new();

        BackgroundJobDrainResult result = await registry.DrainAsync(DrainTimeout);

        Assert.IsTrue(result.Completed);
        Assert.IsEmpty(result.UnfinishedJobs);
    }

    [TestMethod]
    public async Task DrainingCancelsRunningJobsAndWaitsForThem()
    {
        using BackgroundJobRegistry registry = new();
        TaskCompletionSource observed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task job = registry.RunAsync(
            "export",
            async token =>
            {
                using CancellationTokenRegistration _ =
                    token.Register(() => observed.TrySetResult());
                await Task.Delay(Timeout.Infinite, token);
            });

        BackgroundJobDrainResult result = await registry.DrainAsync(DrainTimeout);

        Assert.IsTrue(observed.Task.IsCompleted, "the job was never told to cancel");
        Assert.IsTrue(result.Completed);
        Assert.IsTrue(job.IsCompleted, "drain returned before the job finished");
        Assert.AreEqual(0, registry.ActiveCount);
    }

    [TestMethod]
    public async Task DrainingReportsAJobThatRefusesToStopRatherThanHanging()
    {
        using BackgroundJobRegistry registry = new();
        using ManualResetEventSlim stuck = new(false);

        // A job ignoring its token models a wedged FFmpeg worker. The window must still
        // close: an unclosable app is worse than a leaked process.
        Task job = registry.RunAsync(
            "stuck-export",
            _ => Task.Run(() => stuck.Wait(TimeSpan.FromSeconds(30))));

        BackgroundJobDrainResult result =
            await registry.DrainAsync(TimeSpan.FromMilliseconds(250));

        Assert.IsFalse(result.Completed);
        Assert.HasCount(1, result.UnfinishedJobs);
        Assert.AreEqual("stuck-export", result.UnfinishedJobs[0]);

        stuck.Set();
        await job;
    }

    [TestMethod]
    public async Task DrainingWaitsForEveryJobNotJustTheFirst()
    {
        using BackgroundJobRegistry registry = new();
        int completed = 0;

        for (int index = 0; index < 5; index++)
        {
            _ = registry.RunAsync(
                $"job{index}",
                async token =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref completed);
                        throw;
                    }
                });
        }

        BackgroundJobDrainResult result = await registry.DrainAsync(DrainTimeout);

        Assert.IsTrue(result.Completed);
        Assert.AreEqual(5, completed);
    }

    [TestMethod]
    public async Task AJobStartedAfterDrainingIsCancelledAndNeverRuns()
    {
        using BackgroundJobRegistry registry = new();
        await registry.DrainAsync(DrainTimeout);
        bool ran = false;

        Task job = registry.RunAsync(
            "late",
            _ =>
            {
                ran = true;
                return Task.CompletedTask;
            });

        // Cancellation rather than an exception: the FaultBarrier treats a cancelled
        // operation as expected, so a job racing shutdown does not look like a crash.
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => job);
        Assert.IsFalse(ran, "work must not start once the registry is draining");
    }

    [TestMethod]
    public async Task AnExternalCancellationStopsOneJobWithoutDisturbingOthers()
    {
        using BackgroundJobRegistry registry = new();
        using CancellationTokenSource external = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task cancelled = registry.RunAsync(
            "cancelled",
            token => Task.Delay(Timeout.Infinite, token),
            external.Token);
        Task survivor = registry.RunAsync("survivor", _ => release.Task);

        await external.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled);

        Assert.IsFalse(survivor.IsCompleted, "an unrelated job must keep running");
        Assert.AreEqual(1, registry.ActiveCount);
        release.SetResult();
        await survivor;
    }

    [TestMethod]
    public async Task DrainingIsIdempotent()
    {
        using BackgroundJobRegistry registry = new();

        BackgroundJobDrainResult first = await registry.DrainAsync(DrainTimeout);
        BackgroundJobDrainResult second = await registry.DrainAsync(DrainTimeout);

        Assert.IsTrue(first.Completed);
        Assert.IsTrue(second.Completed);
    }

    [TestMethod]
    public async Task JobsRegisteredConcurrentlyAreAllTracked()
    {
        using BackgroundJobRegistry registry = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        const int JobCount = 8;
        using Barrier start = new(JobCount);

        // Task.Run plus a rendezvous, because a collection expression over a lazy Select
        // materialises synchronously on the calling thread: without this the registrations
        // run one after another and contend for nothing.
        Task[] jobs = [.. Enumerable.Range(0, JobCount).Select(index => Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(10));
            await registry.RunAsync($"job{index}", _ => release.Task);
        }))];

        while (registry.ActiveCount < JobCount)
        {
            await Task.Delay(10);
        }

        Assert.AreEqual(JobCount, registry.ActiveCount);
        release.SetResult();
        await Task.WhenAll(jobs);
        Assert.AreEqual(0, registry.ActiveCount);
    }

    [TestMethod]
    public async Task ADrainRecordsUnfinishedJobsSoTheyCanBeInvestigated()
    {
        RecordingDiagnosticLog log = new();
        using BackgroundJobRegistry registry = new(log);
        using ManualResetEventSlim stuck = new(false);

        Task job = registry.RunAsync(
            "stuck-export",
            _ => Task.Run(() => stuck.Wait(TimeSpan.FromSeconds(30))));

        await registry.DrainAsync(TimeSpan.FromMilliseconds(250));

        Assert.IsTrue(
            log.Entries.Any(entry =>
                entry.Message.Contains("stuck-export", StringComparison.Ordinal)),
            "a job that outlived shutdown must leave evidence");

        stuck.Set();
        await job;
    }

    [TestMethod]
    public async Task AThrowingCancellationCallbackDoesNotTakeTheDrainDown()
    {
        RecordingDiagnosticLog log = new();
        using BackgroundJobRegistry registry = new(log);

        Task job = registry.RunAsync(
            "hostile",
            async token =>
            {
                // CancelAsync runs registered callbacks and faults its task if one throws.
                // Letting that escape would propagate out of shutdown, and the window close
                // path caches its shutdown task forever - so the app could never be quit.
                token.Register(() => throw new InvalidOperationException("callback"));
                await Task.Delay(Timeout.Infinite, token);
            });

        BackgroundJobDrainResult result = await registry.DrainAsync(DrainTimeout);

        Assert.IsTrue(result.Completed);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => job);
    }

    [TestMethod]
    public async Task UnfinishedJobsNamesOnlyTheJobsThatAreActuallyStuck()
    {
        using BackgroundJobRegistry registry = new();
        using ManualResetEventSlim stuck = new(false);

        Task willStop = registry.RunAsync(
            "co-operative",
            token => Task.Delay(Timeout.Infinite, token));
        Task wontStop = registry.RunAsync(
            "stubborn",
            _ => Task.Run(() => stuck.Wait(TimeSpan.FromSeconds(30))));

        BackgroundJobDrainResult result =
            await registry.DrainAsync(TimeSpan.FromMilliseconds(400));

        Assert.IsFalse(result.Completed);
        Assert.HasCount(1, result.UnfinishedJobs);
        Assert.AreEqual("stubborn", result.UnfinishedJobs[0]);
        Assert.IsTrue(willStop.IsCompleted, "the co-operative job should have stopped");

        stuck.Set();
        await wontStop;
    }

    [TestMethod]
    public async Task RegistrationsRacingADrainAreEitherTrackedOrRefusedNeverBoth()
    {
        using BackgroundJobRegistry registry = new();
        const int Racers = 8;
        using Barrier start = new(Racers + 1);
        int started = 0;
        int refused = 0;

        Task[] racers = [.. Enumerable.Range(0, Racers).Select(_ => Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(10));
            try
            {
                await registry.RunAsync(
                    "racer",
                    token =>
                    {
                        Interlocked.Increment(ref started);
                        return Task.Delay(Timeout.Infinite, token);
                    });
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref refused);
            }
        }))];

        Task<BackgroundJobDrainResult> drain = Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(10));
            return await registry.DrainAsync(DrainTimeout);
        });

        await Task.WhenAll(racers);
        BackgroundJobDrainResult result = await drain;

        // Whatever the interleaving, every job is accounted for exactly once and the
        // drain still completes - nothing is stranded in the registry.
        Assert.AreEqual(Racers, started + refused);
        Assert.IsTrue(result.Completed);
        Assert.AreEqual(0, registry.ActiveCount);
    }

    private sealed class RecordingDiagnosticLog : Diagnostics.IDiagnosticLog
    {
        private readonly List<Diagnostics.DiagnosticEntry> _entries = [];

        public IReadOnlyList<Diagnostics.DiagnosticEntry> Entries
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
            Diagnostics.DiagnosticSeverity severity,
            string source,
            string message,
            Exception? exception = null)
        {
            lock (_entries)
            {
                _entries.Add(
                    new Diagnostics.DiagnosticEntry(
                        DateTimeOffset.UtcNow,
                        severity,
                        source,
                        message,
                        exception?.ToString()));
            }
        }
    }
}
