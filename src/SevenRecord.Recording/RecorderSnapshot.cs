namespace SevenRecord.Recording;

public sealed record RecorderSnapshot(
    long Revision,
    RecorderState State,
    Guid? SessionId,
    string? Failure)
{
    public bool IsActive =>
        State is RecorderState.Starting or
            RecorderState.Recording or
            RecorderState.Paused or
            RecorderState.Stopping;
}
