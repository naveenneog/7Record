using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SevenRecord.Domain.Captions;
using SevenRecord.Domain.Timeline;
using SevenRecord.Domain.Video;
using SevenRecord.Media;
using SevenRecord.Transcription;

namespace SevenRecord.Export;

public sealed record FfmpegExportCommand(
    IReadOnlyList<string> Arguments);

public sealed record RenderPlanExportResult(
    bool Succeeded,
    string OutputPath,
    string? Error);

public static class FfmpegRenderPlanExporter
{
    public static FfmpegExportCommand CreateCommand(
        RenderPlan plan,
        string outputPath,
        string? subtitlePath = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        TimelineAutomationEvent[] unsupported = plan.Automation
            .Where(item =>
                item.Kind is not "PresenterLayout" and
                not nameof(SevenRecord.Domain.Audio.AudioRepairEventKind.AdjustPlaybackRate) and
                not nameof(SevenRecord.Domain.Audio.AudioRepairEventKind.InsertSilence) and
                not "CursorZoom" and
                not "LoadingSpeed")
            .ToArray();
        if (unsupported.Length > 0)
        {
            throw new NotSupportedException(
                "Enabled automation is not exportable yet: " +
                string.Join(", ", unsupported.Select(item => item.Kind).Distinct()));
        }

        TimelineClip[] screenClips = ClipsFor(
            plan,
            TimelineTrackKind.Screen);
        if (screenClips.Length == 0)
        {
            throw new InvalidOperationException(
                "The render plan has no screen source.");
        }
        TimelineClip[] cameraClips = ClipsFor(
            plan,
            TimelineTrackKind.Camera);
        TimelineClip[] microphoneClips = ClipsFor(
            plan,
            TimelineTrackKind.Microphone);
        TimelineClip[] systemAudioClips = ClipsFor(
            plan,
            TimelineTrackKind.SystemAudio);

        List<TimelineClip> inputs =
        [
            .. screenClips,
            .. cameraClips,
            .. microphoneClips,
            .. systemAudioClips,
        ];

        List<string> arguments = ["-hide_banner", "-loglevel", "error", "-y"];
        foreach (TimelineClip input in inputs)
        {
            arguments.Add("-i");
            arguments.Add(ResolveSourcePath(plan.ProjectPath, input.SourcePath));
        }

        int[] screenIndices = InputIndices(inputs, screenClips);
        int[] cameraIndices = InputIndices(inputs, cameraClips);
        int[] microphoneIndices = InputIndices(inputs, microphoneClips);
        int[] systemAudioIndices = InputIndices(inputs, systemAudioClips);
        TimeSpan sourceDuration = plan.EditSlices.Count > 0
            ? TimeSpan.FromTicks(
                plan.EditSlices.Sum(
                    slice => slice.SourceRange.Duration.Ticks))
            : plan.Clips.Count == 0
                ? plan.Duration
                : plan.Clips.Max(clip => clip.Range.End);
        TimelineAutomationEvent[] loadingEvents = plan.Automation
            .Where(item => item.Kind == "LoadingSpeed")
            .OrderBy(item => item.Range.Start)
            .ToArray();
        List<string> filters = [];
        string screenSource = AddConcatFilter(
            filters,
            screenClips,
            screenIndices,
            "screenSource",
            isVideo: true);
        screenSource = AddTimelineEditFilter(
            filters,
            screenSource,
            plan.EditSlices,
            "screenEdited",
            isVideo: true);
        filters.Add(
            $"[{screenSource}]scale={plan.Canvas.Width}:{plan.Canvas.Height}:" +
            "force_original_aspect_ratio=decrease," +
            $"pad={plan.Canvas.Width}:{plan.Canvas.Height}:(ow-iw)/2:(oh-ih)/2:color=black[base]");
        string screenLabel = "base";
        TimelineAutomationEvent[] zooms = plan.Automation
            .Where(item => item.Kind == "CursorZoom")
            .OrderBy(item => item.Range.Start)
            .ToArray();
        if (zooms.Length > 0)
        {
            string zoomExpression = "1" + string.Concat(
                zooms.Select(zoom =>
                    $"+({ZoomValue(zoom, "scale", 1.8)}-1)*" +
                    $"if(between(in_time,{Seconds(zoom.Range.Start.TotalSeconds)}," +
                    $"{Seconds(zoom.Range.End.TotalSeconds)})," +
                    $"sin(PI*(in_time-{Seconds(zoom.Range.Start.TotalSeconds)})/" +
                    $"{Seconds(zoom.Range.Duration.TotalSeconds)}),0)"));
            string xExpression = "(iw-iw/zoom)/2";
            string yExpression = "(ih-ih/zoom)/2";
            foreach (TimelineAutomationEvent zoom in zooms.Reverse())
            {
                string active =
                    $"between(in_time,{Seconds(zoom.Range.Start.TotalSeconds)}," +
                    $"{Seconds(zoom.Range.End.TotalSeconds)})";
                xExpression =
                    $"if({active},max(0,min(iw-iw/zoom," +
                    $"{Seconds(ZoomValue(zoom, "centerX", 0.5))}*iw-iw/zoom/2)),{xExpression})";
                yExpression =
                    $"if({active},max(0,min(ih-ih/zoom," +
                    $"{Seconds(ZoomValue(zoom, "centerY", 0.5))}*ih-ih/zoom/2)),{yExpression})";
            }

            filters.Add(
                $"[base]zoompan=z='{zoomExpression}':x='{xExpression}':y='{yExpression}':" +
                $"d=1:s={plan.Canvas.Width}x{plan.Canvas.Height}:fps=30[zoomed]");
            screenLabel = "zoomed";
        }

        TimelineAutomationEvent? presenterLayout = plan.Automation
            .FirstOrDefault(item => item.Kind == "PresenterLayout");
        PresenterLayoutMode presenterMode = (PresenterLayoutMode)(int)
            AutomationValue(
                presenterLayout,
                "mode",
                (double)PresenterLayoutMode.RoundedOverlay);
        if (cameraIndices.Length > 0 &&
            presenterMode is not PresenterLayoutMode.ScreenOnly)
        {
            string cameraSource = AddConcatFilter(
                filters,
                cameraClips,
                cameraIndices,
                "cameraSource",
                isVideo: true);
            cameraSource = AddTimelineEditFilter(
                filters,
                cameraSource,
                plan.EditSlices,
                "cameraEdited",
                isVideo: true);
            double widthRatio = AutomationValue(
                presenterLayout,
                "width",
                PresenterLayoutSettings.DefaultOverlay.Width);
            double heightRatio = AutomationValue(
                presenterLayout,
                "height",
                PresenterLayoutSettings.DefaultOverlay.Height);
            int cameraWidth = EvenDimension(
                plan.Canvas.Width * Math.Clamp(widthRatio, 0.1, 1));
            int cameraHeight = EvenDimension(
                plan.Canvas.Height * Math.Clamp(heightRatio, 0.1, 1));
            int cameraX = (int)Math.Round(
                plan.Canvas.Width *
                Math.Clamp(
                    AutomationValue(
                        presenterLayout,
                        "x",
                        PresenterLayoutSettings.DefaultOverlay.X),
                    0,
                    1 - (double)cameraWidth / plan.Canvas.Width));
            int cameraY = (int)Math.Round(
                plan.Canvas.Height *
                Math.Clamp(
                    AutomationValue(
                        presenterLayout,
                        "y",
                        PresenterLayoutSettings.DefaultOverlay.Y),
                    0,
                    1 - (double)cameraHeight / plan.Canvas.Height));
            double cornerRadius = Math.Clamp(
                AutomationValue(
                    presenterLayout,
                    "cornerRadius",
                    PresenterLayoutSettings.DefaultOverlay.CornerRadius),
                0,
                0.5);
            double cameraZoom = Math.Clamp(
                AutomationValue(
                    presenterLayout,
                    "cameraZoom",
                    CameraFramingSettings.Default.Zoom),
                1,
                4);
            double cameraCenterX = Math.Clamp(
                AutomationValue(
                    presenterLayout,
                    "cameraCenterX",
                    CameraFramingSettings.Default.CenterX),
                0,
                1);
            double cameraCenterY = Math.Clamp(
                AutomationValue(
                    presenterLayout,
                    "cameraCenterY",
                    CameraFramingSettings.Default.CenterY),
                0,
                1);
            double cameraExposure = Math.Clamp(
                AutomationValue(
                    presenterLayout,
                    "cameraExposure",
                    CameraEffectSettings.Default.Exposure),
                -1,
                1);
            double targetAspect = cameraWidth / (double)cameraHeight;
            string cameraFilter =
                $"[{cameraSource}]crop=" +
                $"w='if(gt(iw/ih,{Seconds(targetAspect)}),ih*{Seconds(targetAspect)},iw)/{Seconds(cameraZoom)}':" +
                $"h='if(gt(iw/ih,{Seconds(targetAspect)}),ih,iw/{Seconds(targetAspect)})/{Seconds(cameraZoom)}':" +
                $"x='max(0,min(iw-ow,iw*{Seconds(cameraCenterX)}-ow/2))':" +
                $"y='max(0,min(ih-oh,ih*{Seconds(cameraCenterY)}-oh/2))'," +
                $"eq=contrast={Seconds(Math.Pow(2, cameraExposure))}:" +
                $"brightness={Seconds((1 - Math.Pow(2, cameraExposure)) / 2)}," +
                $"scale={cameraWidth}:{cameraHeight}:" +
                "force_original_aspect_ratio=increase," +
                $"crop={cameraWidth}:{cameraHeight},format=rgba";
            if (cornerRadius >= 0.49)
            {
                cameraFilter +=
                    ",geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':" +
                    "a='if(lte(pow((X-W/2)/(W/2),2)+" +
                    "pow((Y-H/2)/(H/2),2),1),255,0)'";
            }
            filters.Add(cameraFilter + "[camera]");
            filters.Add(
                $"[{screenLabel}][camera]overlay={cameraX}:{cameraY}:" +
                "format=auto[composite]");
        }
        else
        {
            filters.Add($"[{screenLabel}]null[composite]");
        }

        if (!string.IsNullOrWhiteSpace(subtitlePath))
        {
            filters.Add(
                $"[composite]subtitles='{EscapeFilterPath(subtitlePath)}'[visual]");
        }
        else
        {
            filters.Add("[composite]null[visual]");
        }
        AddSpeedFilters(
            filters,
            "visual",
            "video",
            loadingEvents,
            sourceDuration,
            isVideo: true);

        List<AudioFilterOutput> audioOutputs = [];
        AddAudioFilter(
            filters,
            audioOutputs,
            plan,
            microphoneClips,
            microphoneIndices,
            TimelineTrackKind.Microphone,
            "microphone");
        AddAudioFilter(
            filters,
            audioOutputs,
            plan,
            systemAudioClips,
            systemAudioIndices,
            TimelineTrackKind.SystemAudio,
            "system");
        if (audioOutputs.Count == 2)
        {
            string microphoneMix = AddVolumeFilter(
                filters,
                audioOutputs[0],
                "microphoneMixed");
            string systemMix = AddVolumeFilter(
                filters,
                audioOutputs[1],
                "systemMixed");
            filters.Add(
                $"[{microphoneMix}][{systemMix}]" +
                "amix=inputs=2:duration=longest:normalize=0,loudnorm=I=-16:TP=-1.5:LRA=11[mixed]");
        }
        else if (audioOutputs.Count == 1)
        {
            filters.Add(
                $"[{audioOutputs[0].Label}]" +
                "loudnorm=I=-16:TP=-1.5:LRA=11[singleNormalized]");
            _ = AddVolumeFilter(
                filters,
                audioOutputs[0] with { Label = "singleNormalized" },
                "mixed");
        }
        if (audioOutputs.Count > 0)
        {
            AddSpeedFilters(
                filters,
                "mixed",
                "audio",
                loadingEvents,
                sourceDuration,
                isVideo: false);
        }

        arguments.Add("-filter_complex");
        arguments.Add(string.Join(";", filters));
        arguments.Add("-map");
        arguments.Add("[video]");
        if (audioOutputs.Count > 0)
        {
            arguments.Add("-map");
            arguments.Add("[audio]");
        }

        arguments.AddRange(
        [
            "-c:v", "libx264",
            "-preset", "medium",
            "-pix_fmt", "yuv420p",
            "-r", "30"
        ]);
        if (audioOutputs.Count > 0)
        {
            arguments.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        }

        arguments.Add("-movflags");
        arguments.Add("+faststart");
        arguments.Add("-t");
        arguments.Add(plan.Duration.TotalSeconds.ToString("F6", CultureInfo.InvariantCulture));
        arguments.Add(Path.GetFullPath(outputPath));

        return new FfmpegExportCommand(arguments);
    }

