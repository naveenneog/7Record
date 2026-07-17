using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace SevenRecord.Media;

public sealed record RawVideoEncoderSettings(
    int Width,
    int Height,
    int FramesPerSecond,
    string EncoderName,
    string OutputPath);

public sealed record RawVideoEncoderResult(
    bool Succeeded,
    int? ExitCode,
    string? Error);

public static class FfmpegRawVideoEncoder
{
    public static async Task<RawVideoEncoderResult> EncodeAsync(
        Stream bgraInput,
        string ffmpegPath,
        RawVideoEncoderSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bgraInput);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.Height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.FramesPerSecond);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.EncoderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.OutputPath);

        string outputPath = Path.GetFullPath(settings.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string arguments = string.Create(
            CultureInfo.InvariantCulture,
            $"-hide_banner -loglevel error -y " +
            $"-f rawvideo -pixel_format bgra -video_size {settings.Width}x{settings.Height} " +
            $"-framerate {settings.FramesPerSecond} -i pipe:0 -an " +
            $"-c:v {settings.EncoderName} -g {settings.FramesPerSecond * 2} " +
            $"-f matroska \"{outputPath}\"");

        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };

            if (!process.Start())
            {
                return new RawVideoEncoderResult(false, null, "FFmpeg could not be started.");
            }

            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await bgraInput.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            _ = await standardOutput;
            string error = await standardError;

            return process.ExitCode == 0
                ? new RawVideoEncoderResult(true, 0, null)
                : new RawVideoEncoderResult(
                    false,
                    process.ExitCode,
                    string.IsNullOrWhiteSpace(error)
                        ? $"FFmpeg exited with code {process.ExitCode}."
                        : error.Trim());
        }
        catch (Win32Exception)
        {
            return new RawVideoEncoderResult(false, null, "FFmpeg could not be executed.");
        }
    }
}
