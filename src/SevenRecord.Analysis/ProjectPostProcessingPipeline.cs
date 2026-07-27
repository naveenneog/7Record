using System.Text;
using System.Text.Json;
using SevenRecord.Domain.Audio;
using SevenRecord.Domain.Input;
using SevenRecord.Domain.Video;
using SevenRecord.Media;
using SevenRecord.Recording;

namespace SevenRecord.Analysis;

public enum ProjectPostProcessingStageState
{
    Completed,
    Skipped,
    Failed,
}

public sealed record ProjectPostProcessingStageResult(
    string Stage,
    ProjectPostProcessingStageState State,
    int SuggestedEdits,
    bool Changed,
    string? Message);

public sealed record ProjectPostProcessingResult(
    string ProjectRoot,
    IReadOnlyList<ProjectPostProcessingStageResult> Stages)
{
    public bool Succeeded =>
        Stages.All(stage => stage.State is not ProjectPostProcessingStageState.Failed);

    public int SuggestedEdits => Stages.Sum(stage => stage.SuggestedEdits);
}

public delegate Task<LoadingDetectionWorkerResult> LoadingDetectionRunner(
    string workerPath,
    string screenMediaPath,
    string outputJsonPath,
    CancellationToken cancellationToken);

public delegate Task<SegmentConcatenationResult> SegmentConcatenationRunner(
    string workerPath,
    IReadOnlyList<string> inputPaths,
    string outputPath,
    CancellationToken cancellationToken);

public delegate Task<SilenceDetectionWorkerResult> SilenceDetectionRunner(
    string workerPath,
    string audioMediaPath,
    string outputJsonPath,
    CancellationToken cancellationToken);

public sealed class ProjectPostProcessingPipeline
{
    public const string AudioRepairStage = "audio-repair";
    public const string CursorZoomStage = "cursor-zoom";
    public const string LoadingSpeedStage = "loading-speed";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private readonly LoadingDetectionRunner _loadingDetectionRunner;
    private readonly SegmentConcatenationRunner _segmentConcatenationRunner;
    private readonly SilenceDetectionRunner _silenceDetectionRunner;

    public ProjectPostProcessingPipeline(
        LoadingDetectionRunner? loadingDetectionRunner = null,
        SegmentConcatenationRunner? segmentConcatenationRunner = null,
        SilenceDetectionRunner? silenceDetectionRunner = null)
    {
        _loadingDetectionRunner =
            loadingDetectionRunner ?? MediaWorkerLoadingClient.DetectAsync;
        _segmentConcatenationRunner =
            segmentConcatenationRunner ??
            MediaWorkerConcatenationClient.ConcatenateAsync;
        _silenceDetectionRunner =
            silenceDetectionRunner ?? MediaWorkerSilenceClient.DetectAsync;
    }

    public async Task<ProjectPostProcessingResult> RunAsync(
        string projectRoot,
        string? mediaWorkerPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string fullProjectRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(fullProjectRoot))
        {
            throw new DirectoryNotFoundException(
                $"Recording project '{fullProjectRoot}' does not exist.");
        }

