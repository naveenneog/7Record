namespace SevenRecord.Media;

public sealed class ConstantFrameRatePacer : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _expectedFrameBytes;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _writer;
    private readonly Task _writerTask;
    private byte[]? _latestFrame;
    private long _framesWritten;

    public ConstantFrameRatePacer(
        int width,
        int height,
        int framesPerSecond,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        ArgumentNullException.ThrowIfNull(writer);

        _expectedFrameBytes = checked(width * height * 4);
        FramesPerSecond = framesPerSecond;
        _writer = writer;
        _writerTask = Task.Run(WriteFramesAsync);
    }

    public int FramesPerSecond { get; }

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public void UpdateFrame(byte[] bgraFrame)
    {
        ArgumentNullException.ThrowIfNull(bgraFrame);
        if (bgraFrame.Length != _expectedFrameBytes)
        {
            throw new ArgumentException(
                $"Expected {_expectedFrameBytes} BGRA bytes but received {bgraFrame.Length}.",
                nameof(bgraFrame));
        }

        Interlocked.Exchange(ref _latestFrame, bgraFrame);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _writerTask;
        _shutdown.Dispose();
    }

    private async Task WriteFramesAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1d / FramesPerSecond));

        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                byte[]? frame = Volatile.Read(ref _latestFrame);
                if (frame is null)
                {
                    continue;
                }

                await _writer(frame, _shutdown.Token);
                Interlocked.Increment(ref _framesWritten);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }
}
