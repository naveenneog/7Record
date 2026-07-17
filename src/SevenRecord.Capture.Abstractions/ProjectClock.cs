using System.Diagnostics;

namespace SevenRecord.Capture.Abstractions;

public readonly record struct QpcTimestamp
{
    public QpcTimestamp(long ticks, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);

        Ticks = ticks;
        Frequency = frequency;
    }

    public long Ticks { get; }

    public long Frequency { get; }

    public TimeSpan SystemRelativeTime => TimeSpan.FromSeconds(Ticks / (double)Frequency);

    public static QpcTimestamp Now() => new(Stopwatch.GetTimestamp(), Stopwatch.Frequency);

    public static QpcTimestamp FromSystemRelativeTime(TimeSpan time, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(time, TimeSpan.Zero);

        long ticks = checked((long)Math.Round(time.TotalSeconds * frequency));
        return new QpcTimestamp(ticks, frequency);
    }
}

public sealed class ProjectClock
{
    public ProjectClock(QpcTimestamp origin)
    {
        Origin = origin;
    }

    public QpcTimestamp Origin { get; }

    public static ProjectClock StartNew() => new(QpcTimestamp.Now());

    public TimeSpan Normalize(QpcTimestamp timestamp)
    {
        if (timestamp.Frequency != Origin.Frequency)
        {
            return NormalizeSystemRelativeTime(timestamp.SystemRelativeTime);
        }

        long elapsedTicks = timestamp.Ticks - Origin.Ticks;
        if (elapsedTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestamp), "A capture timestamp cannot precede the project clock.");
        }

        return TimeSpan.FromSeconds(elapsedTicks / (double)Origin.Frequency);
    }

    public TimeSpan NormalizeSystemRelativeTime(TimeSpan systemRelativeTime)
    {
        TimeSpan elapsed = systemRelativeTime - Origin.SystemRelativeTime;
        if (elapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(systemRelativeTime),
                "A capture timestamp cannot precede the project clock.");
        }

        return elapsed;
    }
}
