using System.Text.Json.Serialization;

namespace SevenRecord.Domain.Timeline;

public readonly record struct TimelineRange
{
    [JsonConstructor]
    public TimelineRange(TimeSpan start, TimeSpan end)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        Start = start;
        End = end;
    }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public TimeSpan Duration => End - Start;

    public static TimelineRange FromStartAndDuration(TimeSpan start, TimeSpan duration)
    {
        if (start < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(start), "Timeline positions cannot be negative.");
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Timeline durations cannot be negative.");
        }

        return new TimelineRange(start, start + duration);
    }

    public bool Contains(TimeSpan position) => position >= Start && position < End;

    public TimelineRange Shift(TimeSpan delta)
    {
        TimeSpan shiftedStart = Start + delta;
        if (shiftedStart < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "A shifted range cannot start before the timeline.");
        }

        return new TimelineRange(shiftedStart, End + delta);
    }
}
