using System.Diagnostics;
using SevenRecord.Capture.Windows;
using SevenRecord.Media;
using SevenRecord.Recording;

namespace SevenRecord.App;

internal sealed class ScreenSegmentRecorder : IAsyncDisposable
{
    private const int FramesPerSecond = 30;

    private readonly RecordingJournal _journal;
    private readonly ConstantFrameRatePacer _pacer;
    private readonly RecordingSegmentPublisher _publisher;
    private readonly Stopwatch _recordingTime = Stopwatch.StartNew();
    private readonly MediaWorkerRawVideoSession _worker;
    private readonly string _temporaryPath;
    private bool _completed;

    private ScreenSegmentRecorder(
        RecordingJournal journal,
        RecordingSegmentPublisher publisher,
        MediaWorkerRawVideoSession worker,
        ConstantFrameRatePacer pacer,
        string temporaryPath)
    {
        _journal = journal;
        _publisher = publisher;
        _worker = worker;
        _pacer = pacer;
        _temporaryPath = temporaryPath;
    }

    public static ScreenSegmentRecorder Start(
        string projectRoot,
        string workerPath,
        EncoderSelection encoder,
        int width,
        int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        ArgumentNullException.ThrowIfNull(encoder);

        Directory.CreateDirectory(projectRoot);
        string temporaryPath = Path.Combine(projectRoot, "temp", "screen.mkv.partial");
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);

        RecordingJournal journal = new(Path.Combine(projectRoot, "recording.journal"));
        RecordingSegmentPublisher publisher = new(projectRoot, journal);
        RawVideoEncoderSettings settings = new(
            width,
            height,
            FramesPerSecond,
            encoder.FfmpegName,
            temporaryPath);
        MediaWorkerRawVideoSession worker = MediaWorkerRawVideoSession.Start(workerPath, settings);
        ConstantFrameRatePacer pacer = new(
            width,
            height,
            FramesPerSecond,
            worker.WriteFrameAsync);

        return new ScreenSegmentRecorder(journal, publisher, worker, pacer, temporaryPath);
    }

    public ValueTask ProcessFrameAsync(
        ScreenCaptureFrameLease frame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pacer.UpdateFrame(frame.CopyBgra8());
        return ValueTask.CompletedTask;
    }

    public async Task<RecordingSegmentEntry> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The screen segment is already complete.");
        }

        _completed = true;
        _recordingTime.Stop();
        await _pacer.DisposeAsync();
        RawVideoEncoderResult encoded = await _worker.CompleteAsync(cancellationToken);
        if (!encoded.Succeeded)
        {
            throw new InvalidOperationException(encoded.Error ?? "Screen segment encoding failed.");
        }

        return await _publisher.PublishAsync(
            _temporaryPath,
            sequence: 1,
            sourceId: "screen",
            start: TimeSpan.Zero,
            duration: _recordingTime.Elapsed,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            await _pacer.DisposeAsync();
        }

        await _worker.DisposeAsync();
        _journal.Dispose();
    }
}
