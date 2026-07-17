namespace SevenRecord.Media.Tests;

[TestClass]
public sealed class EncoderSelectionPolicyTests
{
    [TestMethod]
    public void AutoPrefersNvencThenKeepsSoftwareAsFallback()
    {
        FfmpegEncoderCapabilities capabilities = new(
            HasNvenc: true,
            HasQuickSync: true,
            HasAmf: true,
            HasSoftwareH264: true);

        EncoderSelection selection = EncoderSelectionPolicy.Select(capabilities)!;

        Assert.AreEqual(EncoderKind.NvidiaNvenc, selection.Kind);
        Assert.AreEqual("h264_nvenc", selection.FfmpegName);
        Assert.IsTrue(selection.IsHardware);
        Assert.IsFalse(selection.IsFallback);
    }

    [TestMethod]
    public void RequestedUnavailableEncoderFallsBackDeterministically()
    {
        FfmpegEncoderCapabilities capabilities = new(
            HasNvenc: false,
            HasQuickSync: true,
            HasAmf: false,
            HasSoftwareH264: true);

        EncoderSelection selection = EncoderSelectionPolicy.Select(
            capabilities,
            EncoderPreference.Nvidia)!;

        Assert.AreEqual(EncoderKind.IntelQuickSync, selection.Kind);
        Assert.IsTrue(selection.IsFallback);
    }

    [TestMethod]
    public void CandidateOrderKeepsSoftwareAsTheLastAutomaticFallback()
    {
        FfmpegEncoderCapabilities capabilities = new(true, true, true, true);

        IReadOnlyList<EncoderSelection> candidates =
            EncoderSelectionPolicy.GetCandidates(capabilities);

        CollectionAssert.AreEqual(
            new[]
            {
                EncoderKind.NvidiaNvenc,
                EncoderKind.IntelQuickSync,
                EncoderKind.AmdAmf,
                EncoderKind.SoftwareX264,
            },
            candidates.Select(candidate => candidate.Kind).ToArray());
    }

    [TestMethod]
    public void SoftwarePreferenceWinsWhenAvailable()
    {
        FfmpegEncoderCapabilities capabilities = new(
            HasNvenc: true,
            HasQuickSync: false,
            HasAmf: false,
            HasSoftwareH264: true);

        EncoderSelection selection = EncoderSelectionPolicy.Select(
            capabilities,
            EncoderPreference.Software)!;

        Assert.AreEqual(EncoderKind.SoftwareX264, selection.Kind);
        Assert.IsFalse(selection.IsHardware);
        Assert.IsFalse(selection.IsFallback);
    }

    [TestMethod]
    public void NoCompatibleEncoderReturnsNull()
    {
        FfmpegEncoderCapabilities capabilities = new(false, false, false, false);

        Assert.IsNull(EncoderSelectionPolicy.Select(capabilities));
    }
}
