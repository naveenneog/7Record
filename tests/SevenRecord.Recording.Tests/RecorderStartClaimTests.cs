using SevenRecord.Recording;

namespace SevenRecord.Recording.Tests;

/// <summary>
/// Covers the atomic claim that stops two concurrent Record commands from racing.
/// </summary>
/// <remarks>
/// The defect these pin: <c>OnStartRecordingClicked</c> read the recorder state, then
/// awaited display selection, readiness and camera shutdown, and only then called
/// <see cref="RecorderStateMachine.BeginStart"/>. A second click or global hotkey arriving
/// during those awaits observed <c>Idle</c> too, and the loser reached
/// <see cref="RecorderStateMachine.BeginStart"/> after the winner had already moved the
/// state to <c>Starting</c> - which threw on the UI thread out of an <c>async void</c>
/// handler, terminating the process.
/// </remarks>
[TestClass]
public sealed class RecorderStartClaimTests
{
    [TestMethod]
    public void TryBeginStartClaimsIdleAndMovesToStarting()
    {
        RecorderStateMachine machine = new();
        Guid sessionId = Guid.NewGuid();

        bool claimed = machine.TryBeginStart(out RecorderSnapshot snapshot, sessionId);

        Assert.IsTrue(claimed);
        Assert.AreEqual(RecorderState.Starting, snapshot.State);
        Assert.AreEqual(sessionId, snapshot.SessionId);
        Assert.AreEqual(1, snapshot.Revision);
    }

    [TestMethod]
    public void ASecondClaimIsRefusedRatherThanThrowing()
    {
        RecorderStateMachine machine = new();
        machine.TryBeginStart(out RecorderSnapshot first);

        // BeginStart would throw here. That throw, on the UI thread, out of an async void
        // handler, is the process kill this whole packet exists to remove.
        bool claimed = machine.TryBeginStart(out RecorderSnapshot second);

        Assert.IsFalse(claimed);
        Assert.AreEqual(RecorderState.Starting, second.State);
        Assert.AreEqual(first.Revision, second.Revision);
        Assert.AreEqual(first.SessionId, second.SessionId);
    }

    [TestMethod]
    public void ARefusedClaimDoesNotDisturbTheSnapshot()
    {
        RecorderStateMachine machine = new();
        machine.TryBeginStart(out _);
        machine.MarkRecording();
        RecorderSnapshot before = machine.Snapshot;

        bool claimed = machine.TryBeginStart(out _);

        Assert.IsFalse(claimed);
        Assert.AreEqual(before, machine.Snapshot);
    }

    [TestMethod]
    public void OnlyIdleCanBeClaimed()
    {
        foreach (RecorderState occupied in OccupiedStates)
        {
            RecorderStateMachine machine = MachineIn(occupied);

            bool claimed = machine.TryBeginStart(out _);

            Assert.IsFalse(
                claimed,
                $"a start was claimed while the recorder was {occupied}");
        }
    }

    [TestMethod]
    public void AFaultedRecorderCannotBeClaimedUntilItIsReset()
    {
        RecorderStateMachine machine = new();
        machine.TryBeginStart(out _);
        machine.MarkFaulted("capture died");

        Assert.IsFalse(machine.TryBeginStart(out _));

        machine.Reset();

        Assert.IsTrue(machine.TryBeginStart(out _));
    }

