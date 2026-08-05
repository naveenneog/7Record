using SevenRecord.Domain.Audio;
using SevenRecord.Domain.Timeline;
using SevenRecord.Domain.Video;

namespace SevenRecord.Export.Tests;

/// <summary>
/// Pins the ORDER of stages in the generated FFmpeg filter graph.
/// </summary>
/// <remarks>
/// <para>
/// These rules were learned by rendering real video and looking at it. Every one of them is
/// order-dependent and silent when broken: reordering two filters does not throw, does not
/// fail a build, and does not fail any test that merely asserts a filter is <i>present</i> —
/// it just produces subtly wrong video or audio that nobody notices until a user does.
/// </para>
/// <para>
/// <see cref="FfmpegRenderPlanExporter.CreateCommand"/> is a pure function, so pinning it
/// costs nothing at runtime and needs no FFmpeg binary.
/// </para>
/// </remarks>
[TestClass]
public sealed class FfmpegFilterGraphOrderTests
{
    [TestMethod]
    public void CameraIsCroppedBeforeExposureIsApplied()
    {
        string graph = FilterGraph(FullTimeline());

        // Exposure before crop would grade pixels that are about to be thrown away, and
        // would grade them against the whole frame's range rather than the visible crop.
        AssertOrder(graph, "crop=w=", "eq=contrast=");
    }

    [TestMethod]
    public void CameraExposureIsAppliedBeforeScaling()
    {
        string graph = FilterGraph(FullTimeline());

        // Grading after downscaling bakes resampling artefacts into the correction.
        AssertOrder(graph, "eq=contrast=", "force_original_aspect_ratio=increase");
    }

    [TestMethod]
    public void CameraRoundedMaskIsAppliedBeforeTheOverlay()
    {
        string graph = FilterGraph(FullTimeline(cornerRadius: 0.5));

        // The alpha mask has to exist before the compositor consumes it, or the camera
        // bubble overlays as a hard rectangle.
        AssertOrder(graph, "geq=", "overlay=");
    }

    [TestMethod]
    public void ScreenIsScaledToCanvasBeforeTheCameraIsOverlaid()
    {
        string graph = FilterGraph(FullTimeline());

        AssertOrder(graph, "[base]", "overlay=");
    }

    [TestMethod]
    public void CaptionsAreBurnedInAfterTheCameraOverlay()
    {
        string graph = FilterGraph(FullTimeline(), subtitlePath: "C:\\project\\captions.srt");

        // Burning captions before the overlay would let the camera bubble cover them.
        AssertOrder(graph, "overlay=", "subtitles=");
    }

    [TestMethod]
    public void TwoAudioTracksApplyGainBeforeMixing()
    {
        string graph = FilterGraph(
            FullTimeline(),
            audioMix: new ProjectAudioMixSettings(
                new AudioMixSettings(6, false),
                new AudioMixSettings(-3, false)));

        // Gain must be per-track and pre-mix. Applying it after amix would change the
        // combined level while leaving the balance between mic and system audio wrong,
        // which is the entire point of having a mixer.
        AssertOrder(graph, "volume=6.00dB", "amix=inputs=2");
        AssertOrder(graph, "volume=-3.00dB", "amix=inputs=2");
    }

    [TestMethod]
    public void TwoAudioTracksNormaliseAfterMixing()
    {
        string graph = FilterGraph(
            FullTimeline(),
            audioMix: new ProjectAudioMixSettings(
                new AudioMixSettings(6, false),
                new AudioMixSettings(0, false)));

        AssertOrder(graph, "amix=inputs=2", "loudnorm=");
    }

    [TestMethod]
    public void ASingleAudioTrackAppliesGainAFTERNormalising()
    {
        string graph = FilterGraph(
            MicrophoneOnlyTimeline(),
            audioMix: new ProjectAudioMixSettings(
                new AudioMixSettings(6, false),
                AudioMixSettings.Default));

        // The mirror image of the two-track rule, and the reason both cases exist.
        // loudnorm targets an absolute integrated loudness, so normalising AFTER the gain
        // would simply undo it and the user's +6 dB would do nothing at all.
        AssertOrder(graph, "loudnorm=", "volume=6.00dB");
    }

    [TestMethod]
    public void AudioRepairIsAppliedBeforeClipEdits()
    {
        string graph = FilterGraph(
            FullTimeline(withSilenceGap: true),
            editSlices:
            [
                new TimelineEditSlice(
                    "a",
                    TimelineRange.FromStartAndDuration(
                        TimeSpan.FromSeconds(1),
                        TimeSpan.FromSeconds(2))),
            ]);

        // Clip-edit ranges are expressed in repaired ("project") time, so the repair has to
        // happen first or every edit boundary lands in the wrong place.
        AssertOrder(graph, "anullsrc=", "[microphoneEdited]");
        AssertOrder(graph, "[microphoneprepared]", "[microphoneEdited]");
    }

    [TestMethod]
    public void ClipEditsAreAppliedToEveryTrackSoTracksStayInSync()
    {
        string graph = FilterGraph(
            FullTimeline(),
            editSlices:
            [
                new TimelineEditSlice(
                    "a",
                    TimelineRange.FromStartAndDuration(
                        TimeSpan.FromSeconds(3),
                        TimeSpan.FromSeconds(2))),
                new TimelineEditSlice(
                    "b",
                    TimelineRange.FromStartAndDuration(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(2))),
            ]);

        // The same slice list applied to all four tracks is the whole synchronisation
        // guarantee of non-destructive editing. If a track were ever missed, audio and
        // video would drift apart by exactly the edited amount.
        foreach (string label in EditedLabels)
        {
            StringAssert.Contains(
                graph,
                label,
                $"{label} missing: that track would not follow the clip edits");
        }
    }

