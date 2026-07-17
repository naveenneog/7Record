namespace SevenRecord.Capture.Abstractions;

public readonly record struct ClockDriftEstimate(
    TimeSpan Drift,
    TimeSpan ObservedDuration,
    double PartsPerMillion)
{
    public bool Exceeds(TimeSpan tolerance) => Drift.Duration() > tolerance;
}

public sealed class ClockDriftEstimator
{
    private readonly object _gate = new();
    private TimeSpan? _firstProjectTime;
    private TimeSpan? _firstSourceTime;

    public ClockDriftEstimate AddSample(
        TimeSpan projectTime,
        long sourceSamplePosition,
        int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(projectTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceSamplePosition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        TimeSpan sourceTime = TimeSpan.FromSeconds(sourceSamplePosition / (double)sampleRate);

        lock (_gate)
        {
            _firstProjectTime ??= projectTime;
            _firstSourceTime ??= sourceTime;

            TimeSpan projectElapsed = projectTime - _firstProjectTime.Value;
            TimeSpan sourceElapsed = sourceTime - _firstSourceTime.Value;
            TimeSpan drift = sourceElapsed - projectElapsed;
            double partsPerMillion = projectElapsed == TimeSpan.Zero
                ? 0
                : drift.TotalSeconds / projectElapsed.TotalSeconds * 1_000_000d;

            return new ClockDriftEstimate(drift, projectElapsed, partsPerMillion);
        }
    }
}
