using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SevenRecord.Domain.Timeline;
using SevenRecord.Media;

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
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        TimelineAutomationEvent[] unsupported = plan.Automation
            .Where(item =>
                item.Kind is not "PresenterLayout" and
                not nameof(SevenRecord.Domain.Audio.AudioRepairEventKind.AdjustPlaybackRate) and
                not nameof(SevenRecord.Domain.Audio.AudioRepairEventKind.InsertSilence))
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
        List<string> filters =
        [
            $"[{screenIndex}:v]scale={plan.Canvas.Width}:{plan.Canvas.Height}:" +
            "force_original_aspect_ratio=decrease," +
            $"pad={plan.Canvas.Width}:{plan.Canvas.Height}:(ow-iw)/2:(oh-ih)/2:color=black[base]"
        ];

        if (cameraIndex >= 0)
        {
            int cameraWidth = Math.Max(240, plan.Canvas.Width / 4);
            filters.Add($"[{cameraIndex}:v]scale={cameraWidth}:-2[camera]");
            filters.Add("[base][camera]overlay=W-w-48:H-h-48[video]");
        }
        else
        {
            filters.Add("[base]null[video]");
        }

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
                "amix=inputs=2:duration=longest:normalize=0,loudnorm=I=-16:TP=-1.5:LRA=11[audio]");
        }
        else if (audioLabels.Count == 1)
        {
            filters.Add($"{audioLabels[0]}loudnorm=I=-16:TP=-1.5:LRA=11[audio]");
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

        FfmpegExportCommand command;
        try
        {
            command = CreateCommand(plan, outputPath);
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
}
