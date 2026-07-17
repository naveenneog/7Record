using System.Text.Json;
using SevenRecord.Domain.Audio;
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
                await journal.AppendAsync(Entry(1, "screen", "screen.mp4"));
                await journal.AppendAsync(Entry(2, "microphone", "microphone.wav"));
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

            TimelineDocument timeline = await ProjectTimelineLoader.LoadAsync(project);

            Assert.HasCount(2, timeline.Clips);
            Assert.IsTrue(timeline.Clips.Any(clip => clip.Track is TimelineTrackKind.Screen));
            Assert.IsTrue(timeline.Clips.Any(clip => clip.Track is TimelineTrackKind.Microphone));
            Assert.HasCount(2, timeline.Automation);
            Assert.IsTrue(timeline.Automation.All(item => item.IsEnabled));
            Assert.AreEqual(TimeSpan.FromSeconds(5), timeline.Duration);
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    private static RecordingSegmentEntry Entry(
        int sequence,
        string source,
        string path) =>
        new(
            sequence,
            Guid.NewGuid().ToString("N"),
            source,
            path,
            0,
            TimeSpan.FromSeconds(5).Ticks,
            1,
            new string('A', 64));

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
