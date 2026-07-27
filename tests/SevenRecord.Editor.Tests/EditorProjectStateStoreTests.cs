namespace SevenRecord.Editor.Tests;

[TestClass]
public sealed class EditorProjectStateStoreTests
{
    [TestMethod]
    public async Task SavesAndRestoresEditorChoices()
    {
        string project = CreateTemporaryProject();
        try
        {
            EditorProjectState state = new(
                1,
                2,
                ["zoom-2", "zoom-1", "zoom-1"]);

            await EditorProjectStateStore.SaveAsync(project, state);
            EditorProjectStateLoadResult loaded =
                await EditorProjectStateStore.LoadAsync(project);

            Assert.IsNull(loaded.Warning);
            Assert.AreEqual(2, loaded.State.RenderPresetIndex);
            CollectionAssert.AreEquivalent(
                new[] { "zoom-1", "zoom-2" },
                loaded.State.DisabledAutomationIds.ToArray());
        }
        finally
        {
            Directory.Delete(project, recursive: true);
        }
    }

    [TestMethod]
    public async Task InvalidStateResetsWithWarning()
    {
        string project = CreateTemporaryProject();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(project, "editor-state.json"),
                "{invalid");

            EditorProjectStateLoadResult loaded =
                await EditorProjectStateStore.LoadAsync(project);

            Assert.IsNotNull(loaded.Warning);
            Assert.AreEqual(0, loaded.State.RenderPresetIndex);
            Assert.IsEmpty(loaded.State.DisabledAutomationIds);
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
