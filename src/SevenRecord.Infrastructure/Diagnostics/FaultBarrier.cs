namespace SevenRecord.Infrastructure.Diagnostics;

/// <summary>
/// A fault that was contained, described for the user, and recorded.
/// </summary>
/// <param name="Operation">The named operation that failed, for the log and for support.</param>
/// <param name="Exception">The original exception, unwrapped and unmodified.</param>
/// <param name="UserMessage">Something worth showing a human.</param>
public sealed record FaultReport(
    string Operation,
    Exception Exception,
    string UserMessage);

/// <summary>
/// Runs work so that a failure is <b>recorded and reported</b> instead of escaping.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a specific, measured hazard. 7Record's UI layer has 33
/// <c>async void</c> event handlers. An exception escaping an <c>async void</c> method is
/// rethrown on the captured synchronisation context, which for WinUI means the UI thread,
/// which means the process dies. In a Release build there is no handler at all, so it dies
/// silently — and if a recording is in progress, the user loses it.
/// </para>
/// <para>
/// Wrapping a handler body in <see cref="GuardAsync"/> makes that structurally impossible
/// for that handler, and puts the invariant in one reviewable place instead of relying on
/// 33 separate authors remembering it.
/// </para>
/// <para>
/// This is a backstop, not an excuse. Code that can predict its own failure should still
/// handle it locally, where the surrounding state is understood.
/// </para>
/// </remarks>
public sealed class FaultBarrier
{
    private readonly IDiagnosticLog _log;
    private readonly Action<FaultReport>? _onFault;
    private int _faultCount;

    public FaultBarrier(IDiagnosticLog log, Action<FaultReport>? onFault = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _onFault = onFault;
    }

    /// <summary>How many faults this barrier has contained since construction.</summary>
    public int FaultCount => Volatile.Read(ref _faultCount);

    /// <summary>
    /// Awaits <paramref name="work"/> and guarantees no exception escapes.
    /// </summary>
    public async Task GuardAsync(string operation, Func<Task> work)
    {
        string name = Describe(operation);
        try
        {
            // Argument checks live inside the guard, not in front of it. A barrier whose
            // own validation can throw is not a barrier - and in an async void handler
            // that throw is a process kill, which is the whole thing being prevented.
            ArgumentNullException.ThrowIfNull(work);

            // work() is invoked inside the try on purpose: a delegate that throws before
            // returning a Task is a different code path from a faulted Task, and both
            // must be contained.
            await work().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            RecordCancellation(name);
        }
        catch (Exception exception)
        {
            Contain(name, exception);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> and guarantees no exception escapes.
    /// </summary>
    public void Guard(string operation, Action work)
    {
        string name = Describe(operation);
        try
        {
            ArgumentNullException.ThrowIfNull(work);
            work();
        }
        catch (OperationCanceledException)
        {
            RecordCancellation(name);
        }
        catch (Exception exception)
        {
            Contain(name, exception);
        }
    }

    /// <summary>
    /// Never throws, so that a missing operation name degrades the log rather than the app.
    /// </summary>
    private static string Describe(string operation) =>
        string.IsNullOrWhiteSpace(operation) ? "UnnamedOperation" : operation;

    private void RecordCancellation(string operation) =>
        SafeLog(
            DiagnosticSeverity.Info,
            operation,
            "Operation was cancelled.",
            exception: null);

    private void Contain(string operation, Exception exception)
    {
        Interlocked.Increment(ref _faultCount);
        SafeLog(
            DiagnosticSeverity.Fault,
            operation,
            "Operation failed and was contained.",
            exception);

        if (_onFault is null)
        {
            return;
        }

        try
        {
            _onFault(
                new FaultReport(
                    operation,
                    exception,
                    DescribeForUser(exception)));
        }
        catch (Exception notifierFailure)
        {
            // Reporting a fault must not become a fault. This happens for real: a handler
            // faults during shutdown and the notifier tries to touch a torn-down UI.
            SafeLog(
                DiagnosticSeverity.Fault,
                operation,
                "The fault notifier itself failed.",
                notifierFailure);
        }
    }

    private void SafeLog(
        DiagnosticSeverity severity,
        string source,
        string message,
        Exception? exception)
    {
        try
        {
            _log.Write(severity, source, message, exception);
        }
        catch (Exception)
        {
            // The log is the last line of defence; if it is broken there is nowhere left
            // to report that, and throwing from here would defeat the entire barrier.
        }
    }

    private static string DescribeForUser(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException =>
                $"7Record does not have permission to complete that: {exception.Message} " +
                "The app kept running and your recording is unaffected.",
            IOException =>
                $"A file could not be read or written: {exception.Message} " +
                "The app kept running and your recording is unaffected.",
            _ =>
                $"{exception.Message} " +
                "The app kept running; details were written to the diagnostics log.",
        };
}
