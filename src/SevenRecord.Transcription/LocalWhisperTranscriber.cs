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
            // Best-effort: a cancelled FFmpeg may still be releasing the handle, and an
            // IOException thrown from a finally would replace the real
            // OperationCanceledException with a misleading sharing-violation message.
            try
            {
                if (File.Exists(normalizedPath))
                {
                    File.Delete(normalizedPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
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
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // `using Process` only releases handles; it does not terminate. Without
                // this, cancelling caption generation at shutdown abandons a live
                // ffmpeg.exe that outlives 7Record - the exact leak the background job
                // barrier exists to prevent. Every other process launcher in this repo
                // already does this; this one was missed because until the job registry
                // wired a real token through, this path could never be cancelled.
                await TerminateProcessAsync(process).ConfigureAwait(false);
                throw;
            }
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

    private static async Task TerminateProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (TimeoutException)
        {
        }
    }
}
