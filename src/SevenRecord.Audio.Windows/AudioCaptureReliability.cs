namespace SevenRecord.Audio.Windows;

public static class AudioCaptureReliability
{
    public static readonly TimeSpan MissingDurationWarning =
        TimeSpan.FromMilliseconds(100);

    public static bool IsAtRisk(AudioCaptureHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return health.Discontinuities > 0 ||
            health.TotalMissingDuration >= MissingDurationWarning ||
            health.QueueOverflows > 0;
    }

    public static string Describe(AudioCaptureHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return
            $"{health.Discontinuities} discontinuities, " +
            $"{health.TotalMissingDuration.TotalMilliseconds:0.#} ms missing, " +
            $"{health.QueueOverflows} queue overflows";
    }
}
