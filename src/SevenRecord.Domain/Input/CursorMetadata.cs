namespace SevenRecord.Domain.Input;

public enum CursorEventKind
{
    Move,
    Click,
}

public enum CursorButton
{
    None,
    Left,
    Right,
}

public sealed record CursorMetadataEvent(
    TimeSpan ProjectTime,
    int ScreenX,
    int ScreenY,
    double NormalizedX,
    double NormalizedY,
    CursorEventKind Kind,
    CursorButton Button);

public sealed record CursorMetadataDocument(
    int SchemaVersion,
    IReadOnlyList<CursorMetadataEvent> Events);

public sealed record CursorZoomEvent(
    string Id,
    TimeSpan Start,
    TimeSpan Duration,
    double CenterX,
    double CenterY,
    double Scale,
    double Confidence);
