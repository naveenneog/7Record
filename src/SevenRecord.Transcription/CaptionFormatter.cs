using System.Globalization;
using System.Text;
using SevenRecord.Domain.Captions;

namespace SevenRecord.Transcription;

public static class CaptionFormatter
{
    public static string ToSrt(CaptionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        StringBuilder builder = new();
        CaptionSegment[] segments = document.Segments
            .OrderBy(segment => segment.Start)
            .ToArray();
        for (int index = 0; index < segments.Length; index++)
        {
            CaptionSegment segment = segments[index];
            builder.AppendLine((index + 1).ToString(CultureInfo.InvariantCulture));
            builder.Append(FormatTimestamp(segment.Start, ','));
            builder.Append(" --> ");
            builder.AppendLine(FormatTimestamp(segment.End, ','));
            builder.AppendLine(segment.Text.Trim());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string ToVtt(CaptionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        StringBuilder builder = new("WEBVTT");
        builder.AppendLine();
        builder.AppendLine();
        foreach (CaptionSegment segment in document.Segments.OrderBy(
                     segment => segment.Start))
        {
            builder.Append(FormatTimestamp(segment.Start, '.'));
            builder.Append(" --> ");
            builder.AppendLine(FormatTimestamp(segment.End, '.'));
            builder.AppendLine(segment.Text.Trim());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string FormatTimestamp(TimeSpan value, char millisecondSeparator) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}" +
        $"{millisecondSeparator}{value.Milliseconds:000}";
}
