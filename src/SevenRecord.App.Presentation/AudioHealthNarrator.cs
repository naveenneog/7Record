using SevenRecord.Audio.Windows;

namespace SevenRecord.App.Presentation;

/// <summary>
/// Turns audio capture telemetry into the sentences a user actually reads.
/// </summary>
/// <remarks>
/// Pure functions, deliberately free of any XAML type, so the wording and — more
/// importantly — the thresholds that decide whether to warn at all can be tested without a
/// UI thread. These previously lived as private statics inside a 3,900-line page, where
/// nothing could reach them.
/// </remarks>
public static class AudioHealthNarrator
{
    /// <summary>Drift beyond this is audible enough to tell the user about.</summary>
    public static TimeSpan DriftWarningThreshold { get; } =
        TimeSpan.FromMilliseconds(40);

    /// <summary>Missing audio beyond this means the recording has real gaps.</summary>
    public static TimeSpan MissingWarningThreshold { get; } =
        TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A one-line status for a single capture source.
    /// </summary>
    public static string Describe(string source, AudioCaptureHealth? health)
    {
        if (health is null)
        {
            return $"{source}: waiting for samples.";
        }

        string missing = health.TotalMissingDuration > TimeSpan.Zero
            ? $", {health.TotalMissingDuration.TotalMilliseconds:0.#} ms missing"
            : string.Empty;
        string queueOverflows = health.QueueOverflows > 0
            ? $", {health.QueueOverflows} queue overflows"
            : string.Empty;
        return
            $"{source}: {health.Drift.Drift.TotalMilliseconds:+0.0;-0.0;0.0} ms drift " +
            $"({health.Drift.PartsPerMillion:+0;-0;0} ppm), " +
            $"{health.Discontinuities} discontinuities{missing}{queueOverflows}.";
    }

    /// <summary>
    /// Whether this source is far enough out of sync to warn about.
    /// </summary>
    public static bool HasSyncRisk(AudioCaptureHealth? health) =>
        health is not null &&
        (health.Drift.Exceeds(DriftWarningThreshold) ||
         health.Discontinuities > 0 ||
         health.QueueOverflows > 0 ||
         health.TotalMissingDuration >= MissingWarningThreshold);

    /// <summary>
    /// The combined warning shown while recording, naming only the sources at risk.
    /// </summary>
    public static string BuildWarning(
        AudioCaptureHealth? microphoneHealth,
        AudioCaptureHealth? systemAudioHealth)
    {
        List<string> details = [];
        if (HasSyncRisk(microphoneHealth))
        {
            details.Add(Detail("Mic", microphoneHealth!));
        }

        if (HasSyncRisk(systemAudioHealth))
        {
            details.Add(Detail("System", systemAudioHealth!));
        }

        return details.Count == 0
            ? "Audio sync risk detected. Consider restarting capture."
            : $"Audio sync risk detected: {string.Join("; ", details)}. " +
              "Consider restarting capture.";
    }

    private static string Detail(string source, AudioCaptureHealth health) =>
        $"{source} {health.Drift.Drift.TotalMilliseconds:+0.0;-0.0;0.0} ms drift, " +
        $"{health.Drift.PartsPerMillion:+0;-0;0} ppm, " +
        $"{health.Discontinuities} discontinuities, " +
        $"{health.TotalMissingDuration.TotalMilliseconds:0.#} ms missing, " +
        $"{health.QueueOverflows} queue overflows";
}
