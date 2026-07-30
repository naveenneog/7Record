using SevenRecord.Domain.Timeline;
using SevenRecord.Domain.Audio;

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

    public ProjectAudioMixSettings AudioMix { get; init; } =
        ProjectAudioMixSettings.Default;
}

public static class RenderPlanBuilder
{
    public static RenderPlan Build(
        TimelineDocument timeline,
        ExportAspectRatioPreset preset,
        IReadOnlySet<string>? disabledAutomation = null,
        ProjectAudioMixSettings? audioMix = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        disabledAutomation ??= new HashSet<string>(StringComparer.Ordinal);

        TimelineAutomationEvent[] automation = timeline.Automation
            .Where(item => item.IsEnabled && !disabledAutomation.Contains(item.Id))
            .ToArray();
        double removedSeconds = automation
            .Where(item => item.Kind == "LoadingSpeed")
            .Sum(item =>
            {
                double speed = item.NumericData.TryGetValue("speed", out double value)
                    ? value
                    : 4;
                return item.Range.Duration.TotalSeconds * (1d - 1d / speed);
            });
        TimeSpan renderDuration = TimeSpan.FromSeconds(
            Math.Max(0, timeline.Duration.TotalSeconds - removedSeconds));

        return new RenderPlan(
            timeline.ProjectPath,
            renderDuration,
            preset,
            CanvasFor(preset),
            timeline.Clips.ToArray(),
            automation)
        {
            Captions = timeline.Captions.ToArray(),
            AudioMix =
                (audioMix ?? ProjectAudioMixSettings.Default).Constrain(),
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
