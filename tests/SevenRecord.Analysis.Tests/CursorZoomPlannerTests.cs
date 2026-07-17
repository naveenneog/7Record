using SevenRecord.Domain.Input;

namespace SevenRecord.Analysis.Tests;

[TestClass]
public sealed class CursorZoomPlannerTests
{
    [TestMethod]
    public void ClickCreatesAPreRollZoomAtThePointer()
    {
        CursorMetadataDocument document = new(
            1,
            [
                new CursorMetadataEvent(
                    TimeSpan.FromSeconds(2),
                    100,
                    200,
                    0.25,
                    0.75,
                    CursorEventKind.Click,
                    CursorButton.Left)
            ]);

        CursorZoomEvent zoom = CursorZoomPlanner.CreatePlan(document).Single();

        Assert.AreEqual(TimeSpan.FromSeconds(1.8), zoom.Start);
        Assert.AreEqual(0.25d, zoom.CenterX);
        Assert.AreEqual(0.75d, zoom.CenterY);
        Assert.AreEqual(1.8d, zoom.Scale);
    }
}
