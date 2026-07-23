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

        Assert.True(PuppeteerWallpaperSession.IsReviewedTargetDocument(
            "codex-page",
            "app://codex/index.html?thread=1#latest",
            endpoint));
        Assert.False(PuppeteerWallpaperSession.IsReviewedTargetDocument(
            "different-page",
            "app://codex/index.html",
            endpoint));
        Assert.False(PuppeteerWallpaperSession.IsReviewedTargetDocument(
            "codex-page",
            "app://codex/auth/index.html",
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
        var session = new PuppeteerWallpaperSession();
        var page = DispatchProxy.Create<IPage, ThrowingPageProxy>();

        TrackPendingCleanup(session, page, generation: 9);
        TrackPendingCleanup(session, page, generation: 4);
        RemovePendingCleanupUpTo(session, page, generation: 4);

        Assert.Equal(9, PreparedPages(session)[page]);

        RemovePendingCleanupUpTo(session, page, generation: 9);

        Assert.Empty(PreparedPages(session));
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

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_preparedPages")]
    private static extern ref Dictionary<IPage, long> PreparedPages(
        PuppeteerWallpaperSession session);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TrackPendingCleanup")]
    private static extern void TrackPendingCleanup(
        PuppeteerWallpaperSession session,
        IPage page,
        long generation);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "RemovePendingCleanupUpTo")]
    private static extern void RemovePendingCleanupUpTo(
        PuppeteerWallpaperSession session,
        IPage page,
        long generation);
}
