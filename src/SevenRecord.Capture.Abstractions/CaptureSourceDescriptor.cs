namespace SevenRecord.Capture.Abstractions;

public enum CaptureSourceKind
{
    Display,
    Window,
    Region,
    Camera,
    Microphone,
    SystemAudio,
}

public enum CaptureSourceState
{
    Unavailable,
    Ready,
    Warning,
    Error,
}

public sealed record CaptureSourceDescriptor(
    string Id,
    string Name,
    CaptureSourceKind Kind,
    CaptureSourceState State,
    string? StatusMessage = null);