    [TestMethod]
    public void AnIdentityEditAddsNoTrimFiltersAtAll()
    {
        string edited = FilterGraph(
            FullTimeline(),
            editSlices:
            [
                new TimelineEditSlice(
                    "whole",
                    TimelineRange.FromStartAndDuration(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(10))),
            ]);

        // A single slice covering the full duration must render byte-identically to no
        // edit at all, so that merely opening the clip editor cannot change an export.
        Assert.AreEqual(FilterGraph(FullTimeline()), edited);
    }

    [TestMethod]
    public void AMutedTrackIsSilencedRatherThanOmitted()
    {
        string graph = FilterGraph(
            FullTimeline(),
            audioMix: new ProjectAudioMixSettings(
                new AudioMixSettings(0, true),
                AudioMixSettings.Default));

        // Dropping the input instead would shorten amix's longest-duration calculation
        // and shift the remaining track.
        StringAssert.Contains(graph, "volume=0[");
        StringAssert.Contains(graph, "amix=inputs=2");
    }

    [TestMethod]
    public void VideoAndAudioAreBothMappedAndOutputIsFastStart()
    {
        IReadOnlyList<string> arguments = Command(FullTimeline()).Arguments;
        string joined = string.Join(" ", arguments);

        StringAssert.Contains(joined, "-map [video]");
        StringAssert.Contains(joined, "-map [audio]");
        StringAssert.Contains(joined, "+faststart");
        Assert.AreEqual("-y", arguments[3]);
    }

    private static readonly string[] EditedLabels =
    [
        "[screenEdited]",
        "[cameraEdited]",
        "[microphoneEdited]",
        "[systemEdited]",
    ];

    private static void AssertOrder(string graph, string first, string second)
    {
        int firstIndex = graph.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = graph.IndexOf(second, StringComparison.Ordinal);

        Assert.IsGreaterThan(-1, firstIndex, $"'{first}' is not in the filter graph");
        Assert.IsGreaterThan(-1, secondIndex, $"'{second}' is not in the filter graph");
        Assert.IsLessThan(
            secondIndex,
            firstIndex,
            $"'{first}' must come before '{second}' in the filter graph");
    }

    private static string FilterGraph(
        TimelineDocument timeline,
        ProjectAudioMixSettings? audioMix = null,
        IReadOnlyList<TimelineEditSlice>? editSlices = null,
        string? subtitlePath = null)
    {
        IReadOnlyList<string> arguments =
            Command(timeline, audioMix, editSlices, subtitlePath).Arguments;
        int index = arguments.ToList().IndexOf("-filter_complex");
        Assert.IsGreaterThan(-1, index, "the command has no -filter_complex argument");
        return arguments[index + 1];
    }

    private static FfmpegExportCommand Command(
        TimelineDocument timeline,
        ProjectAudioMixSettings? audioMix = null,
        IReadOnlyList<TimelineEditSlice>? editSlices = null,
        string? subtitlePath = null)
    {
        RenderPlan plan = RenderPlanBuilder.Build(
            timeline,
            ExportAspectRatioPreset.Landscape1080p,
            disabledAutomation: null,
            audioMix,
            editSlices is null
                ? null
                : new TimelineEditDocument(1, editSlices));

        return FfmpegRenderPlanExporter.CreateCommand(
            plan,
            "C:\\output\\video.mp4",
            subtitlePath);
    }

    private static TimelineDocument FullTimeline(
        double cornerRadius = 0.25,
        bool withSilenceGap = false)
    {
        List<TimelineAutomationEvent> automation =
        [
            new TimelineAutomationEvent(
                "layout",
                "PresenterLayout",
                TimelineTrackKind.Camera,
                TimelineRange.FromStartAndDuration(
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(10)),
                "RoundedOverlay",
                true)
            {
                NumericData = new Dictionary<string, double>
                {
                    ["mode"] = (double)PresenterLayoutMode.RoundedOverlay,
                    ["x"] = 0.2,
                    ["y"] = 0.3,
                    ["width"] = 0.3,
                    ["height"] = 0.4,
                    ["cornerRadius"] = cornerRadius,
                    ["cameraZoom"] = 1.5,
                    ["cameraCenterX"] = 0.3,
                    ["cameraCenterY"] = 0.7,
                    ["cameraExposure"] = 0.2,
                },
            },
        ];

        if (withSilenceGap)
        {
            automation.Add(
                new TimelineAutomationEvent(
                    "gap",
                    nameof(AudioRepairEventKind.InsertSilence),
                    TimelineTrackKind.Microphone,
                    TimelineRange.FromStartAndDuration(
                        TimeSpan.FromSeconds(2),
                        TimeSpan.FromMilliseconds(200)),
                    "Insert 200 ms silence",
                    true));
        }

        return new TimelineDocument(
            "C:\\project",
            TimeSpan.FromSeconds(10),
            [
                Clip("screen", TimelineTrackKind.Screen, "screen.mp4"),
                Clip("camera", TimelineTrackKind.Camera, "camera.mp4"),
                Clip("mic", TimelineTrackKind.Microphone, "mic.wav"),
                Clip("system", TimelineTrackKind.SystemAudio, "system.wav"),
            ],
            automation);
    }

    private static TimelineDocument MicrophoneOnlyTimeline() =>
        new(
            "C:\\project",
            TimeSpan.FromSeconds(10),
            [
                Clip("screen", TimelineTrackKind.Screen, "screen.mp4"),
                Clip("mic", TimelineTrackKind.Microphone, "mic.wav"),
            ],
            []);

    private static TimelineClip Clip(
        string id,
        TimelineTrackKind track,
        string source) =>
        new(
            id,
            track,
            source,
            TimelineRange.FromStartAndDuration(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(10)));
}
