using SevenRecord.Domain.Timeline;

namespace SevenRecord.Export.Tests;

[TestClass]
public sealed class RenderPlanBuilderTests
{
    [TestMethod]
    public void DisabledAutomationIsExcludedWithoutChangingSources()
    {
        TimelineClip clip = new(
            "screen",
            TimelineTrackKind.Screen,
            "screen.mp4",
            TimelineRange.FromStartAndDuration(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(5)));
        TimelineAutomationEvent enabled = Automation("enabled");
        TimelineAutomationEvent disabled = Automation("disabled");
        TimelineDocument timeline = new(
            "project",
            TimeSpan.FromSeconds(5),
            [clip],
            [enabled, disabled]);

        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Portrait1080p,
            new HashSet<string> { disabled.Id });

        Assert.HasCount(1, plan.Clips);
        Assert.AreEqual(clip, plan.Clips.Single());
        Assert.AreEqual(enabled, plan.Automation.Single());
        Assert.AreEqual(1080, plan.Canvas.Width);
        Assert.AreEqual(1920, plan.Canvas.Height);
    }

    [TestMethod]
    public void ExportCommandIncludesOverlayAndMixedAudio()
    {
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(5),
            [
                Clip("screen", TimelineTrackKind.Screen, "screen.mp4"),
                Clip("camera", TimelineTrackKind.Camera, "camera.mp4"),
                Clip("mic", TimelineTrackKind.Microphone, "mic.wav"),
                Clip("system", TimelineTrackKind.SystemAudio, "system.wav")
            ],
            [
                new TimelineAutomationEvent(
                    "layout",
                    "PresenterLayout",
                    TimelineTrackKind.Camera,
                    TimelineRange.FromStartAndDuration(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(5)),
                    "RoundedOverlay",
                    true)
            ]);
        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p);

        FfmpegExportCommand command = FfmpegRenderPlanExporter.CreateCommand(
            plan,
            "C:\\output\\video.mp4");
        string joined = string.Join(" ", command.Arguments);

        StringAssert.Contains(joined, "overlay=");
        StringAssert.Contains(joined, "amix=inputs=2");
        StringAssert.Contains(joined, "1920:1080");
    }

    [TestMethod]
    public void UnsupportedEnabledAutomationFailsExplicitly()
    {
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(5),
            [Clip("screen", TimelineTrackKind.Screen, "screen.mp4")],
            [
                new TimelineAutomationEvent(
                    "gap",
                    "LoadingSpeed",
                    TimelineTrackKind.Screen,
                    TimelineRange.FromStartAndDuration(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromMilliseconds(100)),
                    "gap",
                    true)
            ]);
        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p);

        Assert.ThrowsExactly<NotSupportedException>(
            () => FfmpegRenderPlanExporter.CreateCommand(
                plan,
                "C:\\output\\video.mp4"));
    }

    [TestMethod]
    public void MidTrackSilenceUsesTrimSilenceAndConcatFilters()
    {
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(5),
            [
                Clip("screen", TimelineTrackKind.Screen, "screen.mp4"),
                Clip("mic", TimelineTrackKind.Microphone, "mic.wav")
            ],
            [
                new TimelineAutomationEvent(
                    "gap",
                    nameof(SevenRecord.Domain.Audio.AudioRepairEventKind.InsertSilence),
                    TimelineTrackKind.Microphone,
                    TimelineRange.FromStartAndDuration(
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromMilliseconds(200)),
                    "Insert 200 ms silence",
                    true)
            ]);
        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p);

        string command = string.Join(
            " ",
            FfmpegRenderPlanExporter.CreateCommand(
                plan,
                "C:\\output\\video.mp4").Arguments);

        StringAssert.Contains(command, "anullsrc=r=48000:cl=stereo:d=0.200000");
        StringAssert.Contains(command, "concat=n=3:v=0:a=1");
        StringAssert.Contains(command, "atrim=start=0.000000:end=2.000000");
    }

    [TestMethod]
    public void CaptionsFlowIntoPlanAndSubtitleFilter()
    {
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(5),
            [Clip("screen", TimelineTrackKind.Screen, "screen.mp4")],
            [])
        {
            Captions =
            [
                new TimelineCaption(
                    "caption",
                    TimelineRange.FromStartAndDuration(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2)),
                    "Hello")
            ],
        };

        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p);
        string command = string.Join(
            " ",
            FfmpegRenderPlanExporter.CreateCommand(
                plan,
                "C:\\output\\video.mp4",
                "C:\\temp\\captions.srt").Arguments);

        Assert.HasCount(1, plan.Captions);
        StringAssert.Contains(command, "subtitles='C\\:/temp/captions.srt'");
    }

    [TestMethod]
    public void CursorZoomUsesStructuredZoompanExpressions()
    {
        TimelineAutomationEvent zoom = new(
            "zoom",
            "CursorZoom",
            TimelineTrackKind.Screen,
            TimelineRange.FromStartAndDuration(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1.2)),
            "zoom",
            true)
        {
            NumericData = new Dictionary<string, double>
            {
                ["centerX"] = 0.25,
                ["centerY"] = 0.75,
                ["scale"] = 1.8,
            },
        };
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(5),
            [Clip("screen", TimelineTrackKind.Screen, "screen.mp4")],
            [zoom]);
        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p);

        string command = string.Join(
            " ",
            FfmpegRenderPlanExporter.CreateCommand(
                plan,
                "C:\\output\\video.mp4").Arguments);

        StringAssert.Contains(command, "zoompan=");
        StringAssert.Contains(command, "0.250000*iw");
        StringAssert.Contains(command, "0.750000*ih");
        StringAssert.Contains(command, "(1.8-1)");
    }

    private static TimelineAutomationEvent Automation(string id) =>
        new(
            id,
            "Repair",
            TimelineTrackKind.Microphone,
            TimelineRange.FromStartAndDuration(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(100)),
            id,
            true);

    private static TimelineClip Clip(
        string id,
        TimelineTrackKind track,
        string path) =>
        new(
            id,
            track,
            path,
            TimelineRange.FromStartAndDuration(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(5)));
}