    public static async Task<RenderPlanExportResult> ExportAsync(
        RenderPlan plan,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        string? ffmpegPath = FfmpegLocator.FindExecutable();
        if (ffmpegPath is null)
        {
            return new RenderPlanExportResult(
                false,
                outputPath,
                "FFmpeg is not installed or is not available on PATH.");
        }

        string fullOutputPath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        string consolidationRoot = Path.Combine(
            plan.ProjectPath,
            "temp",
            plan.IsPreview
                ? $"preview-export-" +
                  $"{Path.GetFileName(plan.PreviewScratchId ?? Guid.NewGuid().ToString("N"))}"
                : $"export-{Guid.NewGuid():N}");
        string temporaryOutputPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileNameWithoutExtension(fullOutputPath)}-{Guid.NewGuid():N}.partial.mp4");
        string? subtitlePath = null;
        try
        {
            if (plan.Captions.Count > 0)
            {
                subtitlePath = Path.Combine(
                    Path.GetTempPath(),
                    "SevenRecord.Export",
                    $"{Guid.NewGuid():N}.srt");
                Directory.CreateDirectory(Path.GetDirectoryName(subtitlePath)!);
                CaptionDocument captions = new(
                    1,
                    "und",
                    plan.Captions
                        .Select(caption => new CaptionSegment(
                            caption.Id,
                            caption.Range.Start,
                            caption.Range.End,
                            caption.Text))
                        .ToArray());
                await File.WriteAllTextAsync(
                    subtitlePath,
                    CaptionFormatter.ToSrt(captions),
                    cancellationToken);
            }

            RenderPlan preparedPlan = await ConsolidateSegmentsAsync(
                plan,
                consolidationRoot,
                cancellationToken);
            FfmpegExportCommand command;
            try
            {
                command = CreateCommand(
                    preparedPlan,
                    temporaryOutputPath,
                    subtitlePath);
            }
            catch (InvalidOperationException exception)
            {
                return new RenderPlanExportResult(false, outputPath, exception.Message);
            }
            catch (NotSupportedException exception)
            {
                return new RenderPlanExportResult(false, outputPath, exception.Message);
            }

            Directory.CreateDirectory(outputDirectory);
            ProcessStartInfo startInfo = new()
            {
                FileName = ffmpegPath,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (string argument in command.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            try
            {
                using Process process = new() { StartInfo = startInfo };
                if (!process.Start())
                {
                    return new RenderPlanExportResult(
                        false,
                        outputPath,
                        "FFmpeg export could not be started.");
                }

                Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
                Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                _ = await output;
                string standardError = await error;
                if (process.ExitCode != 0)
                {
                    return new RenderPlanExportResult(
                        false,
                        outputPath,
                        string.IsNullOrWhiteSpace(standardError)
                            ? $"FFmpeg exited with code {process.ExitCode}."
                            : standardError.Trim());
                }
                if (!File.Exists(temporaryOutputPath) ||
                    new FileInfo(temporaryOutputPath).Length == 0)
                {
                    return new RenderPlanExportResult(
                        false,
                        outputPath,
                        "FFmpeg completed without producing a valid export.");
                }

                File.Move(temporaryOutputPath, fullOutputPath, overwrite: true);
                return new RenderPlanExportResult(true, fullOutputPath, null);
            }
            catch (Win32Exception)
            {
                return new RenderPlanExportResult(
                    false,
                    outputPath,
                    "FFmpeg export could not be executed.");
            }
        }
        finally
        {
            if (File.Exists(temporaryOutputPath))
            {
                File.Delete(temporaryOutputPath);
            }
            if (subtitlePath is not null && File.Exists(subtitlePath))
            {
                File.Delete(subtitlePath);
            }
            if (Directory.Exists(consolidationRoot))
            {
                Directory.Delete(consolidationRoot, recursive: true);
            }
        }
    }

    private static async Task<RenderPlan> ConsolidateSegmentsAsync(
        RenderPlan plan,
        string temporaryRoot,
        CancellationToken cancellationToken)
    {
        List<TimelineClip> consolidated = [];
        foreach (IGrouping<TimelineTrackKind, TimelineClip> track in plan.Clips
                     .GroupBy(clip => clip.Track))
        {
            TimelineClip[] clips = track
                .OrderBy(clip => clip.Range.Start)
                .ToArray();
            if (clips.Length == 1)
            {
                consolidated.Add(clips[0]);
                continue;
            }

            ValidateContiguous(clips);
            Directory.CreateDirectory(temporaryRoot);
            string extension = Path.GetExtension(clips[0].SourcePath);
            string outputPath = Path.Combine(
                temporaryRoot,
                $"{track.Key}{extension}");
            SegmentConcatenationResult result =
                await FfmpegSegmentConcatenator.ConcatenateAsync(
                    clips.Select(clip =>
                            ResolveSourcePath(
                                plan.ProjectPath,
                                clip.SourcePath))
                        .ToArray(),
                    outputPath,
                    cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"{track.Key} segments could not be joined: {result.Error}");
            }

            consolidated.Add(
                new TimelineClip(
                    $"{track.Key}-consolidated",
                    track.Key,
                    result.OutputPath,
                    TimelineRange.FromStartAndDuration(
                        TimeSpan.Zero,
                        clips[^1].Range.End)));
        }

        return plan with
        {
            Clips = consolidated
                .OrderBy(clip => clip.Track)
                .ToArray(),
        };
    }

