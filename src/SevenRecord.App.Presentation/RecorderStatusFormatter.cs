using SevenRecord.Capture.Abstractions;
using SevenRecord.Domain.Audio;

namespace SevenRecord.App.Presentation;

/// <summary>
/// Pure recorder logic that the page reads from but does not own.
/// </summary>
public static class RecorderStatusFormatter
{
    /// <summary>
    /// How a single track's mix setting reads in the UI.
    /// </summary>
    public static string DescribeMix(AudioMixSettings mix)
    {
        ArgumentNullException.ThrowIfNull(mix);
        return mix.IsMuted
            ? "muted"
            : $"{mix.GainDecibels:+0.0;-0.0;0.0} dB";
    }

    /// <summary>
    /// Combines two capture-source states into the one worth showing.
    /// </summary>
    /// <remarks>
    /// The overall indicator must reflect the worst source, not the last one written.
    /// Reporting "Ready" while a source is in error is how a user records for an hour and
    /// only then finds out the microphone was never captured.
    /// </remarks>
    public static CaptureSourceState WorstState(
        CaptureSourceState first,
        CaptureSourceState second) =>
        Severity(first) >= Severity(second) ? first : second;

    /// <summary>
    /// Ranks a state so two can be compared. Error and Unavailable rank equally: both mean
    /// the source produced nothing.
    /// </summary>
    public static int Severity(CaptureSourceState state) =>
        state switch
        {
            CaptureSourceState.Error => 3,
            CaptureSourceState.Unavailable => 3,
            CaptureSourceState.Warning => 2,
            _ => 1,
        };

    /// <summary>
    /// Builds the path for a new recording project.
    /// </summary>
    /// <remarks>
    /// The name carries a sortable timestamp plus a GUID: the timestamp is what
    /// <see cref="RecorderTextFormatter.FormatProjectDisplayName"/> turns back into a
    /// readable date, and the GUID is what stops two recordings started in the same second
    /// from colliding.
    /// </remarks>
    public static string CreateProjectRoot()
    {
        string videos = Environment.GetFolderPath(
            Environment.SpecialFolder.MyVideos);
        string projectName =
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        return Path.Combine(videos, "7Record", "Projects", projectName);
    }
}
