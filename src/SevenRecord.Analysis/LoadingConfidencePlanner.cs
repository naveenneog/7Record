using SevenRecord.Domain.Input;
using SevenRecord.Domain.Audio;
using SevenRecord.Domain.Video;

namespace SevenRecord.Analysis;

public static class LoadingConfidencePlanner
{
    public static IReadOnlyList<LoadingSpeedEvent> Refine(
        IReadOnlyList<LoadingSpeedEvent> visualCandidates,
        CursorMetadataDocument? cursor,
        IReadOnlyList<IReadOnlyList<AudioSilenceInterval>> audioTracks,
        IReadOnlyList<AudioGapMetadata>? audioGaps = null)
    {
        ArgumentNullException.ThrowIfNull(visualCandidates);
        ArgumentNullException.ThrowIfNull(audioTracks);
        if (cursor is null ||
            cursor.Events.Count == 0 ||
            audioTracks.Count == 0)
        {
            return [];
        }

        List<LoadingSpeedEvent> accepted = [];
        foreach (LoadingSpeedEvent candidate in visualCandidates)
        {
            TimeSpan end = candidate.Start + candidate.Duration;
            if (audioGaps?.Any(gap =>
                    gap.Start < end &&
                    gap.Start + gap.Duration > candidate.Start) is true)
            {
                continue;
            }
            if (cursor is not null &&
                HasInteraction(cursor, candidate.Start, end))
            {
                continue;
            }
            if (audioTracks.Any(track =>
                    !IsSilent(track, candidate.Start, end)))
            {
                continue;
            }

            accepted.Add(candidate with { Confidence = 0.9 });
        }
        return accepted;
    }

    private static bool HasInteraction(
        CursorMetadataDocument cursor,
        TimeSpan start,
        TimeSpan end)
    {
        CursorMetadataEvent? baseline = cursor.Events
            .Where(item =>
                item.Kind is CursorEventKind.Move &&
                item.ProjectTime < start)
            .OrderByDescending(item => item.ProjectTime)
            .FirstOrDefault();
        CursorMetadataEvent[] events = cursor.Events
            .Where(item =>
                item.ProjectTime >= start &&
                item.ProjectTime <= end)
            .OrderBy(item => item.ProjectTime)
            .ToArray();
        if (baseline is null ||
            start - baseline.ProjectTime > TimeSpan.FromMilliseconds(500) ||
            events.Length == 0 ||
            end - events[^1].ProjectTime > TimeSpan.FromMilliseconds(500))
        {
            return true;
        }
        if (events.Any(item => item.Kind is CursorEventKind.Click))
        {
            return true;
        }
        CursorMetadataEvent[] moves = events
            .Where(item => item.Kind is CursorEventKind.Move)
            .ToArray();
        CursorMetadataEvent? previous = baseline;
        foreach (CursorMetadataEvent move in moves)
        {
            if (previous is not null)
            {
                double deltaX =
                    move.NormalizedX - previous.NormalizedX;
                double deltaY =
                    move.NormalizedY - previous.NormalizedY;
                if (Math.Sqrt(deltaX * deltaX + deltaY * deltaY) >= 0.003)
                {
                    return true;
                }
            }
            previous = move;
        }
        return false;
    }

    private static bool IsSilent(
        IReadOnlyList<AudioSilenceInterval> intervals,
        TimeSpan start,
        TimeSpan end)
    {
        double requiredSeconds = (end - start).TotalSeconds * 0.8;
        double coveredSeconds = intervals.Sum(interval =>
        {
            TimeSpan overlapStart = interval.Start > start
                ? interval.Start
                : start;
            TimeSpan overlapEnd = interval.End < end
                ? interval.End
                : end;
            return overlapEnd > overlapStart
                ? (overlapEnd - overlapStart).TotalSeconds
                : 0;
        });
        return coveredSeconds >= requiredSeconds;
    }
}
