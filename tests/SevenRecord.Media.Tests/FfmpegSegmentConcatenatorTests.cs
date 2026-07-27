namespace SevenRecord.Media.Tests;

[TestClass]
public sealed class FfmpegSegmentConcatenatorTests
{
    [TestMethod]
    public void ManifestUsesAbsoluteForwardSlashPaths()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "SevenRecord",
            "segment one.mp4");

        string manifest = FfmpegSegmentConcatenator.BuildManifest([path]);

        StringAssert.StartsWith(manifest, "file '");
        StringAssert.Contains(manifest, "segment one.mp4");
        CollectionAssert.DoesNotContain(manifest.ToCharArray(), '\\');
    }
}
