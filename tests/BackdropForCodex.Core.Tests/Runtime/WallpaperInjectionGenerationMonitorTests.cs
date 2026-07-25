using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Runtime;
using Xunit;

namespace BackdropForCodex.Core.Tests.Runtime;

public sealed class WallpaperInjectionGenerationMonitorTests
{
    [Fact]
    public void Capabilities_PreserveLastAttemptAndIgnoreStaleGeneration()
    {
        var source = new FakeInjectionObservationSource();
        var eventSender = new object();
        var monitor = new WallpaperInjectionGenerationMonitor(
            source,
            source,
            eventSender,
            _ => Task.CompletedTask);
        var securityRejected = CompatibilityCapabilities.SecurityRejected();
        var declared = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var contract = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            ContractMatchState.Matched);
        var observedEvents = new List<WallpaperInjectionCapabilitiesChangedEventArgs>();
        monitor.CapabilitiesChanged += (sender, eventArgs) =>
        {
            Assert.Same(eventSender, sender);
            observedEvents.Add(eventArgs);
        };
        monitor.CaptureCapabilities(securityRejected);
        monitor.BeginCapabilityObservation(generation: 8);

        source.RaiseCapabilities(generation: 7, securityRejected, declared, contract);

        Assert.Equal(securityRejected, monitor.Capabilities);
        Assert.Empty(observedEvents);

        source.RaiseCapabilities(generation: 8, securityRejected, declared, contract);

        Assert.Equal(declared, monitor.Capabilities);
        Assert.Equal(contract, monitor.Compatibility.Presentation);
        Assert.Single(observedEvents);

        monitor.BeginAttempt();