        List<ProjectPostProcessingStageResult> stages = [];
        cancellationToken.ThrowIfCancellationRequested();
        stages.Add(
            await RunCursorZoomStageAsync(fullProjectRoot, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        stages.Add(
            await RunLoadingSpeedStageAsync(
                fullProjectRoot,
                mediaWorkerPath,
                cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        stages.Add(
            await RunAudioRepairStageAsync(fullProjectRoot, cancellationToken));
        return new ProjectPostProcessingResult(fullProjectRoot, stages);
    }

    private static async Task<ProjectPostProcessingStageResult> RunCursorZoomStageAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        string inputPath = Path.Combine(projectRoot, "cursor-events.json");
        if (!File.Exists(inputPath))
        {
            return Skipped(CursorZoomStage, "Cursor metadata is unavailable.");
        }

        try
        {
            CursorMetadataDocument document =
                await ReadRequiredJsonAsync<CursorMetadataDocument>(
                    inputPath,
                    cancellationToken);
            IReadOnlyList<CursorZoomEvent> zooms =
                CursorZoomPlanner.CreatePlan(document);
            bool changed = await WriteJsonIfChangedAsync(
                Path.Combine(projectRoot, "cursor-zoom-plan.json"),
                zooms,
                cancellationToken);
            return Completed(CursorZoomStage, zooms.Count, changed);
        }
        catch (Exception exception) when (IsStageFailure(exception))
        {
            return Failed(CursorZoomStage, exception);
        }
    }

    private async Task<ProjectPostProcessingStageResult> RunLoadingSpeedStageAsync(
        string projectRoot,
        string? mediaWorkerPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mediaWorkerPath) ||
            !File.Exists(mediaWorkerPath))
        {
            return Skipped(
                LoadingSpeedStage,
                "The media worker is unavailable.");
        }

        string journalPath = Path.Combine(projectRoot, "recording.journal");
        if (!File.Exists(journalPath))
        {
            return Skipped(LoadingSpeedStage, "The recording journal is unavailable.");
        }

        string outputPath = Path.Combine(projectRoot, "loading-speed-plan.json");
        try
        {
            using RecordingJournal journal = new(journalPath);
            RecordingJournalReplay replay =
                await journal.ReplayAsync(cancellationToken);
            RecordingSegmentEntry[] screens = replay.Entries
                .Where(entry =>
                    string.Equals(
                        entry.SourceId,
                        "screen",
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Sequence)
                .OrderBy(entry => entry.StartTicks)
                .ThenBy(entry => entry.Sequence)
                .ToArray();
            if (screens.Length == 0)
            {
                return Skipped(LoadingSpeedStage, "No screen source was published.");
            }

            List<string> screenPaths = [];
            foreach (RecordingSegmentEntry screen in screens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string screenPath = RecordingPathGuard.ResolveWithinRoot(
                    projectRoot,
                    screen.RelativePath);
                if (!File.Exists(screenPath))
                {
                    throw new FileNotFoundException(
                        "The screen source referenced by the journal is missing.",
                        screenPath);
                }
                screenPaths.Add(screenPath);
            }

            string? concatenatedPath = null;
            string analysisPath = screenPaths[0];
            string workerOutputPath =
                outputPath + $".worker-{Guid.NewGuid():N}.tmp";
            try
            {
                if (screenPaths.Count > 1)
                {
                    concatenatedPath = Path.Combine(
                        projectRoot,
                        "temp",
                        $"loading-analysis-{Guid.NewGuid():N}.mp4");
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(concatenatedPath)!);
                    SegmentConcatenationResult concatenation =
                        await _segmentConcatenationRunner(
                            Path.GetFullPath(mediaWorkerPath),
                            screenPaths,
                            concatenatedPath,
                            cancellationToken);
                    if (!concatenation.Succeeded)
                    {
                        InvalidatePlan(outputPath);
                        return Failed(
                            LoadingSpeedStage,
                            new InvalidOperationException(
                                concatenation.Error ??
                                "Screen segments could not be joined."));
                    }
                    analysisPath = concatenation.OutputPath;
                }

                LoadingDetectionWorkerResult workerResult =
                    await _loadingDetectionRunner(
                        Path.GetFullPath(mediaWorkerPath),
                        analysisPath,
                        workerOutputPath,
                        cancellationToken);
                if (!workerResult.Succeeded)
                {
                    InvalidatePlan(outputPath);
                    return new ProjectPostProcessingStageResult(
                        LoadingSpeedStage,
                        ProjectPostProcessingStageState.Failed,
                        0,
                        false,
                        workerResult.Error ?? "Loading detection failed.");
                }

                LoadingSpeedEvent[] events =
                    await ReadRequiredJsonAsync<LoadingSpeedEvent[]>(
                        workerOutputPath,
                        cancellationToken);
                CursorMetadataDocument? cursor = await TryReadJsonAsync<
                    CursorMetadataDocument>(
                    Path.Combine(projectRoot, "cursor-events.json"),
                    cancellationToken);
                AudioTimingManifest? audioTiming =
                    await TryReadJsonAsync<AudioTimingManifest>(
                        Path.Combine(projectRoot, "audio-timing.json"),
                        cancellationToken);
                HashSet<AudioTrackKind> journaledAudioTracks = replay.Entries
                    .Where(entry => entry.SourceId is
                        "microphone" or "system-audio")
                    .Select(entry => entry.SourceId == "microphone"
                        ? AudioTrackKind.Microphone
                        : AudioTrackKind.SystemAudio)
                    .ToHashSet();
                HashSet<AudioTrackKind> timedAudioTracks =
                    audioTiming?.Tracks
                        .Select(track => track.Track)
                        .ToHashSet() ?? [];
                if (!journaledAudioTracks.IsSubsetOf(timedAudioTracks))
                {
                    throw new InvalidDataException(
                        "Loading confidence requires timing metadata for every audio track.");
                }
                AudioGapMetadata[] microphoneGaps = audioTiming?.Tracks
                    .Where(track =>
                        track.Track is AudioTrackKind.Microphone)
                    .SelectMany(track => track.Gaps)
                    .OrderBy(gap => gap.Start)
                    .ToArray() ?? [];
                AudioGapMetadata[] systemAudioGaps = audioTiming?.Tracks
                    .Where(track =>
                        track.Track is AudioTrackKind.SystemAudio)
                    .SelectMany(track => track.Gaps)
                    .OrderBy(gap => gap.Start)
                    .ToArray() ?? [];
                AudioGapMetadata[] allAudioGaps =
                [
                    .. microphoneGaps,
                    .. systemAudioGaps,
                ];
                List<IReadOnlyList<AudioSilenceInterval>> audioSilence = [];
                IReadOnlyList<AudioSilenceInterval>? microphoneSilence =
                    await DetectAudioSilenceAsync(
                        projectRoot,
                        replay,
                        "microphone",
                        Path.GetFullPath(mediaWorkerPath),
                        microphoneGaps,
                        cancellationToken);
                if (microphoneSilence is not null)
                {
                    audioSilence.Add(microphoneSilence);
                }
                IReadOnlyList<AudioSilenceInterval>? systemSilence =
                    await DetectAudioSilenceAsync(
                        projectRoot,
                        replay,
                        "system-audio",
                        Path.GetFullPath(mediaWorkerPath),
                        systemAudioGaps,
                        cancellationToken);
                if (systemSilence is not null)
                {
                    audioSilence.Add(systemSilence);
                }
                LoadingSpeedEvent[] normalized =
                    LoadingConfidencePlanner.Refine(
                            events,
                            cursor,
                            audioSilence,
                            allAudioGaps)
                .Select((item, index) => item with
                {
                    Id = $"loading-{index:D4}-{item.Start.Ticks:x16}",
                })
                .ToArray();
                bool changed = await WriteJsonIfChangedAsync(
                    outputPath,
                    normalized,
                    cancellationToken);
                return Completed(
                    LoadingSpeedStage,
                    normalized.Length,
                    changed);
            }
            finally
            {
                if (File.Exists(workerOutputPath))
                {
                    File.Delete(workerOutputPath);
                }
                if (concatenatedPath is not null &&
                    File.Exists(concatenatedPath))
                {
                    File.Delete(concatenatedPath);
                }
            }

        }
        catch (OperationCanceledException)
        {
            InvalidatePlan(outputPath);
            throw;
        }
        catch (Exception exception) when (IsStageFailure(exception))
        {
            InvalidatePlan(outputPath);
            return Failed(LoadingSpeedStage, exception);
        }
    }

