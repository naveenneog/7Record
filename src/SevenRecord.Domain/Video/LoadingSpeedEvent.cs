namespace SevenRecord.Domain.Video;

public sealed record LoadingSpeedEvent(
    string Id,
    TimeSpan Start,
    TimeSpan Duration,
    double Speed,
    double Confidence);
