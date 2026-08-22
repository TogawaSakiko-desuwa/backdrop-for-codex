using System.Runtime.CompilerServices;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class DynamicWallpaperActivationFactoryTests
{
    [Fact]
    public async Task ActivateAsyncWaitsForPresentationReadinessBeforeStartingAnyMediaResource()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events);
        var capture = new FakeCaptureFactory(events);
        var encoder = new FakeEncoderFactory(events);
        var page = new BlockingReadinessPageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(window, capture, encoder, page);
        var project = new FakeProjectLease(Resolution(), events);

        var activation = factory.ActivateAsync(Request(project.Resolution), project).AsTask();
        await page.ReadinessStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(window.Options);
        Assert.Empty(capture.Requests);
        Assert.Empty(encoder.Descriptors);
        Assert.Equal(0, page.PageSessionStartCount);

        page.ReleaseReadiness.TrySetResult();
        var result = await activation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PresentationContractCatalog.CodexShellId, result.Presentation.ActiveContractId);
        Assert.Equal(
            PresentationContractCatalog.CreateFullySupportedCapabilities(),
            result.Capabilities);
        await result.Lease.DisposeAsync();
    }

    [Fact]
    public async Task ActivateAsyncDoesNotStartMediaWhenPresentationReadinessFails()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events);
        var capture = new FakeCaptureFactory(events);
        var encoder = new FakeEncoderFactory(events);
        var page = new RejectingReadinessPageSessionFactory();
        var factory = new DynamicWallpaperActivationFactory(window, capture, encoder, page);
        var project = new FakeProjectLease(Resolution(), events);

        await Assert.ThrowsAsync<WallpaperPresentationContractException>(() =>
            factory.ActivateAsync(Request(project.Resolution), project).AsTask());

        Assert.Empty(window.Options);
        Assert.Empty(capture.Requests);
        Assert.Empty(encoder.Descriptors);
        Assert.Equal(0, page.PageSessionStartCount);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task ActivateAsyncRejectsALeaseForAnotherCanonicalProjectIdentifier()
    {
        var requested = Resolution();
        var leasedReference = requested.CanonicalReference with
        {
            SourceIdentifier = "987654321",
        };
        var leased = new WallpaperSourceResolution(
            leasedReference,
            new WallpaperSourceDescriptor(
                leasedReference.SourceKind,
                leasedReference.SourceIdentifier,
                "Different fixture scene",
                WallpaperContentKind.Scene,
                WallpaperDeliveryKind.WallpaperEngineWindow,
                WallpaperDeliveryCapabilities.DynamicFrames),
            directMediaMetadata: null);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer([]),
            new FakeCaptureFactory([]),
            new FakeEncoderFactory([]),
            new FakePageSessionFactory([]));
        var project = new FakeProjectLease(leased, []);

        await Assert.ThrowsAsync<WallpaperSourceCapabilityException>(() =>
            factory.ActivateAsync(Request(requested), project).AsTask());

        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task ActivateAsync_PublishesPrimaryTierAndDisposesInSafetyOrder()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events);
        var capture = new FakeCaptureFactory(events);
        var encoder = new FakeEncoderFactory(events);
        var page = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(window, capture, encoder, page);
        var project = new FakeProjectLease(Resolution(), events);

        var result = await factory.ActivateAsync(Request(project.Resolution), project);
        var active = result.Lease;

        Assert.Equal(ActiveWallpaperDeliveryKind.DynamicStream, active.DeliveryKind);
        Assert.Equal(
            new PresentationContractSnapshot(
                PresentationContractCatalog.CodexShellId,
                ContractMatchState.Matched),
            result.Presentation);
        Assert.Equal(
            PresentationContractCatalog.CreateFullySupportedCapabilities(),
            result.Capabilities);
        Assert.Equal(DynamicWallpaperRenderProfiles.Primary.Width, window.Options.Single().Width);
        Assert.Equal(DynamicWallpaperRenderProfiles.Primary.Height, window.Options.Single().Height);
        Assert.Equal(DynamicWallpaperRenderProfiles.Primary.FrameRate,
            capture.Requests.Single().FrameRate);
        Assert.False(project.IsDisposed);

        await active.DisposeAsync();

        Assert.True(project.IsDisposed);
        Assert.Equal(
            ["page-dispose", "capture-dispose", "encoder-dispose", "window-dispose", "project-dispose"],
            events.Where(item => item.EndsWith("-dispose", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ActivateAsyncPropagatesTheRuntimeMutationSignalToPageOptions()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        var mutationSignal = new RuntimeMutationSignal(
            generation: 41,
            reportPossibleMutation: () => { });
        var request = new DynamicWallpaperActivationRequest(
            generation: 41,
            Endpoint(),
            project.Resolution,
            WallpaperProfile.CreateDefault(),
            mutationSignal);

        var active = (await factory.ActivateAsync(request, project)).Lease;

        Assert.Same(mutationSignal, Assert.Single(pages.Options).MutationSignal);
        await active.DisposeAsync();
    }

    [Fact]
    public async Task ActivateAsyncRejectsPageLeaseForAnotherGenerationAndReleasesStartedPipeline()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events)
        {
            LeaseGenerationOverride = 42,
        };
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);

        _ = await Assert.ThrowsAnyAsync<Exception>(() =>
            factory.ActivateAsync(Request(project.Resolution), project).AsTask());

        Assert.Equal(1, Assert.Single(pages.Leases).DisposeCount);
        Assert.Equal(
            ["page-dispose", "capture-dispose", "encoder-dispose", "window-dispose"],
            events.Where(item => item.EndsWith("-dispose", StringComparison.Ordinal)));
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task ResumeStartsANewCaptureWithTheReauthorizedWindowTarget()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            new FakeEncoderFactory(events),
            new FakePageSessionFactory(events));
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var pauseController = Assert.IsAssignableFrom<IPausableActiveWallpaperLease>(active);
        var initialTarget = Assert.Single(capture.Requests).CaptureTarget;

        events.Clear();
        await pauseController.SetPausedAsync(paused: true);
        Assert.Equal(
            [
                "page-pause:True",
                "capture-dispose",
                "encoder-pause-boundary",
                "window-pause:True",
                "page-drain",
            ],
            events);

        events.Clear();
        await pauseController.SetPausedAsync(paused: false);
        Assert.Equal(
            ["window-pause:False", "capture-start", "page-wait:1", "page-pause:False"],
            events);

        Assert.Equal(2, capture.Requests.Count);
        Assert.NotSame(initialTarget, capture.Requests[1].CaptureTarget);
    }

    [Fact]
    public async Task ResumeKeepsThePageFrozenUntilANewKeyFrameIsAcknowledged()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events) { BlockKeyFrameWait = true };
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var pauseController = Assert.IsAssignableFrom<IPausableActiveWallpaperLease>(active);

        await pauseController.SetPausedAsync(paused: true);
        events.Clear();
        var resuming = pauseController.SetPausedAsync(paused: false).AsTask();
        var page = Assert.Single(pages.Leases);
        await page.KeyFrameWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(resuming.IsCompleted);
        Assert.DoesNotContain("page-pause:False", events);
        page.AllowKeyFrameAcknowledgement.TrySetResult();
        await resuming.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("page-pause:False", events[^1]);
    }

    [Fact]
    public async Task ActivateAsync_RestartsWholePipelineAtFallbackTierAfterPrimaryFailure()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events);
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
        };
        var encoder = new FakeEncoderFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            window,
            capture,
            encoder,
            new FakePageSessionFactory(events));
        var project = new FakeProjectLease(Resolution(), events);

        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;

        Assert.Collection(
            window.Options,
            item => Assert.Equal((1920, 1080), (item.Width, item.Height)),
            item => Assert.Equal((1280, 720), (item.Width, item.Height)));
        Assert.Collection(
            capture.Requests,
            item => Assert.Equal(30, item.FrameRate),
            item => Assert.Equal(30, item.FrameRate));
        Assert.Collection(
            encoder.Descriptors,
            item => Assert.Equal(30, item.FrameRate),
            item => Assert.Equal(15, item.FrameRate));
        Assert.Equal(1, events.Count(item => item == "window-dispose"));
        Assert.False(project.IsDisposed);
    }

    [Theory]
    [InlineData(DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure)]
    [InlineData(DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable)]
    [InlineData(DynamicWallpaperCapabilityReasonCode.HardwareEncoderUnavailable)]
    [InlineData(DynamicWallpaperCapabilityReasonCode.CodecUnavailable)]
    public async Task ActivePrimaryRestartsWholePipelineAtFallbackAfterARecoverableTierFailure(
        DynamicWallpaperCapabilityReasonCode reasonCode)
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events);
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        pages.Leases.Single().Fail(reasonCode);

        await WaitUntilAsync(
            () => pages.Leases.Count == 2,
            TimeSpan.FromSeconds(5));
        Assert.Collection(
            window.Options,
            item => Assert.Equal((1920, 1080), (item.Width, item.Height)),
            item => Assert.Equal((1280, 720), (item.Width, item.Height)));
        Assert.False(health.Completion.IsCompleted);
        Assert.False(project.IsDisposed);
        Assert.True(events.IndexOf("page-dispose") < events.IndexOf("capture-dispose"));
        Assert.True(events.IndexOf("capture-dispose") < events.IndexOf("window-dispose"));
    }

    [Fact]
    public async Task RecoveryConfirmsStructuralFailuresBeforePublishingCapabilityDowngrades()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var matchedPresentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            ContractMatchState.Matched);
        var fullySupported = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var structurallyWeak = PresentationContractCatalog.Observe(
            matchedPresentation,
            new PresentationEvidence(
                GlobalStructure: true,
                ShellStructure: false,
                BackdropFilterSupported: true,
                SelectorHasSupported: true));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, fullySupported));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, structurallyWeak));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, structurallyWeak));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, structurallyWeak));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, fullySupported));
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        var activation = await factory.ActivateAsync(Request(project.Resolution), project);
        await using var active = activation.Lease;
        var capabilitySource =
            Assert.IsAssignableFrom<IWallpaperInjectionCapabilitySource>(active);
        var changes = new List<WallpaperInjectionCapabilitiesChangedEventArgs>();
        capabilitySource.CapabilitiesChanged += (_, eventArgs) => changes.Add(eventArgs);

        pages.Leases[0].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);

        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(fullySupported, capabilitySource.Capabilities);
        Assert.Empty(changes);

        pages.Leases[1].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 3, TimeSpan.FromSeconds(5));
        Assert.Equal(fullySupported, capabilitySource.Capabilities);
        Assert.Empty(changes);

        pages.Leases[2].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(
            () => capabilitySource.Capabilities == structurallyWeak,
            TimeSpan.FromSeconds(5));
        var downgrade = Assert.Single(changes);
        Assert.Equal(active.Generation, downgrade.Generation);
        Assert.Equal(fullySupported, downgrade.Previous);
        Assert.Equal(structurallyWeak, downgrade.Current);
        Assert.Equal(matchedPresentation, downgrade.PresentationContract);
        Assert.Equal(matchedPresentation, capabilitySource.PresentationContract);

        pages.Leases[3].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 5, TimeSpan.FromSeconds(5));
        await Assert.IsAssignableFrom<IPausableActiveWallpaperLease>(active)
            .SetPausedAsync(paused: true);

        Assert.Equal(structurallyWeak, capabilitySource.Capabilities);
        Assert.Equal(matchedPresentation, capabilitySource.PresentationContract);
        Assert.Single(changes);
    }

    [Fact]
    public async Task RecoveryAccumulatesWeakEvidenceFromFailedReplacementAttempts()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var matchedPresentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            ContractMatchState.Matched);
        var fullySupported = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var structurallyWeak = PresentationContractCatalog.Observe(
            matchedPresentation,
            new PresentationEvidence(
                GlobalStructure: true,
                ShellStructure: false,
                BackdropFilterSupported: true,
                SelectorHasSupported: true));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, fullySupported));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, structurallyWeak));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, structurallyWeak));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, structurallyWeak));
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var pause = Assert.IsAssignableFrom<IPausableActiveWallpaperLease>(active);
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        var capabilitySource =
            Assert.IsAssignableFrom<IWallpaperInjectionCapabilitySource>(active);
        var changes = new List<WallpaperInjectionCapabilitiesChangedEventArgs>();
        capabilitySource.CapabilitiesChanged += (_, eventArgs) => changes.Add(eventArgs);
        await pause.SetPausedAsync(paused: true);
        pages.FailNextPause(new DynamicWallpaperPageSessionException(
            "fixture first transient pause restore failure"));
        pages.FailNextPause(new DynamicWallpaperPageSessionException(
            "fixture second transient pause restore failure"));

        pages.Leases[0].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);

        await WaitUntilAsync(
            () => capabilitySource.Capabilities == structurallyWeak,
            TimeSpan.FromSeconds(5));
        Assert.Equal(4, pages.Leases.Count);
        var downgrade = Assert.Single(changes);
        Assert.Equal(fullySupported, downgrade.Previous);
        Assert.Equal(structurallyWeak, downgrade.Current);
        Assert.False(health.Completion.IsCompleted);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task FailedThirdWeakReplacementDoesNotCommitBeforePositiveRecovery()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var matchedPresentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            ContractMatchState.Matched);
        var fullySupported = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var structurallyWeak = PresentationContractCatalog.Observe(
            matchedPresentation,
            new PresentationEvidence(
                GlobalStructure: true,
                ShellStructure: false,
                BackdropFilterSupported: true,
                SelectorHasSupported: true));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, fullySupported));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, structurallyWeak));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, structurallyWeak));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, structurallyWeak));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, fullySupported));
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var pause = Assert.IsAssignableFrom<IPausableActiveWallpaperLease>(active);
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        var capabilitySource =
            Assert.IsAssignableFrom<IWallpaperInjectionCapabilitySource>(active);
        var changes = new List<WallpaperInjectionCapabilitiesChangedEventArgs>();
        capabilitySource.CapabilitiesChanged += (_, eventArgs) => changes.Add(eventArgs);
        await pause.SetPausedAsync(paused: true);

        pages.Leases[0].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        pages.Leases[1].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 3, TimeSpan.FromSeconds(5));
        pages.FailNextPause(new DynamicWallpaperPageSessionException(
            "fixture third weak pause restore failure"));
        pages.Leases[2].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);

        await WaitUntilAsync(() => pages.Leases.Count == 5, TimeSpan.FromSeconds(5));
        Assert.Equal(fullySupported, capabilitySource.Capabilities);
        Assert.Empty(changes);
        Assert.False(health.Completion.IsCompleted);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task RecoveryRejectsPageFactoryThatChangesTheLockedPresentationAndDisposesReplacement()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events)
        {
            IgnoreLockedCompatibility = true,
        };
        var matchedPresentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            ContractMatchState.Matched);
        var fullySupported = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var globalOnly = PresentationContractCatalog.Match(
            new PresentationEvidence(
                GlobalStructure: true,
                ShellStructure: false,
                BackdropFilterSupported: true,
                SelectorHasSupported: true),
            finalizeBaselineFallback: true);
        pages.CompatibilityObservations.Enqueue((matchedPresentation, fullySupported));
        pages.CompatibilityObservations.Enqueue((globalOnly.Snapshot, globalOnly.Capabilities));
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        pages.Leases[0].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);

        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => health.Completion.IsCompleted, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => health.Completion);
        Assert.Equal(1, pages.Leases[1].DisposeCount);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task RecoveryRejectsPageFactoryCapabilitiesAboveTheCeilingAndDisposesReplacement()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events)
        {
            IgnoreLockedCompatibility = true,
        };
        var matchedPresentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            ContractMatchState.Matched);
        var capabilityCeiling = PresentationContractCatalog.Observe(
            matchedPresentation,
            new PresentationEvidence(
                GlobalStructure: true,
                ShellStructure: true,
                BackdropFilterSupported: false,
                SelectorHasSupported: true));
        pages.CompatibilityObservations.Enqueue((matchedPresentation, capabilityCeiling));
        pages.CompatibilityObservations.Enqueue((
            matchedPresentation,
            PresentationContractCatalog.CreateFullySupportedCapabilities()));
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        pages.Leases[0].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);

        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => health.Completion.IsCompleted, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => health.Completion);
        Assert.Equal(1, pages.Leases[1].DisposeCount);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task ActivateAsyncDoesNotRetryFallbackAfterASafetyGateFailure()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events);
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable,
        };
        var factory = new DynamicWallpaperActivationFactory(
            window,
            capture,
            new FakeEncoderFactory(events),
            new FakePageSessionFactory(events));
        var project = new FakeProjectLease(Resolution(), events);

        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            factory.ActivateAsync(Request(project.Resolution), project).AsTask());

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable,
            failure.ReasonCode);
        Assert.Single(window.Options);
        Assert.Single(capture.Requests);
        Assert.False(project.IsDisposed);
    }

    [Theory]
    [InlineData(DynamicWallpaperCapabilityReasonCode.HardwareEncoderUnavailable)]
    [InlineData(DynamicWallpaperCapabilityReasonCode.CodecUnavailable)]
    public async Task InitialEncoderCapabilityFailureStopsAfterTheDocumentedProfileLadder(
        DynamicWallpaperCapabilityReasonCode reasonCode)
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events);
        var encoder = new FakeEncoderFactory(events)
        {
            FailStarts = int.MaxValue,
            StartFailureReason = reasonCode,
        };
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            encoder,
            new FakePageSessionFactory(events));
        var project = new FakeProjectLease(Resolution(), events);

        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            factory.ActivateAsync(Request(project.Resolution), project).AsTask());

        Assert.Equal(reasonCode, failure.ReasonCode);
        Assert.Equal(DynamicWallpaperRenderProfiles.InPreferenceOrder.Count, window.Options.Count);
        Assert.Equal(DynamicWallpaperRenderProfiles.InPreferenceOrder.Count, encoder.Descriptors.Count);
        Assert.Equal(
            DynamicWallpaperRenderProfiles.InPreferenceOrder.Count,
            events.Count(item => item == "window-dispose"));
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task ActiveFallbackFailureDegradesToCompatibilityInsteadOfCompletingHealth()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var window = new FakeWindowRenderer(events);
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        pages.Leases.Single().Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(
            () => pages.Leases.Count == 2,
            TimeSpan.FromSeconds(5));

        pages.Leases[1].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);

        await WaitUntilAsync(
            () => pages.Leases.Count == 3,
            TimeSpan.FromSeconds(5));
        Assert.Collection(
            window.Options,
            item => Assert.Equal((1920, 1080), (item.Width, item.Height)),
            item => Assert.Equal((1280, 720), (item.Width, item.Height)),
            item => Assert.Equal((960, 540), (item.Width, item.Height)));
        Assert.False(health.Completion.IsCompleted);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task ActiveTransientPageTransportFailureRestartsLocally()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        pages.Leases.Single().Fail(new IOException("fixture transient CDP transport failure"));

        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        Assert.False(health.Completion.IsCompleted);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task PageSessionFailureWithCleanupDiagnosticIsTerminal()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        var pageFailure = new DynamicWallpaperPageSessionException(
            "fixture page transport and cleanup failure",
            new IOException("fixture primary failure"),
            new IOException("fixture cleanup failure"));

        pages.Leases.Single().Fail(pageFailure);

        await WaitUntilAsync(
            () => health.Completion.IsCompleted || pages.Leases.Count == 2,
            TimeSpan.FromSeconds(2));
        Assert.True(health.Completion.IsCompleted);
        var observed = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(
            () => health.Completion);
        Assert.Same(pageFailure, observed);
        Assert.Single(pages.Leases);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task PageSessionFailureWithoutCleanupDiagnosticRestartsLocally()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        pages.Leases.Single().Fail(new DynamicWallpaperPageSessionException(
            "fixture page transport failure"));

        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        Assert.False(health.Completion.IsCompleted);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task PageOwnershipFailureIsTerminalInsteadOfRestartingLocally()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        var pageFailure = new DynamicWallpaperPageSessionException(
            "fixture page ownership failure",
            DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven);

        pages.Leases.Single().Fail(pageFailure);

        await WaitUntilAsync(
            () => health.Completion.IsCompleted || pages.Leases.Count == 2,
            TimeSpan.FromSeconds(2));
        Assert.True(health.Completion.IsCompleted);
        var observed = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(
            () => health.Completion);
        Assert.Same(pageFailure, observed);
        Assert.Single(pages.Leases);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task PausedRecoveryRetriesTransientPageStateRestoreWithoutCompletingHealth()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var window = new FakeWindowRenderer(events);
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var pause = Assert.IsAssignableFrom<IPausableActiveWallpaperLease>(active);
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        await pause.SetPausedAsync(paused: true);
        pages.FailNextPause(new DynamicWallpaperPageSessionException(
            "fixture transient pause transport failure"));

        pages.Leases.Single().Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);

        await WaitUntilAsync(() => pages.Leases.Count == 3, TimeSpan.FromSeconds(5));
        Assert.Equal(
            (960, 540),
            (window.Options[^1].Width, window.Options[^1].Height));
        Assert.False(health.Completion.IsCompleted);
        Assert.False(project.IsDisposed);
        Assert.Equal(3, events.Count(item => item == "page-pause:True"));
    }

    [Fact]
    public async Task CompatibilityTierRetriesLocallyWhenTheFirstRestartCannotStart()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var window = new FakeWindowRenderer(events);
        var capture = new FakeCaptureFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            window,
            capture,
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        pages.Leases[0].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        pages.Leases[1].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 3, TimeSpan.FromSeconds(5));

        capture.FailStarts = 1;
        pages.Leases[2].Fail(DynamicWallpaperCapabilityReasonCode.EncodingFailed);

        await WaitUntilAsync(() => pages.Leases.Count == 4, TimeSpan.FromSeconds(5));
        Assert.Equal((960, 540), (window.Options[^1].Width, window.Options[^1].Height));
        Assert.False(health.Completion.IsCompleted);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task RepeatedCompatibilityBackpressureRestartsLocallyWithoutCompletingHealth()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var window = new FakeWindowRenderer(events);
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        pages.Leases[0].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        pages.Leases[1].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 3, TimeSpan.FromSeconds(5));

        for (var expectedLeaseCount = 4; expectedLeaseCount <= 5; expectedLeaseCount++)
        {
            pages.Leases[^1].Fail(
                DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);

            await WaitUntilAsync(
                () => pages.Leases.Count == expectedLeaseCount,
                TimeSpan.FromSeconds(5));
            Assert.Equal(
                (960, 540),
                (window.Options[^1].Width, window.Options[^1].Height));
            Assert.False(health.Completion.IsCompleted);
            Assert.False(project.IsDisposed);
        }
    }

    [Theory]
    [InlineData(DynamicWallpaperCapabilityReasonCode.HardwareEncoderUnavailable)]
    [InlineData(DynamicWallpaperCapabilityReasonCode.CodecUnavailable)]
    public async Task CompatibilityEncoderCapabilityFailureRestartsLocallyWithoutCompletingHealth(
        DynamicWallpaperCapabilityReasonCode reasonCode)
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var window = new FakeWindowRenderer(events);
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        pages.Leases[0].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        pages.Leases[1].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 3, TimeSpan.FromSeconds(5));

        pages.Leases[2].Fail(reasonCode);
        var restarted = WaitUntilAsync(() => pages.Leases.Count == 4, TimeSpan.FromSeconds(2));
        _ = await Task.WhenAny(restarted, health.Completion);

        Assert.False(health.Completion.IsCompleted);
        await restarted;
        Assert.Equal((960, 540), (window.Options[^1].Width, window.Options[^1].Height));
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task NestedOwnershipFailureAtCompatibilityIsTerminalEvenWhenOuterReasonIsRecoverable()
    {
        var events = new List<string>();
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        pages.Leases[0].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        pages.Leases[1].Fail(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        await WaitUntilAsync(() => pages.Leases.Count == 3, TimeSpan.FromSeconds(5));
        var ownershipFailure = new WallpaperEnginePlatformUnavailableException(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven);
        var wrappedFailure = new DynamicWallpaperUnavailableException(
            DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable,
            new AggregateException("fixture nested platform failure", ownershipFailure));

        pages.Leases[2].Fail(wrappedFailure);

        await WaitUntilAsync(
            () => health.Completion.IsCompleted || pages.Leases.Count == 4,
            TimeSpan.FromSeconds(2));
        Assert.True(health.Completion.IsCompleted);
        var observed = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(
            () => health.Completion);
        Assert.Same(wrappedFailure, observed);
        Assert.Equal(3, pages.Leases.Count);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task ActiveFallbackDegradesWhenCaptureStopsProducingFrames()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
        };
        var deadline = new ManualCaptureFrameDeadlineScheduler();
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            new FakeEncoderFactory(events),
            pages,
            TimeSpan.FromSeconds(15),
            deadline);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        await WaitUntilAsync(() => deadline.HasPendingWait, TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), deadline.LastRequestedDelay);

        Assert.True(deadline.ExpireCurrent());

        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        Assert.False(health.Completion.IsCompleted);
    }

    [Fact]
    public async Task FallbackPacerCoalescesToLatestThenReplaysOncePerAbsoluteTick()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
            FramesPerSession = 2,
            RecordFrameDisposal = true,
        };
        var encoder = new FakeEncoderFactory(events);
        var pacerClock = new ManualWallpaperFramePacerClock();
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            encoder,
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            new ManualCaptureFrameDeadlineScheduler(),
            pacerClock);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var fallbackEncoder = encoder.Encoders[^1];
        await WaitUntilAsync(
            () => pacerClock.HasPendingWaitWithin(TimeSpan.FromSeconds(1)) &&
                capture.Frames.Count == 2,
            TimeSpan.FromSeconds(5));

        Assert.Single(fallbackEncoder.EncodedFrames);
        Assert.Same(capture.Frames[0], fallbackEncoder.EncodedFrames[0]);
        Assert.Equal(1, capture.Frames[0].DisposeCount);
        pacerClock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(
            () => pacerClock.HasPendingWaitWithin(TimeSpan.FromSeconds(1)) &&
                fallbackEncoder.EncodedFrames.Count == 2,
            TimeSpan.FromSeconds(5));

        Assert.Same(capture.Frames[1], fallbackEncoder.EncodedFrames[1]);
        Assert.Equal(1, pacerClock.PendingWaitCount);
        pacerClock.Advance(TimeSpan.FromTicks(333_334));
        await WaitUntilAsync(
            () => fallbackEncoder.EncodedFrames.Count == 3,
            TimeSpan.FromSeconds(5));

        Assert.Same(capture.Frames[1], fallbackEncoder.EncodedFrames[2]);
        var pause = Assert.IsAssignableFrom<IPausableActiveWallpaperLease>(active);
        await pause.SetPausedAsync(paused: true);
        Assert.True(events.IndexOf("frame-dispose:1") <
            events.IndexOf("encoder-pause-boundary"));
    }

    [Fact]
    public async Task FallbackPacerDrainsACompletedBoundedReadBeforeSampling()
    {
        var events = new List<string>();
        var pacerClock = new ManualWallpaperFramePacerClock();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
            FramesPerSession = 3,
            FrameDisposing = index =>
            {
                if (index == 0)
                {
                    pacerClock.Advance(TimeSpan.FromTicks(666_667));
                }
            },
        };
        var encoder = new FakeEncoderFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            encoder,
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            new ManualCaptureFrameDeadlineScheduler(),
            pacerClock);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var fallbackEncoder = encoder.Encoders[^1];

        await WaitUntilAsync(
            () => fallbackEncoder.EncodedFrames.Count >= 2,
            TimeSpan.FromSeconds(5));

        Assert.Same(capture.Frames[2], fallbackEncoder.EncodedFrames[1]);
        Assert.Equal(1, capture.Frames[0].DisposeCount);
        Assert.Equal(1, capture.Frames[1].DisposeCount);
    }

    [Fact]
    public async Task FallbackPacerSkipsMissedSlotsWithoutBurstingOrFaulting()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
        };
        var encoder = new FakeEncoderFactory(events);
        var pacerClock = new ManualWallpaperFramePacerClock();
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            encoder,
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            new ManualCaptureFrameDeadlineScheduler(),
            pacerClock);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        var fallbackEncoder = encoder.Encoders[^1];
        await WaitUntilAsync(
            () => pacerClock.HasPendingWaitWithin(TimeSpan.FromSeconds(1)),
            TimeSpan.FromSeconds(5));

        pacerClock.Advance(TimeSpan.FromTicks(2_000_001));
        await WaitUntilAsync(
            () => fallbackEncoder.EncodedFrames.Count == 2,
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(
            () => pacerClock.HasPendingWaitWithin(TimeSpan.FromSeconds(1)),
            TimeSpan.FromSeconds(5));

        Assert.False(health.Completion.IsCompleted);
        Assert.Equal(2, fallbackEncoder.EncodedFrames.Count);
        Assert.Equal(1, pacerClock.PendingWaitCount);
    }

    [Fact]
    public async Task FallbackEncodeTimeoutCancelsTheEncoderAndDegradesLocally()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
        };
        var encoder = new FakeEncoderFactory(events)
        {
            BlockAfterFirstEncode = true,
        };
        var pacerClock = new ManualWallpaperFramePacerClock();
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            encoder,
            pages,
            TimeSpan.FromSeconds(15),
            new ManualCaptureFrameDeadlineScheduler(),
            pacerClock);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        var fallbackEncoder = encoder.Encoders[^1];
        await WaitUntilAsync(
            () => pacerClock.HasPendingWaitWithin(TimeSpan.FromSeconds(1)),
            TimeSpan.FromSeconds(5));
        pacerClock.Advance(TimeSpan.FromTicks(666_667));
        await fallbackEncoder.EncodingBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        pacerClock.Advance(TimeSpan.FromSeconds(5));

        await fallbackEncoder.EncodingCancellationObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        Assert.False(health.Completion.IsCompleted);
        Assert.Equal(2, fallbackEncoder.EncodedFrames.Count);
    }

    [Fact]
    public async Task StartupNoFrameDeadlineFailsBeforeThePageCanPublish()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
            YieldFrames = false,
        };
        var pages = new FakePageSessionFactory(events);
        var deadline = new ManualCaptureFrameDeadlineScheduler();
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            new FakeEncoderFactory(events),
            pages,
            TimeSpan.FromSeconds(15),
            deadline);
        var project = new FakeProjectLease(Resolution(), events);
        var activation = factory.ActivateAsync(Request(project.Resolution), project).AsTask();
        await WaitUntilAsync(() => deadline.HasPendingWait, TimeSpan.FromSeconds(5));

        var firstArmCount = deadline.ArmCount;
        Assert.True(deadline.ExpireCurrent());
        await WaitUntilAsync(
            () => deadline.ArmCount > firstArmCount && deadline.HasPendingWait,
            TimeSpan.FromSeconds(5));
        Assert.True(deadline.ExpireCurrent());

        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            activation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.FallbackRenderTierUnavailable,
            failure.ReasonCode);
        Assert.Empty(pages.Leases);
        Assert.DoesNotContain("page-dispose", events);
        Assert.Equal(
            ["capture-dispose", "encoder-dispose", "window-dispose"],
            events.Where(item => item.EndsWith("-dispose", StringComparison.Ordinal)).TakeLast(3));
        Assert.False(project.IsDisposed);
        await project.DisposeAsync();
    }

    [Fact]
    public async Task PauseCancelsTheNoFrameDeadlineAndResumeArmsANewDeadline()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
        };
        var deadline = new ManualCaptureFrameDeadlineScheduler();
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            new FakeEncoderFactory(events),
            pages,
            TimeSpan.FromSeconds(15),
            deadline);
        var project = new FakeProjectLease(Resolution(), events);
        await using var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        var pause = Assert.IsAssignableFrom<IPausableActiveWallpaperLease>(active);
        await WaitUntilAsync(() => deadline.HasPendingWait, TimeSpan.FromSeconds(5));

        await pause.SetPausedAsync(paused: true);

        await WaitUntilAsync(() => !deadline.HasPendingWait, TimeSpan.FromSeconds(5));
        Assert.False(deadline.ExpireCurrent());
        Assert.False(health.Completion.IsCompleted);
        var armCountBeforeResume = deadline.ArmCount;

        await pause.SetPausedAsync(paused: false);
        await WaitUntilAsync(
            () => deadline.ArmCount > armCountBeforeResume && deadline.HasPendingWait,
            TimeSpan.FromSeconds(5));
        Assert.True(deadline.ExpireCurrent());

        await WaitUntilAsync(() => pages.Leases.Count == 2, TimeSpan.FromSeconds(5));
        Assert.False(health.Completion.IsCompleted);
    }

    [Fact]
    public async Task PauseTreatsCanceledFalseReadAsCancellationAndResumeRearmsDeadline()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
            ReturnFalseOnCancellation = true,
            IgnorePendingReadCancellation = true,
            YieldLateFrameAfterRelease = true,
        };
        var deadline = new ManualCaptureFrameDeadlineScheduler
        {
            CancellationDelay = TimeSpan.FromMilliseconds(100),
        };
        var pacerClock = new ManualWallpaperFramePacerClock
        {
            IgnoreCancellationForWaitsBelow = TimeSpan.FromSeconds(1),
        };
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            new FakeEncoderFactory(events),
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            deadline,
            pacerClock);
        var project = new FakeProjectLease(Resolution(), events);
        var active = (await factory.ActivateAsync(
            Request(project.Resolution),
            project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        var pause = Assert.IsAssignableFrom<IPausableActiveWallpaperLease>(active);
        try
        {
            await capture.PendingReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            capture.ReleasePendingRead.TrySetResult();
            await capture.CancellationReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => deadline.HasPendingWait, TimeSpan.FromSeconds(2));

            await pause.SetPausedAsync(paused: true).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            await Task.Delay(TimeSpan.FromMilliseconds(150));
            Assert.False(health.Completion.IsCompleted);
            var armCountBeforeResume = deadline.ArmCount;
            pacerClock.IgnoreCancellationForWaitsBelow = TimeSpan.Zero;
            pacerClock.CancelAll();

            await pause.SetPausedAsync(paused: false).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => deadline.ArmCount > armCountBeforeResume && deadline.HasPendingWait,
                TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            Assert.False(health.Completion.IsCompleted);
        }
        finally
        {
            pacerClock.IgnoreCancellationForWaitsBelow = TimeSpan.Zero;
            pacerClock.CancelAll();
            capture.ReleasePendingRead.TrySetResult();
            try
            {
                await active.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ReadWatchdogOnlyBecomesTerminalWhenLateFrameCleanupIsUnsafe()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
            IgnorePendingReadCancellation = true,
            YieldLateFrameAfterRelease = true,
            LateFrameDisposeFailures = 1,
        };
        var deadline = new ManualCaptureFrameDeadlineScheduler();
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            new FakeEncoderFactory(events),
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            deadline);
        var project = new FakeProjectLease(Resolution(), events);
        var active = (await factory.ActivateAsync(Request(project.Resolution), project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        try
        {
            await capture.PendingReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => deadline.HasPendingWait, TimeSpan.FromSeconds(2));
            Assert.True(deadline.ExpireCurrent());

            await WaitUntilAsync(
                () => events.Contains("page-dispose"),
                TimeSpan.FromSeconds(2));
            Assert.False(health.Completion.IsCompleted);

            capture.ReleasePendingRead.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() =>
                health.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
            var lateFrame = Assert.Single(capture.Frames, frame => frame.Sequence == 1);
            Assert.Equal(1, lateFrame.DisposeCount);

            await active.DisposeAsync();
            Assert.Equal(2, lateFrame.DisposeCount);
            await active.DisposeAsync();
            Assert.Equal(2, lateFrame.DisposeCount);
        }
        finally
        {
            capture.ReleasePendingRead.TrySetResult();
            try
            {
                await active.DisposeAsync();
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task PrimaryEncodeWatchdogDetachesBlockedCancellationAndExactFrameOwnership()
    {
        var events = new List<string>();
        var frameDisposalBlocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFrameDisposal = new ManualResetEventSlim();
        var capture = new FakeCaptureFactory(events)
        {
            RecordFrameDisposal = true,
            FrameDisposing = _ =>
            {
                frameDisposalBlocked.TrySetResult();
                releaseFrameDisposal.Wait();
            },
        };
        var encoder = new FakeEncoderFactory(events)
        {
            BlockOnFirstEncode = true,
            BlockCancellationCallback = true,
            IgnoreBlockedEncodeCancellation = true,
        };
        var frameDeadline = new ManualCaptureFrameDeadlineScheduler();
        var ownerDeadline = new ManualCaptureFrameDeadlineScheduler();
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            encoder,
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            frameDeadline,
            new ManualWallpaperFramePacerClock(),
            ownerDeadline);
        var project = new FakeProjectLease(Resolution(), events);
        var activation = factory.ActivateAsync(Request(project.Resolution), project).AsTask();

        try
        {
            await WaitUntilAsync(() => encoder.Encoders.Count == 1, TimeSpan.FromSeconds(2));
            var blockedEncoder = encoder.Encoders[0];
            await blockedEncoder.EncodingBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var blockedFrame = Assert.Single(capture.Frames);
            Assert.Equal(0, blockedFrame.DisposeCount);

            await WaitUntilAsync(() => frameDeadline.HasPendingWait, TimeSpan.FromSeconds(2));
            Assert.True(frameDeadline.ExpireCurrent());
            await blockedEncoder.CancellationCallbackBlocked.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => ownerDeadline.HasPendingWait, TimeSpan.FromSeconds(2));
            Assert.True(ownerDeadline.ExpireCurrent());

            var activationFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
                activation.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("quarantined", activationFailure.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, blockedFrame.DisposeCount);
            Assert.Equal(0, blockedEncoder.DisposeCount);
            Assert.DoesNotContain("capture-dispose", events);
            Assert.DoesNotContain("window-dispose", events);
            Assert.False(project.IsDisposed);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

            blockedEncoder.ReleaseBlockedEncoding.TrySetResult();
            blockedEncoder.ReleaseCancellationCallback.TrySetResult();
            await frameDisposalBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, blockedFrame.DisposeCount);
            Assert.Equal(0, blockedEncoder.DisposeCount);
            Assert.DoesNotContain("capture-dispose", events);
            Assert.DoesNotContain("window-dispose", events);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

            releaseFrameDisposal.Set();
            await WaitUntilAsync(
                () => blockedEncoder.DisposeCount == 1 &&
                    events.Count(item => item == "window-dispose") == 1,
                TimeSpan.FromSeconds(2));
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await factory.DisposeAsync();

            Assert.Equal(1, blockedFrame.DisposeCount);
            Assert.Equal(1, blockedEncoder.DisposeCount);
            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.False(project.IsDisposed);
            await project.DisposeAsync();
        }
        finally
        {
            encoder.Encoders.FirstOrDefault()?.ReleaseBlockedEncoding.TrySetResult();
            encoder.Encoders.FirstOrDefault()?.ReleaseCancellationCallback.TrySetResult();
            releaseFrameDisposal.Set();
            try
            {
                await factory.DisposeAsync();
            }
            catch
            {
            }

            if (!project.IsDisposed)
            {
                await project.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task EncodeWatchdogDetachesItsOwnerAndRecoversWithoutUnsafeCleanup()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
            RecordFrameDisposal = true,
        };
        var encoder = new FakeEncoderFactory(events)
        {
            BlockAfterFirstEncode = true,
            IgnoreBlockedEncodeCancellation = true,
        };
        var pacerClock = new ManualWallpaperFramePacerClock();
        var ownerDeadline = new ManualCaptureFrameDeadlineScheduler();
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            encoder,
            pages,
            TimeSpan.FromSeconds(15),
            new ManualCaptureFrameDeadlineScheduler(),
            pacerClock,
            ownerDeadline);
        var project = new FakeProjectLease(Resolution(), events);
        var active = (await factory.ActivateAsync(Request(project.Resolution), project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);
        var fallbackEncoder = encoder.Encoders[^1];

        try
        {
            await WaitUntilAsync(
                () => pacerClock.HasPendingWaitWithin(TimeSpan.FromSeconds(1)),
                TimeSpan.FromSeconds(2));
            pacerClock.Advance(TimeSpan.FromTicks(666_667));
            await fallbackEncoder.EncodingBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var blockedFrame = Assert.Single(capture.Frames);
            Assert.Equal(0, blockedFrame.DisposeCount);

            pacerClock.Advance(TimeSpan.FromSeconds(5));

            await WaitUntilAsync(
                () => ownerDeadline.HasPendingWait,
                TimeSpan.FromSeconds(2));
            Assert.Equal(TimeSpan.FromSeconds(5), ownerDeadline.LastRequestedDelay);
            Assert.True(ownerDeadline.ExpireCurrent());

            var healthFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                health.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("quarantined", healthFailure.Message, StringComparison.Ordinal);
            Assert.Single(pages.Leases);
            Assert.Equal(0, fallbackEncoder.DisposeCount);
            Assert.Equal(0, blockedFrame.DisposeCount);
            Assert.DoesNotContain("capture-dispose", events);
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.False(project.IsDisposed);

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                active.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, fallbackEncoder.DisposeCount);
            Assert.Equal(0, blockedFrame.DisposeCount);
            Assert.DoesNotContain("capture-dispose", events);
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.False(project.IsDisposed);

            fallbackEncoder.ReleaseBlockedEncoding.TrySetResult();
            await WaitUntilAsync(
                () => fallbackEncoder.DisposeCount == 1 &&
                    events.Count(item => item == "window-dispose") == 2,
                TimeSpan.FromSeconds(2));
            await active.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await factory.DisposeAsync();

            Assert.Equal(1, fallbackEncoder.DisposeCount);
            Assert.Equal(1, blockedFrame.DisposeCount);
            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(2, events.Count(item => item == "window-dispose"));
            Assert.True(
                events.FindLastIndex(item => item.StartsWith("frame-dispose:", StringComparison.Ordinal)) <
                events.FindLastIndex(item => item == "capture-dispose"));
            Assert.True(
                events.FindLastIndex(item => item == "capture-dispose") <
                events.FindLastIndex(item => item == "encoder-dispose"));
            Assert.True(
                events.FindLastIndex(item => item == "encoder-dispose") <
                events.FindLastIndex(item => item == "window-dispose"));
            Assert.True(project.IsDisposed);
        }
        finally
        {
            fallbackEncoder.ReleaseBlockedEncoding.TrySetResult();
            try
            {
                await active.DisposeAsync();
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task StopWatchdogOwnsThePumpWhenItsDeadlineWinsTheInnerEncodeRace()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FramesPerSession = 2,
            RecordFrameDisposal = true,
        };
        var encoder = new FakeEncoderFactory(events)
        {
            BlockAfterFirstEncode = true,
            IgnoreBlockedEncodeCancellation = true,
        };
        var ownerDeadline = new QueuedCaptureFrameDeadlineScheduler();
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            encoder,
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            new ManualCaptureFrameDeadlineScheduler(),
            new ManualWallpaperFramePacerClock(),
            ownerDeadline);
        var project = new FakeProjectLease(Resolution(), events);
        var active = (await factory.ActivateAsync(Request(project.Resolution), project)).Lease;
        var blockedEncoder = encoder.Encoders[0];

        try
        {
            await blockedEncoder.EncodingBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var blockedFrame = capture.Frames.Single(frame => frame.Sequence == 1);
            Assert.Equal(0, blockedFrame.DisposeCount);
            Assert.Equal(1, capture.Frames.Single(frame => frame.Sequence == 0).DisposeCount);

            var stopping = active.DisposeAsync().AsTask();
            await WaitUntilAsync(() => ownerDeadline.PendingCount >= 1, TimeSpan.FromSeconds(2));
            Assert.True(ownerDeadline.ExpireNext());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                stopping.WaitAsync(TimeSpan.FromSeconds(2)));

            await WaitUntilAsync(() => ownerDeadline.PendingCount >= 1, TimeSpan.FromSeconds(2));
            Assert.True(ownerDeadline.ExpireNext());
            Assert.Equal(0, blockedFrame.DisposeCount);
            Assert.Equal(0, blockedEncoder.DisposeCount);
            Assert.DoesNotContain("capture-dispose", events);
            Assert.DoesNotContain("window-dispose", events);
            Assert.False(project.IsDisposed);

            blockedEncoder.ReleaseBlockedEncoding.TrySetResult();
            await WaitUntilAsync(
                () => blockedEncoder.DisposeCount == 1 &&
                    events.Count(item => item == "window-dispose") == 1,
                TimeSpan.FromSeconds(2));
            await active.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, blockedFrame.DisposeCount);
            Assert.Equal(1, blockedEncoder.DisposeCount);
            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.True(project.IsDisposed);
        }
        finally
        {
            blockedEncoder.ReleaseBlockedEncoding.TrySetResult();
            try
            {
                await active.DisposeAsync();
            }
            catch
            {
            }

            try
            {
                await factory.DisposeAsync();
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task MoveNextWatchdogDetachesItsOwnerAndBoundsFactoryDisposal()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            IgnorePendingReadCancellation = true,
        };
        var frameDeadline = new ManualCaptureFrameDeadlineScheduler();
        var ownerDeadline = new ManualCaptureFrameDeadlineScheduler();
        var encoder = new FakeEncoderFactory(events);
        var pages = new FakePageSessionFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            encoder,
            pages,
            TimeSpan.FromSeconds(15),
            frameDeadline,
            new ManualWallpaperFramePacerClock(),
            ownerDeadline);
        var project = new FakeProjectLease(Resolution(), events);
        var active = (await factory.ActivateAsync(Request(project.Resolution), project)).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(active);

        try
        {
            await capture.PendingReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => frameDeadline.HasPendingWait, TimeSpan.FromSeconds(2));
            Assert.True(frameDeadline.ExpireCurrent());
            await WaitUntilAsync(() => ownerDeadline.HasPendingWait, TimeSpan.FromSeconds(2));

            Assert.True(ownerDeadline.ExpireCurrent());
            var healthFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                health.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("quarantined", healthFailure.Message, StringComparison.Ordinal);
            Assert.Single(pages.Leases);
            Assert.DoesNotContain("capture-dispose", events);
            Assert.Equal(0, encoder.Encoders[0].DisposeCount);
            Assert.DoesNotContain("window-dispose", events);
            Assert.False(project.IsDisposed);

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                active.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.DoesNotContain("capture-dispose", events);
            Assert.DoesNotContain("window-dispose", events);
            Assert.False(project.IsDisposed);

            capture.ReleasePendingRead.TrySetResult();
            await WaitUntilAsync(
                () => encoder.Encoders[0].DisposeCount == 1 &&
                    events.Count(item => item == "window-dispose") == 1,
                TimeSpan.FromSeconds(2));
            await active.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await factory.DisposeAsync();

            Assert.Equal(1, encoder.Encoders[0].DisposeCount);
            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.True(project.IsDisposed);
        }
        finally
        {
            capture.ReleasePendingRead.TrySetResult();
            try
            {
                await active.DisposeAsync();
            }
            catch
            {
            }

            try
            {
                await factory.DisposeAsync();
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task OuterStopOwnerRetainsALateMoveNextFrameUntilThePumpFinishes()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            IgnorePendingReadCancellation = true,
            YieldLateFrameAfterRelease = true,
            RecordFrameDisposal = true,
        };
        var frameDeadline = new ManualCaptureFrameDeadlineScheduler();
        var ownerDeadline = new QueuedCaptureFrameDeadlineScheduler();
        var encoder = new FakeEncoderFactory(events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            encoder,
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            frameDeadline,
            new ManualWallpaperFramePacerClock(),
            ownerDeadline);
        var project = new FakeProjectLease(Resolution(), events);
        var active = (await factory.ActivateAsync(Request(project.Resolution), project)).Lease;

        try
        {
            await capture.PendingReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stopping = active.DisposeAsync().AsTask();
            await WaitUntilAsync(() => ownerDeadline.PendingCount >= 1, TimeSpan.FromSeconds(2));
            Assert.True(ownerDeadline.ExpireNext());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                stopping.WaitAsync(TimeSpan.FromSeconds(2)));

            if (frameDeadline.HasPendingWait)
            {
                Assert.True(frameDeadline.ExpireCurrent());
            }

            await WaitUntilAsync(() => ownerDeadline.PendingCount >= 1, TimeSpan.FromSeconds(2));
            Assert.True(ownerDeadline.ExpireNext());
            Assert.Single(capture.Frames);
            Assert.DoesNotContain("capture-dispose", events);
            Assert.Equal(0, encoder.Encoders[0].DisposeCount);
            Assert.DoesNotContain("window-dispose", events);

            capture.ReleasePendingRead.TrySetResult();
            await WaitUntilAsync(
                () => capture.Frames.Count == 2 &&
                    capture.Frames[1].DisposeCount == 1 &&
                    encoder.Encoders[0].DisposeCount == 1 &&
                    events.Count(item => item == "window-dispose") == 1,
                TimeSpan.FromSeconds(2));
            await active.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, capture.Frames[1].DisposeCount);
            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(1, encoder.Encoders[0].DisposeCount);
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.True(
                events.FindIndex(item => item == "frame-dispose:1") <
                events.FindIndex(item => item == "capture-dispose"));
            Assert.True(
                events.FindIndex(item => item == "capture-dispose") <
                events.FindIndex(item => item == "encoder-dispose"));
            Assert.True(
                events.FindIndex(item => item == "encoder-dispose") <
                events.FindIndex(item => item == "window-dispose"));
            Assert.True(project.IsDisposed);
        }
        finally
        {
            capture.ReleasePendingRead.TrySetResult();
            try
            {
                await active.DisposeAsync();
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CaptureDisposeTimeoutTransfersTheSingleAttemptBeforeEncoderAndWindow()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events) { BlockDispose = true };
        var encoder = new FakeEncoderFactory(events);
        var window = new FakeWindowRenderer(events);
        var ownerDeadline = new ManualCaptureFrameDeadlineScheduler();
        var factory = new DynamicWallpaperActivationFactory(
            window,
            capture,
            encoder,
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            new ManualCaptureFrameDeadlineScheduler(),
            new ManualWallpaperFramePacerClock(),
            ownerDeadline);
        var project = new FakeProjectLease(Resolution(), events);
        var active = (await factory.ActivateAsync(Request(project.Resolution), project)).Lease;

        try
        {
            var disposing = active.DisposeAsync().AsTask();
            await capture.DisposeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => ownerDeadline.HasPendingWait, TimeSpan.FromSeconds(2));
            Assert.True(ownerDeadline.ExpireCurrent());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                disposing.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(0, encoder.Encoders[0].DisposeCount);
            Assert.DoesNotContain("window-dispose", events);
            Assert.False(project.IsDisposed);

            capture.ReleaseDispose.TrySetResult();
            await WaitUntilAsync(
                () => encoder.Encoders[0].DisposeCount == 1 &&
                    events.Count(item => item == "window-dispose") == 1,
                TimeSpan.FromSeconds(2));
            await active.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(1, encoder.Encoders[0].DisposeCount);
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.True(project.IsDisposed);
        }
        finally
        {
            capture.ReleaseDispose.TrySetResult();
            try
            {
                await active.DisposeAsync();
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task EncoderDisposeTimeoutTransfersTheSingleAttemptBeforeWindow()
    {
        var events = new List<string>();
        var encoder = new FakeEncoderFactory(events) { BlockDispose = true };
        var window = new FakeWindowRenderer(events);
        var ownerDeadline = new ManualCaptureFrameDeadlineScheduler();
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            encoder,
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            new ManualCaptureFrameDeadlineScheduler(),
            new ManualWallpaperFramePacerClock(),
            ownerDeadline);
        var project = new FakeProjectLease(Resolution(), events);
        var active = (await factory.ActivateAsync(Request(project.Resolution), project)).Lease;
        var ownedEncoder = encoder.Encoders[0];

        try
        {
            var disposing = active.DisposeAsync().AsTask();
            await ownedEncoder.DisposeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => ownerDeadline.HasPendingWait, TimeSpan.FromSeconds(2));
            Assert.True(ownerDeadline.ExpireCurrent());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                disposing.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(1, ownedEncoder.DisposeCount);
            Assert.DoesNotContain("window-dispose", events);
            Assert.False(project.IsDisposed);

            ownedEncoder.ReleaseDispose.TrySetResult();
            await WaitUntilAsync(
                () => events.Count(item => item == "window-dispose") == 1,
                TimeSpan.FromSeconds(2));
            await active.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(1, ownedEncoder.DisposeCount);
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.True(project.IsDisposed);
        }
        finally
        {
            ownedEncoder.ReleaseDispose.TrySetResult();
            try
            {
                await active.DisposeAsync();
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task WindowDisposeTimeoutUsesTheSameBoundedSingleAttemptOwner()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events) { BlockDispose = true };
        var ownerDeadline = new ManualCaptureFrameDeadlineScheduler();
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            new ManualCaptureFrameDeadlineScheduler(),
            new ManualWallpaperFramePacerClock(),
            ownerDeadline);
        var project = new FakeProjectLease(Resolution(), events);
        var active = (await factory.ActivateAsync(Request(project.Resolution), project)).Lease;

        try
        {
            var disposing = active.DisposeAsync().AsTask();
            await window.DisposeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => ownerDeadline.HasPendingWait, TimeSpan.FromSeconds(2));
            Assert.True(ownerDeadline.ExpireCurrent());
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                disposing.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(1, events.Count(item => item == "encoder-dispose"));
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.False(project.IsDisposed);

            window.ReleaseDispose.TrySetResult();
            await DisposeEventuallyAsync(active, TimeSpan.FromSeconds(2));
            await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, events.Count(item => item == "capture-dispose"));
            Assert.Equal(1, events.Count(item => item == "encoder-dispose"));
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.True(project.IsDisposed);
        }
        finally
        {
            window.ReleaseDispose.TrySetResult();
            try
            {
                await active.DisposeAsync();
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task DisposeRetriesOnlyTheCaptureAfterItsFirstReleaseFailsAndDelaysProjectRelease()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events) { DisposeFailures = 1 };
        var project = new FakeProjectLease(Resolution(), events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            new FakeEncoderFactory(events),
            new FakePageSessionFactory(events));
        var active = (await factory.ActivateAsync(Request(project.Resolution), project)).Lease;

        await Assert.ThrowsAsync<IOException>(() => active.DisposeAsync().AsTask());

        Assert.Equal(1, events.Count(item => item == "capture-dispose"));
        Assert.DoesNotContain("encoder-dispose", events);
        Assert.DoesNotContain("window-dispose", events);
        Assert.DoesNotContain("project-dispose", events);
        Assert.False(project.IsDisposed);

        events.Clear();
        await active.DisposeAsync();

        Assert.Equal(
            ["capture-dispose", "encoder-dispose", "window-dispose", "project-dispose"],
            events);
        Assert.True(project.IsDisposed);
    }

    [Fact]
    public async Task DisposeRetriesOnlyTheWindowAfterItsFirstCloseFailsAndDelaysProjectRelease()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events) { DisposeFailures = 1 };
        var project = new FakeProjectLease(Resolution(), events);
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            new FakePageSessionFactory(events));
        var active = (await factory.ActivateAsync(Request(project.Resolution), project)).Lease;

        var firstFailure = await Assert.ThrowsAsync<IOException>(
            () => active.DisposeAsync().AsTask());

        Assert.Equal("fixture window close failed", firstFailure.Message);
        Assert.False(project.IsDisposed);
        Assert.Equal(1, events.Count(item => item == "page-dispose"));
        Assert.Equal(1, events.Count(item => item == "capture-dispose"));
        Assert.Equal(1, events.Count(item => item == "encoder-dispose"));
        Assert.Equal(1, events.Count(item => item == "window-dispose"));
        Assert.DoesNotContain("project-dispose", events);

        events.Clear();
        await active.DisposeAsync();

        Assert.True(project.IsDisposed);
        Assert.Equal(["window-dispose", "project-dispose"], events);
    }

    [Fact]
    public async Task StartupFailurePreservesThePrimaryCauseAndAggregatesOrderedCleanupFailures()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events) { DisposeFailures = 1 };
        var capture = new FakeCaptureFactory(events) { DisposeFailures = 1 };
        var pages = new FakePageSessionFactory(events)
        {
            InvalidDeliveryKind = true,
            DisposeFailures = 1,
        };
        var project = new FakeProjectLease(Resolution(), events);
        var factory = new DynamicWallpaperActivationFactory(
            window,
            capture,
            new FakeEncoderFactory(events),
            pages);

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            factory.ActivateAsync(Request(project.Resolution), project).AsTask());

        Assert.Collection(
            failure.InnerExceptions,
            primary => Assert.IsType<ArgumentException>(primary),
            page => Assert.Equal("fixture page dispose failed", page.Message),
            captureAndEncoder => Assert.Equal(
                "fixture capture dispose failed",
                captureAndEncoder.Message));
        Assert.Equal(
            ["page-dispose", "capture-dispose"],
            events.Where(item => item.EndsWith("-dispose", StringComparison.Ordinal)));
        Assert.False(project.IsDisposed);

        events.Clear();
        var retainedFailure = await Assert.ThrowsAsync<IOException>(
            () => factory.DisposeAsync().AsTask());
        Assert.Equal("fixture window close failed", retainedFailure.Message);
        Assert.Equal(
            ["page-dispose", "capture-dispose", "encoder-dispose", "window-dispose"],
            events.Where(item => item.EndsWith("-dispose", StringComparison.Ordinal)));

        events.Clear();
        await factory.DisposeAsync();
        Assert.Equal(["window-dispose"], events);
    }

    [Fact]
    public async Task CaptureStartupFailureRemainsPrimaryWhenEncoderCleanupAlsoFails()
    {
        var events = new List<string>();
        var capture = new FakeCaptureFactory(events)
        {
            FailStarts = 1,
            StartFailureReason =
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable,
        };
        var encoder = new FakeEncoderFactory(events) { DisposeFailures = 1 };
        var project = new FakeProjectLease(Resolution(), events);
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            capture,
            encoder,
            new FakePageSessionFactory(events));

        var failure = await Assert.ThrowsAsync<AggregateException>(() =>
            factory.ActivateAsync(Request(project.Resolution), project).AsTask());

        Assert.Collection(
            failure.InnerExceptions,
            primary => Assert.Equal(
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable,
                Assert.IsType<DynamicWallpaperUnavailableException>(primary).ReasonCode),
            cleanup => Assert.Equal("fixture encoder dispose failed", cleanup.Message));
        Assert.Equal(
            ["encoder-dispose"],
            events.Where(item => item.EndsWith("-dispose", StringComparison.Ordinal)));
        Assert.False(project.IsDisposed);

        events.Clear();
        await factory.DisposeAsync();

        Assert.Equal(["encoder-dispose", "window-dispose"], events);
    }

    [Fact]
    public async Task FailedStartupEncoderDisposeTimeoutRetainsWindowUntilTheSingleAttemptFinishes()
    {
        var events = new List<string>();
        var encoder = new FakeEncoderFactory(events)
        {
            MissingPauseBoundary = true,
            BlockDispose = true,
        };
        var ownerDeadline = new ManualCaptureFrameDeadlineScheduler();
        var factory = new DynamicWallpaperActivationFactory(
            new FakeWindowRenderer(events),
            new FakeCaptureFactory(events),
            encoder,
            new FakePageSessionFactory(events),
            TimeSpan.FromSeconds(15),
            new ManualCaptureFrameDeadlineScheduler(),
            new ManualWallpaperFramePacerClock(),
            ownerDeadline);
        var project = new FakeProjectLease(Resolution(), events);
        var activation = factory.ActivateAsync(Request(project.Resolution), project).AsTask();

        try
        {
            await WaitUntilAsync(() => encoder.Encoders.Count == 1, TimeSpan.FromSeconds(2));
            var pendingEncoder = encoder.Encoders[0];
            await pendingEncoder.DisposeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => ownerDeadline.HasPendingWait, TimeSpan.FromSeconds(2));
            Assert.True(ownerDeadline.ExpireCurrent());

            var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
                activation.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("quarantined", failure.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, pendingEncoder.DisposeCount);
            Assert.DoesNotContain("window-dispose", events);
            Assert.False(project.IsDisposed);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

            pendingEncoder.ReleaseDispose.TrySetResult();
            await WaitUntilAsync(
                () => events.Count(item => item == "window-dispose") == 1,
                TimeSpan.FromSeconds(2));
            await DisposeEventuallyAsync(factory, TimeSpan.FromSeconds(2));

            Assert.Equal(1, pendingEncoder.DisposeCount);
            Assert.Equal(1, events.Count(item => item == "window-dispose"));
            Assert.False(project.IsDisposed);
            await project.DisposeAsync();
        }
        finally
        {
            encoder.Encoders.FirstOrDefault()?.ReleaseDispose.TrySetResult();
            try
            {
                await factory.DisposeAsync();
            }
            catch
            {
            }

            if (!project.IsDisposed)
            {
                await project.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task RetainedFailedStartCleanupBlocksTheNextActivationUntilRetrySucceeds()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events);
        var pages = new FakePageSessionFactory(events)
        {
            InvalidDeliveryKind = true,
            DisposeFailures = 2,
        };
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            pages);

        var firstResolution = Resolution();
        await Assert.ThrowsAsync<AggregateException>(() =>
            factory.ActivateAsync(
                Request(firstResolution),
                new FakeProjectLease(firstResolution, events)).AsTask());
        Assert.Single(window.Options);

        var blockedResolution = Resolution();
        await Assert.ThrowsAsync<IOException>(() =>
            factory.ActivateAsync(
                Request(blockedResolution),
                new FakeProjectLease(blockedResolution, events)).AsTask());
        Assert.Single(window.Options);

        pages.InvalidDeliveryKind = false;
        var resolution = Resolution();
        await using var active = (await factory.ActivateAsync(
            Request(resolution),
            new FakeProjectLease(resolution, events))).Lease;

        Assert.Equal(2, window.Options.Count);
        Assert.True(
            events.FindLastIndex(item => item == "page-dispose") <
            events.FindLastIndex(item => item == "capture-start"));
    }

    [Fact]
    public async Task FactoryDisposeRetainsFailedStartupResourcesAndCanBeRetried()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events) { DisposeFailures = 2 };
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events),
            new FakeEncoderFactory(events),
            new FakePageSessionFactory(events) { InvalidDeliveryKind = true });

        var resolution = Resolution();
        await Assert.ThrowsAsync<AggregateException>(() =>
            factory.ActivateAsync(
                Request(resolution),
                new FakeProjectLease(resolution, events)).AsTask());
        var cleanupOwner = Assert.IsAssignableFrom<IAsyncDisposable>(factory);

        await Assert.ThrowsAsync<IOException>(() => cleanupOwner.DisposeAsync().AsTask());
        await cleanupOwner.DisposeAsync();

        Assert.Equal(3, events.Count(item => item == "window-dispose"));
    }

    [Fact]
    public async Task ActivateAsync_DoesNotRepeatAnAmbiguousStartupTimeoutAtFallbackTier()
    {
        var events = new List<string>();
        var window = new FakeWindowRenderer(events);
        var factory = new DynamicWallpaperActivationFactory(
            window,
            new FakeCaptureFactory(events) { YieldFrames = false },
            new FakeEncoderFactory(events),
            new FakePageSessionFactory(events),
            TimeSpan.FromMilliseconds(25));
        var project = new FakeProjectLease(Resolution(), events);

        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            factory.ActivateAsync(Request(project.Resolution), project).AsTask());

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.DynamicStartupTimedOut,
            failure.ReasonCode);
        Assert.Equal(3, window.Options.Count);
        Assert.DoesNotContain("page-dispose", events);
        Assert.Equal(3, events.Count(item => item == "window-dispose"));
        Assert.False(project.IsDisposed);
        await project.DisposeAsync();
    }

    private static DynamicWallpaperActivationRequest Request(
        WallpaperSourceResolution resolution) => new(
            generation: 41,
            Endpoint(),
            resolution,
            WallpaperProfile.CreateDefault());

    private static WallpaperSourceResolution Resolution()
    {
        var reference = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "123456789",
            LastKnownContentKind = WallpaperContentKind.Scene,
            LastKnownDisplayName = "Fixture scene",
        };
        return new WallpaperSourceResolution(
            reference,
            new WallpaperSourceDescriptor(
                reference.SourceKind,
                reference.SourceIdentifier,
                "Fixture scene",
                WallpaperContentKind.Scene,
                WallpaperDeliveryKind.WallpaperEngineWindow,
                WallpaperDeliveryCapabilities.DynamicFrames),
            directMediaMetadata: null);
    }

    private static VerifiedCdpEndpoint Endpoint()
    {
        var identity = Codex.CodexSecurityValidatorTests.GetIdentity();
        var target = new CdpTargetDescriptor(
            "codex-page",
            "page",
            "Codex",
            "app://codex/index.html",
            "ws://127.0.0.1:9222/devtools/page/codex-page");
        return new VerifiedCdpEndpoint(
            new CdpEndpointCandidate(
                1234,
                "ChatGPT.exe",
                identity.PackageFamilyName,
                identity.PackageFullName,
                DateTimeOffset.UtcNow,
                WindowsCodexProcessSnapshotSource.CurrentSessionId,
                new Uri("http://127.0.0.1:9222/")),
            new CdpBrowserVersion(
                "Chrome/140.0.0.0",
                "1.3",
                null,
                null,
                "ws://127.0.0.1:9222/devtools/browser/test"),
            new Uri("ws://127.0.0.1:9222/devtools/browser/test"),
            [new ClassifiedCdpTarget(target, CdpTargetClassification.CodexPage)],
            identity);
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The dynamic pipeline did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task DisposeEventuallyAsync(
        IAsyncDisposable resource,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                await resource.DisposeAsync();
                return;
            }
            catch (InvalidOperationException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
        }
    }

    private sealed class FakeProjectLease(
        WallpaperSourceResolution resolution,
        List<string> events) : IWallpaperEngineProjectLease
    {
        public WallpaperSourceResolution Resolution { get; } = resolution;

        public string LaunchPath { get; } = @"C:\Fixtures\scene.pkg";

        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            events.Add("project-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWindowRenderer(List<string> events)
        : IWallpaperEngineWindowRenderer
    {
        private int _nextHandle = 100;

        public int DisposeFailures { get; set; }

        public bool BlockDispose { get; set; }

        public TaskCompletionSource DisposeBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WallpaperDeliveryCapabilities Capabilities =>
            WallpaperDeliveryCapabilities.DynamicFrames;

        public List<WallpaperEngineWindowOptions> Options { get; } = [];

        public ValueTask<IWallpaperEngineWindowLease> StartAsync(
            IWallpaperEngineProjectLease projectLease,
            WallpaperEngineWindowOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Options.Add(options);
            return ValueTask.FromResult<IWallpaperEngineWindowLease>(
                new FakeWindowLease(
                    Interlocked.Increment(ref _nextHandle),
                    events,
                    () => DisposeFailures-- > 0,
                    BlockDispose,
                    DisposeBlocked,
                    ReleaseDispose));
        }
    }

    private sealed class FakeWindowLease : IWallpaperEngineWindowLease
    {
        private readonly nint _handle;
        private readonly List<string> _events;
        private readonly Func<bool> _shouldFailDispose;
        private readonly bool _blockDispose;
        private readonly TaskCompletionSource _disposeBlocked;
        private readonly TaskCompletionSource _releaseDispose;

        public FakeWindowLease(
            nint handle,
            List<string> events,
            Func<bool> shouldFailDispose,
            bool blockDispose,
            TaskCompletionSource disposeBlocked,
            TaskCompletionSource releaseDispose)
        {
            _handle = handle;
            _events = events;
            _shouldFailDispose = shouldFailDispose;
            _blockDispose = blockDispose;
            _disposeBlocked = disposeBlocked;
            _releaseDispose = releaseDispose;
            CaptureTarget = new WallpaperEngineWindowCaptureTarget(
                new FixedCaptureAuthority(handle));
        }

        public WallpaperEngineWindowCaptureTarget? CaptureTarget { get; private set; }

        public ValueTask SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add($"window-pause:{paused}");
            CaptureTarget = paused
                ? null
                : new WallpaperEngineWindowCaptureTarget(
                    new FixedCaptureAuthority(_handle));
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            _events.Add("window-dispose");
            if (_blockDispose)
            {
                _disposeBlocked.TrySetResult();
                await _releaseDispose.Task.ConfigureAwait(false);
            }

            if (_shouldFailDispose())
            {
                throw new IOException("fixture window close failed");
            }

            CaptureTarget = null;
        }
    }

    private sealed class FixedCaptureAuthority(nint windowHandle)
        : IWallpaperEngineWindowCaptureAuthority
    {
        public ValueTask<nint> RevalidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(windowHandle);
        }
    }

    private sealed class FakeCaptureFactory(List<string> events)
        : IWallpaperWindowCaptureFactory
    {
        public int FailStarts { get; set; }

        public DynamicWallpaperCapabilityReasonCode StartFailureReason { get; set; } =
            DynamicWallpaperCapabilityReasonCode.CapturedFrameSizeMismatch;

        public int DisposeFailures { get; set; }

        public bool BlockDispose { get; set; }

        public bool YieldFrames { get; set; } = true;

        public int FramesPerSession { get; set; } = 1;

        public bool RecordFrameDisposal { get; set; }

        public Action<int>? FrameDisposing { get; set; }

        public bool ReturnFalseOnCancellation { get; set; }

        public bool IgnorePendingReadCancellation { get; set; }

        public bool YieldLateFrameAfterRelease { get; set; }

        public int LateFrameDisposeFailures { get; set; }

        public TaskCompletionSource PendingReadBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleasePendingRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationReadBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposeBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<FakeFrame> Frames { get; } = [];

        public List<WallpaperWindowCaptureRequest> Requests { get; } = [];

        public ValueTask<DynamicWallpaperCapability> ProbeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DynamicWallpaperCapability.Available());

        public ValueTask<IWallpaperWindowCaptureSession> StartAsync(
            WallpaperWindowCaptureRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add("capture-start");
            Requests.Add(request);
            if (FailStarts-- > 0)
            {
                throw new DynamicWallpaperUnavailableException(
                    StartFailureReason);
            }

            return ValueTask.FromResult<IWallpaperWindowCaptureSession>(
                new FakeCaptureSession(
                    request,
                    events,
                    () => DisposeFailures-- > 0,
                    BlockDispose,
                    DisposeBlocked,
                    ReleaseDispose,
                    YieldFrames,
                    FramesPerSession,
                    RecordFrameDisposal,
                    FrameDisposing,
                    ReturnFalseOnCancellation,
                    IgnorePendingReadCancellation,
                    YieldLateFrameAfterRelease,
                    PendingReadBlocked,
                    ReleasePendingRead,
                    CancellationReadBlocked,
                    () => LateFrameDisposeFailures-- > 0,
                    Frames));
        }
    }

    private sealed class ManualCaptureFrameDeadlineScheduler
        : ICaptureFrameDeadlineScheduler
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _current;
        private int _armCount;
        private TimeSpan _lastRequestedDelay;

        public TimeSpan CancellationDelay { get; init; }

        public int ArmCount => Volatile.Read(ref _armCount);

        public TimeSpan LastRequestedDelay
        {
            get
            {
                lock (_sync)
                {
                    return _lastRequestedDelay;
                }
            }
        }

        public bool HasPendingWait
        {
            get
            {
                lock (_sync)
                {
                    return _current is { Task.IsCompleted: false };
                }
            }
        }

        public bool ExpireCurrent()
        {
            TaskCompletionSource? current;
            lock (_sync)
            {
                current = _current;
            }

            return current?.TrySetResult() == true;
        }

        public async ValueTask WaitAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            var current = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _current = current;
                _lastRequestedDelay = delay;
            }

            Interlocked.Increment(ref _armCount);

            using var registration = cancellationToken.Register(
                () => ScheduleCancellation(current, CancellationDelay));
            await current.Task.ConfigureAwait(false);
        }

        private static void ScheduleCancellation(
            TaskCompletionSource completion,
            TimeSpan delay)
        {
            if (delay == TimeSpan.Zero)
            {
                completion.TrySetCanceled();
                return;
            }

            _ = CompleteCancellationAfterDelayAsync(completion, delay);
        }

        private static async Task CompleteCancellationAfterDelayAsync(
            TaskCompletionSource completion,
            TimeSpan delay)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            completion.TrySetCanceled();
        }
    }

    private sealed class QueuedCaptureFrameDeadlineScheduler
        : ICaptureFrameDeadlineScheduler
    {
        private readonly object _sync = new();
        private readonly Queue<TaskCompletionSource> _pending = [];

        public int PendingCount
        {
            get
            {
                lock (_sync)
                {
                    return _pending.Count(item => !item.Task.IsCompleted);
                }
            }
        }

        public bool ExpireNext()
        {
            lock (_sync)
            {
                while (_pending.TryDequeue(out var current))
                {
                    if (current.TrySetResult())
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public async ValueTask WaitAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            _ = delay;
            var current = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _pending.Enqueue(current);
            }

            using var registration = cancellationToken.Register(
                () => current.TrySetCanceled(cancellationToken));
            await current.Task.ConfigureAwait(false);
        }
    }

    private sealed class ManualWallpaperFramePacerClock : IWallpaperFramePacerClock
    {
        private readonly object _sync = new();
        private readonly List<Waiter> _waiters = [];
        private TimeSpan _now;

        public TimeSpan IgnoreCancellationForWaitsBelow { get; set; }

        public int PendingWaitCount
        {
            get
            {
                lock (_sync)
                {
                    return _waiters.Count(waiter => !waiter.Completion.Task.IsCompleted);
                }
            }
        }

        public bool HasPendingWaitWithin(TimeSpan delay)
        {
            lock (_sync)
            {
                return _waiters.Any(
                    waiter =>
                        !waiter.Completion.Task.IsCompleted &&
                        waiter.DueAt - _now <= delay);
            }
        }

        public TimeSpan GetMonotonicNow()
        {
            lock (_sync)
            {
                return _now;
            }
        }

        public async ValueTask WaitAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
            if (delay == TimeSpan.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            Waiter waiter;
            lock (_sync)
            {
                waiter = new Waiter(_now + delay);
                _waiters.Add(waiter);
            }

            using var registration = delay < IgnoreCancellationForWaitsBelow
                ? default
                : cancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetCanceled(),
                    waiter.Completion);
            await waiter.Completion.Task.ConfigureAwait(false);
        }

        public void Advance(TimeSpan elapsed)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
            Waiter[] due;
            lock (_sync)
            {
                _now += elapsed;
                due = _waiters
                    .Where(waiter => waiter.DueAt <= _now)
                    .ToArray();
                _waiters.RemoveAll(waiter => waiter.DueAt <= _now);
            }

            foreach (var waiter in due)
            {
                waiter.Completion.TrySetResult();
            }
        }

        public void CancelAll()
        {
            Waiter[] waiters;
            lock (_sync)
            {
                waiters = _waiters.ToArray();
                _waiters.Clear();
            }

            foreach (var waiter in waiters)
            {
                waiter.Completion.TrySetCanceled();
            }
        }

        private sealed record Waiter(TimeSpan DueAt)
        {
            internal TaskCompletionSource Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class FakeCaptureSession(
        WallpaperWindowCaptureRequest request,
        List<string> events,
        Func<bool> shouldFailDispose,
        bool blockDispose,
        TaskCompletionSource disposeBlocked,
        TaskCompletionSource releaseDispose,
        bool yieldFrames,
        int framesPerSession,
        bool recordFrameDisposal,
        Action<int>? frameDisposing,
        bool returnFalseOnCancellation,
        bool ignorePendingReadCancellation,
        bool yieldLateFrameAfterRelease,
        TaskCompletionSource pendingReadBlocked,
        TaskCompletionSource releasePendingRead,
        TaskCompletionSource cancellationReadBlocked,
        Func<bool> shouldFailLateFrameDispose,
        List<FakeFrame> createdFrames) : IWallpaperWindowCaptureSession
    {
        public long Generation => request.Generation;

        public async IAsyncEnumerable<IWallpaperCapturedFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (yieldFrames)
            {
                for (var index = 0; index < framesPerSession; index++)
                {
                    var frame = new FakeFrame(
                        request,
                        index,
                        events,
                        recordFrameDisposal,
                        frameDisposing,
                        shouldFailDispose: null);
                    createdFrames.Add(frame);
                    yield return frame;
                }
            }

            pendingReadBlocked.TrySetResult();
            if (ignorePendingReadCancellation)
            {
                await releasePendingRead.Task.ConfigureAwait(false);
                if (yieldLateFrameAfterRelease)
                {
                    var lateFrame = new FakeFrame(
                        request,
                        framesPerSession,
                        events,
                        recordFrameDisposal,
                        frameDisposing,
                        shouldFailLateFrameDispose);
                    createdFrames.Add(lateFrame);
                    yield return lateFrame;
                }

                if (!returnFalseOnCancellation)
                {
                    yield break;
                }
            }

            cancellationReadBlocked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (
                returnFalseOnCancellation && cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            events.Add("capture-dispose");
            if (blockDispose)
            {
                disposeBlocked.TrySetResult();
                await releaseDispose.Task.ConfigureAwait(false);
            }

            if (shouldFailDispose())
            {
                throw new IOException("fixture capture dispose failed");
            }
        }
    }

    private sealed class FakeFrame(
        WallpaperWindowCaptureRequest request,
        int index,
        List<string> events,
        bool recordDisposal,
        Action<int>? disposing,
        Func<bool>? shouldFailDispose) : IWallpaperCapturedFrame
    {
        public long Generation => request.Generation;

        public long Sequence => index;

        public TimeSpan Timestamp => TimeSpan.FromMilliseconds(index);

        public int Width => request.Width;

        public int Height => request.Height;

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            disposing?.Invoke(index);
            if (recordDisposal)
            {
                events.Add($"frame-dispose:{index}");
            }

            if (shouldFailDispose?.Invoke() == true)
            {
                throw new IOException("fixture frame dispose failed");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeEncoderFactory(List<string> events)
        : IFragmentedMp4WallpaperEncoderFactory
    {
        public int FailStarts { get; set; }

        public DynamicWallpaperCapabilityReasonCode StartFailureReason { get; set; } =
            DynamicWallpaperCapabilityReasonCode.EncodingFailed;

        public int DisposeFailures { get; set; }

        public bool BlockAfterFirstEncode { get; set; }

        public bool BlockOnFirstEncode { get; set; }

        public bool BlockCancellationCallback { get; set; }

        public bool BlockDispose { get; set; }

        public bool MissingPauseBoundary { get; set; }

        public bool IgnoreBlockedEncodeCancellation { get; set; }

        public List<EncodedWallpaperStreamDescriptor> Descriptors { get; } = [];

        public List<FakeEncoder> Encoders { get; } = [];

        public ValueTask<DynamicWallpaperCapability> ProbeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DynamicWallpaperCapability.Available());

        public ValueTask<IFragmentedMp4WallpaperEncoder> StartAsync(
            EncodedWallpaperStreamDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            Descriptors.Add(descriptor);
            if (FailStarts-- > 0)
            {
                throw new DynamicWallpaperUnavailableException(StartFailureReason);
            }

            var encoder = new FakeEncoder(
                descriptor,
                events,
                () => DisposeFailures-- > 0,
                BlockAfterFirstEncode,
                BlockOnFirstEncode,
                BlockCancellationCallback,
                BlockDispose,
                IgnoreBlockedEncodeCancellation);
            Encoders.Add(encoder);
            return ValueTask.FromResult<IFragmentedMp4WallpaperEncoder>(
                MissingPauseBoundary
                    ? new NonPauseBoundaryEncoder(encoder)
                    : encoder);
        }
    }

    private sealed class NonPauseBoundaryEncoder(FakeEncoder inner)
        : IFragmentedMp4WallpaperEncoder
    {
        public EncodedWallpaperStreamDescriptor Descriptor => inner.Descriptor;

        public ValueTask EncodeAsync(
            IWallpaperCapturedFrame frame,
            IEncodedWallpaperSegmentSink output,
            CancellationToken cancellationToken = default) =>
            inner.EncodeAsync(frame, output, cancellationToken);

        public ValueTask CompleteAsync(
            IEncodedWallpaperSegmentSink output,
            CancellationToken cancellationToken = default) =>
            inner.CompleteAsync(output, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FakeEncoder(
        EncodedWallpaperStreamDescriptor descriptor,
        List<string> events,
        Func<bool> shouldFailDispose,
        bool blockAfterFirstEncode,
        bool blockOnFirstEncode,
        bool blockCancellationCallback,
        bool blockDispose,
        bool ignoreBlockedEncodeCancellation) :
        IFragmentedMp4WallpaperEncoder,
        ICapturePauseBoundaryFragmentedMp4WallpaperEncoder
    {
        private int _encoded;
        private int _encodeCalls;

        public List<IWallpaperCapturedFrame> EncodedFrames { get; } = [];

        public TaskCompletionSource EncodingBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource EncodingCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationCallbackBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCancellationCallback { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DisposeBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseBlockedEncoding { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EncodedWallpaperStreamDescriptor Descriptor { get; } = descriptor;

        public int DisposeCount { get; private set; }

        public ValueTask DiscardPendingFragmentForCapturePauseAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("encoder-pause-boundary");
            return ValueTask.CompletedTask;
        }

        public async ValueTask EncodeAsync(
            IWallpaperCapturedFrame frame,
            IEncodedWallpaperSegmentSink output,
            CancellationToken cancellationToken = default)
        {
            lock (EncodedFrames)
            {
                EncodedFrames.Add(frame);
            }

            var encodeCall = Interlocked.Increment(ref _encodeCalls);
            if ((blockOnFirstEncode && encodeCall == 1) ||
                (blockAfterFirstEncode && encodeCall > 1))
            {
                EncodingBlocked.TrySetResult();
                using var cancellationRegistration = blockCancellationCallback
                    ? cancellationToken.Register(
                        () =>
                        {
                            CancellationCallbackBlocked.TrySetResult();
                            ReleaseCancellationCallback.Task.GetAwaiter().GetResult();
                        })
                    : default;
                if (ignoreBlockedEncodeCancellation)
                {
                    await ReleaseBlockedEncoding.Task.ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        cancellationToken.IsCancellationRequested)
                    {
                        EncodingCancellationObserved.TrySetResult();
                        throw;
                    }
                }
            }

            if (Interlocked.Exchange(ref _encoded, 1) == 0)
            {
                _ = output.TryWrite(new EncodedWallpaperSegment(
                    Descriptor.Generation,
                    0,
                    EncodedWallpaperSegmentKind.Initialization,
                    isKeyFrame: false,
                    new byte[] { 1 }));
                _ = output.TryWrite(new EncodedWallpaperSegment(
                    Descriptor.Generation,
                    1,
                    EncodedWallpaperSegmentKind.Media,
                    isKeyFrame: true,
                    new byte[] { 2 }));
            }

        }

        public ValueTask CompleteAsync(
            IEncodedWallpaperSegmentSink output,
            CancellationToken cancellationToken = default)
        {
            events.Add("encoder-complete");
            output.Complete();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            events.Add("encoder-dispose");
            if (blockDispose)
            {
                DisposeBlocked.TrySetResult();
                await ReleaseDispose.Task.ConfigureAwait(false);
            }

            if (shouldFailDispose())
            {
                throw new IOException("fixture encoder dispose failed");
            }
        }
    }

    private sealed class FakePageSessionFactory(List<string> events)
        : IDynamicWallpaperPageSessionFactory
    {
        private readonly object _sync = new();
        private readonly Queue<Exception> _pauseFailures = new();

        public List<FakePageLease> Leases { get; } = [];

        public List<DynamicWallpaperInjectionOptions> Options { get; } = [];

        public Queue<(
            PresentationContractSnapshot Presentation,
            CompatibilityCapabilities Capabilities)>
            CompatibilityObservations
        {
            get;
        } = [];

        public bool BlockKeyFrameWait { get; init; }

        public bool InvalidDeliveryKind { get; set; }

        public long? LeaseGenerationOverride { get; init; }

        public bool IgnoreLockedCompatibility { get; init; }

        public bool ThrowOnDispose { get; init; }

        public int DisposeFailures { get; set; }

        public void FailNextPause(Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            lock (_sync)
            {
                _pauseFailures.Enqueue(failure);
            }
        }

        public async ValueTask<DynamicWallpaperActivationResult> StartAsync(
            VerifiedCdpEndpoint endpoint,
            EncodedWallpaperStreamBuffer buffer,
            DynamicWallpaperInjectionOptions options,
            CancellationToken cancellationToken = default)
        {
            await buffer.WaitForStartupAsync(cancellationToken);
            Options.Add(options);
            var lease = new FakePageLease(
                buffer,
                LeaseGenerationOverride,
                events,
                BlockKeyFrameWait,
                InvalidDeliveryKind,
                () => ThrowOnDispose || DisposeFailures-- > 0,
                TakePauseFailure);
            Leases.Add(lease);
            IActiveWallpaperLease resultLease = InvalidDeliveryKind
                ? new NonPlaybackPageLease(lease)
                : lease;
            var compatibility = CompatibilityObservations.TryDequeue(out var observation)
                ? observation
                : (
                    Presentation: new PresentationContractSnapshot(
                        PresentationContractCatalog.CodexShellId,
                        ContractMatchState.Matched),
                    Capabilities: PresentationContractCatalog.CreateFullySupportedCapabilities());
            if (!IgnoreLockedCompatibility &&
                options.LockedPresentationContract is { } lockedPresentation)
            {
                var rawCapabilities = options.CapabilityCeiling!
                    .DowngradeWith(compatibility.Capabilities);
                if (options.CapabilityState is { } capabilityState)
                {
                    capabilityState.Begin(
                        rawCapabilities,
                        continuesCurrentGeneration: true);
                    rawCapabilities = capabilityState.Current;
                }

                compatibility = (
                    lockedPresentation,
                    rawCapabilities);
            }

            return new DynamicWallpaperActivationResult(
                resultLease,
                compatibility.Presentation,
                compatibility.Capabilities);
        }

        private Exception? TakePauseFailure()
        {
            lock (_sync)
            {
                return _pauseFailures.TryDequeue(out var failure) ? failure : null;
            }
        }
    }

    private sealed class BlockingReadinessPageSessionFactory(List<string> events) :
        IDynamicWallpaperPageSessionFactory,
        IDynamicWallpaperInitialPresentationReadinessSource
    {
        private readonly FakePageSessionFactory _inner = new(events);

        internal TaskCompletionSource ReadinessStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseReadiness { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int PageSessionStartCount => _inner.Leases.Count;

        public async ValueTask<DynamicWallpaperInitialPresentationReadiness>
            WaitForInitialPresentationAsync(
                VerifiedCdpEndpoint endpoint,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            events.Add("readiness");
            ReadinessStarted.TrySetResult();
            await ReleaseReadiness.Task.WaitAsync(cancellationToken);
            return new DynamicWallpaperInitialPresentationReadiness(
                "codex-page",
                new PresentationContractSnapshot(
                    PresentationContractCatalog.CodexShellId,
                    ContractMatchState.Matched),
                PresentationContractCatalog.CreateFullySupportedCapabilities());
        }

        public ValueTask<DynamicWallpaperActivationResult> StartAsync(
            VerifiedCdpEndpoint endpoint,
            EncodedWallpaperStreamBuffer buffer,
            DynamicWallpaperInjectionOptions options,
            CancellationToken cancellationToken = default) =>
            _inner.StartAsync(endpoint, buffer, options, cancellationToken);
    }

    private sealed class RejectingReadinessPageSessionFactory :
        IDynamicWallpaperPageSessionFactory,
        IDynamicWallpaperInitialPresentationReadinessSource
    {
        internal int PageSessionStartCount { get; private set; }

        public ValueTask<DynamicWallpaperInitialPresentationReadiness>
            WaitForInitialPresentationAsync(
                VerifiedCdpEndpoint endpoint,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<DynamicWallpaperInitialPresentationReadiness>(
                new WallpaperPresentationContractException(
                    "fixture global presentation baseline failed"));
        }

        public ValueTask<DynamicWallpaperActivationResult> StartAsync(
            VerifiedCdpEndpoint endpoint,
            EncodedWallpaperStreamBuffer buffer,
            DynamicWallpaperInjectionOptions options,
            CancellationToken cancellationToken = default)
        {
            PageSessionStartCount++;
            return ValueTask.FromException<DynamicWallpaperActivationResult>(
                new InvalidOperationException(
                    "The page session must not start after readiness failure."));
        }
    }

    private sealed class NonPlaybackPageLease(FakePageLease inner) :
        IActiveWallpaperLease
    {
        public long Generation => inner.Generation;

        public ActiveWallpaperDeliveryKind DeliveryKind =>
            ActiveWallpaperDeliveryKind.DynamicStream;

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FakePageLease(
        EncodedWallpaperStreamBuffer buffer,
        long? generationOverride,
        List<string> events,
        bool blockKeyFrameWait,
        bool invalidDeliveryKind,
        Func<bool> shouldFailDispose,
        Func<Exception?> takePauseFailure) :
        IDynamicWallpaperPagePlaybackLease,
        IActiveWallpaperHealthSource
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource KeyFrameWaitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowKeyFrameAcknowledgement { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public long Generation => generationOverride ?? buffer.Descriptor.Generation;

        public ActiveWallpaperDeliveryKind DeliveryKind => invalidDeliveryKind
            ? ActiveWallpaperDeliveryKind.DirectMedia
            : ActiveWallpaperDeliveryKind.DynamicStream;

        public Task Completion => _completion.Task;

        public long LastAcknowledgedKeyFrameSequence { get; private set; } = 1;

        public ValueTask SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"page-pause:{paused}");
            if (takePauseFailure() is { } failure)
            {
                return ValueTask.FromException(failure);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask WaitForBufferedSegmentsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("page-drain");
            return ValueTask.CompletedTask;
        }

        public async ValueTask WaitForKeyFrameAfterAsync(
            long sequence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add($"page-wait:{sequence}");
            KeyFrameWaitStarted.TrySetResult();
            if (blockKeyFrameWait)
            {
                await AllowKeyFrameAcknowledgement.Task.WaitAsync(cancellationToken);
            }

            LastAcknowledgedKeyFrameSequence = sequence + 1;
        }

        public void Fail(DynamicWallpaperCapabilityReasonCode reasonCode) =>
            _completion.TrySetException(new DynamicWallpaperUnavailableException(reasonCode));

        public void Fail(Exception failure) => _completion.TrySetException(failure);

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            events.Add("page-dispose");
            _completion.TrySetCanceled();
            await buffer.DisposeAsync();
            if (shouldFailDispose())
            {
                throw new IOException("fixture page dispose failed");
            }
        }
    }
}
