namespace SevenRecord.Domain.Timeline;

public sealed record TimelineEditSlice(
    string Id,
    TimelineRange SourceRange);

public sealed record TimelineEditDocument(
    int SchemaVersion,
    IReadOnlyList<TimelineEditSlice> Slices)
{
    public TimeSpan OutputDuration =>
        TimeSpan.FromTicks(Slices.Sum(slice => slice.SourceRange.Duration.Ticks));

    public static TimelineEditDocument CreateDefault(TimeSpan duration) =>
        new(
            1,
            duration > TimeSpan.Zero
                ? [
                    new TimelineEditSlice(
                        "clip-1",
                        TimelineRange.FromStartAndDuration(
                            TimeSpan.Zero,
                            duration))
                ]
                : []);

    public TimelineEditDocument Validate(TimeSpan sourceDuration)
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidDataException(
                "The clip-edit format is unsupported.");
        }
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (TimelineEditSlice slice in Slices)
        {
            if (string.IsNullOrWhiteSpace(slice.Id) ||
                !ids.Add(slice.Id))
            {
                throw new InvalidDataException(
                    "Every clip edit requires a unique identifier.");
            }
            if (slice.SourceRange.Duration <= TimeSpan.Zero ||
                slice.SourceRange.End > sourceDuration)
            {
                throw new InvalidDataException(
                    $"Clip '{slice.Id}' has an invalid source range.");
            }
        }
        return this with { Slices = Slices.ToArray() };
    }
}

public readonly record struct TimelineMappedRange(
    string SliceId,
    TimelineRange SourceRange,
    TimelineRange OutputRange);

public static class TimelineEditMapper
{
    public static IReadOnlyList<TimelineMappedRange> MapRange(
        TimelineRange sourceRange,
        TimelineEditDocument edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        List<TimelineMappedRange> mapped = [];
        TimeSpan outputCursor = TimeSpan.Zero;
        foreach (TimelineEditSlice slice in edits.Slices)
        {
            TimeSpan start = sourceRange.Start > slice.SourceRange.Start
                ? sourceRange.Start
                : slice.SourceRange.Start;
            TimeSpan end = sourceRange.End < slice.SourceRange.End
                ? sourceRange.End
                : slice.SourceRange.End;
            if (end > start)
            {
                TimeSpan outputStart =
                    outputCursor + (start - slice.SourceRange.Start);
                mapped.Add(
                    new TimelineMappedRange(
                        slice.Id,
                        new TimelineRange(start, end),
                        TimelineRange.FromStartAndDuration(
                            outputStart,
                            end - start)));
            }
            outputCursor += slice.SourceRange.Duration;
        }
        return mapped;
    }
}
