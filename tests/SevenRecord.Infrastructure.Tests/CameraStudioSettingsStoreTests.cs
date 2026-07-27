using SevenRecord.Domain.Video;

namespace SevenRecord.Infrastructure.Tests;

[TestClass]
public sealed class CameraStudioSettingsStoreTests
{
    [TestMethod]
    public async Task PersistsFramingAndExposure()
    {
        string path = TemporaryPath();
        PresenterLayoutSettings layout =
            PresenterLayoutSettings.DefaultOverlay with
            {
                Width = 0.32,
                Framing = new CameraFramingSettings(1.8, 0.3, 0.7),
                Effects = new CameraEffectSettings(0.25),
            };
        try
        {
            await CameraStudioSettingsStore.SaveAsync(layout, path);
            CameraStudioSettingsLoadResult loaded =
                await CameraStudioSettingsStore.LoadAsync(path);

            Assert.IsNull(loaded.Warning);
            Assert.AreEqual(0.32, loaded.Layout.Width);
            Assert.AreEqual(1.8, loaded.Layout.Framing.Zoom);
            Assert.AreEqual(0.3, loaded.Layout.Framing.CenterX);
            Assert.AreEqual(0.25, loaded.Layout.Effects.Exposure);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task InvalidSettingsResetWithWarning()
    {
        string path = TemporaryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{invalid");
        try
        {
            CameraStudioSettingsLoadResult loaded =
                await CameraStudioSettingsStore.LoadAsync(path);

            Assert.IsNotNull(loaded.Warning);
            Assert.AreEqual(
                PresenterLayoutSettings.DefaultOverlay,
                loaded.Layout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TemporaryPath() =>
        Path.Combine(
            Path.GetTempPath(),
            "SevenRecord.Infrastructure.Tests",
            Guid.NewGuid().ToString("N"),
            "camera-studio.json");
}
