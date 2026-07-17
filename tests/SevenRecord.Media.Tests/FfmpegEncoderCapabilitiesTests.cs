namespace SevenRecord.Media.Tests;

[TestClass]
public sealed class FfmpegEncoderCapabilitiesTests
{
    [TestMethod]
    public void ParseFindsHardwareAndSoftwareEncoders()
    {
        const string output = """
            V....D h264_nvenc           NVIDIA NVENC H.264 encoder
            V..... h264_qsv             H.264 / AVC / MPEG-4 AVC
            V....D h264_amf             AMD AMF H.264 Encoder
            V....D libx264              libx264 H.264
            """;

        FfmpegEncoderCapabilities capabilities = FfmpegEncoderCapabilities.Parse(output);

        Assert.IsTrue(capabilities.HasNvenc);
        Assert.IsTrue(capabilities.HasQuickSync);
        Assert.IsTrue(capabilities.HasAmf);
        Assert.IsTrue(capabilities.HasSoftwareH264);
        Assert.IsTrue(capabilities.HasHardwareEncoder);
    }

    [TestMethod]
    public void ParseReportsNoEncoderForUnrelatedOutput()
    {
        FfmpegEncoderCapabilities capabilities = FfmpegEncoderCapabilities.Parse("A..... aac");

        Assert.IsFalse(capabilities.HasHardwareEncoder);
        Assert.IsFalse(capabilities.HasSoftwareH264);
        Assert.AreEqual("No H.264 encoder found.", capabilities.Describe());
    }
}
