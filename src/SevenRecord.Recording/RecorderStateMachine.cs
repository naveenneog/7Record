namespace SevenRecord.Recording;

public sealed class RecorderStateMachine
{
    private readonly object _gate = new();
    private RecorderSnapshot _snapshot =
        new(0, RecorderState.Idle, null, null);

    public event Action<RecorderSnapshot>? StateChanged;

    public RecorderSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public RecorderSnapshot BeginStart(Guid? sessionId = null) =>
        Transition(
            RecorderState.Starting,
            sessionId ?? Guid.NewGuid(),
            null,
            false,
            RecorderState.Idle);

    /// <summary>
    /// Atomically claims an idle recorder for a start, without throwing when it is lost.
    /// </summary>
    /// <remarks>
    /// Preferred over <see cref="BeginStart"/> from any UI command path. A start command
    /// has to select a capture target, refresh readiness and shut down the camera studio
    /// before it can actually begin, and every one of those is an await. Reading the state
    /// first and calling <see cref="BeginStart"/> afterwards leaves a window in which a
    /// second click or global hotkey also sees <see cref="RecorderState.Idle"/>; the loser
    /// then throws out of an <c>async void</c> handler, which kills the process.
    /// Claiming first turns that race into a refusal.
    /// </remarks>
    /// <returns><see langword="true"/> when this caller now owns the start.</returns>
    public bool TryBeginStart(
        out RecorderSnapshot snapshot,
        Guid? sessionId = null)
    {
        Action<RecorderSnapshot>? changed;
        lock (_gate)
        {
            if (_snapshot.State is not RecorderState.Idle)
            {
                snapshot = _snapshot;
                return false;
            }

            snapshot = NextSnapshot(
                RecorderState.Starting,
                sessionId ?? Guid.NewGuid(),
                null);
            _snapshot = snapshot;
            changed = StateChanged;
        }

        changed?.Invoke(snapshot);
        return true;
    }

    /// <summary>
    /// Abandons a claimed start that never actually began.
    /// </summary>
    /// <remarks>
    /// Used when a precondition fails after the claim - no capture target, readiness not
    /// met, camera studio refusing to release. It returns straight to
    /// <see cref="RecorderState.Idle"/> rather than travelling through
    /// <see cref="RecorderState.Stopping"/>, because telling the user their recording is
    /// being torn down when it never started is a lie the UI would then have to explain.
    /// Refused once a stop has claimed the recorder, since that stop owns the teardown and
    /// something is already awaiting it.
    /// </remarks>
    public bool TryAbortStart(out RecorderSnapshot snapshot)
    {
        Action<RecorderSnapshot>? changed;
        lock (_gate)
        {
            if (_snapshot.State is not RecorderState.Starting)
            {
                snapshot = _snapshot;
                return false;
            }

            snapshot = NextSnapshot(RecorderState.Idle, null, null);
            _snapshot = snapshot;
            changed = StateChanged;
        }

        changed?.Invoke(snapshot);
        return true;
    }

    public RecorderSnapshot MarkRecording() =>
        Transition(
            RecorderState.Recording,
            allowed: [RecorderState.Starting]);

    public RecorderSnapshot Pause() =>
        Transition(
            RecorderState.Paused,
            allowed: [RecorderState.Recording]);

    public RecorderSnapshot Resume() =>
        Transition(
            RecorderState.Recording,
            allowed: [RecorderState.Paused]);

    public RecorderSnapshot BeginStop() =>
        Transition(
            RecorderState.Stopping,
            allowed:
            [
                RecorderState.Starting,
                RecorderState.Recording,
                RecorderState.Paused,
            ]);

    public bool TryBeginStop(out RecorderSnapshot snapshot)
    {
        Action<RecorderSnapshot>? changed = null;
        lock (_gate)
        {
            if (_snapshot.State is not (
                RecorderState.Starting or
                RecorderState.Recording or
                RecorderState.Paused))
            {
                snapshot = _snapshot;
                return false;
            }

            snapshot = NextSnapshot(
                RecorderState.Stopping,
                _snapshot.SessionId,
                null);
            _snapshot = snapshot;
            changed = StateChanged;
        }

        changed?.Invoke(snapshot);
        return true;
    }

    public RecorderSnapshot CompleteStop() =>
        Transition(
            RecorderState.Idle,
            clearSession: true,
            allowed: [RecorderState.Stopping]);

    public RecorderSnapshot MarkFaulted(string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        return Transition(
            RecorderState.Faulted,
            failure: failure,
            allowed:
            [
                RecorderState.Starting,
                RecorderState.Recording,
                RecorderState.Paused,
                RecorderState.Stopping,
            ]);
    }

    public RecorderSnapshot Reset() =>
        Transition(
            RecorderState.Idle,
            clearSession: true,
            allowed: [RecorderState.Faulted]);

    private RecorderSnapshot Transition(
        RecorderState next,
        Guid? sessionId = null,
        string? failure = null,
        bool clearSession = false,
        params RecorderState[] allowed)
    {
        Action<RecorderSnapshot>? changed;
        RecorderSnapshot snapshot;
        lock (_gate)
        {
            if (!allowed.Contains(_snapshot.State))
            {
                throw new InvalidOperationException(
                    $"Recorder cannot transition from {_snapshot.State} to {next}.");
            }

            snapshot = NextSnapshot(
                next,
                clearSession ? null : sessionId ?? _snapshot.SessionId,
                failure);
            _snapshot = snapshot;
            changed = StateChanged;
        }

        changed?.Invoke(snapshot);
        return snapshot;
    }

    private RecorderSnapshot NextSnapshot(
        RecorderState next,
        Guid? sessionId,
        string? failure) =>
        new(
            checked(_snapshot.Revision + 1),
            next,
            sessionId,
            failure);
}
