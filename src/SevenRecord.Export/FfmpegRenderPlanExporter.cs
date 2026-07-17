using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SevenRecord.Domain.Captions;
using SevenRecord.Domain.Timeline;
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

        TimelineClip screen = plan.Clips
            .FirstOrDefault(clip => clip.Track is TimelineTrackKind.Screen)
            ?? throw new InvalidOperationException("The render plan has no screen source.");
        TimelineClip? camera = plan.Clips
            .FirstOrDefault(clip => clip.Track is TimelineTrackKind.Camera);
        TimelineClip? microphone = plan.Clips
            .FirstOrDefault(clip => clip.Track is TimelineTrackKind.Microphone);
        TimelineClip? systemAudio = plan.Clips
            .FirstOrDefault(clip => clip.Track is TimelineTrackKind.SystemAudio);

        List<TimelineClip> inputs = [screen];
        if (camera is not null)
        {
            inputs.Add(camera);
        }

        if (microphone is not null)
        {
            inputs.Add(microphone);
        }

        if (systemAudio is not null)
        {
            inputs.Add(systemAudio);
        }

        List<string> arguments = ["-hide_banner", "-loglevel", "error", "-y"];
        foreach (TimelineClip input in inputs)
        {
            arguments.Add("-i");
            arguments.Add(ResolveSourcePath(plan.ProjectPath, input.SourcePath));
        }

        int screenIndex = inputs.IndexOf(screen);
        int cameraIndex = camera is null ? -1 : inputs.IndexOf(camera);
        int microphoneIndex = microphone is null ? -1 : inputs.IndexOf(microphone);
        int systemAudioIndex = systemAudio is null ? -1 : inputs.IndexOf(systemAudio);
        TimeSpan sourceDuration = plan.Clips.Count == 0
            ? plan.Duration
            : plan.Clips.Max(clip => clip.Range.End);
        TimelineAutomationEvent[] loadingEvents = plan.Automation
            .Where(item => item.Kind == "LoadingSpeed")
            .OrderBy(item => item.Range.Start)
            .ToArray();
        List<string> filters =
        [
            $"[{screenIndex}:v]scale={plan.Canvas.Width}:{plan.Canvas.Height}:" +
            "force_original_aspect_ratio=decrease," +
            $"pad={plan.Canvas.Width}:{plan.Canvas.Height}:(ow-iw)/2:(oh-ih)/2:color=black[base]"
        ];
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

        if (cameraIndex >= 0)
        {
            int cameraWidth = Math.Max(240, plan.Canvas.Width / 4);
            filters.Add($"[{cameraIndex}:v]scale={cameraWidth}:-2[camera]");
            filters.Add($"[{screenLabel}][camera]overlay=W-w-48:H-h-48[composite]");
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

        List<string> audioLabels = [];
        AddAudioFilter(
            filters,
            audioLabels,
            plan,
            microphoneIndex,
            TimelineTrackKind.Microphone,
            "microphone");
        AddAudioFilter(
            filters,
            audioLabels,
            plan,
            systemAudioIndex,
            TimelineTrackKind.SystemAudio,
            "system");
        if (audioLabels.Count == 2)
        {
            filters.Add(
                $"{audioLabels[0]}{audioLabels[1]}" +
                "amix=inputs=2:duration=longest:normalize=0,loudnorm=I=-16:TP=-1.5:LRA=11[mixed]");
        }
        else if (audioLabels.Count == 1)
        {
            filters.Add($"{audioLabels[0]}loudnorm=I=-16:TP=-1.5:LRA=11[mixed]");
        }
        if (audioLabels.Count > 0)
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
        if (audioLabels.Count > 0)
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
        if (audioLabels.Count > 0)
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

            FfmpegExportCommand command;
            try
            {
                command = CreateCommand(plan, outputPath, subtitlePath);
            }
            catch (InvalidOperationException exception)
            {
                return new RenderPlanExportResult(false, outputPath, exception.Message);
            }
            catch (NotSupportedException exception)
            {
                return new RenderPlanExportResult(false, outputPath, exception.Message);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
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
                return process.ExitCode == 0
                    ? new RenderPlanExportResult(true, Path.GetFullPath(outputPath), null)
                    : new RenderPlanExportResult(
                        false,
                        outputPath,
                        string.IsNullOrWhiteSpace(standardError)
                            ? $"FFmpeg exited with code {process.ExitCode}."
                            : standardError.Trim());
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
            if (subtitlePath is not null && File.Exists(subtitlePath))
            {
                File.Delete(subtitlePath);
            }
        }
    }

    private static void AddAudioFilter(
        List<string> filters,
        List<string> labels,
        RenderPlan plan,
        int inputIndex,
        TimelineTrackKind track,
        string label)
    {
        if (inputIndex < 0)
        {
            return;
        }

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
            filters.Add($"[{inputIndex}:a]aresample=48000[{preparedLabel}]");
        }
        else
        {
            string splitLabels = string.Concat(
                Enumerable.Range(0, gaps.Length + 1)
                    .Select(index => $"[{label}source{index}]"));
            filters.Add($"[{inputIndex}:a]asplit={gaps.Length + 1}{splitLabels}");

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

        filters.Add(
            rate == 1
                ? $"[{preparedLabel}]anull[{label}]"
                : $"[{preparedLabel}]atempo={rate.ToString("F6", CultureInfo.InvariantCulture)}[{label}]");
        labels.Add($"[{label}]");
    }

    private static string Seconds(double value) =>
        value.ToString("F6", CultureInfo.InvariantCulture);

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