    private static void AddAudioFilter(
        List<string> filters,
        List<AudioFilterOutput> outputs,
        RenderPlan plan,
        TimelineClip[] clips,
        int[] inputIndices,
        TimelineTrackKind track,
        string label)
    {
        if (inputIndices.Length == 0)
        {
            return;
        }
        string sourceLabel = AddConcatFilter(
            filters,
            clips,
            inputIndices,
            $"{label}Source",
            isVideo: false);

        TimelineAutomationEvent? rateEvent = plan.Automation.FirstOrDefault(item =>
            item.TargetTrack == track &&
            item.Kind == nameof(SevenRecord.Domain.Audio.AudioRepairEventKind.AdjustPlaybackRate));
        double rate = 1;
        if (rateEvent is not null)
        {
            string value = rateEvent.Description.Replace(
                "Playback rate ",
                string.Empty,
                StringComparison.OrdinalIgnoreCase);
            if (!double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out rate))
            {
                throw new InvalidOperationException(
                    $"Invalid playback-rate automation: {rateEvent.Description}");
            }
        }

        TimelineAutomationEvent[] gaps = plan.Automation
            .Where(item =>
                item.TargetTrack == track &&
                item.Kind == nameof(SevenRecord.Domain.Audio.AudioRepairEventKind.InsertSilence))
            .OrderBy(item => item.Range.Start)
            .ToArray();
        string preparedLabel = $"{label}prepared";
        if (gaps.Length == 0)
        {
            filters.Add($"[{sourceLabel}]aresample=48000[{preparedLabel}]");
        }
        else
        {
            string splitLabels = string.Concat(
                Enumerable.Range(0, gaps.Length + 1)
                    .Select(index => $"[{label}source{index}]"));
            filters.Add($"[{sourceLabel}]asplit={gaps.Length + 1}{splitLabels}");

            List<string> concatLabels = [];
            double sourceCursor = 0;
            double insertedDuration = 0;
            for (int index = 0; index < gaps.Length; index++)
            {
                double sourceEnd =
                    gaps[index].Range.Start.TotalSeconds - insertedDuration;
                if (sourceEnd < sourceCursor)
                {
                    throw new InvalidOperationException(
                        $"Overlapping audio gap automation on {track}.");
                }

                string clipLabel = $"{label}clip{index}";
                filters.Add(
                    $"[{label}source{index}]atrim=start={Seconds(sourceCursor)}:" +
                    $"end={Seconds(sourceEnd)},asetpts=PTS-STARTPTS,aresample=48000[{clipLabel}]");
                concatLabels.Add($"[{clipLabel}]");

                string silenceLabel = $"{label}silence{index}";
                filters.Add(
                    $"anullsrc=r=48000:cl=stereo:d={Seconds(gaps[index].Range.Duration.TotalSeconds)}" +
                    $"[{silenceLabel}]");
                concatLabels.Add($"[{silenceLabel}]");

                sourceCursor = sourceEnd;
                insertedDuration += gaps[index].Range.Duration.TotalSeconds;
            }

            string finalClipLabel = $"{label}clip{gaps.Length}";
            filters.Add(
                $"[{label}source{gaps.Length}]atrim=start={Seconds(sourceCursor)}," +
                $"asetpts=PTS-STARTPTS,aresample=48000[{finalClipLabel}]");
            concatLabels.Add($"[{finalClipLabel}]");
            filters.Add(
                $"{string.Concat(concatLabels)}concat=n={concatLabels.Count}:v=0:a=1[{preparedLabel}]");
        }

