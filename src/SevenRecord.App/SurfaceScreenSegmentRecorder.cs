using System.Diagnostics;
using SevenRecord.Capture.Windows;
using SevenRecord.Media.Windows;
using SevenRecord.Recording;

namespace SevenRecord.App;

internal sealed class SurfaceScreenSegmentRecorder : IAsyncDisposable
{
    private const int FramesPerSecond = 60;
    private const uint Bitrate = 12_000_000;

    private readonly Direct3DSurfaceVideoEncoder _encoder;
    private readonly RecordingJournal _journal;
    private readonly RecordingSegmentPublisher _publisher;
    private readonly Stopwatch _recordingTime = Stopwatch.StartNew();
    private readonly string _temporaryPath;
    private bool _completed;

    private SurfaceScreenSegmentRecorder(
        Direct3DSurfaceVideoEncoder encoder,
        RecordingJournal journal,
        RecordingSegmentPublisher publisher,
        string temporaryPath)
    {
        _encoder = encoder;
        _journal = journal;
        _publisher = publisher;
        _temporaryPath = temporaryPath;
    }

    public static async Task<SurfaceScreenSegmentRecorder> CreateAsync(
        string projectRoot,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        Directory.CreateDirectory(projectRoot);

        string temporaryPath = Path.Combine(projectRoot, "temp", "screen.partial.mp4");
        RecordingJournal journal = new(Path.Combine(projectRoot, "recording.journal"));
        RecordingSegmentPublisher publisher = new(projectRoot, journal);

        try
        {
            Direct3DSurfaceVideoEncoder encoder =
                await Direct3DSurfaceVideoEncoder.CreateAsync(
                    temporaryPath,
                    width,
                    height,
                    FramesPerSecond,
                    Bitrate,
                    cancellationToken);
            return new SurfaceScreenSegmentRecorder(
                encoder,
                journal,
                publisher,
                temporaryPath);
        }
        catch
        {
            journal.Dispose();
            throw;
        }
    }

    public ValueTask ProcessFrameAsync(
        ScreenCaptureFrameLease frame,
        CancellationToken cancellationToken) =>
        _encoder.ProcessSurfaceAsync(
            frame.Surface,
            frame.ProjectTime,
            cancellationToken);

    public async Task<RecordingSegmentEntry> CompleteAsync()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The surface segment is already complete.");
        }

        _completed = true;
        _recordingTime.Stop();
        await _encoder.CompleteAsync();
        return await _publisher.PublishAsync(
            _temporaryPath,
            sequence: 1,
            sourceId: "screen",
            start: TimeSpan.Zero,
            duration: _recordingTime.Elapsed);
    }

    public async ValueTask DisposeAsync()
    {
        await _encoder.DisposeAsync();
        _journal.Dispose();
    }
}
