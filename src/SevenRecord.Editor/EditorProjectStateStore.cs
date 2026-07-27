using System.Text.Json;

namespace SevenRecord.Editor;

public sealed record EditorProjectState(
    int SchemaVersion,
    int RenderPresetIndex,
    IReadOnlyList<string> DisabledAutomationIds)
{
    public static EditorProjectState Default { get; } = new(1, 0, []);
}

public sealed record EditorProjectStateLoadResult(
    EditorProjectState State,
    string? Warning);

public static class EditorProjectStateStore
{
    private const string FileName = "editor-state.json";
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<EditorProjectStateLoadResult> LoadAsync(
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string path = Path.Combine(Path.GetFullPath(projectPath), FileName);
        if (!File.Exists(path))
        {
            return new EditorProjectStateLoadResult(
                EditorProjectState.Default,
                null);
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, cancellationToken);
            EditorProjectState? state =
                JsonSerializer.Deserialize<EditorProjectState>(
                    json,
                    SerializerOptions);
            if (state is null || state.SchemaVersion != 1)
            {
                return new EditorProjectStateLoadResult(
                    EditorProjectState.Default,
                    "Saved editor choices use an unsupported format and were reset.");
            }

            return new EditorProjectStateLoadResult(
                state with
                {
                    RenderPresetIndex = Math.Clamp(state.RenderPresetIndex, 0, 2),
                    DisabledAutomationIds = state.DisabledAutomationIds
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                },
                null);
        }
        catch (JsonException exception)
        {
            return new EditorProjectStateLoadResult(
                EditorProjectState.Default,
                $"Saved editor choices were invalid and were reset: {exception.Message}");
        }
    }

    public static async Task SaveAsync(
        string projectPath,
        EditorProjectState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(state);
        string path = Path.Combine(Path.GetFullPath(projectPath), FileName);
        string temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(state, SerializerOptions),
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