        Assert.All(
            GetCapabilities(monitor.Capabilities),
            capability => Assert.Equal(
                CompatibilityCapabilityReasonCode.DisabledForGeneration,
                capability.ReasonCode));
    }

    [Fact]
    public void CapabilityObserverFailure_CannotInterruptOtherObservers()
    {
        var source = new FakeInjectionObservationSource();
        var monitor = new WallpaperInjectionGenerationMonitor(
            source,
            source,
            new object(),
            _ => Task.CompletedTask);
        var declared = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var secondObserverCalled = false;
        monitor.BeginCapabilityObservation(generation: 3);
        monitor.CapabilitiesChanged += (_, _) => throw new InvalidOperationException("observer");
        monitor.CapabilitiesChanged += (_, _) => secondObserverCalled = true;

        source.RaiseCapabilities(generation: 3, monitor.Capabilities, declared);

        Assert.True(secondObserverCalled);
        Assert.Equal(declared, monitor.Capabilities);
    }

    [Fact]
    public async Task HealthFaults_QueueNewGenerationWhileOldHandlerIsBlocked()
    {
        var source = new FakeInjectionObservationSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedGenerations = new List<long>();
        var monitor = new WallpaperInjectionGenerationMonitor(
            source,
            source,
            new object(),
            async generation =>
            {
                observedGenerations.Add(generation);
                if (generation == 4)
                {
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                }
            });
        monitor.BeginCapabilityObservation(generation: 4);

        source.RaiseHealthFault(generation: 4);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        source.RaiseHealthFault(generation: 5);
        var stopTask = monitor.StopObserving();

        Assert.False(stopTask.IsCompleted);

        release.TrySetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));

        source.RaiseHealthFault(generation: 6);
        source.RaiseCapabilities(
            generation: 4,
            monitor.Capabilities,
            PresentationContractCatalog.CreateFullySupportedCapabilities());

        Assert.Equal([4, 5], observedGenerations);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.DisabledForGeneration,
            monitor.Capabilities.Global.ReasonCode);
    }

    [Fact]
    public void ActiveGeneration_IsExplicitAndRejectsStaleValues()
    {
        var monitor = new WallpaperInjectionGenerationMonitor(
            healthSource: null,
            capabilitySource: null,
            new object(),
            _ => Task.CompletedTask);

        monitor.MarkActive(generation: 11);

        Assert.True(monitor.IsActiveGeneration(11));
        Assert.False(monitor.IsActiveGeneration(10));

        monitor.ClearActive();

        Assert.False(monitor.IsActiveGeneration(11));
    }

    [Fact]
    public void CaptureSecurity_RetainsVersionAndFailClosedTerminalSnapshot()
    {
        var monitor = new WallpaperInjectionGenerationMonitor(
            healthSource: null,
            capabilitySource: null,
            new object(),
            _ => Task.CompletedTask);
        var identity = BackdropForCodex.Core.Tests.Codex.CodexSecurityValidatorTests
            .GetIdentity(new Version(999, 1, 2, 3));

        monitor.CaptureSecurity(CodexSecurityResult.Verified(
            identity,
            CodexSecurityStage.TargetValidation));
        monitor.CaptureSecurity(CodexSecurityResult.Rejected(
            CodexSecurityStage.TargetValidation,
            CodexSecurityFailureCode.TargetRevalidationFailed,
            "The target changed after it was verified.",
            identity));

        Assert.Equal(new Version(999, 1, 2, 3), monitor.Compatibility.CodexVersion);
        Assert.Equal(CodexSecurityStatus.Rejected, monitor.Compatibility.Security.Status);
        Assert.Equal(
            CodexSecurityFailureCode.TargetRevalidationFailed,
            monitor.Compatibility.Security.FailureCode);
        Assert.All(
            GetCapabilities(monitor.Capabilities),
            capability => Assert.Equal(
                CompatibilityCapabilityReasonCode.SecurityRejected,
                capability.ReasonCode));
    }

    [Fact]
    public async Task QueuedHealthFault_StillRunsAfterPredecessorFailureAndStopReportsFailure()
    {
        var source = new FakeInjectionObservationSource();
        var observedGenerations = new List<long>();
        var monitor = new WallpaperInjectionGenerationMonitor(
            source,
            capabilitySource: null,
            new object(),
            generation =>
            {
                observedGenerations.Add(generation);
                return generation == 1
                    ? Task.FromException(new InvalidOperationException("first fault"))
                    : Task.CompletedTask;
            });

        source.RaiseHealthFault(generation: 1);
        source.RaiseHealthFault(generation: 2);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => monitor.StopObserving());

        Assert.Equal("first fault", exception.Message);
        Assert.Equal([1, 2], observedGenerations);
        Assert.Same(monitor.StopObserving(), monitor.StopObserving());
    }

    private static CompatibilityCapability[] GetCapabilities(
        CompatibilityCapabilities capabilities) =>
        [
            capabilities.Global,
            capabilities.Regions,
            capabilities.Glass,
            capabilities.Audio,
            capabilities.Advanced,
        ];

    private sealed class FakeInjectionObservationSource :
        IWallpaperInjectionHealthSource,
        IWallpaperInjectionCapabilitySource
    {
        public event EventHandler<WallpaperInjectionHealthFaultedEventArgs>? HealthFaulted;

        public event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>?
            CapabilitiesChanged;

        public CompatibilityCapabilities Capabilities { get; private set; } =
            CompatibilityCapabilities.AllUnavailable(
                CompatibilityCapabilityReasonCode.DisabledForGeneration);

        public PresentationContractSnapshot PresentationContract { get; private set; } =
            PresentationContractSnapshot.NotEvaluated;

        public void RaiseHealthFault(long generation) =>
            HealthFaulted?.Invoke(
                this,
                new WallpaperInjectionHealthFaultedEventArgs(generation, "test fault"));

        public void RaiseCapabilities(
            long generation,
            CompatibilityCapabilities previous,
            CompatibilityCapabilities current,
            PresentationContractSnapshot? presentationContract = null)
        {
            Capabilities = current;
            PresentationContract = presentationContract ?? new PresentationContractSnapshot(
                PresentationContractCatalog.CodexShellId,
                ContractMatchState.Matched);
            CapabilitiesChanged?.Invoke(
                this,
                new WallpaperInjectionCapabilitiesChangedEventArgs(
                    generation,
                    previous,
                    current,
                    PresentationContract));
        }
    }
}
