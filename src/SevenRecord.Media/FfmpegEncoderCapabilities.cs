namespace SevenRecord.Media;

public sealed record FfmpegEncoderCapabilities(
    bool HasNvenc,
    bool HasQuickSync,
    bool HasAmf,
    bool HasSoftwareH264)
{
    public bool HasHardwareEncoder => HasNvenc || HasQuickSync || HasAmf;

    public static FfmpegEncoderCapabilities Parse(string encoderOutput)
    {
        ArgumentNullException.ThrowIfNull(encoderOutput);

        return new FfmpegEncoderCapabilities(
            encoderOutput.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase),
            encoderOutput.Contains("h264_qsv", StringComparison.OrdinalIgnoreCase),
            encoderOutput.Contains("h264_amf", StringComparison.OrdinalIgnoreCase),
            encoderOutput.Contains("libx264", StringComparison.OrdinalIgnoreCase));
    }

    public string Describe()
    {
        List<string> encoders = [];
        if (HasNvenc)
        {
            encoders.Add("NVIDIA NVENC");
        }

        if (HasQuickSync)
        {
            encoders.Add("Intel Quick Sync");
        }

        if (HasAmf)
        {
            encoders.Add("AMD AMF");
        }

        if (HasSoftwareH264)
        {
            encoders.Add("software H.264");
        }

        return encoders.Count == 0 ? "No H.264 encoder found." : string.Join(", ", encoders);
    }
}
