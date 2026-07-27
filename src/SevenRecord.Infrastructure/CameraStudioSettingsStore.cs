using System.Text.Json;
using SevenRecord.Domain.Video;

namespace SevenRecord.Infrastructure;

public sealed record CameraStudioSettingsDocument(
    int SchemaVersion,
    PresenterLayoutSettings Layout);

public sealed record CameraStudioSettingsLoadResult(
    PresenterLayoutSettings Layout,
    string? Warning);

public static class CameraStudioSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "7Record",
            "Settings",
            "camera-studio.json");

    public static async Task<CameraStudioSettingsLoadResult> LoadAsync(
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        string settingsPath = Path.GetFullPath(path ?? DefaultPath);
        if (!File.Exists(settingsPath))
        {
            return new CameraStudioSettingsLoadResult(
                PresenterLayoutSettings.DefaultOverlay,
                null);
        }

        try
        {
            string json = await File.ReadAllTextAsync(
                settingsPath,
                cancellationToken);
            CameraStudioSettingsDocument? document =
                JsonSerializer.Deserialize<CameraStudioSettingsDocument>(
                    json,
                    SerializerOptions);
            if (document is null ||
                document.SchemaVersion != 1 ||
                document.Layout is null)
            {
                return new CameraStudioSettingsLoadResult(
                    PresenterLayoutSettings.DefaultOverlay,
                    "Saved camera settings were reset because their format is unsupported.");
            }
            return new CameraStudioSettingsLoadResult(
                document.Layout.ConstrainToFrame(),
                null);
        }
        catch (JsonException exception)
        {
            return new CameraStudioSettingsLoadResult(
                PresenterLayoutSettings.DefaultOverlay,
                $"Saved camera settings were invalid and were reset: {exception.Message}");
        }
    }

    public static async Task SaveAsync(
        PresenterLayoutSettings layout,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string settingsPath = Path.GetFullPath(path ?? DefaultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        string temporaryPath = settingsPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(
                    new CameraStudioSettingsDocument(
                        1,
                        layout.ConstrainToFrame()),
                    SerializerOptions),
                cancellationToken);
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
