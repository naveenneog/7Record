using System.Text.Json;
using SevenRecord.Domain.Audio;
using SevenRecord.Domain.Input;
using SevenRecord.Domain.Video;
using SevenRecord.Media;
using SevenRecord.Recording;

namespace SevenRecord.Analysis.Tests;

[TestClass]
public sealed class ProjectPostProcessingPipelineTests
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task RerunKeepsGeneratedPlansByteForByteStable()
    {
        string project = CreateTemporaryProject();
        try
        {
            await CreateProjectInputsAsync(project);
            string workerPath = Path.Combine(project, "worker.exe");
            await File.WriteAllBytesAsync(workerPath, [0]);
            ProjectPostProcessingPipeline pipeline = new(FakeLoadingDetectionAsync);

            ProjectPostProcessingResult first =
                await pipeline.RunAsync(project, workerPath);
            Dictionary<string, string> firstPlans = await ReadPlansAsync(project);
            ProjectPostProcessingResult second =
                await pipeline.RunAsync(project, workerPath);
            Dictionary<string, string> secondPlans = await ReadPlansAsync(project);

            Assert.IsTrue(first.Succeeded);
            Assert.IsTrue(first.Stages.All(stage => stage.Changed));
            Assert.IsTrue(second.Succeeded);
            Assert.IsTrue(second.Stages.All(stage => !stage.Changed));
            CollectionAssert.AreEquivalent(firstPlans, secondPlans);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [TestMethod]
    public async Task InvalidCursorMetadataDoesNotBlockAudioRepair()
    {
        string project = CreateTemporaryProject();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(project, "cursor-events.json"),
                "{ invalid");
            await WriteAudioTimingAsync(project);
            ProjectPostProcessingPipeline pipeline = new();

            ProjectPostProcessingResult result =
                await pipeline.RunAsync(project, mediaWorkerPath: null);

            ProjectPostProcessingStageResult cursor = result.Stages.Single(
                stage =>
                    stage.Stage == ProjectPostProcessingPipeline.CursorZoomStage);
            ProjectPostProcessingStageResult audio = result.Stages.Single(
                stage =>
                    stage.Stage == ProjectPostProcessingPipeline.AudioRepairStage);
            Assert.AreEqual(ProjectPostProcessingStageState.Failed, cursor.State);
            Assert.AreEqual(ProjectPostProcessingStageState.Completed, audio.State);
            Assert.IsTrue(
                File.Exists(Path.Combine(project, "audio-repair-plan.json")));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [TestMethod]
    public async Task CanceledRunDoesNotReportSuccessfulProcessing()
    {
        string project = CreateTemporaryProject();
        try
        {
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            ProjectPostProcessingPipeline pipeline = new();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(
                () => pipeline.RunAsync(
                    project,
                    mediaWorkerPath: null,
                    cancellation.Token));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [TestMethod]
    public async Task LoadingDetectionUsesConcatenatedScreenSegments()
    {
        string project = CreateTemporaryProject();
        try
        {
            string workerPath = Path.Combine(project, "worker.exe");
            await File.WriteAllBytesAsync(workerPath, [0]);
            await File.WriteAllBytesAsync(
                Path.Combine(project, "screen-1.mp4"),
                [0]);
            await File.WriteAllBytesAsync(
                Path.Combine(project, "screen-2.mp4"),
                [0]);
            await File.WriteAllBytesAsync(
                Path.Combine(project, "microphone.wav"),
                [0]);
            await File.WriteAllTextAsync(
                Path.Combine(project, "cursor-events.json"),
                JsonSerializer.Serialize(
                    new CursorMetadataDocument(
                        1,
                        [
                            new CursorMetadataEvent(
                                TimeSpan.FromSeconds(1.9),
                                10,
                                10,
                                0.5,
                                0.5,
                                CursorEventKind.Move,
                                CursorButton.None),
                            new CursorMetadataEvent(
                                TimeSpan.FromSeconds(3),
                                10,
                                10,
                                0.5,
                                0.5,
                                CursorEventKind.Move,
                                CursorButton.None),
                            new CursorMetadataEvent(
                                TimeSpan.FromSeconds(4.9),
                                10,
                                10,
                                0.5,
                                0.5,
                                CursorEventKind.Move,
                                CursorButton.None)
                        ])));
            await WriteAudioTimingAsync(project);
            using (RecordingJournal journal = new(
                       Path.Combine(project, "recording.journal")))
            {
                await journal.AppendAsync(
                    ScreenEntry(1, "screen-1.mp4", TimeSpan.Zero));
                await journal.AppendAsync(
                    ScreenEntry(
                        2,
                        "screen-2.mp4",
                        TimeSpan.FromSeconds(5)));
                await journal.AppendAsync(
                    new RecordingSegmentEntry(
                        3,
                        "microphone",
                        "microphone",
                        "microphone.wav",
                        0,
                        TimeSpan.FromSeconds(10).Ticks,
                        1,
                        new string('A', 64)));
            }

            ProjectPostProcessingPipeline pipeline = new(
                FakeLoadingDetectionAsync,
                FakeConcatenationAsync,
                FakeSilenceDetectionAsync);
            ProjectPostProcessingResult result =
                await pipeline.RunAsync(project, workerPath);
            LoadingSpeedEvent[] events = JsonSerializer.Deserialize<
                LoadingSpeedEvent[]>(
                await File.ReadAllTextAsync(
                    Path.Combine(project, "loading-speed-plan.json")),
                SerializerOptions) ?? [];

            Assert.IsTrue(result.Succeeded);
            Assert.HasCount(1, events);
            Assert.AreEqual(TimeSpan.FromSeconds(2), events[0].Start);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [TestMethod]
    public async Task ConfidenceFailureInvalidatesExistingLoadingPlan()
    {
        string project = CreateTemporaryProject();
        try
        {
            string workerPath = Path.Combine(project, "worker.exe");
            await File.WriteAllBytesAsync(workerPath, [0]);
            await File.WriteAllBytesAsync(
                Path.Combine(project, "screen.mp4"),
                [0]);
            await File.WriteAllBytesAsync(
                Path.Combine(project, "microphone.wav"),
                [0]);
            await File.WriteAllTextAsync(
                Path.Combine(project, "cursor-events.json"),
                JsonSerializer.Serialize(
                    new CursorMetadataDocument(1, [])));
            string loadingPath = Path.Combine(
                project,
                "loading-speed-plan.json");
            await File.WriteAllTextAsync(loadingPath, "[{\"old\":true}]");
            using (RecordingJournal journal = new(
                       Path.Combine(project, "recording.journal")))
            {
                await journal.AppendAsync(
                    ScreenEntry(1, "screen.mp4", TimeSpan.Zero));
                await journal.AppendAsync(
                    new RecordingSegmentEntry(
                        2,
                        "microphone",
                        "microphone",
                        "microphone.wav",
                        0,
                        TimeSpan.FromSeconds(5).Ticks,
                        1,
                        new string('A', 64)));
            }
            ProjectPostProcessingPipeline pipeline = new(
                FakeLoadingDetectionAsync,
                FakeConcatenationAsync,
                FakeSilenceFailureAsync);

            ProjectPostProcessingResult result =
                await pipeline.RunAsync(project, workerPath);

            Assert.IsFalse(result.Succeeded);
            Assert.IsFalse(File.Exists(loadingPath));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    private static async Task CreateProjectInputsAsync(string project)
    {
        CursorMetadataDocument cursor = new(
            1,
            [
                new CursorMetadataEvent(
                    TimeSpan.FromSeconds(2),
                    200,
                    300,
                    0.25,
                    0.75,
                    CursorEventKind.Click,
                    CursorButton.Left)
            ]);
        await File.WriteAllTextAsync(
            Path.Combine(project, "cursor-events.json"),
            JsonSerializer.Serialize(cursor));
        await WriteAudioTimingAsync(project);

        string screenPath = Path.Combine(project, "screen.mp4");
        await File.WriteAllBytesAsync(screenPath, [0]);
        using RecordingJournal journal = new(
            Path.Combine(project, "recording.journal"));
        await journal.AppendAsync(
            new RecordingSegmentEntry(
                1,
                "screen-segment",
                "screen",
                "screen.mp4",
                0,
                TimeSpan.FromSeconds(5).Ticks,
                1,
                new string('A', 64)));
    }

    private static async Task WriteAudioTimingAsync(string project)
    {
        AudioTimingManifest timing = new(
            1,
            [
                new AudioTrackTimingMetadata(
                    AudioTrackKind.Microphone,
                    [
                        new AudioGapMetadata(
                            TimeSpan.FromSeconds(1),
                            TimeSpan.FromMilliseconds(120))
                    ],
                    new AudioClockMetadata(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(10),
                        0))
            ]);
        await File.WriteAllTextAsync(
            Path.Combine(project, "audio-timing.json"),
            JsonSerializer.Serialize(timing));
    }

    private static RecordingSegmentEntry ScreenEntry(
        int sequence,
        string path,
        TimeSpan start) =>
        new(
            sequence,
            $"screen-{sequence}",
            "screen",
            path,
            start.Ticks,
            TimeSpan.FromSeconds(5).Ticks,
            1,
            new string('A', 64));

    private static async Task<LoadingDetectionWorkerResult>
        FakeLoadingDetectionAsync(
            string workerPath,
            string screenMediaPath,
            string outputJsonPath,
            CancellationToken cancellationToken)
    {
        LoadingSpeedEvent[] events =
        [
            new(
                Guid.NewGuid().ToString("N"),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(3),
                4,
                0.65)
        ];
        await File.WriteAllTextAsync(
            outputJsonPath,
            JsonSerializer.Serialize(events),
            cancellationToken);
        return new LoadingDetectionWorkerResult(true, events.Length, null);
    }

    private static async Task<SegmentConcatenationResult>
        FakeConcatenationAsync(
            string workerPath,
            IReadOnlyList<string> inputPaths,
            string outputPath,
            CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(
            outputPath,
            [0],
            cancellationToken);
        return new SegmentConcatenationResult(true, outputPath, null);
    }

    private static Task<SilenceDetectionWorkerResult>
        FakeSilenceFailureAsync(
            string workerPath,
            string audioMediaPath,
            string outputJsonPath,
            CancellationToken cancellationToken) =>
        Task.FromResult(
            new SilenceDetectionWorkerResult(
                false,
                0,
                "Synthetic silence failure."));

    private static async Task<SilenceDetectionWorkerResult>
        FakeSilenceDetectionAsync(
            string workerPath,
            string audioMediaPath,
            string outputJsonPath,
            CancellationToken cancellationToken)
    {
        AudioSilenceInterval[] intervals =
        [
            new(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(10))
        ];
        await File.WriteAllTextAsync(
            outputJsonPath,
            JsonSerializer.Serialize(intervals),
            cancellationToken);
        return new SilenceDetectionWorkerResult(
            true,
            intervals.Length,
            null);
    }

    private static async Task<Dictionary<string, string>> ReadPlansAsync(
        string project)
    {
        string[] names =
        [
            "cursor-zoom-plan.json",
            "loading-speed-plan.json",
            "audio-repair-plan.json",
        ];
        Dictionary<string, string> plans = new();
        foreach (string name in names)
        {
            plans[name] = await File.ReadAllTextAsync(Path.Combine(project, name));
        }

        return plans;
    }

    private static string CreateTemporaryProject()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "SevenRecord.Analysis.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
