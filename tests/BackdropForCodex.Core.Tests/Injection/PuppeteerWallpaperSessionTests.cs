using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using PuppeteerSharp;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed class PuppeteerWallpaperSessionTests
{
    [Fact]
    public void IsReviewedTargetDocument_RequiresExactTargetIdAndDocumentPath()
    {
        var endpoint = VerifiedEndpoint();

        Assert.True(VerifiedCodexPageSelector.IsReviewedTargetDocument(
            "codex-page",
            "app://codex/index.html?thread=1#latest",
            endpoint));
        Assert.False(VerifiedCodexPageSelector.IsReviewedTargetDocument(
            "different-page",
            "app://codex/index.html",
            endpoint));
        Assert.False(VerifiedCodexPageSelector.IsReviewedTargetDocument(
            "codex-page",
            "app://codex/auth/index.html",
            endpoint));
        Assert.False(VerifiedCodexPageSelector.IsReviewedTargetDocument(
            "codex-page",
            "app://codex/index.html?initialRoute=%2Favatar-overlay",
            endpoint));
    }

    [Theory]
    [InlineData("app://codex/index.html?initialRoute=/avatar-overlay")]
    [InlineData("app://codex/index.html?initialRoute=%2Favatar-overlay")]
    public void IsSameReviewedDocument_RejectsAvatarOverlayNavigation(string pageUrl)
    {
        Assert.False(VerifiedCodexPageSelector.IsSameReviewedDocument(
            new Uri(pageUrl),
            "app://codex/index.html"));
        Assert.True(VerifiedCodexPageSelector.IsSameReviewedDocument(
            new Uri("app://codex/index.html?thread=2"),
            "app://codex/index.html?thread=1"));
    }

    [Fact]
    public void IsEligibleTargetDocument_RejectsTargetAbsentFromVerifiedSnapshot()
    {
        var endpoint = VerifiedEndpoint();

        Assert.False(VerifiedCodexPageSelector.IsEligibleTargetDocument(
            "new-codex-page",
            "app://codex/index.html?thread=2",
            endpoint));
        Assert.False(VerifiedCodexPageSelector.IsEligibleTargetDocument(
            "new-auth-page",
            "https://auth.openai.com/login",
            endpoint));
        Assert.True(VerifiedCodexPageSelector.IsEligibleTargetDocument(
            "codex-page",
            "app://codex/index.html?thread=2",
            endpoint));
    }

    [Fact]
    public async Task StopAndDispose_AreIdempotentWithoutConnection()
    {
        var session = new PuppeteerWallpaperSession();

        await session.StopAsync();
        await session.StopAsync();
        await session.DisposeAsync();
        await session.DisposeAsync();
        await session.StopAsync();

        Assert.False(session.IsActive);
    }

    [Fact]
    public async Task FaultCleanup_PreservesGenerationUntilCoordinatorStopsSession()
    {
        await using var session = new PuppeteerWallpaperSession();

        await StopCoreAsync(
            session,
            observeHeartbeat: false,
            preservedFaultGeneration: 42);

        Assert.Equal(42, session.Generation);

        await session.StopAsync();

        Assert.Equal(0, session.Generation);
    }

    [Fact]
    public async Task StopCorePreservingCompatibilityAsync_KeepsContractAndDegradationLocks()
    {
        await using var session = new PuppeteerWallpaperSession();
        var contractState = ContractState(session);
        var capabilityState = CapabilityState(session);
        var selected = contractState.Select(
            PresentationEvidence.FullySupported,
            finalizeBaselineFallback: false);
        capabilityState.Begin(
            selected.Capabilities,
            continuesCurrentGeneration: false);
        capabilityState.Observe(
            PresentationContractCatalog.Observe(
                selected.Snapshot,
                PresentationEvidence.FullySupported with
                {
                    BackdropFilterSupported = false,
                }));

        var contractBeforeReconnect = session.PresentationContract;
        var capabilitiesBeforeReconnect = session.Capabilities;

        await StopCorePreservingCompatibilityAsync(session);

        Assert.Equal(contractBeforeReconnect, session.PresentationContract);
        Assert.Equal(capabilitiesBeforeReconnect, session.Capabilities);

        capabilityState.Observe(selected.Capabilities);
        var attemptedContractSwitch = contractState.Select(
            PresentationEvidence.FullySupported with
            {
                ShellStructure = false,
            },
            finalizeBaselineFallback: true);

        Assert.False(session.Capabilities.Glass.IsAvailable);
        Assert.Equal(contractBeforeReconnect, attemptedContractSwitch.Snapshot);
        Assert.Equal(contractBeforeReconnect, session.PresentationContract);
    }

    [Fact]
    public async Task ApplyAsync_CanceledSameGenerationReconnectKeepsCompatibilityLocks()
    {
        using var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            port: 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = VerifiedEndpoint(port, "reconnect-page");
        await using var session = new PuppeteerWallpaperSession();
        Options(session) = InjectionOptions(generation: 7);
        Endpoint(session) = endpoint;
        var contractState = ContractState(session);
        var capabilityState = CapabilityState(session);
        var selected = contractState.Select(
            PresentationEvidence.FullySupported,
            finalizeBaselineFallback: false);
        capabilityState.Begin(
            selected.Capabilities,
            continuesCurrentGeneration: false);
        capabilityState.Observe(
            PresentationContractCatalog.Observe(
                selected.Snapshot,
                PresentationEvidence.FullySupported with
                {
                    BackdropFilterSupported = false,
                }));
        var contractBeforeReconnect = session.PresentationContract;
        var capabilitiesBeforeReconnect = session.Capabilities;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.ApplyAsync(
                endpoint,
                InjectionOptions(generation: 7),
                cancellation.Token));

        Assert.Equal(contractBeforeReconnect, session.PresentationContract);
        Assert.Equal(capabilitiesBeforeReconnect, session.Capabilities);
        Assert.Equal(7, session.Generation);
    }

    [Fact]
    public void ReconnectCompatibilityPolicy_UsesOnlyGeneration()
    {
        Assert.True(PuppeteerWallpaperSession.ContinuesCompatibilityGeneration(
            InjectionOptions(generation: 7),
            InjectionOptions(generation: 7)));
        Assert.False(PuppeteerWallpaperSession.ContinuesCompatibilityGeneration(
            InjectionOptions(generation: 7),
            InjectionOptions(generation: 8)));
    }

    [Theory]
    [InlineData(false, 1, 0, false, true)]
    [InlineData(true, 1, 0, false, false)]
    [InlineData(false, 2, 0, true, false)]
    [InlineData(false, 1, 1, false, false)]
    public void FinalBaselineAttemptRunsOnlyForPendingUniqueUnappliedContract(
        bool contractFinalized,
        int eligibleCount,
        int appliedCount,
        bool isAmbiguous,
        bool expected)
    {
        var applyResult = new PageApplyResult(
            eligibleCount,
            appliedCount,
            isAmbiguous,
            AmbiguousTargetsObserved: isAmbiguous);

        Assert.Equal(
            expected,
            PuppeteerWallpaperSession.ShouldRunFinalBaselineAttempt(
                applyResult,
                contractFinalized));
    }

    [Fact]
    public async Task StopCoreAsync_ResetsCompatibilityStateForNewGeneration()
    {
        await using var session = new PuppeteerWallpaperSession();
        var selected = ContractState(session).Select(
            PresentationEvidence.FullySupported,
            finalizeBaselineFallback: false);
        CapabilityState(session).Begin(
            selected.Capabilities,
            continuesCurrentGeneration: false);

        await StopCoreAsync(
            session,
            observeHeartbeat: false,
            preservedFaultGeneration: 0);

        Assert.Equal(
            PresentationContractSnapshot.NotEvaluated,
            session.PresentationContract);
        Assert.False(session.Capabilities.Global.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.DisabledForGeneration,
            session.Capabilities.Global.ReasonCode);

        var reselection = ContractState(session).Select(
            PresentationEvidence.FullySupported with
            {
                ShellStructure = false,
            },
            finalizeBaselineFallback: true);

        Assert.True(reselection.IsFinalized);
        Assert.Equal(
            PresentationContractCatalog.GlobalBaselineId,
            reselection.Snapshot.ActiveContractId);
        Assert.NotEqual(selected.Snapshot, reselection.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_DoesNotAwaitHeartbeatWhileHoldingLifecycleGate(bool dispose)
    {
        var session = new PuppeteerWallpaperSession();
        var gate = LifecycleGate(session);
        await gate.WaitAsync(CancellationToken.None);

        Task shutdownTask;
        Task heartbeatTask;
        try
        {
            shutdownTask = dispose ? session.DisposeAsync().AsTask() : session.StopAsync();
            var heartbeatCancellation = new CancellationTokenSource();
            heartbeatTask = WaitForCancellationThenGateAsync(
                gate,
                heartbeatCancellation.Token);
            HeartbeatCancellation(session) = heartbeatCancellation;
            HeartbeatTask(session) = heartbeatTask;
        }
        finally
        {
            gate.Release();
        }

        await shutdownTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(heartbeatTask.IsCompletedSuccessfully);
        if (!dispose)
        {
            await session.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationQueuedBeforeDispose_RechecksDisposedAfterAcquiringGate(
        bool apply)
    {
        var session = new PuppeteerWallpaperSession();
        var gate = LifecycleGate(session);
        await gate.WaitAsync(CancellationToken.None);

        Task operationTask;
        Task disposeTask;
        try
        {
            operationTask = apply
                ? session.ApplyAsync(VerifiedEndpoint(), InjectionOptions())
                : session.SetPausedAsync(paused: true);
            await Task.Yield();
            disposeTask = session.DisposeAsync().AsTask();
        }
        finally
        {
            gate.Release();
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => operationTask);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(1)));
        gate.Release();
    }

    [Fact]
    public void ResetHeartbeatFailureWindow_ClearsOnlyWhenGenerationChanges()
    {
        var failureGeneration = 7L;
        var consecutiveFailures = 2;

        PuppeteerWallpaperSession.ResetHeartbeatFailureWindow(
            observedGeneration: 7,
            ref failureGeneration,
            ref consecutiveFailures);

        Assert.Equal(7, failureGeneration);
        Assert.Equal(2, consecutiveFailures);

        PuppeteerWallpaperSession.ResetHeartbeatFailureWindow(
            observedGeneration: 8,
            ref failureGeneration,
            ref consecutiveFailures);

        Assert.Equal(8, failureGeneration);
        Assert.Equal(0, consecutiveFailures);
    }

    [Fact]
    public void PendingCleanupTracking_PreservesNewestGeneration()
    {
        var registry = new InjectedPageRegistry();
        var page = DispatchProxy.Create<IPage, ThrowingPageProxy>();

        registry.TrackPendingCleanup(page, generation: 9);
        registry.TrackPendingCleanup(page, generation: 4);
        registry.RemovePendingCleanupUpTo(page, generation: 4);

        Assert.True(registry.TryGetPendingGeneration(page, out var pendingGeneration));
        Assert.Equal(9, pendingGeneration);

        registry.RemovePendingCleanupUpTo(page, generation: 9);

        Assert.False(registry.TryGetPendingGeneration(page, out _));
    }

    [Fact]
    public async Task PendingCleanupTracking_SurvivesDeadlineCancellation()
    {
        var registry = new InjectedPageRegistry();
        var page = DispatchProxy.Create<IPage, CleanupPageProxy>();
        var proxy = (CleanupPageProxy)(object)page;
        var evaluation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.EvaluationTask = evaluation.Task;
        registry.TrackPendingCleanup(page, generation: 7);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry
                .CleanupOrTrackPendingAsync(page, generation: 7, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(2)));
        evaluation.TrySetResult(false);

        Assert.True(registry.TryGetPendingGeneration(page, out var pendingGeneration));
        Assert.Equal(7, pendingGeneration);
    }

    [Fact]
    public void TrySelectSoleEligiblePage_RejectsAmbiguousTargetsWithoutSelectingEither()
    {
        var first = DispatchProxy.Create<IPage, ThrowingPageProxy>();
        var second = DispatchProxy.Create<IPage, ThrowingPageProxy>();

        var selected = VerifiedCodexPageSelector.TrySelectSoleEligiblePage(
            [first, second],
            out var page);

        Assert.False(selected);
        Assert.Null(page);
    }

    [Fact]
    public void TrySelectSoleEligiblePage_SelectsExactlyOneTarget()
    {
        var expected = DispatchProxy.Create<IPage, ThrowingPageProxy>();

        var selected = VerifiedCodexPageSelector.TrySelectSoleEligiblePage(
            [expected],
            out var page);

        Assert.True(selected);
        Assert.Same(expected, page);
    }

    [Fact]
    public async Task PageSelector_DeadlineCancelsAHangingCdpDetach()
    {
        var endpoint = VerifiedEndpoint();
        var created = CreatePage("codex-page");
        var cdpProxy = (CdpSessionProxy)(object)created.Proxy.Session;
        var detach = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        cdpProxy.DetachTask = detach.Task;
        var selector = new VerifiedCodexPageSelector();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => selector
                .IsEligibleVerifiedPageAsync(
                    created.Page,
                    endpoint,
                    cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(2)));
        detach.TrySetResult(true);

        Assert.Equal(1, cdpProxy.DetachCallCount);
    }

    [Fact]
    public async Task MediaHandleCleanup_DeadlineCancelsAHangingDispose()
    {
        await using var session = new PuppeteerWallpaperSession();
        var handle = DispatchProxy.Create<IJSHandle, JsHandleProxy>();
        var proxy = (JsHandleProxy)(object)handle;
        var dispose = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.DisposeTask = dispose.Task;
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DisposeHandleBestEffortAsync(session, handle, cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(2)));
        dispose.TrySetResult(true);

        Assert.Equal(1, proxy.DisposeCallCount);
    }

    [Fact]
    public async Task ApplyToCurrentPagesAsync_PersistentTargetAmbiguityNeverRunsPresentationEvidenceProbe()
    {
        var endpoint = VerifiedEndpoint("first-codex-page", "second-codex-page");
        var first = CreatePage("first-codex-page");
        var second = CreatePage("second-codex-page");
        var browser = CreateBrowser(first.Page, second.Page);
        await using var session = CreatePreparedSession(browser, endpoint);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await ApplyToCurrentPagesAsync(
                session,
                CancellationToken.None,
                finalizeBaselineFallback: false);

            Assert.Equal(2, result.EligibleCount);
            Assert.True(result.IsAmbiguous);
            Assert.Equal(0, result.AppliedCount);
        }

        Assert.Equal(0, first.Proxy.PresentationEvidenceProbeCount);
        Assert.Equal(0, second.Proxy.PresentationEvidenceProbeCount);
    }

    [Fact]
    public async Task ApplyToCurrentPagesAsync_UnverifiedTargetNeverRunsPresentationEvidenceProbe()
    {
        var endpoint = VerifiedEndpoint();
        var unverified = CreatePage("unverified-page");
        var browser = CreateBrowser(unverified.Page);
        await using var session = CreatePreparedSession(browser, endpoint);

        var result = await ApplyToCurrentPagesAsync(
            session,
            CancellationToken.None,
            finalizeBaselineFallback: false);

        Assert.Equal(0, result.EligibleCount);
        Assert.False(result.IsAmbiguous);
        Assert.Equal(0, result.AppliedCount);
        Assert.Equal(0, unverified.Proxy.PresentationEvidenceProbeCount);
    }

    private static async Task WaitForCancellationThenGateAsync(
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            cancellationObserved);
        await cancellationObserved.Task;
        await gate.WaitAsync(CancellationToken.None);
        gate.Release();
    }

    private static VerifiedCdpEndpoint VerifiedEndpoint(params string[] targetIds) =>
        VerifiedEndpoint(port: 9222, targetIds);

    private static VerifiedCdpEndpoint VerifiedEndpoint(int port, params string[] targetIds)
    {
        if (targetIds.Length == 0)
        {
            targetIds = ["codex-page"];
        }

        var identity = BackdropForCodex.Core.Tests.Codex.CodexSecurityValidatorTests
            .GetIdentity();
        var candidate = new CdpEndpointCandidate(
            1234,
            "ChatGPT.exe",
            identity.PackageFamilyName,
            identity.PackageFullName,
            new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
            WindowsCodexProcessSnapshotSource.CurrentSessionId,
            new Uri($"http://127.0.0.1:{port}/"));
        var browser = new CdpBrowserVersion(
            "Chrome/140.0.0.0",
            "1.3",
            null,
            null,
            $"ws://127.0.0.1:{port}/devtools/browser/browser-id");
        var targets = targetIds
            .Select(targetId => new CdpTargetDescriptor(
                targetId,
                "page",
                "Codex",
                "app://codex/index.html",
                $"ws://127.0.0.1:{port}/devtools/page/{targetId}"))
            .ToArray();

        var result = CdpEndpointIdentityVerifier.Verify(
            candidate,
            identity,
            browser,
            targets);
        return Assert.IsType<VerifiedCdpEndpoint>(result.Endpoint);
    }

    private static PuppeteerWallpaperSession CreatePreparedSession(
        IBrowser browser,
        VerifiedCdpEndpoint endpoint)
    {
        var session = new PuppeteerWallpaperSession();
        Browser(session) = browser;
        Endpoint(session) = endpoint;
        Options(session) = InjectionOptions();
        return session;
    }

    private static IBrowser CreateBrowser(params IPage[] pages)
    {
        var browser = DispatchProxy.Create<IBrowser, BrowserProxy>();
        ((BrowserProxy)(object)browser).Pages = pages;
        return browser;
    }

    private static (IPage Page, PageProxy Proxy) CreatePage(string targetId)
    {
        var session = DispatchProxy.Create<ICDPSession, CdpSessionProxy>();
        ((CdpSessionProxy)(object)session).TargetId = targetId;
        var page = DispatchProxy.Create<IPage, PageProxy>();
        var proxy = (PageProxy)(object)page;
        proxy.Session = session;
        return (page, proxy);
    }

    private static WallpaperInjectionOptions InjectionOptions(long generation = 1) => new(
        generation,
        source: new Uri("http://127.0.0.1:9/wallpaper.png"),
        localMediaPath: Path.Combine(
            Path.GetPathRoot(Environment.SystemDirectory)!,
            "wallpaper.png"),
        expectedContentLength: 1,
        WallpaperMediaKind.Image);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy requires a non-sealed proxy base type.")]
    private class ThrowingPageProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("The tracking test must not call page members.");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy requires a non-sealed proxy base type.")]
    private class BrowserProxy : DispatchProxy
    {
        public IPage[] Pages { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "PagesAsync" => Task.FromResult(Pages),
                "Disconnect" => null,
                _ => throw new InvalidOperationException(
                    $"The browser test proxy does not implement {targetMethod?.Name}."),
            };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy requires a non-sealed proxy base type.")]
    private class PageProxy : DispatchProxy
    {
        public ICDPSession Session { get; set; } = null!;

        public int PresentationEvidenceProbeCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "get_IsClosed" => false,
                "get_Url" => "app://codex/index.html",
                "GetTitleAsync" => Task.FromResult("Codex"),
                "CreateCDPSessionAsync" => Task.FromResult(Session),
                "EvaluateExpressionAsync" => CountUnexpectedPresentationEvidenceProbe(),
                _ => throw new InvalidOperationException(
                    $"The page test proxy does not implement {targetMethod?.Name}."),
            };
        }

        private object CountUnexpectedPresentationEvidenceProbe()
        {
            PresentationEvidenceProbeCount++;
            throw new InvalidOperationException(
                "Presentation evidence must not be evaluated before target validation succeeds.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy requires a non-sealed proxy base type.")]
    private class CleanupPageProxy : DispatchProxy
    {
        public Task<bool> EvaluationTask { get; set; } = Task.FromResult(false);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                "get_IsClosed" => false,
                "EvaluateExpressionAsync" => EvaluationTask,
                _ => throw new InvalidOperationException(
                    $"The cleanup page test proxy does not implement {targetMethod?.Name}."),
            };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy requires a non-sealed proxy base type.")]
    private class JsHandleProxy : DispatchProxy
    {
        public Task DisposeTask { get; set; } = Task.CompletedTask;

        public int DisposeCallCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (string.Equals(targetMethod?.Name, "DisposeAsync", StringComparison.Ordinal))
            {
                DisposeCallCount++;
                return new ValueTask(DisposeTask);
            }

            throw new InvalidOperationException(
                $"The JS handle test proxy does not implement {targetMethod?.Name}.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1852:Seal internal types",
        Justification = "DispatchProxy requires a non-sealed proxy base type.")]
    private class CdpSessionProxy : DispatchProxy
    {
        public string TargetId { get; set; } = string.Empty;

        public Task DetachTask { get; set; } = Task.CompletedTask;

        public int DetachCallCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (string.Equals(targetMethod?.Name, "DetachAsync", StringComparison.Ordinal))
            {
                DetachCallCount++;
                return DetachTask;
            }

            if (string.Equals(targetMethod?.Name, "SendAsync", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(
                    JsonSerializer.Serialize(
                        new
                        {
                            targetInfo = new
                            {
                                targetId = TargetId,
                            },
                        }));
                return Task.FromResult(document.RootElement.Clone());
            }

            throw new InvalidOperationException(
                $"The CDP session test proxy does not implement {targetMethod?.Name}.");
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lifecycleGate")]
    private static extern ref SemaphoreSlim LifecycleGate(PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_heartbeatCancellation")]
    private static extern ref CancellationTokenSource? HeartbeatCancellation(
        PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_heartbeatTask")]
    private static extern ref Task? HeartbeatTask(PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_browser")]
    private static extern ref IBrowser? Browser(PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_endpoint")]
    private static extern ref VerifiedCdpEndpoint? Endpoint(PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_options")]
    private static extern ref WallpaperInjectionOptions? Options(PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_capabilityState")]
    private static extern ref InjectionCapabilityState CapabilityState(
        PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_presentationContractState")]
    private static extern ref PresentationContractState ContractState(
        PuppeteerWallpaperSession session);

    [UnsafeAccessor(
        UnsafeAccessorKind.StaticMethod,
        Name = "DisposeHandleBestEffortAsync")]
    private static extern Task DisposeHandleBestEffortAsync(
        PuppeteerWallpaperSession session,
        IJSHandle? handle,
        CancellationToken cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1068:CancellationToken parameters must come last",
        Justification = "UnsafeAccessor must preserve the private production method's parameter order.")]
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ApplyToCurrentPagesAsync")]
    private static extern Task<PageApplyResult> ApplyToCurrentPagesAsync(
        PuppeteerWallpaperSession session,
        CancellationToken cancellationToken,
        bool finalizeBaselineFallback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "StopCoreAsync")]
    private static extern Task<Task?> StopCoreAsync(
        PuppeteerWallpaperSession session,
        bool observeHeartbeat,
        long preservedFaultGeneration);

    [UnsafeAccessor(
        UnsafeAccessorKind.Method,
        Name = "StopCorePreservingCompatibilityAsync")]
    private static extern Task<Task?> StopCorePreservingCompatibilityAsync(
        PuppeteerWallpaperSession session);
}
