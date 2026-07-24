using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using PuppeteerSharp;
using System.Reflection;
using System.Runtime.CompilerServices;
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

    private static VerifiedCdpEndpoint VerifiedEndpoint()
    {
        var candidate = new CdpEndpointCandidate(
            1234,
            "ChatGPT.exe",
            CodexCompatibilityCatalog.OfficialPackageFamilyName,
            CodexCompatibilityCatalog.SupportedPackageFullName,
            new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
            WindowsCodexProcessSnapshotSource.CurrentSessionId,
            new Uri("http://127.0.0.1:9222/"));
        var browser = new CdpBrowserVersion(
            "Chrome/140.0.0.0",
            "1.3",
            null,
            null,
            "ws://127.0.0.1:9222/devtools/browser/browser-id");
        var target = new CdpTargetDescriptor(
            "codex-page",
            "page",
            "Codex",
            "app://codex/index.html",
            "ws://127.0.0.1:9222/devtools/page/codex-page");

        var result = CdpEndpointIdentityVerifier.Verify(
            candidate,
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile(),
            browser,
            [target]);
        return Assert.IsType<VerifiedCdpEndpoint>(result.Endpoint);
    }

    private static WallpaperInjectionOptions InjectionOptions() => new(
        generation: 1,
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

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lifecycleGate")]
    private static extern ref SemaphoreSlim LifecycleGate(PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_heartbeatCancellation")]
    private static extern ref CancellationTokenSource? HeartbeatCancellation(
        PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_heartbeatTask")]
    private static extern ref Task? HeartbeatTask(PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "StopCoreAsync")]
    private static extern Task<Task?> StopCoreAsync(
        PuppeteerWallpaperSession session,
        bool observeHeartbeat,
        long preservedFaultGeneration);
}
