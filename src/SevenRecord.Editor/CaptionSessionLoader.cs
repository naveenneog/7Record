using System.Text.Json;
using SevenRecord.Domain.Captions;

namespace SevenRecord.Editor;

/// <summary>
/// Reads a project's saved captions into an editable session.
/// </summary>
public static class CaptionSessionLoader
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The file captions are persisted to inside a project.</summary>
    public const string FileName = "captions.json";

    /// <summary>
    /// Loads the caption document for a project, or <see langword="null"/> when the project
    /// has no captions yet or the file does not deserialize.
    /// </summary>
    public static async Task<CaptionEditSession?> LoadAsync(
        string projectPath,
        TimeSpan timelineDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string path = Path.Combine(projectPath, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken);
        CaptionDocument? document =
            JsonSerializer.Deserialize<CaptionDocument>(json, SerializerOptions);
        return document is null
            ? null
            : new CaptionEditSession(document, timelineDuration);
    }
}