    private async Task<IReadOnlyList<AudioSilenceInterval>?>
        DetectAudioSilenceAsync(
            string projectRoot,
            RecordingJournalReplay replay,
            string sourceId,
            string mediaWorkerPath,
            IReadOnlyList<AudioGapMetadata> audioGaps,
            CancellationToken cancellationToken)
    {
        RecordingSegmentEntry[] entries = replay.Entries
            .Where(entry => string.Equals(
                entry.SourceId,
                sourceId,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.StartTicks)
            .ThenBy(entry => entry.Sequence)
            .ToArray();
        if (entries.Length == 0)
        {
            return null;
        }

        List<AudioSilenceInterval> allIntervals = [];
        foreach (RecordingSegmentEntry entry in entries)
        {
            string analysisPath = RecordingPathGuard.ResolveWithinRoot(
                projectRoot,
                entry.RelativePath);
            string silenceOutputPath = Path.Combine(
                projectRoot,
                "temp",
                $"{sourceId}-silence-{Guid.NewGuid():N}.json");
            try
            {
                SilenceDetectionWorkerResult result =
                    await _silenceDetectionRunner(
                        mediaWorkerPath,
                        analysisPath,
                        silenceOutputPath,
                        cancellationToken);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        result.Error ??
                        $"{sourceId} silence detection failed.");
                }
                AudioSilenceInterval[] intervals =
                    await ReadRequiredJsonAsync<AudioSilenceInterval[]>(
                        silenceOutputPath,
                        cancellationToken);
                TimeSpan segmentStart =
                    TimeSpan.FromTicks(entry.StartTicks);
                allIntervals.AddRange(
                    intervals.Select(interval => interval with
                    {
                        Start = MapAudioTimeToProject(
                            segmentStart,
                            interval.Start,
                            audioGaps),
                        Duration =
                            MapAudioTimeToProject(
                                segmentStart,
                                interval.End,
                                audioGaps) -
                            MapAudioTimeToProject(
                                segmentStart,
                                interval.Start,
                                audioGaps),
                    }));
            }
            finally
            {
                if (File.Exists(silenceOutputPath))
                {
                    File.Delete(silenceOutputPath);
                }
            }
        }
        return allIntervals;
    }

    private static TimeSpan MapAudioTimeToProject(
        TimeSpan segmentStart,
        TimeSpan mediaTime,
        IReadOnlyList<AudioGapMetadata> audioGaps)
    {
        TimeSpan projectTime = segmentStart + mediaTime;
        while (true)
        {
            TimeSpan missing = TimeSpan.FromTicks(
                audioGaps
                    .Where(gap =>
                        gap.Start >= segmentStart &&
                        gap.Start <= projectTime)
                    .Sum(gap => gap.Duration.Ticks));
            TimeSpan adjusted = segmentStart + mediaTime + missing;
            if (adjusted == projectTime)
            {
                return adjusted;
            }
            projectTime = adjusted;
        }
    }

    private static async Task<ProjectPostProcessingStageResult> RunAudioRepairStageAsync(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        string inputPath = Path.Combine(projectRoot, "audio-timing.json");
        if (!File.Exists(inputPath))
        {
            return Skipped(AudioRepairStage, "Audio timing metadata is unavailable.");
        }

        try
        {
            AudioTimingManifest manifest =
                await ReadRequiredJsonAsync<AudioTimingManifest>(
                    inputPath,
                    cancellationToken);
            IReadOnlyList<AudioRepairEvent> repairs =
                AudioRepairPlanner.CreatePlan(manifest);
            bool changed = await WriteJsonIfChangedAsync(
                Path.Combine(projectRoot, "audio-repair-plan.json"),
                repairs,
                cancellationToken);
            return Completed(AudioRepairStage, repairs.Count, changed);
        }
        catch (Exception exception) when (IsStageFailure(exception))
        {
            return Failed(AudioRepairStage, exception);
        }
    }

    private static async Task<T> ReadRequiredJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<T>(json, SerializerOptions) ??
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is empty or invalid.");
    }

    private static async Task<T?> TryReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }
        string json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }

    private static void InvalidatePlan(string outputPath)
    {
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }

    private static async Task<bool> WriteJsonIfChangedAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(value, SerializerOptions);
        if (File.Exists(path))
        {
            string existing = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.Equals(existing, json, StringComparison.Ordinal))
            {
                return false;
            }
        }

        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            json,
            Utf8WithoutBom,
            cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
        return true;
    }

    private static bool IsStageFailure(Exception exception) =>
        exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            InvalidOperationException or
            ArgumentException;

    private static ProjectPostProcessingStageResult Completed(
        string stage,
        int suggestedEdits,
        bool changed) =>
        new(
            stage,
            ProjectPostProcessingStageState.Completed,
            suggestedEdits,
            changed,
            changed ? "Plan updated." : "Plan is already current.");

    private static ProjectPostProcessingStageResult Skipped(
        string stage,
        string message) =>
        new(
            stage,
            ProjectPostProcessingStageState.Skipped,
            0,
            false,
            message);

    private static ProjectPostProcessingStageResult Failed(
        string stage,
        Exception exception) =>
        new(
            stage,
            ProjectPostProcessingStageState.Failed,
            0,
            false,
            exception.Message);
}
