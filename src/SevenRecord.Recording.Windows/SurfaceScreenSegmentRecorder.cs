using SevenRecord.Capture.Abstractions;
using SevenRecord.Capture.Windows;
using SevenRecord.Media.Windows;

namespace SevenRecord.Recording.Windows;

public sealed class SurfaceScreenSegmentRecorder : IAsyncDisposable
{
    private const int FramesPerSecond = 60;
    private const uint Bitrate = 12_000_000;

    private readonly Direct3DSurfaceVideoEncoder _encoder;
    private readonly RecordingPauseController _pauseController;
    private readonly RecordingProjectWriter _projectWriter;
    private readonly ProjectClock _projectClock;
    private readonly string _temporaryPath;
    private bool _completed;

    private SurfaceScreenSegmentRecorder(
        Direct3DSurfaceVideoEncoder encoder,
        RecordingProjectWriter projectWriter,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        string temporaryPath)
    {
        _encoder = encoder;
        _projectWriter = projectWriter;
        _projectClock = projectClock;
        _pauseController = pauseController;
        _temporaryPath = temporaryPath;
    }

    public static async Task<SurfaceScreenSegmentRecorder> CreateAsync(
        string projectRoot,
        int width,
        int height,
        ProjectClock projectClock,
        RecordingPauseController pauseController,
        RecordingProjectWriter projectWriter,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(projectClock);
        ArgumentNullException.ThrowIfNull(pauseController);
        ArgumentNullException.ThrowIfNull(projectWriter);
        Directory.CreateDirectory(projectRoot);

        string temporaryPath = Path.Combine(
            projectRoot,
            "temp",
            "screen.partial.mp4");
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
            projectWriter,
            projectClock,
            pauseController,
            temporaryPath);
    }

    public ValueTask ProcessFrameAsync(
        ScreenCaptureFrameLease frame,
        CancellationToken cancellationToken)
    {
        if (_pauseController.IsPaused)
        {
            return ValueTask.CompletedTask;
        }

        return _encoder.ProcessSurfaceAsync(
            frame.Surface,
            _pauseController.Map(frame.ProjectTime),
            cancellationToken);
    }

    public async Task<RecordingSegmentEntry> CompleteAsync()
    {
        if (_completed)
        {
            throw new InvalidOperationException(
                "The surface segment is already complete.");
        }

        _completed = true;
        await _encoder.CompleteAsync();
        TimeSpan rawDuration = _projectClock.Normalize(QpcTimestamp.Now());
        return await _projectWriter.PublishAsync(
            _temporaryPath,
            sourceId: "screen",
            start: TimeSpan.Zero,
            duration: _pauseController.Map(rawDuration));
    }

    public async ValueTask DisposeAsync()
    {
        await _encoder.DisposeAsync();
    }
}
