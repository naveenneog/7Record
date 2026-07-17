namespace SevenRecord.Capture.Abstractions;

public readonly record struct CaptureFrameHealthSnapshot(
    long FramesReceived,
    long FramesDropped,
    TimeSpan LastProjectTime)
{
    public double DropRate => FramesReceived == 0
        ? 0
        : FramesDropped / (double)FramesReceived;
}

public sealed class CaptureFrameHealthCounter
{
    private long _framesDropped;
    private long _framesReceived;
    private long _lastProjectTicks;

    public CaptureFrameHealthSnapshot ReportReceived(TimeSpan projectTime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(projectTime, TimeSpan.Zero);
        Interlocked.Exchange(ref _lastProjectTicks, projectTime.Ticks);
        Interlocked.Increment(ref _framesReceived);
        return Snapshot();
    }

    public CaptureFrameHealthSnapshot ReportDropped()
    {
        Interlocked.Increment(ref _framesDropped);
        return Snapshot();
    }

    public CaptureFrameHealthSnapshot Snapshot() =>
        new(
            Interlocked.Read(ref _framesReceived),
            Interlocked.Read(ref _framesDropped),
            TimeSpan.FromTicks(Interlocked.Read(ref _lastProjectTicks)));
}
