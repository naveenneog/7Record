using SevenRecord.Domain.Timeline;

namespace SevenRecord.Editor;

public sealed class TimelineEditSession
{
    private readonly TimeSpan _sourceDuration;
    private readonly Stack<TimelineEditDocument> _redo = new();
    private readonly Stack<TimelineEditDocument> _undo = new();

    public TimelineEditSession(
        TimelineEditDocument document,
        TimeSpan sourceDuration)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            sourceDuration,
            TimeSpan.Zero);
        _sourceDuration = sourceDuration;
        Current = document.Validate(sourceDuration);
    }

    public bool CanRedo => _redo.Count > 0;

    public bool CanUndo => _undo.Count > 0;

    public TimelineEditDocument Current { get; private set; }

    public void Split(string sliceId, TimeSpan sourcePosition)
    {
        int index = IndexOf(sliceId);
        TimelineEditSlice slice = Current.Slices[index];
        if (sourcePosition <= slice.SourceRange.Start ||
            sourcePosition >= slice.SourceRange.End)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourcePosition),
                "Split must be inside the selected clip.");
        }
        TimelineEditSlice[] slices = Current.Slices.ToArray();
        List<TimelineEditSlice> next = [.. slices.Take(index)];
        next.Add(
            new TimelineEditSlice(
                $"{slice.Id}-a-{sourcePosition.Ticks:x}",
                new TimelineRange(
                    slice.SourceRange.Start,
                    sourcePosition)));
        next.Add(
            new TimelineEditSlice(
                $"{slice.Id}-b-{sourcePosition.Ticks:x}",
                new TimelineRange(
                    sourcePosition,
                    slice.SourceRange.End)));
        next.AddRange(slices.Skip(index + 1));
        Commit(Current with { Slices = next });
    }

    public void Trim(
        string sliceId,
        TimeSpan start,
        TimeSpan end)
    {
        int index = IndexOf(sliceId);
        TimelineEditSlice slice = Current.Slices[index];
        if (start < slice.SourceRange.Start ||
            end > slice.SourceRange.End ||
            end <= start)
        {
            throw new ArgumentOutOfRangeException(
                nameof(start),
                "Trim must remain inside the selected source clip.");
        }
        TimelineEditSlice[] slices = Current.Slices.ToArray();
        slices[index] = slice with
        {
            SourceRange = new TimelineRange(start, end),
        };
        Commit(Current with { Slices = slices });
    }

    public void Delete(string sliceId)
    {
        if (Current.Slices.Count <= 1)
        {
            throw new InvalidOperationException(
                "The last clip cannot be deleted.");
        }
        int index = IndexOf(sliceId);
        Commit(
            Current with
            {
                Slices = Current.Slices
                    .Where((_, itemIndex) => itemIndex != index)
                    .ToArray(),
            });
    }

    public void Move(string sliceId, int delta)
    {
        int index = IndexOf(sliceId);
        int target = Math.Clamp(
            index + delta,
            0,
            Current.Slices.Count - 1);
        if (target == index)
        {
            return;
        }
        List<TimelineEditSlice> slices = [.. Current.Slices];
        TimelineEditSlice item = slices[index];
        slices.RemoveAt(index);
        slices.Insert(target, item);
        Commit(Current with { Slices = slices });
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

    public bool RollbackLastChange()
    {
        if (_undo.Count == 0)
        {
            return false;
        }
        Current = _undo.Pop();
        _redo.Clear();
        return true;
    }

    private int IndexOf(string sliceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sliceId);
        int index = Current.Slices
            .Select((slice, index) => (slice, index))
            .FirstOrDefault(item => item.slice.Id == sliceId)
            .index;
        if (index < 0 ||
            index >= Current.Slices.Count ||
            Current.Slices[index].Id != sliceId)
        {
            throw new KeyNotFoundException(
                $"Clip '{sliceId}' was not found.");
        }
        return index;
    }

    private void Commit(TimelineEditDocument next)
    {
        _undo.Push(Current);
        _redo.Clear();
        Current = next.Validate(_sourceDuration);
    }
}
