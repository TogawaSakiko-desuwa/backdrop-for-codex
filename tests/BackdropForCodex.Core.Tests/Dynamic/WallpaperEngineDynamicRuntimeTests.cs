using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class WallpaperEngineDynamicRuntimeTests
{
    [Fact]
    public async Task ProbeStopsBeforeThePipelineWhenTheValidatedInstallationIsMissing()
    {
        var inner = new FakeActivationFactory();
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(
                new WallpaperEngineUnavailableException(
                    WallpaperEngineAvailabilityReason.NotInstalled)),
            new FakeControlClient(),
            new FakeRecovery(),
            inner);

        var capability = await runtime.ProbeAsync();

        Assert.False(capability.IsAvailable);
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineUnavailable,
            capability.ReasonCode);
        Assert.Equal(0, inner.ProbeCount);
    }

    [Fact]
    public async Task ProbeRequiresAnAlreadyRunningInstanceBeforeRecoveryOrCapture()
    {
        var inner = new FakeActivationFactory();
        var recovery = new FakeRecovery();
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(Installation()),
            new FakeControlClient(
                new WallpaperEnginePlatformUnavailableException(
                    WallpaperEnginePlatformUnavailableReason.StartupNotProven)),
            recovery,
            inner);

        var capability = await runtime.ProbeAsync();

        Assert.False(capability.IsAvailable);
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineNotRunning,
            capability.ReasonCode);
        Assert.Equal(0, recovery.CallCount);
        Assert.Equal(0, inner.ProbeCount);
    }

    [Theory]
    [InlineData((int)WallpaperEngineOwnedWindowRecoveryState.PartiallyRecovered)]
    [InlineData((int)WallpaperEngineOwnedWindowRecoveryState.Deferred)]
    public async Task ProbeFailsClosedWhileAnOwnedWindowRecoveryRemainsPending(
        int stateValue)
    {
        var state = (WallpaperEngineOwnedWindowRecoveryState)stateValue;
        var inner = new FakeActivationFactory();
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(Installation()),
            new FakeControlClient(),
            new FakeRecovery(new WallpaperEngineOwnedWindowRecoveryResult(state, 0, 1)),
            inner);

        var capability = await runtime.ProbeAsync();

        Assert.False(capability.IsAvailable);
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineRecoveryUnavailable,
            capability.ReasonCode);
        Assert.Equal(0, inner.ProbeCount);
    }

    [Fact]
    public async Task ProbeDelegatesToCaptureAndEncoderOnlyAfterPlatformPreflight()
    {
        var inner = new FakeActivationFactory();
        var control = new FakeControlClient();
        var recovery = new FakeRecovery();
        var audio = new FakeAudioIsolation(initialSilenceIsProven: true);
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(Installation()),
            control,
            recovery,
            inner,
            audioIsolation: audio);

        var capability = await runtime.ProbeAsync();

        Assert.True(capability.IsAvailable);
        Assert.Equal(1, control.EnsureRunningCount);
        Assert.Equal(1, recovery.CallCount);
        Assert.Equal(1, audio.CaptureCount);
        Assert.Equal(1, audio.Baseline.DisposeCount);
        Assert.Equal(1, inner.ProbeCount);
    }

    [Fact]
    public async Task ProbeRejectsUnprovenInitialSilenceAfterRecoveryButBeforeOpeningAWindow()
    {
        var inner = new FakeActivationFactory();
        var control = new FakeControlClient();
        var recovery = new FakeRecovery();
        var audio = new FakeAudioIsolation(initialSilenceIsProven: false);
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(Installation()),
            control,
            recovery,
            inner,
            audioIsolation: audio);

        var capability = await runtime.ProbeAsync();

        Assert.False(capability.IsAvailable);
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.InitialAudioSilenceNotProven,
            capability.ReasonCode);
        Assert.Equal(1, audio.CaptureCount);
        Assert.Equal(1, audio.Baseline.DisposeCount);
        Assert.Equal(1, recovery.CallCount);
        Assert.Equal(0, inner.ProbeCount);
        Assert.Equal(0, control.OpenCount);
    }

    [Fact]
    public async Task RecoveryRunsOnlyBeforeTheFirstSuccessfulDynamicPreflight()
    {
        var recovery = new FakeRecovery();
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(Installation()),
            new FakeControlClient(),
            recovery,
            new FakeActivationFactory());

        Assert.True((await runtime.ProbeAsync()).IsAvailable);
        recovery.Result = new WallpaperEngineOwnedWindowRecoveryResult(
            WallpaperEngineOwnedWindowRecoveryState.PartiallyRecovered,
            0,
            1);
        Assert.True((await runtime.ProbeAsync()).IsAvailable);

        Assert.Equal(1, recovery.CallCount);
    }

    [Fact]
    public async Task IncompleteRecoveryIsRetriedAndOnlyASuccessfulResultIsCached()
    {
        var recovery = new FakeRecovery(
            new WallpaperEngineOwnedWindowRecoveryResult(
                WallpaperEngineOwnedWindowRecoveryState.Deferred,
                0,
                1));
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(Installation()),
            new FakeControlClient(),
            recovery,
            new FakeActivationFactory());

        var first = await runtime.ProbeAsync();
        Assert.False(first.IsAvailable);

        recovery.Result = new WallpaperEngineOwnedWindowRecoveryResult(
            WallpaperEngineOwnedWindowRecoveryState.Recovered,
            1,
            0);
        Assert.True((await runtime.ProbeAsync()).IsAvailable);

        recovery.Result = new WallpaperEngineOwnedWindowRecoveryResult(
            WallpaperEngineOwnedWindowRecoveryState.PartiallyRecovered,
            0,
            1);
        Assert.True((await runtime.ProbeAsync()).IsAvailable);
        Assert.Equal(2, recovery.CallCount);
    }

    [Fact]
    public async Task ActivatePreservesTheInnerPresentationDecision()
    {
        var resolution = Resolution();
        var inner = new FakeActivationFactory();
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(Installation()),
            new FakeControlClient(),
            new FakeRecovery(),
            inner);
        var project = new FakeProjectLease(resolution);

        var result = await runtime.ActivateAsync(Request(resolution), project);

        Assert.Same(inner.ActivationResult, result);
        Assert.Equal(
            new PresentationContractSnapshot(
                PresentationContractCatalog.CodexShellId,
                ContractMatchState.Matched),
            result.Presentation);
        Assert.Equal(
            PresentationContractCatalog.CreateFullySupportedCapabilities(),
            result.Capabilities);

        await result.Lease.DisposeAsync();
    }

    [Theory]
    [InlineData(
        (int)WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
        DynamicWallpaperCapabilityReasonCode.WallpaperEngineWindowUnavailable)]
    [InlineData(
        (int)WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven,
        DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable)]
    [InlineData(
        (int)WallpaperEnginePlatformUnavailableReason.InitialAudioSilenceNotProven,
        DynamicWallpaperCapabilityReasonCode.InitialAudioSilenceNotProven)]
    [InlineData(
        (int)WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
        DynamicWallpaperCapabilityReasonCode.WallpaperEnginePlacementUnavailable)]
    public async Task ActivateMapsInternalPlatformFailuresToStablePublicReasons(
        int platformReasonValue,
        DynamicWallpaperCapabilityReasonCode expectedReason)
    {
        var platformReason = (WallpaperEnginePlatformUnavailableReason)platformReasonValue;
        var resolution = Resolution();
        var inner = new FakeActivationFactory
        {
            ActivationFailure = new WallpaperEnginePlatformUnavailableException(platformReason),
        };
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(Installation()),
            new FakeControlClient(),
            new FakeRecovery(),
            inner);
        var project = new FakeProjectLease(resolution);

        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            runtime.ActivateAsync(Request(resolution), project).AsTask());

        Assert.Equal(expectedReason, failure.ReasonCode);
        Assert.Same(inner.ActivationFailure, failure.InnerException);
        Assert.False(project.IsDisposed);
    }

    [Fact]
    public async Task DisposeAwaitsTheInnerCleanupBoundaryAndCanRetryAfterFailure()
    {
        var inner = new FakeActivationFactory { DisposeFailures = 1 };
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(Installation()),
            new FakeControlClient(),
            new FakeRecovery(),
            inner);

        await Assert.ThrowsAsync<IOException>(() => runtime.DisposeAsync().AsTask());
        Assert.Equal(1, inner.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.ProbeAsync().AsTask());

        await runtime.DisposeAsync();

        Assert.Equal(2, inner.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.ProbeAsync().AsTask());
    }

    [Fact]
    public async Task DisposeAggregatesIndependentCleanupFailuresAndRetriesOnlyRetainedOwners()
    {
        var inner = new FakeActivationFactory { DisposeFailures = 1 };
        var owned = new FakeOwnedResource { DisposeFailures = 1 };
        var runtime = new WallpaperEngineDynamicRuntime(
            new FakeInstallationLocator(Installation()),
            new FakeControlClient(),
            new FakeRecovery(),
            inner,
            ownedResource: owned);

        var failure = await Assert.ThrowsAsync<AggregateException>(
            () => runtime.DisposeAsync().AsTask());

        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Equal(1, inner.DisposeCount);
        Assert.Equal(1, owned.DisposeCount);

        await runtime.DisposeAsync();

        Assert.Equal(2, inner.DisposeCount);
        Assert.Equal(2, owned.DisposeCount);
    }

    private static WallpaperEngineInstallation Installation() => new(
        @"C:\Steam",
        @"C:\Steam\steamapps\common\wallpaper_engine",
        @"C:\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe",
        [@"C:\Steam"]);

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

    private sealed class FakeInstallationLocator : IWallpaperEngineInstallationLocator
    {
        private readonly WallpaperEngineInstallation? _installation;
        private readonly Exception? _failure;

        internal FakeInstallationLocator(WallpaperEngineInstallation installation) =>
            _installation = installation;

        internal FakeInstallationLocator(Exception failure) => _failure = failure;

        public ValueTask<WallpaperEngineInstallation> LocateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _failure is null
                ? ValueTask.FromResult(_installation!)
                : ValueTask.FromException<WallpaperEngineInstallation>(_failure);
        }
    }

    private sealed class FakeControlClient : IWallpaperEngineControlClient
    {
        private readonly Exception? _ensureFailure;

        internal FakeControlClient(Exception? ensureFailure = null) =>
            _ensureFailure = ensureFailure;

        internal int EnsureRunningCount { get; private set; }

        internal int OpenCount { get; private set; }

        public ValueTask EnsureRunningAsync(
            WallpaperEngineInstallation installation,
            CancellationToken cancellationToken)
        {
            _ = installation;
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRunningCount++;
            return _ensureFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(_ensureFailure);
        }

        public ValueTask OpenWindowAsync(
            WallpaperEngineInstallation installation,
            string launchPath,
            WallpaperEngineOwnedWindowName windowName,
            WallpaperEngineWindowOptions options,
            WallpaperEngineWindowPlacement placement,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> QueryWindowWallpaperAsync(
            WallpaperEngineInstallation installation,
            WallpaperEngineOwnedWindowName windowName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask CloseWindowAsync(
            WallpaperEngineInstallation installation,
            WallpaperEngineOwnedWindowName windowName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAudioIsolation(bool initialSilenceIsProven)
        : IWallpaperEngineAudioIsolation
    {
        internal FakeAudioBaseline Baseline { get; } = new(initialSilenceIsProven);

        internal int CaptureCount { get; private set; }

        public ValueTask<IWallpaperEngineAudioBaseline> CaptureBaselineAsync(
            WallpaperEngineInstallation installation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return ValueTask.FromResult<IWallpaperEngineAudioBaseline>(Baseline);
        }

        public ValueTask<IWallpaperEngineMutedAudioLease> AcquireMutedSessionAsync(
            WallpaperEngineVerifiedWindow window,
            IWallpaperEngineAudioBaseline baseline,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAudioBaseline(bool initialSilenceIsProven)
        : IWallpaperEngineAudioBaseline
    {
        public bool InitialSilenceIsProven { get; } = initialSilenceIsProven;

        internal int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRecovery : IWallpaperEngineOwnedWindowRecovery
    {
        internal FakeRecovery(WallpaperEngineOwnedWindowRecoveryResult? result = null) =>
            Result = result ?? new WallpaperEngineOwnedWindowRecoveryResult(
                WallpaperEngineOwnedWindowRecoveryState.NothingToRecover,
                0,
                0);

        internal int CallCount { get; private set; }

        internal WallpaperEngineOwnedWindowRecoveryResult Result { get; set; }

        public ValueTask<WallpaperEngineOwnedWindowRecoveryResult> RecoverAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FakeActivationFactory :
        IDynamicWallpaperActivationFactory,
        IAsyncDisposable
    {
        internal int ProbeCount { get; private set; }

        internal Exception? ActivationFailure { get; init; }

        internal DynamicWallpaperActivationResult? ActivationResult { get; private set; }

        internal int DisposeFailures { get; set; }

        internal int DisposeCount { get; private set; }

        public ValueTask<DynamicWallpaperCapability> ProbeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCount++;
            return ValueTask.FromResult(DynamicWallpaperCapability.Available());
        }

        public ValueTask<DynamicWallpaperActivationResult> ActivateAsync(
            DynamicWallpaperActivationRequest request,
            IWallpaperEngineProjectLease projectLease,
            CancellationToken cancellationToken = default)
        {
            _ = request;
            _ = projectLease;
            cancellationToken.ThrowIfCancellationRequested();
            if (ActivationFailure is not null)
            {
                return ValueTask.FromException<DynamicWallpaperActivationResult>(
                    ActivationFailure);
            }

            ActivationResult ??= new DynamicWallpaperActivationResult(
                new FakeActiveLease(request.Generation),
                new PresentationContractSnapshot(
                    PresentationContractCatalog.CodexShellId,
                    ContractMatchState.Matched),
                PresentationContractCatalog.CreateFullySupportedCapabilities());
            return ValueTask.FromResult(ActivationResult);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return DisposeFailures-- > 0
                ? ValueTask.FromException(new IOException("fixture inner dispose failed"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class FakeActiveLease(long generation) : IActiveWallpaperLease
    {
        public long Generation { get; } = generation;

        public ActiveWallpaperDeliveryKind DeliveryKind =>
            ActiveWallpaperDeliveryKind.DynamicStream;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeOwnedResource : IDisposable
    {
        internal int DisposeFailures { get; set; }

        internal int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (DisposeFailures-- > 0)
            {
                throw new IOException("fixture owned resource dispose failed");
            }
        }
    }

    private sealed class FakeProjectLease(WallpaperSourceResolution resolution)
        : IWallpaperEngineProjectLease
    {
        public WallpaperSourceResolution Resolution { get; } = resolution;

        public string LaunchPath { get; } = @"C:\Fixtures\scene.pkg";

        internal bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
