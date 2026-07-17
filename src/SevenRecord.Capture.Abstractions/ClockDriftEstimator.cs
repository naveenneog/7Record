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
    private double _sumProjectSquared;
    private double _sumProjectTimesSource;

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
            if (projectElapsed > TimeSpan.Zero)
            {
                double projectSeconds = projectElapsed.TotalSeconds;
                double sourceSeconds = sourceElapsed.TotalSeconds;
                _sumProjectSquared += projectSeconds * projectSeconds;
                _sumProjectTimesSource += projectSeconds * sourceSeconds;
            }

            double clockRate = _sumProjectSquared == 0
                ? 1
                : _sumProjectTimesSource / _sumProjectSquared;
            double partsPerMillion = (clockRate - 1d) * 1_000_000d;
            TimeSpan drift = TimeSpan.FromSeconds(
                (clockRate - 1d) * projectElapsed.TotalSeconds);

            return new ClockDriftEstimate(drift, projectElapsed, partsPerMillion);
        }
    }
}
