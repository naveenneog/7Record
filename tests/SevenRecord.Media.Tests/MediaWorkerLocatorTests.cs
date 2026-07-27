namespace SevenRecord.Media.Tests;

[TestClass]
public sealed class MediaWorkerLocatorTests
{
    [TestMethod]
    public void PrefersDirectRuntimeWorker()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string worker = Path.Combine(
                root,
                "MediaWorker",
                "SevenRecord.Media.Worker.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(worker)!);
            File.WriteAllBytes(worker, [1]);

            Assert.AreEqual(worker, MediaWorkerLocator.FindExecutable(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void FallsBackToPackagedAppXWorker()
    {
        string root = CreateTemporaryRoot();
        try
        {
            string worker = Path.Combine(
                root,
                "AppX",
                "MediaWorker",
                "SevenRecord.Media.Worker.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(worker)!);
            File.WriteAllBytes(worker, [1]);

            Assert.AreEqual(worker, MediaWorkerLocator.FindExecutable(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SevenRecord.Media.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
