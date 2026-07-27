using SevenRecord.Domain.Captions;

namespace SevenRecord.Editor;

public static class CaptionDocumentValidator
{
    public static CaptionDocument ValidateAndNormalize(
        CaptionDocument document,
        TimeSpan? maximumDuration = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        CaptionSegment[] segments = document.Segments
            .OrderBy(segment => segment.Start)
            .ThenBy(segment => segment.End)
            .ToArray();
        TimeSpan previousEnd = TimeSpan.Zero;
        for (int index = 0; index < segments.Length; index++)
        {
            CaptionSegment segment = segments[index];
            if (string.IsNullOrWhiteSpace(segment.Id))
            {
                throw new InvalidDataException("Every caption requires an identifier.");
            }
            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                throw new InvalidDataException(
                    $"Caption '{segment.Id}' cannot be empty.");
            }
            if (segment.Start < TimeSpan.Zero || segment.End <= segment.Start)
            {
                throw new InvalidDataException(
                    $"Caption '{segment.Id}' has an invalid time range.");
            }
            if (index > 0 && segment.Start < previousEnd)
            {
                throw new InvalidDataException(
                    $"Caption '{segment.Id}' overlaps the previous caption.");
            }
            if (maximumDuration is TimeSpan limit && segment.End > limit)
            {
                throw new InvalidDataException(
                    $"Caption '{segment.Id}' ends after the recording.");
            }

            previousEnd = segment.End;
        }

        return document with { Segments = segments };
    }
}
