namespace SevenRecord.Recording;

public sealed record RecordingSegmentEntry(
    int Sequence,
    string SegmentId,
    string SourceId,
    string RelativePath,
    long StartTicks,
    long DurationTicks,
    long Length,
    string Sha256)
{
    public TimeSpan Start => TimeSpan.FromTicks(StartTicks);

    public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);
}
