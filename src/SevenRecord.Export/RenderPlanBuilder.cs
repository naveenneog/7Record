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

    public IReadOnlyList<TimelineEditSlice> EditSlices { get; init; } = [];

    public bool IsPreview { get; init; }

    public string? PreviewScratchId { get; init; }
}

public static class RenderPlanBuilder
{
    public static RenderPlan Build(
        TimelineDocument timeline,
        ExportAspectRatioPreset preset,
        IReadOnlySet<string>? disabledAutomation = null,
        ProjectAudioMixSettings? audioMix = null,
        TimelineEditDocument? editDocument = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        disabledAutomation ??= new HashSet<string>(StringComparer.Ordinal);
        TimelineEditDocument edits =
            (editDocument ??
             TimelineEditDocument.CreateDefault(timeline.Duration))
            .Validate(timeline.Duration);
        bool identityEdit = edits.Slices.Count == 1 &&
            edits.Slices[0].SourceRange.Start == TimeSpan.Zero &&
            edits.Slices[0].SourceRange.End == timeline.Duration;

        TimelineAutomationEvent[] automation = timeline.Automation
            .Where(item => item.IsEnabled && !disabledAutomation.Contains(item.Id))
            .SelectMany(item =>
                IsAudioRepair(item) || identityEdit
                ? [item]
                : TimelineEditMapper.MapRange(item.Range, edits)
                    .Select(mapped => item with
                    {
                        Id = $"{item.Id}@{mapped.SliceId}",
                        Range = mapped.OutputRange,
                    }))
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
            Math.Max(
                0,
                edits.OutputDuration.TotalSeconds - removedSeconds));
        TimelineCaption[] captions = timeline.Captions
            .SelectMany(caption => identityEdit
                ? [caption]
                :
                TimelineEditMapper.MapRange(caption.Range, edits)
                    .Select(mapped => caption with
                    {
                        Id = $"{caption.Id}@{mapped.SliceId}",
                        Range = mapped.OutputRange,
                    }))
            .OrderBy(caption => caption.Range.Start)
            .ToArray();

        return new RenderPlan(
            timeline.ProjectPath,
            renderDuration,
            preset,
            CanvasFor(preset),
            timeline.Clips.ToArray(),
            automation)
        {
            Captions = captions,
            AudioMix =
                (audioMix ?? ProjectAudioMixSettings.Default).Constrain(),
            EditSlices = identityEdit
                ? []
                : edits.Slices.ToArray(),
        };
    }

    private static bool IsAudioRepair(
        TimelineAutomationEvent item) =>
        item.Kind is
            nameof(AudioRepairEventKind.InsertSilence) or
            nameof(AudioRepairEventKind.AdjustPlaybackRate);

    private static RenderCanvas CanvasFor(ExportAspectRatioPreset preset) =>
        preset switch
        {
            ExportAspectRatioPreset.Landscape1080p => new RenderCanvas(1920, 1080),
            ExportAspectRatioPreset.Portrait1080p => new RenderCanvas(1080, 1920),
            ExportAspectRatioPreset.Square1080p => new RenderCanvas(1080, 1080),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
}
