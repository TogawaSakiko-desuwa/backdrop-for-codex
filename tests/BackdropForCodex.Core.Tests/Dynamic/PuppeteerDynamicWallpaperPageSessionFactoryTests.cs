using System.Reflection;
using System.Threading.Channels;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Runtime;
using PuppeteerSharp;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class PuppeteerDynamicWallpaperPageSessionFactoryTests
{
    private const long Generation = 73;

    [Fact]
    public async Task InitialReadinessWaitsThroughWeakShellSamplesAndKeepsFullCapabilities()
    {
        var events = new List<string>();
        var weakEvidence = new PresentationEvidence(
            GlobalStructure: true,
            ShellStructure: false,
            BackdropFilterSupported: true,
            SelectorHasSupported: true);
        var page = new FakeVerifiedPage(events, weakEvidence);
        page.QueueEvidence(weakEvidence);
        page.QueueEvidence(weakEvidence);
        page.QueueEvidence(PresentationEvidence.FullySupported);
        var connection = new FakeBrowserConnection(events, page);
        await using var factory = Factory(connection, new ManualHeartbeatScheduler());
        var readinessSource = Assert.IsAssignableFrom<
            IDynamicWallpaperInitialPresentationReadinessSource>(factory);

        var readiness = await readinessSource.WaitForInitialPresentationAsync(Endpoint());

        Assert.Equal(PresentationContractCatalog.CodexShellId, readiness.Presentation.ActiveContractId);
        Assert.Equal(
            PresentationContractCatalog.CreateFullySupportedCapabilities(),
            readiness.Capabilities);
        Assert.Equal(3, events.Count(item => item == "evidence"));
        Assert.DoesNotContain("sink", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task InitialReadinessUsesGlobalFallbackOnlyAfterPersistentWeakShellEvidence()
    {
        var events = new List<string>();
        var weakEvidence = new PresentationEvidence(
            GlobalStructure: true,
            ShellStructure: false,
            BackdropFilterSupported: true,
            SelectorHasSupported: true);
        var page = new FakeVerifiedPage(events, weakEvidence);
        var factory = Factory(
            new FakeBrowserConnection(events, page),
            new ManualHeartbeatScheduler(),
            ReadinessGate());
        var readinessSource = Assert.IsAssignableFrom<
            IDynamicWallpaperInitialPresentationReadinessSource>(factory);

        var readiness = await readinessSource.WaitForInitialPresentationAsync(Endpoint());

        Assert.Equal(
            ContractMatchState.NoMatchUsingGlobalBaseline,
            readiness.Presentation.MatchState);
        Assert.True(readiness.Capabilities.Global.IsAvailable);
        Assert.False(readiness.Capabilities.Glass.IsAvailable);
        Assert.False(readiness.Capabilities.Advanced.IsAvailable);
        Assert.DoesNotContain("sink", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task InitialReadinessUsesLastCompletedWeakSampleWhenNextProbeHitsWindowDeadline()
    {
        var events = new List<string>();
        var weakEvidence = new PresentationEvidence(
            GlobalStructure: true,
            ShellStructure: false,
            BackdropFilterSupported: true,
            SelectorHasSupported: true);
        var page = new FakeVerifiedPage(events, weakEvidence)
        {
            EvidenceProvider = (call, token) => call == 2
                ? WaitForCanceledEvidenceAsync(token)
                : ValueTask.FromResult(weakEvidence),
        };
        var factory = Factory(
            new FakeBrowserConnection(events, page),
            new ManualHeartbeatScheduler(),
            ReadinessGate());
        var readinessSource = Assert.IsAssignableFrom<
            IDynamicWallpaperInitialPresentationReadinessSource>(factory);

        var readiness = await readinessSource.WaitForInitialPresentationAsync(Endpoint());

        Assert.Equal(3, page.EvidenceCallCount);
        Assert.Equal(
            ContractMatchState.NoMatchUsingGlobalBaseline,
            readiness.Presentation.MatchState);
        Assert.True(readiness.Capabilities.Global.IsAvailable);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task InitialReadinessRejectsPersistentGlobalBaselineFailure()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.Unavailable);
        var factory = Factory(
            new FakeBrowserConnection(events, page),
            new ManualHeartbeatScheduler(),
            ReadinessGate());
        var readinessSource = Assert.IsAssignableFrom<
            IDynamicWallpaperInitialPresentationReadinessSource>(factory);

        await Assert.ThrowsAsync<WallpaperPresentationContractException>(() =>
            readinessSource.WaitForInitialPresentationAsync(Endpoint()).AsTask());

        Assert.Contains("evidence", events);
        Assert.DoesNotContain("sink", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task InitialReadinessNeverProbesDomWhileTargetsRemainAmbiguous()
    {
        var events = new List<string>();
        var factory = Factory(
            new FakeBrowserConnection(
                events,
                DynamicWallpaperPageVerification.Ambiguous(2)),
            new ManualHeartbeatScheduler(),
            ReadinessGate());
        var readinessSource = Assert.IsAssignableFrom<
            IDynamicWallpaperInitialPresentationReadinessSource>(factory);

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            readinessSource.WaitForInitialPresentationAsync(Endpoint()).AsTask());

        Assert.Equal(
            DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven,
            failure.FailureKind);
        Assert.DoesNotContain("evidence", events);
        Assert.DoesNotContain("sink", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task InitialReadinessRejectsTargetReplacementAfterPinningTheFirstSolePage()
    {
        var events = new List<string>();
        var weakEvidence = new PresentationEvidence(
            GlobalStructure: true,
            ShellStructure: false,
            BackdropFilterSupported: true,
            SelectorHasSupported: true);
        var first = new FakeVerifiedPage(events, weakEvidence)
        {
            StableIdentity = "target-p1",
        };
        var replacement = new FakeVerifiedPage(events, PresentationEvidence.FullySupported)
        {
            StableIdentity = "target-p2",
        };
        var factory = Factory(
            new FakeBrowserConnection(
                events,
                DynamicWallpaperPageVerification.Sole(first),
                DynamicWallpaperPageVerification.Sole(replacement)),
            new ManualHeartbeatScheduler(),
            ReadinessGate());
        var readinessSource = Assert.IsAssignableFrom<
            IDynamicWallpaperInitialPresentationReadinessSource>(factory);

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            readinessSource.WaitForInitialPresentationAsync(Endpoint()).AsTask());

        Assert.Equal(
            DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven,
            failure.FailureKind);
        Assert.Equal(1, events.Count(item => item == "evidence"));
        Assert.DoesNotContain("sink", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task InitialReadinessPrioritizesCallerCancellationAndDisconnects()
    {
        var events = new List<string>();
        var weakEvidence = new PresentationEvidence(
            GlobalStructure: true,
            ShellStructure: false,
            BackdropFilterSupported: true,
            SelectorHasSupported: true);
        var page = new FakeVerifiedPage(events, weakEvidence);
        var factory = Factory(
            new FakeBrowserConnection(events, page),
            new ManualHeartbeatScheduler(),
            new InitialPageReadinessGate(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(10)));
        var readinessSource = Assert.IsAssignableFrom<
            IDynamicWallpaperInitialPresentationReadinessSource>(factory);
        using var cancellation = new CancellationTokenSource();

        var pending = readinessSource
            .WaitForInitialPresentationAsync(Endpoint(), cancellation.Token)
            .AsTask();
        await page.EvidenceObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.DoesNotContain("sink", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task StartPublishesOnlyAfterPageAcknowledgesStartupAndDisconnectsAfterCleanup()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.FullySupported);
        var connection = new FakeBrowserConnection(events, page);
        var scheduler = new ManualHeartbeatScheduler();
        var factory = Factory(connection, scheduler);
        var buffer = BufferWithStartup();

        var result = await factory.StartAsync(
            Endpoint(),
            buffer,
            Options());
        var lease = result.Lease;

        Assert.Equal(Generation, lease.Generation);
        Assert.Equal(ActiveWallpaperDeliveryKind.DynamicStream, lease.DeliveryKind);
        Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(lease);
        Assert.Equal(
            new PresentationContractSnapshot(
                PresentationContractCatalog.CodexShellId,
                ContractMatchState.Matched),
            result.Presentation);
        Assert.Equal(
            PresentationContractCatalog.CreateFullySupportedCapabilities(),
            result.Capabilities);
        Assert.Equal(
            ["connect", "verify:1", "evidence", "verify:1", "sink", "verify:1", "prepare", "append:0", "append:1"],
            events.Take(9));

        await lease.DisposeAsync();

        Assert.True(events.IndexOf("cleanup:73") < events.IndexOf("disconnect"));
        Assert.True(events.IndexOf("sink-dispose") < events.IndexOf("disconnect"));
        Assert.Equal(1, connection.DisconnectCount);
    }

    [Fact]
    public async Task StartFailsClosedForAmbiguousPageAndReleasesOwnedBuffer()
    {
        var events = new List<string>();
        var connection = new FakeBrowserConnection(
            events,
            DynamicWallpaperPageVerification.Ambiguous(2));
        var buffer = BufferWithStartup();
        var factory = Factory(connection, new ManualHeartbeatScheduler());

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            factory.StartAsync(Endpoint(), buffer, Options()).AsTask());

        Assert.Equal(["connect", "verify:2", "disconnect"], events);
        Assert.Throws<ObjectDisposedException>(() =>
            buffer.TryWrite(Media(sequence: 2, isKeyFrame: false)));
    }

    [Fact]
    public async Task StartFailsClosedWhenPresentationGlobalBaselineIsUnavailable()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.Unavailable);
        var connection = new FakeBrowserConnection(events, page);
        var buffer = BufferWithStartup();
        var factory = Factory(connection, new ManualHeartbeatScheduler());

        await Assert.ThrowsAsync<WallpaperPresentationContractException>(() =>
            factory.StartAsync(Endpoint(), buffer, Options()).AsTask());

        Assert.DoesNotContain("sink", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task PublicLockedStartCannotGraceAnUnavailableGlobalBaseline()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.Unavailable);
        var factory = Factory(
            new FakeBrowserConnection(events, page),
            new ManualHeartbeatScheduler());
        var options = new DynamicWallpaperInjectionOptions(
            Generation,
            lockedPresentationContract: new PresentationContractSnapshot(
                PresentationContractCatalog.CodexShellId,
                ContractMatchState.Matched),
            capabilityCeiling: PresentationContractCatalog.CreateFullySupportedCapabilities());

        await Assert.ThrowsAsync<WallpaperPresentationContractException>(() =>
            factory.StartAsync(Endpoint(), BufferWithStartup(), options).AsTask());

        Assert.DoesNotContain("sink", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task PublicLockedStartAppliesOptionalStructuralMissesStrictly()
    {
        var events = new List<string>();
        var weakEvidence = new PresentationEvidence(
            GlobalStructure: true,
            ShellStructure: true,
            BackdropFilterSupported: false,
            SelectorHasSupported: false);
        var page = new FakeVerifiedPage(events, weakEvidence);
        var factory = Factory(
            new FakeBrowserConnection(events, page),
            new ManualHeartbeatScheduler());
        var options = new DynamicWallpaperInjectionOptions(
            Generation,
            lockedPresentationContract: new PresentationContractSnapshot(
                PresentationContractCatalog.CodexShellId,
                ContractMatchState.Matched),
            capabilityCeiling: PresentationContractCatalog.CreateFullySupportedCapabilities());

        var result = await factory.StartAsync(Endpoint(), BufferWithStartup(), options);

        Assert.False(result.Capabilities.Glass.IsAvailable);
        Assert.False(result.Capabilities.Advanced.IsAvailable);
        Assert.Same(result.Capabilities, page.SinkCapabilities);
        await result.Lease.DisposeAsync();
    }

    [Fact]
    public async Task PreparedStartRejectsAReplacementTargetBeforeObservingOrMutatingIt()
    {
        var events = new List<string>();
        var replacement = new FakeVerifiedPage(
            events,
            PresentationEvidence.FullySupported)
        {
            StableIdentity = "replacement-target",
        };
        var factory = Factory(
            new FakeBrowserConnection(events, replacement),
            new ManualHeartbeatScheduler());

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            factory.StartAsync(
                Endpoint(),
                BufferWithStartup(),
                InternalLockedOptions(expectedPageIdentity: "preflight-target")).AsTask());

        Assert.Equal(
            DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven,
            failure.FailureKind);
        Assert.DoesNotContain("evidence", events);
        Assert.DoesNotContain("sink", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task PreparedStartRejectsCurrentGlobalFailureWithoutRecoveryGrace()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.Unavailable);
        var factory = Factory(
            new FakeBrowserConnection(events, page),
            new ManualHeartbeatScheduler());

        await Assert.ThrowsAsync<WallpaperPresentationContractException>(() =>
            factory.StartAsync(
                Endpoint(),
                BufferWithStartup(),
                InternalLockedOptions(
                    requireCurrentGlobalBaseline: true,
                    expectedPageIdentity: page.StableIdentity)).AsTask());

        Assert.DoesNotContain("sink", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task RecoveryKeepsLockedGlobalContractAndCapabilityCeilingForStrongShellEvidence()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.FullySupported);
        var connection = new FakeBrowserConnection(events, page);
        var factory = Factory(connection, new ManualHeartbeatScheduler());
        var presentation = new PresentationContractSnapshot(
            PresentationContractCatalog.GlobalBaselineId,
            ContractMatchState.NoMatchUsingGlobalBaseline);
        var capabilityCeiling = PresentationContractCatalog.Match(
            new PresentationEvidence(
                GlobalStructure: true,
                ShellStructure: false,
                BackdropFilterSupported: true,
                SelectorHasSupported: true),
            finalizeBaselineFallback: true).Capabilities;

        var result = await factory.StartAsync(
            Endpoint(),
            BufferWithStartup(),
            new DynamicWallpaperInjectionOptions(
                Generation,
                lockedPresentationContract: presentation,
                capabilityCeiling: capabilityCeiling));

        Assert.Equal(presentation, result.Presentation);
        Assert.Equal(capabilityCeiling, result.Capabilities);
        Assert.False(result.Capabilities.Glass.IsAvailable);
        Assert.False(result.Capabilities.Advanced.IsAvailable);
        Assert.Same(result.Capabilities, page.SinkCapabilities);

        await result.Lease.DisposeAsync();
    }

    [Fact]
    public async Task RecoveryConfirmsStructuralFailuresBeforeDowngradingLockedShellCapabilities()
    {
        var presentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            ContractMatchState.Matched);
        var capabilityCeiling = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var options = InternalLockedOptions(presentation, capabilityCeiling);
        var weakEvidence = new PresentationEvidence(
            GlobalStructure: true,
            ShellStructure: true,
            BackdropFilterSupported: false,
            SelectorHasSupported: false);

        Assert.Equal(
            capabilityCeiling,
            await StartRecoveryAttemptAsync(
                weakEvidence,
                options));
        Assert.Equal(
            capabilityCeiling,
            await StartRecoveryAttemptAsync(
                weakEvidence,
                options));
        var capabilities = await StartRecoveryAttemptAsync(
            weakEvidence,
            options);

        Assert.True(capabilities.Global.IsAvailable);
        Assert.False(capabilities.Glass.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            capabilities.Glass.ReasonCode);
        Assert.False(capabilities.Advanced.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            capabilities.Advanced.ReasonCode);
    }

    [Fact]
    public async Task RecoveryPositiveEvidenceResetsTheStructuralFailureStreak()
    {
        var presentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            ContractMatchState.Matched);
        var capabilityCeiling = PresentationContractCatalog.CreateFullySupportedCapabilities();
        var options = InternalLockedOptions(presentation, capabilityCeiling);
        var weakEvidence = new PresentationEvidence(
            GlobalStructure: true,
            ShellStructure: true,
            BackdropFilterSupported: false,
            SelectorHasSupported: false);

        Assert.Equal(
            capabilityCeiling,
            await StartRecoveryAttemptAsync(
                weakEvidence,
                options));
        Assert.Equal(
            capabilityCeiling,
            await StartRecoveryAttemptAsync(
                PresentationEvidence.FullySupported,
                options));
        Assert.Equal(
            capabilityCeiling,
            await StartRecoveryAttemptAsync(
                weakEvidence,
                options));
        Assert.Equal(
            capabilityCeiling,
            await StartRecoveryAttemptAsync(
                weakEvidence,
                options));
        var capabilities = await StartRecoveryAttemptAsync(
            weakEvidence,
            options);

        Assert.False(capabilities.Glass.IsAvailable);
        Assert.False(capabilities.Advanced.IsAvailable);
    }

    [Fact]
    public async Task RuntimeRecoveryConfirmsGlobalStructuralFailureBeforeRejectingInjection()
    {
        var options = InternalLockedOptions();
        var full = PresentationContractCatalog.CreateFullySupportedCapabilities();

        Assert.Equal(
            full,
            await StartRecoveryAttemptAsync(PresentationEvidence.Unavailable, options));
        Assert.Equal(
            full,
            await StartRecoveryAttemptAsync(PresentationEvidence.Unavailable, options));

        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.Unavailable);
        await using var factory = Factory(
            new FakeBrowserConnection(events, page),
            new ManualHeartbeatScheduler());

        await Assert.ThrowsAsync<WallpaperPresentationContractException>(() =>
            factory.StartAsync(Endpoint(), BufferWithStartup(), options).AsTask());

        Assert.DoesNotContain("sink", events);
    }

    [Fact]
    public async Task FailedThirdWeakStartDoesNotCommitOptionalDowngradeBeforePositiveRetry()
    {
        var options = InternalLockedOptions();
        var weakEvidence = new PresentationEvidence(
            GlobalStructure: true,
            ShellStructure: true,
            BackdropFilterSupported: false,
            SelectorHasSupported: false);
        var full = PresentationContractCatalog.CreateFullySupportedCapabilities();

        Assert.Equal(full, await StartRecoveryAttemptAsync(weakEvidence, options));
        Assert.Equal(full, await StartRecoveryAttemptAsync(weakEvidence, options));

        var failedEvents = new List<string>();
        var failedPage = new FakeVerifiedPage(failedEvents, weakEvidence)
        {
            EncodedStreamSink = new FailingPrepareSink(failedEvents),
        };
        await using (var factory = Factory(
                         new FakeBrowserConnection(failedEvents, failedPage),
                         new ManualHeartbeatScheduler()))
        {
            await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
                factory.StartAsync(Endpoint(), BufferWithStartup(), options).AsTask());
        }

        var capabilities = await StartRecoveryAttemptAsync(
            PresentationEvidence.FullySupported,
            options);

        Assert.Equal(full, capabilities);
        Assert.True(capabilities.Glass.IsAvailable);
        Assert.True(capabilities.Advanced.IsAvailable);
    }

    [Fact]
    public async Task StartPreservesTypedDynamicStartupFailureForTierFallback()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.FullySupported);
        var connection = new FakeBrowserConnection(events, page);
        var buffer = new EncodedWallpaperStreamBuffer(new EncodedWallpaperStreamDescriptor(
            Generation,
            "video/mp4; codecs=\"avc1.640028\"",
            width: 1920,
            height: 1080,
            frameRate: 30));
        buffer.Complete(new DynamicWallpaperUnavailableException(
            DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable));
        var factory = Factory(connection, new ManualHeartbeatScheduler());

        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            factory.StartAsync(Endpoint(), buffer, Options()).AsTask());

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable,
            failure.ReasonCode);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task StartSanitizesUnexpectedBrowserFailureWithoutRetainingCdpDetails()
    {
        const string sensitiveDetail =
            "ws://127.0.0.1:9222/devtools/browser/private-target C:\\Users\\person\\session.json";
        var sourceFailure = new IOException(sensitiveDetail);
        var factory = new PuppeteerDynamicWallpaperPageSessionFactory(
            new ThrowingBrowserConnector(sourceFailure),
            new ManualHeartbeatScheduler());

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            factory.StartAsync(Endpoint(), BufferWithStartup(), Options()).AsTask());

        Assert.Null(failure.InnerException);
        Assert.Equal(nameof(IOException), failure.PrimaryDiagnosticExceptionType);
        Assert.Equal(sourceFailure.HResult, failure.PrimaryDiagnosticHResult);
        Assert.Null(failure.CleanupDiagnosticExceptionType);
        Assert.Null(failure.CleanupDiagnosticHResult);
        Assert.DoesNotContain(sensitiveDetail, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-target", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartRevalidatesTheSameSoleTargetImmediatelyBeforePageMutation()
    {
        var events = new List<string>();
        var authorized = new FakeVerifiedPage(events, PresentationEvidence.FullySupported);
        var replacement = new FakeVerifiedPage(events, PresentationEvidence.FullySupported);
        var connection = new FakeBrowserConnection(
            events,
            DynamicWallpaperPageVerification.Sole(authorized),
            DynamicWallpaperPageVerification.Sole(authorized),
            DynamicWallpaperPageVerification.Sole(replacement));
        var buffer = BufferWithStartup();
        var factory = Factory(connection, new ManualHeartbeatScheduler());

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            factory.StartAsync(Endpoint(), buffer, Options()).AsTask());

        Assert.Equal(
            DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven,
            failure.FailureKind);
        Assert.DoesNotContain("prepare", events);
        Assert.Equal("disconnect", events[^1]);
    }

    [Fact]
    public async Task SingleRejectedHeartbeatDoesNotCompleteHealth()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(
            events,
            PresentationEvidence.FullySupported)
        {
            HeartbeatResult = false,
        };
        var connection = new FakeBrowserConnection(events, page);
        var scheduler = new ManualHeartbeatScheduler();
        var factory = Factory(connection, scheduler);
        var lease = (await factory.StartAsync(
            Endpoint(),
            BufferWithStartup(),
            Options())).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(lease);
        await scheduler.WaitUntilArmedAsync(1, TimeSpan.FromSeconds(5));

        scheduler.Pulse();

        await scheduler.WaitUntilArmedAsync(2, TimeSpan.FromSeconds(1));
        Assert.False(health.Completion.IsCompleted);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task SingleTransportFailureDoesNotCompleteHealth()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.FullySupported);
        var connection = new FakeBrowserConnection(events, page);
        var scheduler = new ManualHeartbeatScheduler();
        var factory = Factory(connection, scheduler);
        var lease = (await factory.StartAsync(
            Endpoint(),
            BufferWithStartup(),
            Options())).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(lease);
        await scheduler.WaitUntilArmedAsync(1, TimeSpan.FromSeconds(5));
        connection.FailNextVerification(new IOException("fixture transport failure"));

        scheduler.Pulse();

        await scheduler.WaitUntilArmedAsync(2, TimeSpan.FromSeconds(1));
        Assert.False(health.Completion.IsCompleted);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task SingleMissingPageObservationDoesNotCompleteHealth()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.FullySupported);
        var connection = new FakeBrowserConnection(events, page);
        var scheduler = new ManualHeartbeatScheduler();
        var factory = Factory(connection, scheduler);
        var lease = (await factory.StartAsync(
            Endpoint(),
            BufferWithStartup(),
            Options())).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(lease);
        await scheduler.WaitUntilArmedAsync(1, TimeSpan.FromSeconds(5));
        connection.QueueVerification(DynamicWallpaperPageVerification.None);

        scheduler.Pulse();

        await scheduler.WaitUntilArmedAsync(2, TimeSpan.FromSeconds(1));
        Assert.False(health.Completion.IsCompleted);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task CompletionFaultsWhenHeartbeatFindsPageAmbiguity()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.FullySupported);
        var connection = new FakeBrowserConnection(
            events,
            DynamicWallpaperPageVerification.Sole(page),
            DynamicWallpaperPageVerification.Sole(page),
            DynamicWallpaperPageVerification.Sole(page),
            DynamicWallpaperPageVerification.Ambiguous(2));
        var scheduler = new ManualHeartbeatScheduler();
        var factory = Factory(connection, scheduler);
        var lease = (await factory.StartAsync(
            Endpoint(),
            BufferWithStartup(),
            Options())).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(lease);

        scheduler.Pulse();

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            health.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven,
            failure.FailureKind);
        await lease.DisposeAsync();
        Assert.Contains("cleanup:73", events);
        Assert.Equal(1, connection.DisconnectCount);
    }

    [Fact]
    public async Task CompletionFaultsWhenHeartbeatFindsPageIdentityReplacement()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.FullySupported);
        var replacement = new FakeVerifiedPage(events, PresentationEvidence.FullySupported);
        var connection = new FakeBrowserConnection(events, page);
        var scheduler = new ManualHeartbeatScheduler();
        var factory = Factory(connection, scheduler);
        var lease = (await factory.StartAsync(
            Endpoint(),
            BufferWithStartup(),
            Options())).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(lease);
        await scheduler.WaitUntilArmedAsync(1, TimeSpan.FromSeconds(5));
        connection.QueueVerification(DynamicWallpaperPageVerification.Sole(replacement));

        scheduler.Pulse();

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            health.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven,
            failure.FailureKind);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulHeartbeatResetsTheConsecutiveFailureWindow()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(
            events,
            PresentationEvidence.FullySupported)
        {
            HeartbeatResult = false,
        };
        var connection = new FakeBrowserConnection(events, page);
        var scheduler = new ManualHeartbeatScheduler();
        var factory = Factory(connection, scheduler);
        var lease = (await factory.StartAsync(
            Endpoint(),
            BufferWithStartup(),
            Options())).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(lease);
        await scheduler.WaitUntilArmedAsync(1, TimeSpan.FromSeconds(5));

        scheduler.Pulse();
        await scheduler.WaitUntilArmedAsync(2, TimeSpan.FromSeconds(1));
        scheduler.Pulse();
        await scheduler.WaitUntilArmedAsync(3, TimeSpan.FromSeconds(1));

        page.HeartbeatResult = true;
        scheduler.Pulse();
        await scheduler.WaitUntilArmedAsync(4, TimeSpan.FromSeconds(1));

        page.HeartbeatResult = false;
        scheduler.Pulse();
        await scheduler.WaitUntilArmedAsync(5, TimeSpan.FromSeconds(1));
        scheduler.Pulse();
        await scheduler.WaitUntilArmedAsync(6, TimeSpan.FromSeconds(1));

        Assert.False(health.Completion.IsCompleted);
        scheduler.Pulse();
        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            health.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task CompletionFaultsAfterThreeConsecutiveRejectedHeartbeats()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(
            events,
            PresentationEvidence.FullySupported)
        {
            HeartbeatResult = false,
        };
        var connection = new FakeBrowserConnection(events, page);
        var scheduler = new ManualHeartbeatScheduler();
        var factory = Factory(connection, scheduler);
        var lease = (await factory.StartAsync(
            Endpoint(),
            BufferWithStartup(),
            Options())).Lease;
        var health = Assert.IsAssignableFrom<IActiveWallpaperHealthSource>(lease);
        await scheduler.WaitUntilArmedAsync(1, TimeSpan.FromSeconds(5));

        scheduler.Pulse();
        await scheduler.WaitUntilArmedAsync(2, TimeSpan.FromSeconds(1));
        Assert.False(health.Completion.IsCompleted);
        scheduler.Pulse();
        await scheduler.WaitUntilArmedAsync(3, TimeSpan.FromSeconds(1));
        Assert.False(health.Completion.IsCompleted);
        scheduler.Pulse();

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            health.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, events.Count(item => item == "heartbeat:73"));
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task CleanupDeadlineRetainsTheVerifiedConnectionUntilRetrySucceeds()
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, PresentationEvidence.FullySupported)
        {
            EncodedStreamSink = new BlockingCleanupSink(events),
        };
        var connection = new FakeBrowserConnection(events, page);
        var factory = Factory(connection, new ManualHeartbeatScheduler());
        var lease = (await factory.StartAsync(
            Endpoint(),
            BufferWithStartup(),
            Options())).Lease;

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));

        Assert.DoesNotContain("sink-dispose", events);
        Assert.Equal(0, connection.DisconnectCount);

        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(2, events.Count(item => item == "cleanup:73"));
        Assert.True(events.IndexOf("sink-dispose") < events.IndexOf("disconnect"));
        Assert.Equal(1, connection.DisconnectCount);
    }

    [Fact]
    public async Task FailedStartAfterPageMutationRetainsConnectionUntilFactoryCleanupRetries()
    {
        var events = new List<string>();
        var sink = new BlockingCleanupSink(events) { FailKeyFrameAppend = true };
        var page = new FakeVerifiedPage(events, PresentationEvidence.FullySupported)
        {
            EncodedStreamSink = sink,
        };
        var connection = new FakeBrowserConnection(events, page);
        var factory = Factory(connection, new ManualHeartbeatScheduler());

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            factory.StartAsync(Endpoint(), BufferWithStartup(), Options())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(4)));

        Assert.Null(failure.InnerException);
        Assert.Equal(nameof(IOException), failure.PrimaryDiagnosticExceptionType);
        Assert.Equal(
            nameof(DynamicWallpaperPageSessionException),
            failure.CleanupDiagnosticExceptionType);
        Assert.DoesNotContain(
            "fixture key frame append failed",
            failure.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("prepare", events);
        Assert.Contains("append:0", events);
        Assert.Contains("append:1", events);
        Assert.DoesNotContain("sink-dispose", events);
        Assert.Equal(0, connection.DisconnectCount);

        var cleanupOwner = Assert.IsAssignableFrom<IAsyncDisposable>(factory);
        await cleanupOwner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(2, events.Count(item => item == "cleanup:73"));
        Assert.True(events.IndexOf("sink-dispose") < events.IndexOf("disconnect"));
        Assert.Equal(1, connection.DisconnectCount);
    }

    [Fact]
    public async Task RetainedPageCleanupBlocksReconnectAndReleasesTheIncomingOwnedBuffer()
    {
        var events = new List<string>();
        var sink = new BlockingCleanupSink(events)
        {
            CleanupTimeouts = 2,
            FailKeyFrameAppend = true,
        };
        var page = new FakeVerifiedPage(events, PresentationEvidence.FullySupported)
        {
            EncodedStreamSink = sink,
        };
        var connection = new FakeBrowserConnection(events, page);
        var factory = Factory(connection, new ManualHeartbeatScheduler());

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            factory.StartAsync(Endpoint(), BufferWithStartup(), Options())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(4)));
        var blockedBuffer = BufferWithStartup();

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            factory.StartAsync(Endpoint(), blockedBuffer, Options())
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(4)));

        Assert.Equal(1, events.Count(item => item == "connect"));
        Assert.Equal(0, connection.DisconnectCount);
        Assert.Throws<ObjectDisposedException>(() =>
            blockedBuffer.TryWrite(Media(sequence: 2, isKeyFrame: false)));

        await factory.DisposeAsync();
        Assert.Equal(1, connection.DisconnectCount);
    }

    [Fact]
    public async Task ProductionConnectionDoesNotOverlapSelectorScans()
    {
        var (browser, browserControl) = BlockingBrowserProxy.Create();
        var connection = new PuppeteerDynamicWallpaperBrowserConnection(
            browser,
            Endpoint());
        var firstVerification = connection.VerifySolePageAsync().AsTask();
        Assert.Equal(1, browserControl.PagesCallCount);
        var secondVerification = connection.VerifySolePageAsync().AsTask();

        try
        {
            Assert.Equal(1, browserControl.PagesCallCount);
        }
        finally
        {
            browserControl.ReleasePages();
        }

        var results = await Task.WhenAll(firstVerification, secondVerification)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal(0, result.EligiblePageCount));
        Assert.Equal(2, browserControl.PagesCallCount);
    }

    [Fact]
    public async Task ProductionConnectionWaitsForActiveSelectorBeforeDisconnecting()
    {
        var (browser, browserControl) = BlockingBrowserProxy.Create();
        var connection = new PuppeteerDynamicWallpaperBrowserConnection(
            browser,
            Endpoint());
        var verification = connection.VerifySolePageAsync().AsTask();
        Assert.Equal(1, browserControl.PagesCallCount);

        var disconnect = connection.DisconnectAsync().AsTask();
        Assert.False(disconnect.IsCompleted);
        Assert.Equal(0, browserControl.DisconnectCount);

        browserControl.ReleasePages();
        await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, browserControl.DisconnectCount);
        Assert.Equal(0, (await verification).EligiblePageCount);

        var afterDisconnect = await connection.VerifySolePageAsync();
        Assert.Equal(0, afterDisconnect.EligiblePageCount);
        Assert.Equal(1, browserControl.PagesCallCount);
    }

    [Fact]
    public async Task ProductionConnectionSerializesCleanupVerificationWithSelectorScan()
    {
        var (browser, browserControl) = BlockingBrowserProxy.Create();
        var connection = new PuppeteerDynamicWallpaperBrowserConnection(
            browser,
            Endpoint());
        var verification = connection.VerifySolePageAsync().AsTask();
        var unownedPage = new FakeVerifiedPage(
            [],
            PresentationEvidence.FullySupported);
        var cleanupVerification = connection
            .IsPageStillVerifiedAsync(unownedPage)
            .AsTask();

        Assert.False(cleanupVerification.IsCompleted);
        browserControl.ReleasePages();

        Assert.False(await cleanupVerification.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, (await verification).EligiblePageCount);
        Assert.Equal(1, browserControl.PagesCallCount);
    }

    private static PuppeteerDynamicWallpaperPageSessionFactory Factory(
        FakeBrowserConnection connection,
        IDynamicWallpaperHeartbeatScheduler scheduler) =>
        new(new FakeBrowserConnector(connection), scheduler);

    private static PuppeteerDynamicWallpaperPageSessionFactory Factory(
        FakeBrowserConnection connection,
        IDynamicWallpaperHeartbeatScheduler scheduler,
        InitialPageReadinessGate readinessGate) =>
        new(new FakeBrowserConnector(connection), scheduler, readinessGate);

    private static InitialPageReadinessGate ReadinessGate() =>
        new(TimeSpan.FromMilliseconds(35), TimeSpan.FromMilliseconds(1));

    private static async ValueTask<PresentationEvidence> WaitForCanceledEvidenceAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return PresentationEvidence.Unavailable;
    }

    private static EncodedWallpaperStreamBuffer BufferWithStartup()
    {
        var buffer = new EncodedWallpaperStreamBuffer(new EncodedWallpaperStreamDescriptor(
            Generation,
            "video/mp4; codecs=\"avc1.640028\"",
            width: 1920,
            height: 1080,
            frameRate: 30));
        Assert.Equal(
            EncodedWallpaperWriteResult.Accepted,
            buffer.TryWrite(Initialization(sequence: 0)));
        Assert.Equal(
            EncodedWallpaperWriteResult.Accepted,
            buffer.TryWrite(Media(sequence: 1, isKeyFrame: true)));
        return buffer;
    }

    private static async Task<CompatibilityCapabilities> StartRecoveryAttemptAsync(
        PresentationEvidence evidence,
        DynamicWallpaperInjectionOptions options)
    {
        var events = new List<string>();
        var page = new FakeVerifiedPage(events, evidence);
        var connection = new FakeBrowserConnection(events, page);
        await using var factory = Factory(connection, new ManualHeartbeatScheduler());
        var result = await factory.StartAsync(
            Endpoint(),
            BufferWithStartup(),
            options);
        try
        {
            Assert.Equal(options.LockedPresentationContract, result.Presentation);
            Assert.Same(result.Capabilities, page.SinkCapabilities);
            return result.Capabilities;
        }
        finally
        {
            await result.Lease.DisposeAsync();
        }
    }

    private static DynamicWallpaperInjectionOptions Options() => new(Generation);

    private static DynamicWallpaperInjectionOptions InternalLockedOptions(
        bool requireCurrentGlobalBaseline = false,
        string? expectedPageIdentity = null)
    {
        var presentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            ContractMatchState.Matched);
        var capabilities = PresentationContractCatalog.CreateFullySupportedCapabilities();
        return InternalLockedOptions(
            presentation,
            capabilities,
            requireCurrentGlobalBaseline,
            expectedPageIdentity);
    }

    private static DynamicWallpaperInjectionOptions InternalLockedOptions(
        PresentationContractSnapshot presentation,
        CompatibilityCapabilities capabilities,
        bool requireCurrentGlobalBaseline = false,
        string? expectedPageIdentity = null)
    {
        var state = new InjectionCapabilityState();
        state.Begin(capabilities, continuesCurrentGeneration: false);
        return new DynamicWallpaperInjectionOptions(
            Generation,
            WallpaperObjectFit.Cover,
            mediaOpacity: 1,
            glass: null,
            composition: null,
            presentation,
            capabilities,
            state,
            requireCurrentGlobalBaseline,
            expectedPageIdentity);
    }

    private static EncodedWallpaperSegment Initialization(long sequence) =>
        new(
            Generation,
            sequence,
            EncodedWallpaperSegmentKind.Initialization,
            isKeyFrame: false,
            new byte[] { 1 });

    private static EncodedWallpaperSegment Media(long sequence, bool isKeyFrame) =>
        new(
            Generation,
            sequence,
            EncodedWallpaperSegmentKind.Media,
            isKeyFrame,
            new byte[] { 2 });

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
                "ws://127.0.0.1:9222/devtools/browser/browser-id"),
            new Uri("ws://127.0.0.1:9222/devtools/browser/browser-id"),
            [new ClassifiedCdpTarget(target, CdpTargetClassification.CodexPage)],
            identity);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy generates a runtime subtype of this fixture.")]
    private class BlockingBrowserProxy : DispatchProxy
    {
        private readonly TaskCompletionSource<IPage[]> _pages =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disconnectCount;
        private int _pagesCallCount;

        public int DisconnectCount => Volatile.Read(ref _disconnectCount);

        public int PagesCallCount => Volatile.Read(ref _pagesCallCount);

        internal static (IBrowser Browser, BlockingBrowserProxy Control) Create()
        {
            var browser = DispatchProxy.Create<IBrowser, BlockingBrowserProxy>();
            return (browser, (BlockingBrowserProxy)(object)browser);
        }

        internal void ReleasePages() => _pages.TrySetResult([]);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return targetMethod.Name switch
            {
                "get_IsConnected" => DisconnectCount == 0,
                "PagesAsync" => CountAndGetPagesAsync(),
                "Disconnect" => CountDisconnect(),
                _ => throw new NotSupportedException(
                    $"The browser fixture does not implement {targetMethod.Name}."),
            };
        }

        private Task<IPage[]> CountAndGetPagesAsync()
        {
            Interlocked.Increment(ref _pagesCallCount);
            return _pages.Task;
        }

        private object? CountDisconnect()
        {
            Interlocked.Increment(ref _disconnectCount);
            return null;
        }
    }

    private sealed class FakeBrowserConnector : IDynamicWallpaperBrowserConnector
    {
        private readonly FakeBrowserConnection _connection;

        public FakeBrowserConnector(FakeBrowserConnection connection) =>
            _connection = connection;

        public ValueTask<IDynamicWallpaperBrowserConnection> ConnectAsync(
            VerifiedCdpEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            Assert.NotNull(endpoint);
            _connection.Events.Add("connect");
            return ValueTask.FromResult<IDynamicWallpaperBrowserConnection>(_connection);
        }
    }

    private sealed class ThrowingBrowserConnector : IDynamicWallpaperBrowserConnector
    {
        private readonly Exception _failure;

        internal ThrowingBrowserConnector(Exception failure) =>
            _failure = failure ?? throw new ArgumentNullException(nameof(failure));

        public ValueTask<IDynamicWallpaperBrowserConnection> ConnectAsync(
            VerifiedCdpEndpoint endpoint,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<IDynamicWallpaperBrowserConnection>(_failure);
        }
    }

    private sealed class FakeBrowserConnection : IDynamicWallpaperBrowserConnection
    {
        private readonly object _sync = new();
        private readonly Queue<DynamicWallpaperPageVerification> _verifications;
        private readonly Queue<Exception> _verificationFailures = new();
        private DynamicWallpaperPageVerification _lastVerification;

        public FakeBrowserConnection(List<string> events, FakeVerifiedPage page)
            : this(events, DynamicWallpaperPageVerification.Sole(page))
        {
        }

        public FakeBrowserConnection(
            List<string> events,
            params DynamicWallpaperPageVerification[] verifications)
        {
            Events = events;
            _verifications = new Queue<DynamicWallpaperPageVerification>(verifications);
            _lastVerification = verifications[^1];
        }

        public List<string> Events { get; }

        public int DisconnectCount { get; private set; }

        public void FailNextVerification(Exception failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            lock (_sync)
            {
                _verificationFailures.Enqueue(failure);
            }
        }

        public void QueueVerification(DynamicWallpaperPageVerification verification)
        {
            ArgumentNullException.ThrowIfNull(verification);
            lock (_sync)
            {
                _verifications.Enqueue(verification);
            }
        }

        public ValueTask<DynamicWallpaperPageVerification> VerifySolePageAsync(
            CancellationToken cancellationToken = default)
        {
            Exception? failure = null;
            DynamicWallpaperPageVerification verification;
            lock (_sync)
            {
                if (_verificationFailures.TryDequeue(out var queuedFailure))
                {
                    failure = queuedFailure;
                }

                if (failure is null &&
                    _verifications.TryDequeue(out var queuedVerification))
                {
                    _lastVerification = queuedVerification;
                }

                verification = _lastVerification;
            }

            if (failure is not null)
            {
                Events.Add("verify:transport-failed");
                throw failure;
            }

            Events.Add($"verify:{verification.EligiblePageCount}");
            return ValueTask.FromResult(verification);
        }

        public ValueTask<bool> IsPageStillVerifiedAsync(
            IDynamicWallpaperVerifiedPage page,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask DisconnectAsync()
        {
            DisconnectCount++;
            Events.Add("disconnect");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeVerifiedPage : IDynamicWallpaperVerifiedPage
    {
        private readonly List<string> _events;
        private readonly Queue<PresentationEvidence> _evidence = new();
        private PresentationEvidence _lastEvidence;

        public FakeVerifiedPage(
            List<string> events,
            PresentationEvidence evidence)
        {
            _events = events;
            _lastEvidence = evidence;
        }

        public string StableIdentity { get; init; } = "codex-page";

        public bool HeartbeatResult { get; set; } = true;

        public IEncodedWallpaperPageSink? EncodedStreamSink { get; init; }

        public CompatibilityCapabilities? SinkCapabilities { get; private set; }

        public Func<int, CancellationToken, ValueTask<PresentationEvidence>>?
            EvidenceProvider
        { get; init; }

        public int EvidenceCallCount { get; private set; }

        internal TaskCompletionSource EvidenceObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void QueueEvidence(PresentationEvidence evidence) => _evidence.Enqueue(evidence);

        public ValueTask<PresentationEvidence> ObservePresentationEvidenceAsync(
            CancellationToken cancellationToken = default)
        {
            _events.Add("evidence");
            EvidenceObserved.TrySetResult();
            EvidenceCallCount++;
            if (EvidenceProvider is not null)
            {
                return EvidenceProvider(EvidenceCallCount, cancellationToken);
            }

            if (_evidence.TryDequeue(out var evidence))
            {
                _lastEvidence = evidence;
            }

            return ValueTask.FromResult(_lastEvidence);
        }

        public IEncodedWallpaperPageSink CreateEncodedStreamSink(
            CompatibilityCapabilities capabilities)
        {
            Assert.True(capabilities.GlobalBackground.IsAvailable);
            SinkCapabilities = capabilities;
            _events.Add("sink");
            return EncodedStreamSink ?? new RecordingSink(_events);
        }

        public ValueTask<bool> HeartbeatAsync(
            long generation,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"heartbeat:{generation}");
            return ValueTask.FromResult(HeartbeatResult);
        }
    }

    private sealed class RecordingSink : IEncodedWallpaperPageSink
    {
        private readonly List<string> _events;

        public RecordingSink(List<string> events) => _events = events;

        public Task<EncodedWallpaperPrepareReceipt> PrepareAsync(
            DynamicWallpaperInjectionOptions options,
            EncodedWallpaperStreamDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            _events.Add("prepare");
            return Task.FromResult(new EncodedWallpaperPrepareReceipt(
                Prepared: true,
                Reason: "prepared",
                descriptor.Generation));
        }

        public Task<EncodedWallpaperAppendReceipt> AppendAsync(
            EncodedWallpaperSegment segment,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"append:{segment.Sequence}");
            return Task.FromResult(new EncodedWallpaperAppendReceipt(
                Appended: true,
                Published: segment.IsKeyFrame,
                Reason: segment.IsKeyFrame ? "published" : "appended",
                segment.Sequence));
        }

        public Task<bool> SetPausedAsync(
            long generation,
            bool paused,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add($"paused:{generation}:{paused}");
            return Task.FromResult(true);
        }

        public Task<bool> CleanupAsync(
            long generation,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"cleanup:{generation}");
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            _events.Add("sink-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingPrepareSink(List<string> events) :
        IEncodedWallpaperPageSink
    {
        public Task<EncodedWallpaperPrepareReceipt> PrepareAsync(
            DynamicWallpaperInjectionOptions options,
            EncodedWallpaperStreamDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            events.Add("prepare-failed");
            return Task.FromException<EncodedWallpaperPrepareReceipt>(
                new IOException("fixture prepare failed"));
        }

        public Task<EncodedWallpaperAppendReceipt> AppendAsync(
            EncodedWallpaperSegment segment,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Append must not run after prepare failure.");

        public Task<bool> SetPausedAsync(
            long generation,
            bool paused,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pause must not run after prepare failure.");

        public Task<bool> CleanupAsync(
            long generation,
            CancellationToken cancellationToken = default)
        {
            events.Add($"cleanup:{generation}");
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            events.Add("sink-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingCleanupSink : IEncodedWallpaperPageSink
    {
        private readonly List<string> _events;
        private int _cleanupCalls;

        internal BlockingCleanupSink(List<string> events) => _events = events;

        internal bool FailKeyFrameAppend { get; init; }

        internal int CleanupTimeouts { get; init; } = 1;

        public Task<EncodedWallpaperPrepareReceipt> PrepareAsync(
            DynamicWallpaperInjectionOptions options,
            EncodedWallpaperStreamDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            _events.Add("prepare");
            return Task.FromResult(new EncodedWallpaperPrepareReceipt(
                Prepared: true,
                Reason: "prepared",
                descriptor.Generation));
        }

        public Task<EncodedWallpaperAppendReceipt> AppendAsync(
            EncodedWallpaperSegment segment,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"append:{segment.Sequence}");
            if (FailKeyFrameAppend && segment.IsKeyFrame)
            {
                throw new IOException("fixture key frame append failed");
            }

            return Task.FromResult(new EncodedWallpaperAppendReceipt(
                Appended: true,
                Published: segment.IsKeyFrame,
                Reason: segment.IsKeyFrame ? "published" : "appended",
                segment.Sequence));
        }

        public Task<bool> SetPausedAsync(
            long generation,
            bool paused,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add($"paused:{generation}:{paused}");
            return Task.FromResult(true);
        }

        public async Task<bool> CleanupAsync(
            long generation,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"cleanup:{generation}");
            if (Interlocked.Increment(ref _cleanupCalls) <= CleanupTimeouts)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return true;
        }

        public ValueTask DisposeAsync()
        {
            _events.Add("sink-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualHeartbeatScheduler : IDynamicWallpaperHeartbeatScheduler
    {
        private readonly object _sync = new();
        private readonly Channel<bool> _pulses = Channel.CreateUnbounded<bool>();
        private TaskCompletionSource _waitArmed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waitCount;

        public ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource waitArmed;
            lock (_sync)
            {
                _waitCount++;
                waitArmed = _waitArmed;
                _waitArmed = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            waitArmed.TrySetResult();
            return WaitForPulseAsync(cancellationToken);
        }

        public async Task WaitUntilArmedAsync(int expectedCount, TimeSpan timeout)
        {
            using var deadline = new CancellationTokenSource(timeout);
            while (true)
            {
                Task waitArmed;
                lock (_sync)
                {
                    if (_waitCount >= expectedCount)
                    {
                        return;
                    }

                    waitArmed = _waitArmed.Task;
                }

                await waitArmed.WaitAsync(deadline.Token);
            }
        }

        public void Pulse() => Assert.True(_pulses.Writer.TryWrite(true));

        private async ValueTask WaitForPulseAsync(CancellationToken cancellationToken) =>
            _ = await _pulses.Reader.ReadAsync(cancellationToken);
    }
}
