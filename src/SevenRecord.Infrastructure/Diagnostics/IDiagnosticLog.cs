namespace SevenRecord.Infrastructure.Diagnostics;

/// <summary>
/// How much a diagnostic entry should worry whoever reads the log.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Something happened that explains later behaviour. Not a problem.</summary>
    Info,

    /// <summary>Degraded, but the operation still produced a usable result.</summary>
    Warning,

    /// <summary>An operation failed. The user lost something, or nearly did.</summary>
    Fault,
}

/// <summary>
/// One recorded diagnostic event.
/// </summary>
public sealed record DiagnosticEntry(
    DateTimeOffset Timestamp,
    DiagnosticSeverity Severity,
    string Source,
    string Message,
    string? ExceptionDetail);

/// <summary>
/// A durable record of what the app did and how it failed.
/// </summary>
/// <remarks>
/// Implementations must never throw. A diagnostic sink that can fail turns every
/// error path into a second, worse error path.
/// </remarks>
public interface IDiagnosticLog
{
    void Write(
        DiagnosticSeverity severity,
        string source,
        string message,
        Exception? exception = null);
}
