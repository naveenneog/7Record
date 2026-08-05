using SevenRecord.App.Presentation;

namespace SevenRecord.App.Presentation.Tests;

/// <summary>
/// Covers cleanup that must stay silent on the failures that happen routinely, and must
/// stay precise about what it deletes.
/// </summary>
[TestClass]
public sealed class BestEffortCleanupTests
{
    [TestMethod]
    public void DeleteFileRemovesAnExistingFile()
    {
        using TemporaryDirectory directory = new();
        string path = directory.WriteFile("scratch.txt");

        BestEffortCleanup.DeleteFile(path);

        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void DeleteFileIgnoresPathsThatAreNullEmptyOrMissing()
    {
        using TemporaryDirectory directory = new();

        BestEffortCleanup.DeleteFile(null);
        BestEffortCleanup.DeleteFile("   ");
        BestEffortCleanup.DeleteFile(Path.Combine(directory.Path, "never-existed.txt"));
    }

    [TestMethod]
    public void DeleteFileStaysSilentWhenTheFileIsLocked()
    {
        using TemporaryDirectory directory = new();
        string path = directory.WriteFile("locked.mp4");

        // Antivirus, a media player or an Explorer preview handler holding the file open
        // is normal on this cleanup path and must never surface to the user.
        using FileStream hold = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        BestEffortCleanup.DeleteFile(path);

        Assert.IsTrue(File.Exists(path), "the locked file should still be there");
    }

    [TestMethod]
    public void DeleteDirectoryRemovesTheWholeTree()
    {
        using TemporaryDirectory directory = new();
        string nested = Path.Combine(directory.Path, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "deep.txt"), "x");
        string target = Path.Combine(directory.Path, "a");

        BestEffortCleanup.DeleteDirectory(target);

        Assert.IsFalse(Directory.Exists(target));
    }

    [TestMethod]
    public void DeleteDirectoryIgnoresPathsThatAreNullEmptyOrMissing()
    {
        using TemporaryDirectory directory = new();

        BestEffortCleanup.DeleteDirectory(null);
        BestEffortCleanup.DeleteDirectory("   ");
        BestEffortCleanup.DeleteDirectory(Path.Combine(directory.Path, "nope"));
    }

    [TestMethod]
    public void DeletePartialRendersRemovesOnlyThisExportsLeftovers()
    {
        using TemporaryDirectory directory = new();
        string output = Path.Combine(directory.Path, "video.mp4");
        string mine = directory.WriteFile(".video-abc123.partial.mp4");
        string alsoMine = directory.WriteFile(".video-def456.partial.mp4");
        string anotherExport = directory.WriteFile(".other-abc123.partial.mp4");
        string finished = directory.WriteFile("video.mp4");

        BestEffortCleanup.DeletePartialRenders(output);

        Assert.IsFalse(File.Exists(mine));
        Assert.IsFalse(File.Exists(alsoMine));
        Assert.IsTrue(
            File.Exists(anotherExport),
            "a sibling export's partial file must not be deleted");
        Assert.IsTrue(
            File.Exists(finished),
            "the finished render must never be deleted");
    }

    [TestMethod]
    public void DeletePartialRendersIgnoresNullEmptyAndBareFileNames()
    {
        BestEffortCleanup.DeletePartialRenders(null);
        BestEffortCleanup.DeletePartialRenders("   ");

        // A bare file name has no directory component. The original used a null-forgiving
        // operator here and would have thrown.
        BestEffortCleanup.DeletePartialRenders("video.mp4");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SevenRecord.Presentation.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteFile(string name)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, "scratch");
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leaked temp directory must never fail a test run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
