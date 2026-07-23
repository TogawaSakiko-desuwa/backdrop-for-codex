namespace BackdropForCodex.Core.Codex;

public enum InjectionSessionState
{
    Idle = 0,
    Discovering,
    Connecting,
    Injecting,
    Active,
    Recovering,
    Stopping,
    Faulted,
    Disposed,
}

public enum InjectionSessionTrigger
{
    StartRequested = 0,
    EndpointDiscovered,
    CdpConnected,
    InjectionApplied,
    ConnectionLost,
    HeartbeatLeaseExpired,
    RecoveryReady,
    StopRequested,
    StopCompleted,
    Failure,
    Reset,
    Dispose,
}

public sealed record InjectionSessionSnapshot(
    InjectionSessionState State,
    long Generation,
    DateTimeOffset ChangedAt,
    string? FailureReason);

public sealed record InjectionSessionTransition(
    InjectionSessionSnapshot Before,
    InjectionSessionTrigger Trigger,
    InjectionSessionSnapshot After)
{
    public bool Changed => Before != After;
}

/// <summary>
/// Thread-safe lifecycle rules for one CDP/injection session. It has no browser or process side
/// effects; an orchestrator performs those only after a transition has been accepted.
/// </summary>
public sealed class InjectionSessionStateMachine
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private InjectionSessionSnapshot _snapshot;

    public InjectionSessionStateMachine(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _snapshot = new InjectionSessionSnapshot(
            InjectionSessionState.Idle,
            0,
            _timeProvider.GetUtcNow(),
            null);
    }

    public event EventHandler<InjectionSessionTransition>? Transitioned;

    public InjectionSessionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public InjectionSessionTransition Apply(
        InjectionSessionTrigger trigger,
        string? failureReason = null)
    {
        InjectionSessionTransition transition;
        lock (_gate)
        {
            var before = _snapshot;
            var nextState = GetNextState(before.State, trigger);
            if (trigger == InjectionSessionTrigger.Failure)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
            }

            if (nextState == before.State && IsIdempotent(trigger, before.State))
            {
                transition = new InjectionSessionTransition(before, trigger, before);
            }
            else
            {
                var generation = before.Generation;
                if (trigger is InjectionSessionTrigger.StartRequested or
                    InjectionSessionTrigger.RecoveryReady)
                {
                    generation = checked(generation + 1);
                }

                var nextFailure = trigger == InjectionSessionTrigger.Failure
                    ? failureReason
                    : nextState == InjectionSessionState.Faulted
                        ? before.FailureReason
                        : null;
                _snapshot = new InjectionSessionSnapshot(
                    nextState,
                    generation,
                    _timeProvider.GetUtcNow(),
                    nextFailure);
                transition = new InjectionSessionTransition(before, trigger, _snapshot);
            }
        }

        if (transition.Changed)
        {
            Transitioned?.Invoke(this, transition);
        }

        return transition;
    }

    private static InjectionSessionState GetNextState(
        InjectionSessionState state,
        InjectionSessionTrigger trigger)
    {
        if (trigger == InjectionSessionTrigger.Dispose)
        {
            return state == InjectionSessionState.Disposed
                ? state
                : InjectionSessionState.Disposed;
        }

        return (state, trigger) switch
        {
            (InjectionSessionState.Idle, InjectionSessionTrigger.StartRequested) =>
                InjectionSessionState.Discovering,
            (InjectionSessionState.Idle, InjectionSessionTrigger.StopRequested) =>
                InjectionSessionState.Idle,

            (InjectionSessionState.Discovering, InjectionSessionTrigger.EndpointDiscovered) =>
                InjectionSessionState.Connecting,
            (InjectionSessionState.Connecting, InjectionSessionTrigger.CdpConnected) =>
                InjectionSessionState.Injecting,
            (InjectionSessionState.Injecting, InjectionSessionTrigger.InjectionApplied) =>
                InjectionSessionState.Active,

            (InjectionSessionState.Connecting, InjectionSessionTrigger.ConnectionLost) =>
                InjectionSessionState.Recovering,
            (InjectionSessionState.Injecting, InjectionSessionTrigger.ConnectionLost) =>
                InjectionSessionState.Recovering,
            (InjectionSessionState.Active, InjectionSessionTrigger.ConnectionLost) =>
                InjectionSessionState.Recovering,
            (InjectionSessionState.Active, InjectionSessionTrigger.HeartbeatLeaseExpired) =>
                InjectionSessionState.Recovering,
            (InjectionSessionState.Recovering, InjectionSessionTrigger.RecoveryReady) =>
                InjectionSessionState.Discovering,

            (InjectionSessionState.Discovering, InjectionSessionTrigger.StopRequested) =>
                InjectionSessionState.Stopping,
            (InjectionSessionState.Connecting, InjectionSessionTrigger.StopRequested) =>
                InjectionSessionState.Stopping,
            (InjectionSessionState.Injecting, InjectionSessionTrigger.StopRequested) =>
                InjectionSessionState.Stopping,
            (InjectionSessionState.Active, InjectionSessionTrigger.StopRequested) =>
                InjectionSessionState.Stopping,
            (InjectionSessionState.Recovering, InjectionSessionTrigger.StopRequested) =>
                InjectionSessionState.Stopping,
            (InjectionSessionState.Faulted, InjectionSessionTrigger.StopRequested) =>
                InjectionSessionState.Stopping,
            (InjectionSessionState.Stopping, InjectionSessionTrigger.StopRequested) =>
                InjectionSessionState.Stopping,
            (InjectionSessionState.Stopping, InjectionSessionTrigger.StopCompleted) =>
                InjectionSessionState.Idle,

            (InjectionSessionState.Faulted, InjectionSessionTrigger.Reset) =>
                InjectionSessionState.Idle,

            (_, InjectionSessionTrigger.Failure) when state != InjectionSessionState.Disposed =>
                InjectionSessionState.Faulted,

            (InjectionSessionState.Disposed, _) => throw Invalid(state, trigger),
            _ => throw Invalid(state, trigger),
        };
    }

    private static bool IsIdempotent(
        InjectionSessionTrigger trigger,
        InjectionSessionState state) =>
        (state == InjectionSessionState.Idle && trigger == InjectionSessionTrigger.StopRequested) ||
        (state == InjectionSessionState.Stopping && trigger == InjectionSessionTrigger.StopRequested) ||
        (state == InjectionSessionState.Disposed && trigger == InjectionSessionTrigger.Dispose);

    private static InvalidOperationException Invalid(
        InjectionSessionState state,
        InjectionSessionTrigger trigger) => new(
        $"Trigger '{trigger}' is invalid while the injection session is '{state}'.");
}
