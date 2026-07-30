using System.Text.Json;
using SevenRecord.Domain.Timeline;

namespace SevenRecord.Editor;

public static class TimelineEditStore
{
    private const string FileName = "clip-edits.json";
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<TimelineEditDocument> LoadAsync(
        string projectPath,
        TimeSpan sourceDuration,
        CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(
            Path.GetFullPath(projectPath),
            FileName);
        if (!File.Exists(path))
        {
            return TimelineEditDocument.CreateDefault(sourceDuration);
        }
        string json = await File.ReadAllTextAsync(path, cancellationToken);
        TimelineEditDocument? document =
            JsonSerializer.Deserialize<TimelineEditDocument>(
                json,
                SerializerOptions);
        return (document ??
            throw new InvalidDataException(
                "The clip-edit file is empty or invalid."))
            .Validate(sourceDuration);
    }

    public static async Task SaveAsync(
        string projectPath,
        TimelineEditDocument document,
        TimeSpan sourceDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        TimelineEditDocument validated =
            document.Validate(sourceDuration);
        string path = Path.Combine(
            Path.GetFullPath(projectPath),
            FileName);
        string temporaryPath =
            path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(
                    validated,
                    SerializerOptions),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
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
