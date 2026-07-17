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
}
