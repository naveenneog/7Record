using SevenRecord.Domain.Timeline;

namespace SevenRecord.Export;

public enum ExportAspectRatioPreset
{
    Landscape1080p,
    Portrait1080p,
    Square1080p,
}

public sealed record RenderCanvas(
    int Width,
    int Height);

public sealed record RenderPlan(
    string ProjectPath,
    TimeSpan Duration,
    ExportAspectRatioPreset Preset,
    RenderCanvas Canvas,
    IReadOnlyList<TimelineClip> Clips,
    IReadOnlyList<TimelineAutomationEvent> Automation)
{
    public IReadOnlyList<TimelineCaption> Captions { get; init; } = [];
}

public static class RenderPlanBuilder
{
    public static RenderPlan Build(
        TimelineDocument timeline,
        ExportAspectRatioPreset preset,
        IReadOnlySet<string>? disabledAutomation = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        disabledAutomation ??= new HashSet<string>(StringComparer.Ordinal);

        return new RenderPlan(
            timeline.ProjectPath,
            timeline.Duration,
            preset,
            CanvasFor(preset),
            timeline.Clips.ToArray(),
            timeline.Automation
                .Where(item => item.IsEnabled && !disabledAutomation.Contains(item.Id))
                .ToArray())
        {
            Captions = timeline.Captions.ToArray(),
        };
    }

    private static RenderCanvas CanvasFor(ExportAspectRatioPreset preset) =>
        preset switch
        {
            ExportAspectRatioPreset.Landscape1080p => new RenderCanvas(1920, 1080),
            ExportAspectRatioPreset.Portrait1080p => new RenderCanvas(1080, 1920),
            ExportAspectRatioPreset.Square1080p => new RenderCanvas(1080, 1080),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
}
