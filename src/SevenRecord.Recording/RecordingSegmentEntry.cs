using System.Text.Json.Serialization;

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
    [JsonIgnore]
    public TimeSpan Start => TimeSpan.FromTicks(StartTicks);

    [JsonIgnore]
    public TimeSpan Duration => TimeSpan.FromTicks(DurationTicks);
}