    [TestMethod]
    public void ConcurrentClaimsSucceedExactlyOnce()
    {
        RecorderStateMachine machine = new();
        const int Racers = 4;
        using Barrier start = new(Racers);
        int successes = 0;

        // The actual race: several commands arriving at once, all seeing Idle.
        // The rendezvous is bounded, because Parallel.For gives no guarantee that all
        // participants are scheduled simultaneously and MSTest runs methods in parallel
        // against the same pool; an unbounded SignalAndWait would hang a small CI agent
        // rather than fail it.
        Parallel.For(0, Racers, racer =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(10));
            if (machine.TryBeginStart(out _))
            {
                Interlocked.Increment(ref successes);
            }
        });

        Assert.AreEqual(1, successes);
        Assert.AreEqual(RecorderState.Starting, machine.Snapshot.State);
        Assert.AreEqual(1, machine.Snapshot.Revision);
    }

    [TestMethod]
    public void ClaimingRaisesStateChangedExactlyOnce()
    {
        RecorderStateMachine machine = new();
        int notifications = 0;
        machine.StateChanged += _ => Interlocked.Increment(ref notifications);

        machine.TryBeginStart(out _);
        machine.TryBeginStart(out _);

        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public void AbandoningAStartReturnsToIdleAndClearsTheSession()
    {
        RecorderStateMachine machine = new();
        machine.TryBeginStart(out _, Guid.NewGuid());

        bool abandoned = machine.TryAbortStart(out RecorderSnapshot snapshot);

        // A start whose preconditions failed never happened. It must not travel through
        // Stopping, which would tell the user their recording is being torn down.
        Assert.IsTrue(abandoned);
        Assert.AreEqual(RecorderState.Idle, snapshot.State);
        Assert.IsNull(snapshot.SessionId);
    }

    [TestMethod]
    public void AbandoningIsRefusedOnceRecordingHasActuallyBegun()
    {
        RecorderStateMachine machine = new();
        machine.TryBeginStart(out _);
        machine.MarkRecording();

        bool abandoned = machine.TryAbortStart(out _);

        Assert.IsFalse(abandoned);
        Assert.AreEqual(RecorderState.Recording, machine.Snapshot.State);
    }

    [TestMethod]
    public void AbandoningIsRefusedWhenAStopAlreadyClaimedTheRecorder()
    {
        RecorderStateMachine machine = new();
        machine.TryBeginStart(out _);
        machine.TryBeginStop(out _);

        // A stop that arrived mid-startup owns the teardown; abandoning here would strand
        // whoever is awaiting the stop.
        Assert.IsFalse(machine.TryAbortStart(out _));
        Assert.AreEqual(RecorderState.Stopping, machine.Snapshot.State);
    }

    [TestMethod]
    public void AClaimCanBeMadeAgainAfterAbandoning()
    {
        RecorderStateMachine machine = new();
        machine.TryBeginStart(out _);
        machine.TryAbortStart(out _);

        Assert.IsTrue(machine.TryBeginStart(out _));
    }

    [TestMethod]
    public void AbandoningIsRefusedFromIdle()
    {
        RecorderStateMachine machine = new();

        bool abandoned = machine.TryAbortStart(out RecorderSnapshot snapshot);

        Assert.IsFalse(abandoned);
        Assert.AreEqual(RecorderState.Idle, snapshot.State);
        Assert.AreEqual(0, machine.Snapshot.Revision);
    }

    [TestMethod]
    public void AbandoningIsRefusedWhenFaultedAndLeavesTheStateRecoverable()
    {
        RecorderStateMachine machine = new();
        machine.TryBeginStart(out _);
        machine.MarkFaulted("capture died");

        // AbandonClaimedStart falls through both of its branches here. The recorder must
        // stay in a state the next command can recover from with Reset().
        Assert.IsFalse(machine.TryAbortStart(out _));
        Assert.AreEqual(RecorderState.Faulted, machine.Snapshot.State);

        machine.Reset();

        Assert.IsTrue(machine.TryBeginStart(out _));
    }

    [TestMethod]
    public void AbandoningRaisesStateChangedExactlyOnce()
    {
        RecorderStateMachine machine = new();
        machine.TryBeginStart(out _);
        int notifications = 0;
        machine.StateChanged += _ => Interlocked.Increment(ref notifications);

        machine.TryAbortStart(out _);
        machine.TryAbortStart(out _);

        Assert.AreEqual(1, notifications);
    }

    private static readonly RecorderState[] OccupiedStates =
    [
        RecorderState.Starting,
        RecorderState.Recording,
        RecorderState.Paused,
        RecorderState.Stopping,
    ];

    private static RecorderStateMachine MachineIn(RecorderState state)
    {
        RecorderStateMachine machine = new();
        machine.TryBeginStart(out _);
        switch (state)
        {
            case RecorderState.Starting:
                break;
            case RecorderState.Recording:
                machine.MarkRecording();
                break;
            case RecorderState.Paused:
                machine.MarkRecording();
                machine.Pause();
                break;
            case RecorderState.Stopping:
                machine.TryBeginStop(out _);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }

        return machine;
    }
}
