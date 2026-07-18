using SevenRecord.Recording;

namespace SevenRecord.Recording.Tests;

[TestClass]
public sealed class RecorderStateMachineTests
{
    [TestMethod]
    public void FullLifecyclePreservesSessionAndIncrementsRevision()
    {
        RecorderStateMachine machine = new();
        Guid sessionId = Guid.NewGuid();

        RecorderSnapshot starting = machine.BeginStart(sessionId);
        RecorderSnapshot recording = machine.MarkRecording();
        RecorderSnapshot paused = machine.Pause();
        RecorderSnapshot resumed = machine.Resume();
        RecorderSnapshot stopping = machine.BeginStop();
        RecorderSnapshot idle = machine.CompleteStop();

        Assert.AreEqual(RecorderState.Starting, starting.State);
        Assert.AreEqual(RecorderState.Recording, recording.State);
        Assert.AreEqual(RecorderState.Paused, paused.State);
        Assert.AreEqual(RecorderState.Recording, resumed.State);
        Assert.AreEqual(RecorderState.Stopping, stopping.State);
        Assert.AreEqual(RecorderState.Idle, idle.State);
        Assert.AreEqual(6, idle.Revision);
        Assert.AreEqual(sessionId, stopping.SessionId);
        Assert.IsNull(idle.SessionId);
    }

    [TestMethod]
    public void InvalidTransitionIsRejectedWithoutChangingSnapshot()
    {
        RecorderStateMachine machine = new();

        InvalidOperationException exception =
            Assert.ThrowsExactly<InvalidOperationException>(() => machine.Pause());

        StringAssert.Contains(exception.Message, "Idle");
        Assert.AreEqual(
            new RecorderSnapshot(0, RecorderState.Idle, null, null),
            machine.Snapshot);
    }

    [TestMethod]
    public void StopCanBeginWhileStartupIsStillRunning()
    {
        RecorderStateMachine machine = new();
        RecorderSnapshot starting = machine.BeginStart();

        RecorderSnapshot stopping = machine.BeginStop();

        Assert.AreEqual(RecorderState.Stopping, stopping.State);
        Assert.AreEqual(starting.SessionId, stopping.SessionId);
    }

    [TestMethod]
    public void FaultRetainsSessionUntilExplicitReset()
    {
        RecorderStateMachine machine = new();
        RecorderSnapshot starting = machine.BeginStart();

        RecorderSnapshot faulted = machine.MarkFaulted("Camera initialization failed.");
        RecorderSnapshot idle = machine.Reset();

        Assert.AreEqual(RecorderState.Faulted, faulted.State);
        Assert.AreEqual(starting.SessionId, faulted.SessionId);
        Assert.AreEqual("Camera initialization failed.", faulted.Failure);
        Assert.AreEqual(RecorderState.Idle, idle.State);
        Assert.IsNull(idle.SessionId);
        Assert.IsNull(idle.Failure);
    }

    [TestMethod]
    public void ConcurrentStopRequestsProduceOneTransition()
    {
        RecorderStateMachine machine = new();
        machine.BeginStart();
        machine.MarkRecording();
        int successfulTransitions = 0;

        Parallel.For(
            0,
            32,
            _index =>
            {
                if (machine.TryBeginStop(out _))
                {
                    Interlocked.Increment(ref successfulTransitions);
                }
            });

        Assert.AreEqual(1, successfulTransitions);
        Assert.AreEqual(RecorderState.Stopping, machine.Snapshot.State);
        Assert.AreEqual(3, machine.Snapshot.Revision);
    }

    [TestMethod]
    public void StateChangedReceivesCommittedSnapshots()
    {
        RecorderStateMachine machine = new();
        List<RecorderSnapshot> changes = [];
        machine.StateChanged += changes.Add;

        machine.BeginStart();
        machine.MarkRecording();
        machine.BeginStop();
        machine.CompleteStop();

        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3, 4 },
            changes.Select(change => change.Revision).ToArray());
        Assert.IsTrue(changes.All(change => change == machine.Snapshot ||
            change.Revision < machine.Snapshot.Revision));
    }
}
