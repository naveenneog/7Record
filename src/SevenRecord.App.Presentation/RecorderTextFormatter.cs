using System.Globalization;

namespace SevenRecord.App.Presentation;

/// <summary>
/// Formatting helpers for names and messages the recorder shows.
/// </summary>
public static class RecorderTextFormatter
{
    private const string ProjectTimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Turns a timestamped project folder name into something readable, and leaves any
    /// other name untouched.
    /// </summary>
    public static string FormatProjectDisplayName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length >= ProjectTimestampFormat.Length &&
            DateTime.TryParseExact(
                name[..ProjectTimestampFormat.Length],
                ProjectTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime timestamp))
        {
            return $"Recording · {timestamp:MMM d, yyyy} · {timestamp:h:mm tt}";
        }

        return name;
    }

    /// <summary>
    /// Appends the recorder's own explanation for a component that failed to start, or
    /// falls back when it reported no specific reason.
    /// </summary>
    /// <remarks>
    /// Takes plain component/message pairs rather than the recorder's issue type on
    /// purpose. Depending on <c>SevenRecord.Recording.Windows</c> would drag Win2D and the
    /// Windows App SDK into this assembly and into anything that tests it, for the sake of
    /// reading two strings.
    /// </remarks>
    public static string IssueMessage(
        IEnumerable<(string Component, string Message)> issues,
        string component,
        string fallback)
    {
        ArgumentNullException.ThrowIfNull(issues);
        foreach ((string candidate, string message) in issues)
        {
            if (candidate.StartsWith(component, StringComparison.OrdinalIgnoreCase))
            {
                return $"{fallback} {message}";
            }
        }

        return fallback;
    }
}
