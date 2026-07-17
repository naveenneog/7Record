namespace SevenRecord.Domain.Captions;

public sealed record CaptionSegment(
    string Id,
    TimeSpan Start,
    TimeSpan End,
    string Text);

public sealed record CaptionDocument(
    int SchemaVersion,
    string Language,
    IReadOnlyList<CaptionSegment> Segments);
