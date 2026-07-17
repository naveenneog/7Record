namespace SevenRecord.Recording;

public sealed record RecordingSegmentPolicy
{
    public static RecordingSegmentPolicy Default { get; } = new(TimeSpan.FromSeconds(5));

    public RecordingSegmentPolicy(TimeSpan targetDuration)
    {
        if (targetDuration < TimeSpan.FromSeconds(2) || targetDuration > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetDuration),
                "Recording segments must be between 2 and 10 seconds.");
        }

        TargetDuration = targetDuration;
    }

    public TimeSpan TargetDuration { get; }
}
