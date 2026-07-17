namespace SevenRecord.Capture.Abstractions;

public sealed class RecordingPauseController
{
    private readonly object _gate = new();
    private TimeSpan? _pausedAt;
    private TimeSpan _totalPaused;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _pausedAt is not null;
            }
        }
    }

    public void Pause(TimeSpan rawProjectTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rawProjectTime, TimeSpan.Zero);
        lock (_gate)
        {
            if (_pausedAt is not null)
            {
                throw new InvalidOperationException("Recording is already paused.");
            }

            _pausedAt = rawProjectTime;
        }
    }

    public void Resume(TimeSpan rawProjectTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rawProjectTime, TimeSpan.Zero);
        lock (_gate)
        {
            TimeSpan pausedAt = _pausedAt ??
                throw new InvalidOperationException("Recording is not paused.");
            ArgumentOutOfRangeException.ThrowIfLessThan(rawProjectTime, pausedAt);
            _totalPaused += rawProjectTime - pausedAt;
            _pausedAt = null;
        }
    }

    public TimeSpan Map(TimeSpan rawProjectTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rawProjectTime, TimeSpan.Zero);
        lock (_gate)
        {
            TimeSpan effectiveRawTime = _pausedAt is TimeSpan pausedAt
                ? TimeSpan.FromTicks(Math.Min(rawProjectTime.Ticks, pausedAt.Ticks))
                : rawProjectTime;
            return effectiveRawTime - _totalPaused;
        }
    }
}
