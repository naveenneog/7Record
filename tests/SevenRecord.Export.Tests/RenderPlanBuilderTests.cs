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
                {
                    NumericData = new Dictionary<string, double>
                    {
                        ["mode"] = (double)SevenRecord.Domain.Video.PresenterLayoutMode.RoundedOverlay,
                        ["x"] = 0.2,
                        ["y"] = 0.3,
                        ["width"] = 0.3,
                        ["height"] = 0.4,
                        ["cornerRadius"] = 0.5,
                        ["cameraZoom"] = 1.5,
                        ["cameraCenterX"] = 0.3,
                        ["cameraCenterY"] = 0.7,
                        ["cameraExposure"] = 0.2,
                    },
                }
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
        StringAssert.Contains(joined, "scale=576:432");
        StringAssert.Contains(joined, "overlay=384:324");
        StringAssert.Contains(joined, "geq=");
        StringAssert.Contains(joined, "crop=w=");
        StringAssert.Contains(joined, "iw*0.300000");
        StringAssert.Contains(joined, "ih*0.700000");
        StringAssert.Contains(joined, "eq=contrast=1.14869");
        StringAssert.Contains(joined, "brightness=-0.07434");
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
                    "UnknownAutomation",
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

    [TestMethod]
    public void LoadingSpeedRetimeVideoAudioAndPlanDuration()
    {
        TimelineAutomationEvent loading = new(
            "loading",
            "LoadingSpeed",
            TimelineTrackKind.Screen,
            TimelineRange.FromStartAndDuration(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2)),
            "Speed up waiting to 4x",
            true)
        {
            NumericData = new Dictionary<string, double> { ["speed"] = 4 },
        };
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(5),
            [
                Clip("screen", TimelineTrackKind.Screen, "screen.mp4"),
                Clip("mic", TimelineTrackKind.Microphone, "mic.wav")
            ],
            [loading]);
        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p);

        string command = string.Join(
            " ",
            FfmpegRenderPlanExporter.CreateCommand(
                plan,
                "C:\\output\\video.mp4").Arguments);

        Assert.AreEqual(TimeSpan.FromSeconds(3.5), plan.Duration);
        StringAssert.Contains(command, "setpts=(PTS-STARTPTS)/4.000000");
        StringAssert.Contains(command, "atempo=2.000000,atempo=2.000000");
        StringAssert.Contains(command, "concat=n=3:v=1:a=0");
        StringAssert.Contains(command, "concat=n=3:v=0:a=1");
    }

    [TestMethod]
    public async Task FailedExportPreservesExistingOutput()
    {
        if (SevenRecord.Media.FfmpegLocator.FindExecutable() is null)
        {
            Assert.Inconclusive("FFmpeg is unavailable.");
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "SevenRecord.Export.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "existing.mp4");
        byte[] original = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(output, original);
        TimelineDocument timeline = new(
            root,
            TimeSpan.FromSeconds(1),
            [Clip("screen", TimelineTrackKind.Screen, "missing-screen.mp4")],
            []);
        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p);
        try
        {
            RenderPlanExportResult result =
                await FfmpegRenderPlanExporter.ExportAsync(plan, output);

            Assert.IsFalse(result.Succeeded);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(output));
            Assert.IsEmpty(
                Directory.GetFiles(root, "*.partial.mp4", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void MultiSegmentSourcesAreConcatenated()
    {
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(10),
            [
                ClipAt(
                    "screen-1",
                    TimelineTrackKind.Screen,
                    "screen-1.mp4",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(5)),
                ClipAt(
                    "screen-2",
                    TimelineTrackKind.Screen,
                    "screen-2.mp4",
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5)),
                ClipAt(
                    "mic-1",
                    TimelineTrackKind.Microphone,
                    "mic-1.wav",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(5)),
                ClipAt(
                    "mic-2",
                    TimelineTrackKind.Microphone,
                    "mic-2.wav",
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5))
            ],
            []);

        string command = string.Join(
            " ",
            FfmpegRenderPlanExporter.CreateCommand(
                RenderPlanBuilder.Build(
                    timeline,
                    ExportAspectRatioPreset.Landscape1080p),
                "C:\\output\\video.mp4").Arguments);

        StringAssert.Contains(command, "concat=n=2:v=1:a=0[screenSource]");
        StringAssert.Contains(command, "concat=n=2:v=0:a=1[microphoneSource]");
    }

    [TestMethod]
    public void MultiSegmentGapFailsExplicitly()
    {
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(11),
            [
                ClipAt(
                    "screen-1",
                    TimelineTrackKind.Screen,
                    "screen-1.mp4",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(5)),
                ClipAt(
                    "screen-2",
                    TimelineTrackKind.Screen,
                    "screen-2.mp4",
                    TimeSpan.FromSeconds(6),
                    TimeSpan.FromSeconds(5))
            ],
            []);

        Assert.ThrowsExactly<InvalidOperationException>(
            () => FfmpegRenderPlanExporter.CreateCommand(
                RenderPlanBuilder.Build(
                    timeline,
                    ExportAspectRatioPreset.Landscape1080p),
                "C:\\output\\video.mp4"));
    }

    [TestMethod]
    public void AudioMixAppliesGainAndMute()
    {
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(5),
            [
                Clip("screen", TimelineTrackKind.Screen, "screen.mp4"),
                Clip("mic", TimelineTrackKind.Microphone, "mic.wav"),
                Clip("system", TimelineTrackKind.SystemAudio, "system.wav")
            ],
            []);
        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p,
            audioMix: new SevenRecord.Domain.Audio.ProjectAudioMixSettings(
                new SevenRecord.Domain.Audio.AudioMixSettings(3.5, false),
                new SevenRecord.Domain.Audio.AudioMixSettings(0, true)));

        string command = string.Join(
            " ",
            FfmpegRenderPlanExporter.CreateCommand(
                plan,
                "C:\\output\\video.mp4").Arguments);

        StringAssert.Contains(command, "volume=3.50dB[microphoneMixed]");
        StringAssert.Contains(command, "volume=0[systemMixed]");
    }

    [TestMethod]
    public void SingleTrackGainIsAppliedAfterLoudnessNormalization()
    {
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(5),
            [
                Clip("screen", TimelineTrackKind.Screen, "screen.mp4"),
                Clip("mic", TimelineTrackKind.Microphone, "mic.wav")
            ],
            []);
        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p,
            audioMix: new SevenRecord.Domain.Audio.ProjectAudioMixSettings(
                new SevenRecord.Domain.Audio.AudioMixSettings(6, false),
                SevenRecord.Domain.Audio.AudioMixSettings.Default));
        string command = string.Join(
            " ",
            FfmpegRenderPlanExporter.CreateCommand(
                plan,
                "C:\\output\\video.mp4").Arguments);

        StringAssert.Contains(command, "loudnorm");
        int normalizationIndex =
            command.IndexOf("loudnorm", StringComparison.Ordinal);
        StringAssert.Contains(
            command[normalizationIndex..],
            "volume=6.00dB[mixed]");
    }

    [TestMethod]
    public void ClipEditsTrimReorderAndRemapAutomation()
    {
        TimelineAutomationEvent zoom = new(
            "zoom",
            "CursorZoom",
            TimelineTrackKind.Screen,
            TimelineRange.FromStartAndDuration(
                TimeSpan.FromSeconds(5.5),
                TimeSpan.FromSeconds(1)),
            "zoom",
            true);
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(10),
            [ClipAt(
                "screen",
                TimelineTrackKind.Screen,
                "screen.mp4",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(10))],
            [zoom]);
        TimelineEditDocument edits = new(
            1,
            [
                new TimelineEditSlice(
                    "later",
                    new TimelineRange(
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(8))),
                new TimelineEditSlice(
                    "earlier",
                    new TimelineRange(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(2)))
            ]);

        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p,
            editDocument: edits);
        string command = string.Join(
            " ",
            FfmpegRenderPlanExporter.CreateCommand(
                plan,
                "C:\\output\\video.mp4").Arguments);

        Assert.AreEqual(TimeSpan.FromSeconds(5), plan.Duration);
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(500),
            plan.Automation.Single().Range.Start);
        StringAssert.Contains(command, "trim=start=5.000000:end=8.000000");
        StringAssert.Contains(command, "trim=start=0.000000:end=2.000000");
        StringAssert.Contains(command, "concat=n=2:v=1:a=0[screenEdited]");
    }

    [TestMethod]
    public void AudioRepairRunsBeforeClipEditing()
    {
        TimelineAutomationEvent gap = new(
            "gap",
            nameof(SevenRecord.Domain.Audio.AudioRepairEventKind.InsertSilence),
            TimelineTrackKind.Microphone,
            TimelineRange.FromStartAndDuration(
                TimeSpan.FromSeconds(4),
                TimeSpan.FromMilliseconds(200)),
            "Insert 200 ms silence",
            true);
        TimelineDocument timeline = new(
            "C:\\project",
            TimeSpan.FromSeconds(10),
            [
                ClipAt(
                    "screen",
                    TimelineTrackKind.Screen,
                    "screen.mp4",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(10)),
                ClipAt(
                    "mic",
                    TimelineTrackKind.Microphone,
                    "mic.wav",
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(10))
            ],
            [gap]);
        TimelineEditDocument edits = new(
            1,
            [
                new TimelineEditSlice(
                    "keep",
                    new TimelineRange(
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(8)))
            ]);
        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p,
            editDocument: edits);
        string command = string.Join(
            " ",
            FfmpegRenderPlanExporter.CreateCommand(
                plan,
                "C:\\output\\video.mp4").Arguments);

        Assert.AreEqual(gap.Range, plan.Automation.Single().Range);
        StringAssert.Contains(command, "anullsrc");
        int repairIndex =
            command.IndexOf("anullsrc", StringComparison.Ordinal);
        StringAssert.Contains(
            command[repairIndex..],
            "atrim=start=5.000000:end=8.000000");
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

    private static TimelineClip ClipAt(
        string id,
        TimelineTrackKind track,
        string path,
        TimeSpan start,
        TimeSpan duration) =>
        new(
            id,
            track,
            path,
            TimelineRange.FromStartAndDuration(start, duration));
}
