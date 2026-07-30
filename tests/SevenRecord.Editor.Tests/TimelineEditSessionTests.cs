using SevenRecord.Domain.Timeline;

namespace SevenRecord.Editor.Tests;

[TestClass]
public sealed class TimelineEditSessionTests
{
    [TestMethod]
    public void SplitDeleteReorderAndUndoStayNonDestructive()
    {
        TimelineEditSession session = new(
            TimelineEditDocument.CreateDefault(
                TimeSpan.FromSeconds(10)),
            TimeSpan.FromSeconds(10));
        string original = session.Current.Slices.Single().Id;

        session.Split(original, TimeSpan.FromSeconds(4));
        Assert.HasCount(2, session.Current.Slices);
        string second = session.Current.Slices[1].Id;
        session.Move(second, -1);
        Assert.AreEqual(second, session.Current.Slices[0].Id);
        session.Delete(session.Current.Slices[1].Id);
        Assert.HasCount(1, session.Current.Slices);
        Assert.IsTrue(session.Undo());
        Assert.HasCount(2, session.Current.Slices);
    }

    [TestMethod]
    public void TrimCannotExpandOutsideSelectedSource()
    {
        TimelineEditSession session = new(
            TimelineEditDocument.CreateDefault(
                TimeSpan.FromSeconds(10)),
            TimeSpan.FromSeconds(10));
        TimelineEditSlice slice = session.Current.Slices.Single();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => session.Trim(
                slice.Id,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(11)));
    }
}