        string rateLabel = $"{label}rate";
        filters.Add(
            rate == 1
                ? $"[{preparedLabel}]anull[{rateLabel}]"
                : $"[{preparedLabel}]atempo={rate.ToString("F6", CultureInfo.InvariantCulture)}[{rateLabel}]");
        rateLabel = AddTimelineEditFilter(
            filters,
            rateLabel,
            plan.EditSlices,
            $"{label}Edited",
            isVideo: false);
        SevenRecord.Domain.Audio.AudioMixSettings mix = track is
            TimelineTrackKind.Microphone
            ? plan.AudioMix.Microphone
            : plan.AudioMix.SystemAudio;
        outputs.Add(new AudioFilterOutput(rateLabel, mix));
    }

    private static string AddVolumeFilter(
        List<string> filters,
        AudioFilterOutput output,
        string outputLabel)
    {
        filters.Add(
            output.Mix.IsMuted
                ? $"[{output.Label}]volume=0[{outputLabel}]"
                : $"[{output.Label}]volume=" +
                  $"{output.Mix.GainDecibels.ToString("F2", CultureInfo.InvariantCulture)}dB" +
                  $"[{outputLabel}]");
        return outputLabel;
    }

    private sealed record AudioFilterOutput(
        string Label,
        SevenRecord.Domain.Audio.AudioMixSettings Mix);

    private static string Seconds(double value) =>
        value.ToString("F6", CultureInfo.InvariantCulture);

    private static TimelineClip[] ClipsFor(
        RenderPlan plan,
        TimelineTrackKind track) =>
        plan.Clips
            .Where(clip => clip.Track == track)
            .OrderBy(clip => clip.Range.Start)
            .ToArray();

    private static int[] InputIndices(
        List<TimelineClip> inputs,
        TimelineClip[] clips) =>
        clips.Select(clip => inputs.IndexOf(clip)).ToArray();

    private static string AddConcatFilter(
        List<string> filters,
        TimelineClip[] clips,
        int[] inputIndices,
        string label,
        bool isVideo)
    {
        if (clips.Length != inputIndices.Length)
        {
            throw new ArgumentException(
                "Clip and input index counts must match.");
        }
        if (clips.Length == 1)
        {
            return $"{inputIndices[0]}:{(isVideo ? "v" : "a")}";
        }

        ValidateContiguous(clips);
        List<string> parts = [];
        for (int index = 0; index < clips.Length; index++)
        {
            string part = $"{label}Part{index}";
            filters.Add(
                $"[{inputIndices[index]}:{(isVideo ? "v" : "a")}]" +
                $"{(isVideo ? "setpts=PTS-STARTPTS" : "asetpts=PTS-STARTPTS")}" +
                $"[{part}]");
            parts.Add($"[{part}]");
        }
        filters.Add(
            string.Concat(parts) +
            $"concat=n={parts.Count}:v={(isVideo ? 1 : 0)}:" +
            $"a={(isVideo ? 0 : 1)}[{label}]");
        return label;
    }

    private static string AddTimelineEditFilter(
        List<string> filters,
        string inputLabel,
        IReadOnlyList<TimelineEditSlice> editSlices,
        string outputLabel,
        bool isVideo)
    {
        if (editSlices.Count == 0)
        {
            return inputLabel;
        }

        List<string> parts = [];
        string[] inputs;
        if (editSlices.Count == 1)
        {
            inputs = [inputLabel];
        }
        else
        {
            inputs = Enumerable.Range(0, editSlices.Count)
                .Select(index => $"{outputLabel}Source{index}")
                .ToArray();
            filters.Add(
                $"[{inputLabel}]{(isVideo ? "split" : "asplit")}=" +
                $"{editSlices.Count}" +
                string.Concat(inputs.Select(item => $"[{item}]")));
        }
        for (int index = 0; index < editSlices.Count; index++)
        {
            TimelineEditSlice slice = editSlices[index];
            string part = $"{outputLabel}Part{index}";
            filters.Add(
                $"[{inputs[index]}]{(isVideo ? "trim" : "atrim")}=" +
                $"start={Seconds(slice.SourceRange.Start.TotalSeconds)}:" +
                $"end={Seconds(slice.SourceRange.End.TotalSeconds)}," +
                $"{(isVideo ? "setpts" : "asetpts")}=PTS-STARTPTS" +
                $"[{part}]");
            parts.Add($"[{part}]");
        }
        filters.Add(
            string.Concat(parts) +
            $"concat=n={parts.Count}:v={(isVideo ? 1 : 0)}:" +
            $"a={(isVideo ? 0 : 1)}[{outputLabel}]");
        return outputLabel;
    }

    private static void ValidateContiguous(
        IReadOnlyList<TimelineClip> clips)
    {
        TimeSpan expectedStart = TimeSpan.Zero;
        foreach (TimelineClip clip in clips)
        {
            TimeSpan difference = clip.Range.Start - expectedStart;
            if (difference.Duration() > TimeSpan.FromMilliseconds(150))
            {
                throw new InvalidOperationException(
                    $"{clip.Track} segments are not contiguous at " +
                    $"{clip.Range.Start:hh\\:mm\\:ss\\.fff}.");
            }
            expectedStart = clip.Range.End;
        }
    }

    private static int EvenDimension(double value)
    {
        int rounded = Math.Max(2, (int)Math.Round(value));
        return rounded % 2 == 0 ? rounded : rounded - 1;
    }

    private static double AutomationValue(
        TimelineAutomationEvent? automation,
        string key,
        double fallback) =>
        automation is not null &&
        automation.NumericData.TryGetValue(key, out double value) &&
        double.IsFinite(value)
            ? value
            : fallback;

    private static double ZoomValue(
        TimelineAutomationEvent zoom,
        string key,
        double defaultValue) =>
        zoom.NumericData.TryGetValue(key, out double value)
            ? value
            : defaultValue;

    private static void AddSpeedFilters(
        List<string> filters,
        string inputLabel,
        string outputLabel,
        TimelineAutomationEvent[] events,
        TimeSpan sourceDuration,
        bool isVideo)
    {
        if (events.Length == 0)
        {
            filters.Add(
                isVideo
                    ? $"[{inputLabel}]null[{outputLabel}]"
                    : $"[{inputLabel}]anull[{outputLabel}]");
            return;
        }

        List<SpeedSegment> segments = [];
        TimeSpan cursor = TimeSpan.Zero;
        foreach (TimelineAutomationEvent item in events)
        {
            double speed = ZoomValue(item, "speed", 4);
            if (speed <= 1)
            {
                throw new InvalidOperationException(
                    $"Loading speed must be greater than 1: {speed}.");
            }

            if (item.Range.Start < cursor || item.Range.End > sourceDuration)
            {
                throw new InvalidOperationException(
                    "Loading-speed events overlap or exceed source duration.");
            }

            if (item.Range.Start > cursor)
            {
                segments.Add(new SpeedSegment(cursor, item.Range.Start, 1));
            }

            segments.Add(new SpeedSegment(item.Range.Start, item.Range.End, speed));
            cursor = item.Range.End;
        }

        if (cursor < sourceDuration)
        {
            segments.Add(new SpeedSegment(cursor, sourceDuration, 1));
        }

        string splitOutputs = string.Concat(
            Enumerable.Range(0, segments.Count)
                .Select(index => $"[{outputLabel}source{index}]"));
        filters.Add(
            $"[{inputLabel}]{(isVideo ? "split" : "asplit")}={segments.Count}{splitOutputs}");
        List<string> concatLabels = [];
        for (int index = 0; index < segments.Count; index++)
        {
            SpeedSegment segment = segments[index];
            string segmentLabel = $"{outputLabel}segment{index}";
            string trim = isVideo ? "trim" : "atrim";
            string timestamps = isVideo ? "setpts" : "asetpts";
            string rateFilter = segment.Speed == 1
                ? string.Empty
                : isVideo
                    ? $"/{Seconds(segment.Speed)}"
                    : $",{AtempoChain(segment.Speed)}";
            filters.Add(
                $"[{outputLabel}source{index}]{trim}=start={Seconds(segment.Start.TotalSeconds)}:" +
                $"end={Seconds(segment.End.TotalSeconds)},{timestamps}=(PTS-STARTPTS){rateFilter}" +
                $"[{segmentLabel}]");
            concatLabels.Add($"[{segmentLabel}]");
        }

        filters.Add(
            $"{string.Concat(concatLabels)}concat=n={segments.Count}:" +
            $"v={(isVideo ? 1 : 0)}:a={(isVideo ? 0 : 1)}[{outputLabel}]");
    }

    private static string AtempoChain(double speed)
    {
        List<string> filters = [];
        double remaining = speed;
        while (remaining > 2.000001)
        {
            filters.Add("atempo=2.000000");
            remaining /= 2;
        }

        if (Math.Abs(remaining - 1) > 0.000001)
        {
            filters.Add($"atempo={Seconds(remaining)}");
        }

        return string.Join(",", filters);
    }

    private static string EscapeFilterPath(string path) =>
        Path.GetFullPath(path)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    private static string ResolveSourcePath(string projectPath, string sourcePath)
    {
        string root = Path.GetFullPath(projectPath);
        string fullPath = Path.GetFullPath(Path.Combine(root, sourcePath));
        string prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Source path escapes the project: {sourcePath}");
        }

        return fullPath;
    }

    private sealed record SpeedSegment(
        TimeSpan Start,
        TimeSpan End,
        double Speed);
}
