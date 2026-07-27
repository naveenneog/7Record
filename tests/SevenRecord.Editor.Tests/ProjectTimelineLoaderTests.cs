using System.Text.Json;
using System.Security.Cryptography;
using SevenRecord.Domain.Audio;
using SevenRecord.Domain.Captions;
using SevenRecord.Domain.Input;
using SevenRecord.Domain.Timeline;
using SevenRecord.Domain.Video;
using SevenRecord.Recording;

namespace SevenRecord.Editor.Tests;

[TestClass]
public sealed class ProjectTimelineLoaderTests
{
    [TestMethod]
    public async Task LoadsSourceTracksAndReversibleAutomation()
    {
        string project = CreateTemporaryProject();
        try
        {
            using (RecordingJournal journal = new(Path.Combine(project, "recording.journal")))
            {
                await journal.AppendAsync(
                    await EntryAsync(project, 1, "screen", "screen.mp4"));
                await journal.AppendAsync(
                    await EntryAsync(project, 2, "microphone", "microphone.wav"));
            }

            await File.WriteAllTextAsync(
                Path.Combine(project, "audio-repair-plan.json"),
                JsonSerializer.Serialize(
                    new[]
                    {
                        new AudioRepairEvent(
                            AudioTrackKind.Microphone,
                            AudioRepairEventKind.InsertSilence,
                            TimeSpan.FromSeconds(2),
                            TimeSpan.FromMilliseconds(200),
                            1)
                    }));
            await File.WriteAllTextAsync(
                Path.Combine(project, "presenter-layout.json"),
                JsonSerializer.Serialize(PresenterLayoutSettings.DefaultOverlay));
            await File.WriteAllTextAsync(
                Path.Combine(project, "captions.json"),
                JsonSerializer.Serialize(
                    new CaptionDocument(
                        1,
                        "en",
                        [
                            new CaptionSegment(
                                "caption",
                                TimeSpan.FromSeconds(1),
                                TimeSpan.FromSeconds(2),
                                "Hello")
                        ])));
            await File.WriteAllTextAsync(
                Path.Combine(project, "cursor-zoom-plan.json"),
                JsonSerializer.Serialize(
                    new[]
                    {
                        new CursorZoomEvent(
                            "zoom",
                            TimeSpan.FromSeconds(1),
                            TimeSpan.FromSeconds(1.2),
                            0.5,
                            0.5,
                            1.8,
                            1)
                    }));
            await File.WriteAllTextAsync(
                Path.Combine(project, "loading-speed-plan.json"),
                JsonSerializer.Serialize(
                    new[]
                    {
                        new LoadingSpeedEvent(
                            "loading",
                            TimeSpan.FromSeconds(3),
                            TimeSpan.FromSeconds(2),
                            4,
                            0.65)
                    }));

            TimelineDocument timeline = await ProjectTimelineLoader.LoadAsync(project);

            Assert.HasCount(2, timeline.Clips);
            Assert.IsTrue(timeline.Clips.Any(clip => clip.Track is TimelineTrackKind.Screen));
            Assert.IsTrue(timeline.Clips.Any(clip => clip.Track is TimelineTrackKind.Microphone));
            Assert.HasCount(4, timeline.Automation);
            Assert.IsTrue(
                timeline.Automation.Any(item => item.Id == "presenter-layout"));
            Assert.HasCount(1, timeline.Captions);
            Assert.IsTrue(timeline.Automation.All(item => item.IsEnabled));
            Assert.AreEqual(TimeSpan.FromSeconds(5), timeline.Duration);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [TestMethod]
    public async Task RejectsMissingJournaledMedia()
    {
        string project = CreateTemporaryProject();
        try
        {
            using RecordingJournal journal = new(
                Path.Combine(project, "recording.journal"));
            await journal.AppendAsync(
                new RecordingSegmentEntry(
                    1,
                    "missing",
                    "screen",
                    "missing.mp4",
                    0,
                    TimeSpan.FromSeconds(5).Ticks,
                    100,
                    new string('A', 64)));

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => ProjectTimelineLoader.LoadAsync(project));
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    private static async Task<RecordingSegmentEntry> EntryAsync(
        string project,
        int sequence,
        string source,
        string path)
    {
        string fullPath = Path.Combine(project, path);
        byte[] content = [(byte)sequence];
        await File.WriteAllBytesAsync(fullPath, content);
        return new RecordingSegmentEntry(
            sequence,
            Guid.NewGuid().ToString("N"),
            source,
            path,
            0,
            TimeSpan.FromSeconds(5).Ticks,
            content.Length,
            Convert.ToHexString(SHA256.HashData(content)));
    }

    private static string CreateTemporaryProject()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "SevenRecord.Editor.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
