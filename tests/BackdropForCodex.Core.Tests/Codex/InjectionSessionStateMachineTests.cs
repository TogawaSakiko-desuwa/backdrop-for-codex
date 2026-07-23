using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class InjectionSessionStateMachineTests
{
    [Fact]
    public void Apply_FollowsHappyPathAndAllocatesGeneration()
    {
        var machine = new InjectionSessionStateMachine();

        Assert.Equal(InjectionSessionState.Discovering,
            machine.Apply(InjectionSessionTrigger.StartRequested).After.State);
        Assert.Equal(1, machine.Snapshot.Generation);
        Assert.Equal(InjectionSessionState.Connecting,
            machine.Apply(InjectionSessionTrigger.EndpointDiscovered).After.State);
        Assert.Equal(InjectionSessionState.Injecting,
            machine.Apply(InjectionSessionTrigger.CdpConnected).After.State);
        Assert.Equal(InjectionSessionState.Active,
            machine.Apply(InjectionSessionTrigger.InjectionApplied).After.State);
    }

    [Fact]
    public void Apply_RecoveryAllocatesNewGenerationBeforeReinjection()
    {
        var machine = CreateActiveMachine();

        machine.Apply(InjectionSessionTrigger.ConnectionLost);
        var recovery = machine.Apply(InjectionSessionTrigger.RecoveryReady);

        Assert.Equal(InjectionSessionState.Discovering, recovery.After.State);
        Assert.Equal(2, recovery.After.Generation);
    }

    [Fact]
    public void Apply_HeartbeatLeaseExpiryStartsRecovery()
    {
        var machine = CreateActiveMachine();

        var transition = machine.Apply(InjectionSessionTrigger.HeartbeatLeaseExpired);

        Assert.Equal(InjectionSessionState.Recovering, transition.After.State);
        Assert.Equal(1, transition.After.Generation);
    }

    [Fact]
    public void Apply_StopIsIdempotentAndCompletesAtIdle()
    {
        var machine = CreateActiveMachine();

        machine.Apply(InjectionSessionTrigger.StopRequested);
        var duplicate = machine.Apply(InjectionSessionTrigger.StopRequested);
        machine.Apply(InjectionSessionTrigger.StopCompleted);
        var idleStop = machine.Apply(InjectionSessionTrigger.StopRequested);

        Assert.False(duplicate.Changed);
        Assert.False(idleStop.Changed);
        Assert.Equal(InjectionSessionState.Idle, machine.Snapshot.State);
    }

    [Fact]
    public void Apply_FailureRequiresReasonAndReset()
    {
        var machine = new InjectionSessionStateMachine();
        machine.Apply(InjectionSessionTrigger.StartRequested);

        Assert.Throws<ArgumentNullException>(
            () => machine.Apply(InjectionSessionTrigger.Failure));

        var failure = machine.Apply(InjectionSessionTrigger.Failure, "CDP disconnected");
        Assert.Equal(InjectionSessionState.Faulted, failure.After.State);
        Assert.Equal("CDP disconnected", failure.After.FailureReason);

        var reset = machine.Apply(InjectionSessionTrigger.Reset);
        Assert.Equal(InjectionSessionState.Idle, reset.After.State);
        Assert.Null(reset.After.FailureReason);
    }

    [Fact]
    public void Apply_RejectsOutOfOrderTransitionAndAllWorkAfterDispose()
    {
        var machine = new InjectionSessionStateMachine();

        Assert.Throws<InvalidOperationException>(
            () => machine.Apply(InjectionSessionTrigger.CdpConnected));

        machine.Apply(InjectionSessionTrigger.Dispose);
        Assert.Throws<InvalidOperationException>(
            () => machine.Apply(InjectionSessionTrigger.StartRequested));
        Assert.False(machine.Apply(InjectionSessionTrigger.Dispose).Changed);
    }

    private static InjectionSessionStateMachine CreateActiveMachine()
    {
        var machine = new InjectionSessionStateMachine();
        machine.Apply(InjectionSessionTrigger.StartRequested);
        machine.Apply(InjectionSessionTrigger.EndpointDiscovered);
        machine.Apply(InjectionSessionTrigger.CdpConnected);
        machine.Apply(InjectionSessionTrigger.InjectionApplied);
        return machine;
    }
}
