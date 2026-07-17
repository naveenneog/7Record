using SevenRecord.Domain.Input;

namespace SevenRecord.Analysis;

public static class CursorZoomPlanner
{
    public static IReadOnlyList<CursorZoomEvent> CreatePlan(
        CursorMetadataDocument document,
        TimeSpan? duration = null,
        double scale = 1.8)
    {
        ArgumentNullException.ThrowIfNull(document);
        TimeSpan zoomDuration = duration ?? TimeSpan.FromSeconds(1.2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            zoomDuration,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(scale, 1);

        return document.Events
            .Where(item => item.Kind is CursorEventKind.Click)
            .Select(item => new CursorZoomEvent(
                Guid.NewGuid().ToString("N"),
                item.ProjectTime > TimeSpan.FromMilliseconds(200)
                    ? item.ProjectTime - TimeSpan.FromMilliseconds(200)
                    : TimeSpan.Zero,
                zoomDuration,
                item.NormalizedX,
                item.NormalizedY,
                scale,
                1))
            .ToArray();
    }
}
