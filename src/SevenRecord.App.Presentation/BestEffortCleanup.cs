namespace SevenRecord.App.Presentation;

/// <summary>
/// Best-effort file and directory removal for cleanup paths.
/// </summary>
/// <remarks>
/// Deliberately silent on <see cref="IOException"/> and
/// <see cref="UnauthorizedAccessException"/>. Every caller is tidying up a scratch artifact
/// - a partial export, an abandoned preview render, a discarded project folder - where the
/// file being locked by antivirus, a media player or an Explorer preview handler is normal
/// and must never surface as an error the user has to deal with. Anything else still
/// propagates, so a genuine bug is not hidden.
/// </remarks>
public static class BestEffortCleanup
{
    /// <summary>Removes a directory and its contents if it exists and can be removed.</summary>
    public static void DeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes a file if it exists and can be removed.</summary>
    public static void DeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes the hidden <c>.partial.mp4</c> scratch files left beside an export target.
    /// </summary>
    /// <remarks>
    /// Exports render to a partial file and are moved into place only once complete, so a
    /// crash or a cancelled render can leave these behind. They are named from the final
    /// output so only this export's leftovers are removed, never a sibling project's.
    /// </remarks>
    public static void DeletePartialRenders(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        string pattern =
            $".{Path.GetFileNameWithoutExtension(outputPath)}-*.partial.mp4";
        foreach (string partialPath in Directory.GetFiles(
                     directory,
                     pattern,
                     SearchOption.TopDirectoryOnly))
        {
            DeleteFile(partialPath);
        }
    }
}
