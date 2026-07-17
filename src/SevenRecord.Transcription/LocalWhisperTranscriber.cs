using System.ComponentModel;
using System.Diagnostics;
using SevenRecord.Domain.Captions;
using SevenRecord.Media;
using Whisper.net;
using Whisper.net.Ggml;

namespace SevenRecord.Transcription;

public sealed class LocalWhisperTranscriber
{
    public static async Task<CaptionDocument> TranscribeAsync(
        string inputAudioPath,
        string modelPath,
        string language = "auto",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        await EnsureModelAsync(modelPath, cancellationToken);
        string normalizedPath = Path.Combine(
            Path.GetTempPath(),
            "SevenRecord.Transcription",
            $"{Guid.NewGuid():N}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(normalizedPath)!);

        try
        {
            await NormalizeAudioAsync(inputAudioPath, normalizedPath, cancellationToken);
            using WhisperFactory factory = WhisperFactory.FromPath(modelPath);
            using WhisperProcessor processor = factory.CreateBuilder()
                .WithLanguage(language)
                .Build();
            await using FileStream audio = File.OpenRead(normalizedPath);
            List<CaptionSegment> segments = [];
            await foreach (SegmentData segment in processor.ProcessAsync(audio, cancellationToken))
            {
                string text = segment.Text.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                segments.Add(
                    new CaptionSegment(
                        Guid.NewGuid().ToString("N"),
                        segment.Start,
                        segment.End,
                        text));
            }

            return new CaptionDocument(1, language, segments);
        }
        finally
        {
            if (File.Exists(normalizedPath))
            {
                File.Delete(normalizedPath);
            }
        }
    }

    public static async Task EnsureModelAsync(
        string modelPath,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(modelPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(modelPath))!);
        string temporaryPath = modelPath + ".download";
        await using Stream model =
            await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                GgmlType.Tiny,
                QuantizationType.NoQuantization,
                cancellationToken);
        await using (FileStream output = File.Create(temporaryPath))
        {
            await model.CopyToAsync(output, cancellationToken);
        }

        File.Move(temporaryPath, modelPath, overwrite: true);
    }

    private static async Task NormalizeAudioAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        string? ffmpegPath = FfmpegLocator.FindExecutable();
        if (ffmpegPath is null)
        {
            throw new InvalidOperationException(
                "FFmpeg is required to normalize audio for transcription.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = ffmpegPath,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-i", Path.GetFullPath(inputPath),
                     "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
                     Path.GetFullPath(outputPath)
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = new() { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "FFmpeg audio normalization could not be started.");
            }

            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            _ = await output;
            string standardError = await error;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(standardError)
                        ? $"FFmpeg exited with code {process.ExitCode}."
                        : standardError.Trim());
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "FFmpeg audio normalization could not be executed.",
                exception);
        }
    }
}
