namespace SevenRecord.Media;

public enum EncoderPreference
{
    Auto,
    Nvidia,
    Intel,
    Amd,
    Software,
}

public enum EncoderKind
{
    NvidiaNvenc,
    IntelQuickSync,
    AmdAmf,
    SoftwareX264,
}

public sealed record EncoderSelection(
    EncoderKind Kind,
    string FfmpegName,
    bool IsHardware,
    bool IsFallback);

public static class EncoderSelectionPolicy
{
    public static EncoderSelection? Select(
        FfmpegEncoderCapabilities capabilities,
        EncoderPreference preference = EncoderPreference.Auto)
    {
        IReadOnlyList<EncoderSelection> candidates = GetCandidates(capabilities, preference);
        return candidates.Count == 0 ? null : candidates[0];
    }

    public static IReadOnlyList<EncoderSelection> GetCandidates(
        FfmpegEncoderCapabilities capabilities,
        EncoderPreference preference = EncoderPreference.Auto)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        EncoderKind[] candidates = preference switch
        {
            EncoderPreference.Nvidia =>
                [EncoderKind.NvidiaNvenc, EncoderKind.IntelQuickSync, EncoderKind.AmdAmf, EncoderKind.SoftwareX264],
            EncoderPreference.Intel =>
                [EncoderKind.IntelQuickSync, EncoderKind.NvidiaNvenc, EncoderKind.AmdAmf, EncoderKind.SoftwareX264],
            EncoderPreference.Amd =>
                [EncoderKind.AmdAmf, EncoderKind.NvidiaNvenc, EncoderKind.IntelQuickSync, EncoderKind.SoftwareX264],
            EncoderPreference.Software =>
                [EncoderKind.SoftwareX264, EncoderKind.NvidiaNvenc, EncoderKind.IntelQuickSync, EncoderKind.AmdAmf],
            _ =>
                [EncoderKind.NvidiaNvenc, EncoderKind.IntelQuickSync, EncoderKind.AmdAmf, EncoderKind.SoftwareX264],
        };

        EncoderKind? preferredKind = preference switch
        {
            EncoderPreference.Nvidia => EncoderKind.NvidiaNvenc,
            EncoderPreference.Intel => EncoderKind.IntelQuickSync,
            EncoderPreference.Amd => EncoderKind.AmdAmf,
            EncoderPreference.Software => EncoderKind.SoftwareX264,
            _ => null,
        };

        return candidates
            .Where(candidate => IsAvailable(capabilities, candidate))
            .Select(candidate => new EncoderSelection(
                candidate,
                FfmpegName(candidate),
                candidate is not EncoderKind.SoftwareX264,
                preferredKind is not null && candidate != preferredKind))
            .ToArray();
    }

    private static bool IsAvailable(FfmpegEncoderCapabilities capabilities, EncoderKind kind) =>
        kind switch
        {
            EncoderKind.NvidiaNvenc => capabilities.HasNvenc,
            EncoderKind.IntelQuickSync => capabilities.HasQuickSync,
            EncoderKind.AmdAmf => capabilities.HasAmf,
            EncoderKind.SoftwareX264 => capabilities.HasSoftwareH264,
            _ => false,
        };

    private static string FfmpegName(EncoderKind kind) =>
        kind switch
        {
            EncoderKind.NvidiaNvenc => "h264_nvenc",
            EncoderKind.IntelQuickSync => "h264_qsv",
            EncoderKind.AmdAmf => "h264_amf",
            EncoderKind.SoftwareX264 => "libx264",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
