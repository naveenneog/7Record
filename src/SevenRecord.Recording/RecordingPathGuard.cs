namespace SevenRecord.Recording;

public static class RecordingPathGuard
{
    public static string ResolveWithinRoot(string projectRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string root = Path.GetFullPath(projectRoot);
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        string rootedPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path '{relativePath}' escapes the recording project.");
        }

        return candidate;
    }
}
