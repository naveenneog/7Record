using SevenRecord.Domain.Timeline;

namespace SevenRecord.Editor.Tests;

[TestClass]
public sealed class TimelineEditStoreTests
{
    private static readonly string[] ExpectedSliceOrder =
        ["later", "earlier"];

    [TestMethod]
    public async Task RoundTripsOrderedSlices()
    {
        string project = CreateTemporaryProject();
        TimelineEditDocument document = new(
            1,
            [
                new TimelineEditSlice(
                    "later",
                    new TimelineRange(
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(8))),
                new TimelineEditSlice(
                    "earlier",
                    new TimelineRange(
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(3)))
            ]);
        try
        {
            await TimelineEditStore.SaveAsync(
                project,
                document,
                TimeSpan.FromSeconds(10));
            TimelineEditDocument loaded =
                await TimelineEditStore.LoadAsync(
                    project,
                    TimeSpan.FromSeconds(10));

            CollectionAssert.AreEqual(
                ExpectedSliceOrder,
                loaded.Slices.Select(slice => slice.Id).ToArray());
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
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
