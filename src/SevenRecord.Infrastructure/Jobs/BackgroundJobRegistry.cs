using SevenRecord.Infrastructure.Diagnostics;

namespace SevenRecord.Infrastructure.Jobs;

/// <summary>
/// The outcome of draining the registry at shutdown.
/// </summary>
/// <param name="Completed">Whether every job finished within the allowed time.</param>
/// <param name="UnfinishedJobs">
/// Names of jobs that were still running when the wait expired. Non-empty means something
/// outlived the app.
/// </param>
public sealed record BackgroundJobDrainResult(
    bool Completed,
    IReadOnlyList<string> UnfinishedJobs);

/// <summary>
/// Owns the lifetime of long-running background work so that shutdown can cancel and await
/// all of it.
/// </summary>
/// <remarks>
/// <para>
/// 7Record starts several jobs that outlive the click that began them: an MP4 export, an
/// offline transcription, an edited-preview render, project post-processing. Each is driven
/// by an out-of-process worker running FFmpeg or Whisper.
/// </para>
/// <para>
/// Before this existed they were all started fire-and-forget, with no cancellation token
/// and no record of them anywhere. Closing the window neither cancelled nor awaited them,
/// so a worker process could outlive the app that started it and keep writing to a file the
/// user believed was finished.
/// </para>
/// <para>
/// The drain is deliberately <b>bounded</b>. A job that ignores its token must not be able
/// to make the window unclosable — an app you cannot quit is a worse bug than a leaked
/// process, and the leak is at least recorded.
/// </para>
/// </remarks>
public sealed class BackgroundJobRegistry : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, TrackedJob> _jobs = [];
    private readonly IDiagnosticLog? _log;
    private long _nextId;
    private bool _draining;
    private bool _disposed;

    public BackgroundJobRegistry(IDiagnosticLog? log = null) => _log = log;

    /// <summary>How many jobs are currently running.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Count;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> as a tracked job.
    /// </summary>
    /// <param name="name">Identifies the job in diagnostics when it fails to drain.</param>
    /// <param name="work">
    /// Receives a token that is signalled either by <paramref name="externalCancellation"/>
    /// or by shutdown. Honouring it is what makes a clean drain possible.
    /// </param>
    /// <param name="externalCancellation">Caller's own cancellation, if any.</param>
    public Task RunAsync(
        string name,
        Func<CancellationToken, Task> work,
        CancellationToken externalCancellation = default) =>
        RunAsync<object?>(
            name,
            async token =>
            {
                await work(token).ConfigureAwait(false);
                return null;
            },
            externalCancellation);

    /// <summary>
    /// Runs <paramref name="work"/> as a tracked job and returns its result.
    /// </summary>
    public async Task<T> RunAsync<T>(
        string name,
        Func<CancellationToken, Task<T>> work,
        CancellationToken externalCancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);

        CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        long id;

        lock (_gate)
        {
            if (_draining || _disposed)
            {
                linked.Dispose();
                id = -1;
            }
            else
            {
                id = _nextId++;
                _jobs[id] = new TrackedJob(name, linked);
            }
        }

        if (id < 0)
        {
            // Cancellation rather than a fault: a job that races shutdown is expected, and
            // callers route through FaultBarrier, which treats cancellation as normal
            // rather than as a crash to report.
            return await Task.FromCanceled<T>(new CancellationToken(true))
                .ConfigureAwait(false);
        }

        try
        {
            return await work(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _jobs.Remove(id);
            }

            linked.Dispose();
        }
    }

    /// <summary>
    /// Cancels every running job and waits, up to <paramref name="timeout"/>, for them to
    /// finish. Refuses new jobs from this point on.
    /// </summary>
    public async Task<BackgroundJobDrainResult> DrainAsync(TimeSpan timeout)
    {
        TrackedJob[] running;
        lock (_gate)
        {
            _draining = true;
            running = [.. _jobs.Values];
        }

        foreach (TrackedJob job in running)
        {
            try
            {
                await job.Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Deliberately total. CancelAsync runs registered cancellation callbacks
                // and faults its task if any of them throws. Letting that escape would
                // propagate out of shutdown, and the window close path caches its
                // shutdown task forever - so one badly-behaved callback would make the
                // app permanently unquittable, which is the precise outcome this class
                // promises to prevent.
                _log?.Write(
                    DiagnosticSeverity.Warning,
                    nameof(BackgroundJobRegistry),
                    $"Cancelling job '{job.Name}' threw; continuing the drain.",
                    exception);
            }
        }

        // Poll rather than await the job tasks: their Task objects belong to their callers,
        // and a caller may already be awaiting them. Sampling the registry avoids observing
        // another party's exception and turning a clean shutdown into an unobserved fault.
        using CancellationTokenSource deadline = new(timeout);
        try
        {
            while (ActiveCount > 0)
            {
                await Task.Delay(15, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Fall through to report whatever is still running.
        }

        string[] unfinished;
        lock (_gate)
        {
            unfinished = [.. _jobs.Values.Select(job => job.Name)];
        }

        if (unfinished.Length > 0)
        {
            _log?.Write(
                DiagnosticSeverity.Fault,
                nameof(BackgroundJobRegistry),
                "Shutdown proceeded while background work was still running: " +
                string.Join(", ", unfinished) +
                ". A worker process may have outlived the app.");
        }

        return new BackgroundJobDrainResult(unfinished.Length == 0, unfinished);
    }

    public void Dispose()
    {
        TrackedJob[] running;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _draining = true;
            running = [.. _jobs.Values];
        }

        foreach (TrackedJob job in running)
        {
            try
            {
                job.Cancellation.Cancel();
            }
            catch (Exception)
            {
                // Same reasoning as the drain: a throwing cancellation callback must not
                // escape disposal.
            }
        }
    }

    private sealed record TrackedJob(string Name, CancellationTokenSource Cancellation);
}
