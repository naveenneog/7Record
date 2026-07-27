using SevenRecord.Domain.Captions;

namespace SevenRecord.Editor;

public sealed class CaptionEditSession
{
    private readonly Stack<CaptionDocument> _redo = new();
    private readonly Stack<CaptionDocument> _undo = new();
    private readonly TimeSpan? _maximumDuration;

    public CaptionEditSession(
        CaptionDocument document,
        TimeSpan? maximumDuration = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        _maximumDuration = maximumDuration;
        Current = CaptionDocumentValidator.ValidateAndNormalize(
            document,
            maximumDuration);
    }

    public CaptionDocument Current { get; private set; }

    public bool CanRedo => _redo.Count > 0;

    public bool CanUndo => _undo.Count > 0;

    public void UpdateCaption(
        string id,
        string text,
        TimeSpan start,
        TimeSpan end)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(start, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(end, start);

        int index = Current.Segments
            .Select((segment, segmentIndex) => (segment, segmentIndex))
            .FirstOrDefault(item => item.segment.Id == id)
            .segmentIndex;
        if (index < 0 || index >= Current.Segments.Count ||
            Current.Segments[index].Id != id)
        {
            throw new KeyNotFoundException($"Caption '{id}' was not found.");
        }

        CaptionSegment updated = Current.Segments[index] with
        {
            Text = text.Trim(),
            Start = start,
            End = end,
        };
        CaptionSegment[] segments = Current.Segments.ToArray();
        segments[index] = updated;

        CaptionDocument next = CaptionDocumentValidator.ValidateAndNormalize(
            Current with { Segments = segments },
            _maximumDuration);
        _undo.Push(Current);
        _redo.Clear();
        Current = next;
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        _redo.Push(Current);
        Current = _undo.Pop();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        _undo.Push(Current);
        Current = _redo.Pop();
        return true;
    }
}
