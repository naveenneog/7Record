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
