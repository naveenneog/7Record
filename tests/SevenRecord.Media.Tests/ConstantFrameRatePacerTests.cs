namespace SevenRecord.Media.Tests;

[TestClass]
public sealed class ConstantFrameRatePacerTests
{
    [TestMethod]
    public async Task PacerRepeatsTheLatestFrameAtAConstantRate()
    {
        System.Collections.Concurrent.ConcurrentQueue<byte[]> frames = new();
        ConstantFrameRatePacer pacer = new(
            width: 2,
            height: 2,
            framesPerSecond: 20,
            (frame, cancellationToken) =>
            {
                frames.Enqueue(frame.ToArray());
                return ValueTask.CompletedTask;
            });

        byte[] source = Enumerable.Repeat((byte)7, 16).ToArray();
        pacer.UpdateFrame(source);
        await Task.Delay(180);
        await pacer.DisposeAsync();

        Assert.IsGreaterThanOrEqualTo(2, frames.Count);
        Assert.IsTrue(frames.All(frame => frame.SequenceEqual(source)));
    }

    [TestMethod]
    public async Task PacerRejectsTheWrongFrameSize()
    {
        ConstantFrameRatePacer pacer = new(
            width: 2,
            height: 2,
            framesPerSecond: 30,
            (frame, cancellationToken) => ValueTask.CompletedTask);

        Assert.ThrowsExactly<ArgumentException>(() => pacer.UpdateFrame(new byte[15]));
        await pacer.DisposeAsync();
    }
}
