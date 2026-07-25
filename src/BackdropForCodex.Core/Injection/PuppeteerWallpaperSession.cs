using PuppeteerSharp;
using BackdropForCodex.Core.Codex;
using System.Text.Json;

namespace BackdropForCodex.Core.Injection;

public interface IWallpaperInjectionSession : IAsyncDisposable
{
    bool IsActive { get; }

    long Generation { get; }

    Task ApplyAsync(
        VerifiedCdpEndpoint endpoint,
        WallpaperInjectionOptions options,
        CancellationToken cancellationToken = default);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class WallpaperInjectionHealthFaultedEventArgs : EventArgs
{
    public WallpaperInjectionHealthFaultedEventArgs(long generation, string detail)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Generation = generation;
        Detail = detail;
    }

    public long Generation { get; }

    public string Detail { get; }
}

public interface IWallpaperInjectionHealthSource
{
    event EventHandler<WallpaperInjectionHealthFaultedEventArgs>? HealthFaulted;
}

public interface IWallpaperInjectionCapabilitySource
{
    event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>? CapabilitiesChanged;

    CompatibilityCapabilities Capabilities { get; }

    PresentationContractSnapshot PresentationContract { get; }
}

public sealed class WallpaperInjectionCapabilitiesChangedEventArgs : EventArgs
{
    public WallpaperInjectionCapabilitiesChangedEventArgs(
        long generation,
        CompatibilityCapabilities previous,
        CompatibilityCapabilities current,
        PresentationContractSnapshot presentationContract)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Generation = generation;
        Previous = previous ?? throw new ArgumentNullException(nameof(previous));
        Current = current ?? throw new ArgumentNullException(nameof(current));
        PresentationContract = presentationContract ??
            throw new ArgumentNullException(nameof(presentationContract));
    }

    public long Generation { get; }

    public CompatibilityCapabilities Previous { get; }

    public CompatibilityCapabilities Current { get; }

    public PresentationContractSnapshot PresentationContract { get; }
}

/// <summary>
/// Connects to a previously verified CDP endpoint. Disconnecting this controller never closes
/// Codex; <see cref="IBrowser.Disconnect"/> is used instead of CloseAsync/DisposeAsync.
/// </summary>
public sealed class PuppeteerWallpaperSession :
    IWallpaperInjectionSession,
    IWallpaperInjectionHealthSource,
    IWallpaperInjectionCapabilitySource
{
    private const int MaximumConsecutiveHeartbeatFailures = 3;
    private static readonly TimeSpan BrowserResourceCleanupTimeout =
        TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly VerifiedCodexPageSelector _pageSelector = new();
    private readonly InjectedPageRegistry _pageRegistry = new();
    private readonly InitialPageReadinessGate _readinessGate = new();
    private readonly InjectionCapabilityState _capabilityState = new();
    private readonly PresentationContractState _presentationContractState = new();
    private IBrowser? _browser;
    private VerifiedCdpEndpoint? _endpoint;
    private WallpaperInjectionOptions? _options;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatTask;
    private bool _paused;
    private long _faultedGeneration;
    private PresentationContractSnapshot _publishedPresentationContract =
        PresentationContractSnapshot.NotEvaluated;
    private int _disposed;

    public event EventHandler<WallpaperInjectionHealthFaultedEventArgs>? HealthFaulted;

    public event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>? CapabilitiesChanged;

    public bool IsActive =>
        _browser?.IsConnected == true &&
        _options is not null &&
        _heartbeatTask is { IsCompleted: false };

    public long Generation => _options?.Generation ?? Interlocked.Read(ref _faultedGeneration);

    public CompatibilityCapabilities Capabilities => _capabilityState.Current;

    public PresentationContractSnapshot PresentationContract =>
        _presentationContractState.Current;

    internal static bool ContinuesCompatibilityGeneration(
        WallpaperInjectionOptions? currentOptions,
        WallpaperInjectionOptions nextOptions)
    {
        ArgumentNullException.ThrowIfNull(nextOptions);
        return currentOptions?.Generation == nextOptions.Generation;
    }

    public async Task ApplyAsync(
        VerifiedCdpEndpoint endpoint,
        WallpaperInjectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var continuesCurrentGeneration = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            // Endpoint changes are transport events. Only a generation change may release the
            // selected presentation contract or allow a downgraded capability to recover.
            continuesCurrentGeneration = ContinuesCompatibilityGeneration(_options, options);
            if (_browser is null || !_browser.IsConnected || _endpoint != endpoint)
            {
                var heartbeatTask = continuesCurrentGeneration
                    ? await StopCorePreservingCompatibilityAsync().ConfigureAwait(false)
                    : await StopCoreAsync().ConfigureAwait(false);
                ObserveHeartbeatInBackground(heartbeatTask);
                _browser = await ConnectWithoutOwningBrowserAsync(new ConnectOptions
                {
                    BrowserWSEndpoint = endpoint.BrowserWebSocketUri.AbsoluteUri,
                    DefaultViewport = null,
                    // Activation waits for a decoded image or a presentable video frame.
                    // Keep the transport timeout longer than the page-side 10 second media timeout
                    // so the page can report and clean up a controlled load failure first.
                    ProtocolTimeout = 15_000,
                    AcceptInsecureCerts = false,
                    NetworkEnabled = false,
                }, cancellationToken).ConfigureAwait(false);
                _endpoint = endpoint;
            }

            if (!continuesCurrentGeneration)
            {
                _capabilityState.Reset();
                _presentationContractState.Reset();
            }

            _options = options;
            Interlocked.Exchange(ref _faultedGeneration, 0);
            var applyResult = await _readinessGate
                .WaitAsync(
                    token => ApplyToCurrentPagesAsync(
                        token,
                        finalizeBaselineFallback: false),
                    cancellationToken)
                .ConfigureAwait(false);
            if (ShouldRunFinalBaselineAttempt(
                    applyResult,
                    _presentationContractState.IsFinalized))
            {
                applyResult = await _readinessGate
                    .RunFinalAttemptAsync(
                        token => ApplyToCurrentPagesAsync(
                            token,
                            finalizeBaselineFallback: true),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (applyResult.AppliedCount == 0)
            {
                var presentationContract = _presentationContractState.Current;
                ObserveHeartbeatInBackground(
                    await StopCoreAfterApplyFailureAsync(continuesCurrentGeneration)
                        .ConfigureAwait(false));
                if (applyResult.IsAmbiguous)
                {
                    throw new WallpaperTargetAmbiguityException(
                        "More than one eligible Codex work page remained available; no page was modified.");
                }

                if (presentationContract.MatchState ==
                    ContractMatchState.GlobalBaselineFailed)
                {
                    throw new WallpaperPresentationContractException(
                        "The verified Codex page did not match the Global presentation baseline.");
                }

                if (applyResult.EligibleCount != 0)
                {
                    throw new WallpaperMediaLoadException(
                        "The reviewed Codex page could not load the selected wallpaper media.");
                }

                throw new WallpaperInjectionException(
                    "The verified Codex endpoint did not expose a compatible main work page.");
            }

            EnsureHeartbeatLoop();
        }
        catch (FinalPageApplyTimeoutException)
        {
            ObserveHeartbeatInBackground(
                await StopCoreAfterApplyFailureAsync(continuesCurrentGeneration)
                    .ConfigureAwait(false));
            throw;
        }
        catch (WallpaperInjectionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveHeartbeatInBackground(
                await StopCoreAfterApplyFailureAsync(continuesCurrentGeneration)
                    .ConfigureAwait(false));
            throw;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            throw;
        }
        catch (Exception exception)
        {
            ObserveHeartbeatInBackground(
                await StopCoreAfterApplyFailureAsync(continuesCurrentGeneration)
                    .ConfigureAwait(false));
            throw new WallpaperBrowserHandshakeException(
                "The wallpaper runtime could not connect to the verified Codex target.",
                exception);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var generation = Generation;
            if (generation == 0 || _pageRegistry.MutatedCount == 0)
            {
                throw new WallpaperInjectionException("No active wallpaper page can be paused.");
            }

            var script = InjectionScriptBuilder.BuildSetPaused(generation, paused);
            var applied = false;
            foreach (var page in _pageRegistry.GetMutatedPages())
            {
                applied |= await PuppeteerPageScriptExecutor
                    .TryEvaluateAsync(page, script, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!applied)
            {
                throw new WallpaperInjectionException(
                    "The pause state could not be applied to an active wallpaper page.");
            }

            _paused = paused;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Task? heartbeatTask = null;
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            heartbeatTask = await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await ObserveHeartbeatCompletionAsync(heartbeatTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task? heartbeatTask = null;
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            heartbeatTask = await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await ObserveHeartbeatCompletionAsync(heartbeatTask).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void EnsureHeartbeatLoop()
    {
        if (_heartbeatTask is { IsCompleted: false })
        {
            return;
        }

        _heartbeatCancellation?.Dispose();
        _heartbeatCancellation = new CancellationTokenSource();
        _heartbeatTask = RunHeartbeatLoopAsync(_heartbeatCancellation.Token);
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        var failureGeneration = 0L;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var observedGeneration = 0L;
                try
                {
                    await Task.Delay(InjectionScriptBuilder.HeartbeatInterval, cancellationToken)
                        .ConfigureAwait(false);
                    var healthyPageAvailable = false;
                    await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (_browser?.IsConnected != true || _options is null)
                        {
                            return;
                        }

                        observedGeneration = _options.Generation;
                        ResetHeartbeatFailureWindow(
                            observedGeneration,
                            ref failureGeneration,
                            ref consecutiveFailures);

                        var applyResult = await ApplyToCurrentPagesAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (applyResult.IsAmbiguous)
                        {
                            await StopCoreAsync(
                                    observeHeartbeat: false,
                                    preservedFaultGeneration: observedGeneration)
                                .ConfigureAwait(false);
                            PublishHealthFault(
                                observedGeneration,
                                "More than one eligible Codex work page was detected; the wallpaper was removed.");
                            return;
                        }

                        if (applyResult.AppliedCount != 0)
                        {
                            var heartbeat = InjectionScriptBuilder.BuildHeartbeat(_options.Generation);
                            foreach (var page in _pageRegistry.GetMutatedPages())
                            {
                                var alive = await PuppeteerPageScriptExecutor
                                    .TryEvaluateAsync(page, heartbeat, cancellationToken)
                                    .ConfigureAwait(false);
                                if (alive)
                                {
                                    healthyPageAvailable = true;
                                }
                                else
                                {
                                    var cleanupGeneration = observedGeneration;
                                    if (_pageRegistry.RemoveMutation(
                                            page,
                                            out var mutatedGeneration))
                                    {
                                        cleanupGeneration = Math.Max(
                                            cleanupGeneration,
                                            mutatedGeneration);
                                    }

                                    await _pageRegistry
                                        .CleanupOrTrackPendingAsync(
                                            page,
                                            cleanupGeneration,
                                            cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    finally
                    {
                        _lifecycleGate.Release();
                    }

                    if (!healthyPageAvailable)
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= MaximumConsecutiveHeartbeatFailures)
                        {
                            PublishHealthFault(
                                observedGeneration,
                                "No compatible Codex page remained available for the wallpaper heartbeat.");
                            return;
                        }

                        continue;
                    }

                    consecutiveFailures = 0;
                }
                catch (PuppeteerException)
                {
                    var faultedGeneration = observedGeneration != 0
                        ? observedGeneration
                        : Generation;
                    ResetHeartbeatFailureWindow(
                        faultedGeneration,
                        ref failureGeneration,
                        ref consecutiveFailures);
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaximumConsecutiveHeartbeatFailures)
                    {
                        PublishHealthFault(
                            faultedGeneration,
                            "The wallpaper heartbeat repeatedly lost its Codex debugging connection.");
                        return;
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    var faultedGeneration = observedGeneration != 0
                        ? observedGeneration
                        : Generation;
                    PublishHealthFault(
                        faultedGeneration,
                        "The wallpaper heartbeat stopped after an unexpected runtime failure.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<PageApplyResult> ApplyToCurrentPagesAsync(
        CancellationToken cancellationToken,
        bool finalizeBaselineFallback = false)
    {
        if (_browser is null || _endpoint is null || _options is null)
        {
            return default;
        }

        var scan = await _pageSelector.ScanAsync(_browser, _endpoint, cancellationToken)
            .ConfigureAwait(false);
        _pageRegistry.PruneClosedPages(scan.ActivePages);
        foreach (var page in scan.ActivePages)
        {
            if (scan.EligiblePages.Contains(page, ReferenceEqualityComparer.Instance))
            {
                continue;
            }

            await _pageRegistry
                .CleanupTrackedPageAsync(page, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!VerifiedCodexPageSelector.TrySelectSoleEligiblePage(
                scan.EligiblePages,
                out var selectedPage))
        {
            if (scan.EligiblePages.Count > 1)
            {
                foreach (var page in scan.EligiblePages)
                {
                    await _pageRegistry
                        .CleanupTrackedPageAsync(page, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return new PageApplyResult(
                scan.EligiblePages.Count,
                AppliedCount: 0,
                IsAmbiguous: scan.EligiblePages.Count > 1,
                AmbiguousTargetsObserved: scan.EligiblePages.Count > 1);
        }

        var evidence = await ObserveEvidenceAsync(selectedPage, cancellationToken)
            .ConfigureAwait(false);
        var wasFinalized = _presentationContractState.IsFinalized;
        var contractDecision = _presentationContractState.Select(
            evidence,
            finalizeBaselineFallback);
        if (!contractDecision.IsFinalized)
        {
            return new PageApplyResult(
                EligibleCount: 1,
                AppliedCount: 0,
                IsAmbiguous: false,
                AmbiguousTargetsObserved: false);
        }

        CapabilityTransition capabilityTransition;
        if (!wasFinalized)
        {
            var previous = _capabilityState.Current;
            _capabilityState.Begin(
                contractDecision.Capabilities,
                continuesCurrentGeneration: false);
            capabilityTransition = new CapabilityTransition(
                previous,
                _capabilityState.Current);
        }
        else
        {
            capabilityTransition = _capabilityState.Observe(
                _presentationContractState.Observe(evidence));
        }

        if (!capabilityTransition.Current.Global.IsAvailable)
        {
            await _pageRegistry
                .CleanupTrackedPageAsync(selectedPage, cancellationToken)
                .ConfigureAwait(false);
            PublishCapabilitiesChanged(
                _options.Generation,
                capabilityTransition.Previous,
                capabilityTransition.Current);
            return new PageApplyResult(
                EligibleCount: 1,
                AppliedCount: 0,
                IsAmbiguous: false,
            AmbiguousTargetsObserved: false);
        }

        if (_pageRegistry.IsMutatedForGeneration(selectedPage, _options.Generation) &&
            InjectionCapabilityState.RequiresOwnedStyleDowngrade(
                capabilityTransition.Previous,
                capabilityTransition.Current))
        {
            var updated = await PuppeteerPageScriptExecutor.TryEvaluateAsync(
                    selectedPage,
                    InjectionScriptBuilder.BuildCapabilityDowngrade(
                        _options.Generation,
                        capabilityTransition.Current),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!updated)
            {
                await _pageRegistry
                    .CleanupTrackedPageAsync(selectedPage, cancellationToken)
                    .ConfigureAwait(false);
                PublishCapabilitiesChanged(
                    _options.Generation,
                    capabilityTransition.Previous,
                    capabilityTransition.Current);
                return new PageApplyResult(
                    EligibleCount: 1,
                    AppliedCount: 0,
                    IsAmbiguous: false,
                    AmbiguousTargetsObserved: false);
            }
        }

        PublishCapabilitiesChanged(
            _options.Generation,
            capabilityTransition.Previous,
            capabilityTransition.Current);

        if (!_pageRegistry.IsMutatedForGeneration(selectedPage, _options.Generation))
        {
            var installed = await TryInstallMediaAsync(selectedPage, _options, cancellationToken)
                .ConfigureAwait(false);
            if (!installed)
            {
                return new PageApplyResult(
                    EligibleCount: 1,
                    AppliedCount: 0,
                    IsAmbiguous: false,
                    AmbiguousTargetsObserved: false);
            }

            _pageRegistry.MarkMutated(selectedPage, _options.Generation);
            if (_paused)
            {
                await PuppeteerPageScriptExecutor.TryEvaluateAsync(
                    selectedPage,
                    InjectionScriptBuilder.BuildSetPaused(_options.Generation, paused: true),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new PageApplyResult(
            EligibleCount: 1,
            AppliedCount: 1,
            IsAmbiguous: false,
            AmbiguousTargetsObserved: false);
    }

    internal static bool RequiresOwnedStyleDowngrade(
        CompatibilityCapabilities previous,
        CompatibilityCapabilities current) =>
        InjectionCapabilityState.RequiresOwnedStyleDowngrade(previous, current);

    internal static bool ShouldRunFinalBaselineAttempt(
        PageApplyResult applyResult,
        bool presentationContractFinalized) =>
        !presentationContractFinalized &&
        applyResult.AppliedCount == 0 &&
        applyResult.EligibleCount == 1 &&
        !applyResult.IsAmbiguous;

    private static async Task<PresentationEvidence> ObserveEvidenceAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await page.EvaluateExpressionAsync<string>(
                    PresentationEvidenceScriptBuilder.Build())
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return PresentationEvidenceScriptBuilder.Parse(json);
        }
        catch (PuppeteerException)
        {
            return PresentationEvidence.Unavailable;
        }
        catch (JsonException)
        {
            return PresentationEvidence.Unavailable;
        }
    }

    private async Task<bool> TryInstallMediaAsync(
        IPage page,
        WallpaperInjectionOptions options,
        CancellationToken cancellationToken)
    {
        var activated = false;
        IJSHandle? preparedHandle = null;
        _pageRegistry.TrackPendingCleanup(page, options.Generation);
        try
        {
            preparedHandle = await page
                .EvaluateExpressionHandleAsync(
                    InjectionScriptBuilder.BuildInstall(options, _capabilityState.Current))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (preparedHandle is not IElementHandle fileInput)
            {
                return false;
            }

            // The install expression returns the exact input it created. Revalidating only after
            // capturing that handle means a same-URL navigation detaches the authorized element;
            // the new document is never queried for a predictable lookalike before upload.
            if (!await _pageSelector
                    .IsEligibleVerifiedPageAsync(page, _endpoint!, cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }

            await fileInput.UploadFileAsync(resolveFilePaths: false, [options.LocalMediaPath])
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            activated = await PuppeteerPageScriptExecutor.TryEvaluateAsync(
                page,
                WrapActivateExpression(
                    InjectionScriptBuilder.BuildActivateMedia(options.Generation)),
                cancellationToken).ConfigureAwait(false);
            return activated;
        }
        catch (PuppeteerException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (activated)
                {
                    _pageRegistry.RemovePendingCleanupUpTo(page, options.Generation);
                }
                else
                {
                    var cleaned = await PuppeteerPageScriptExecutor
                        .CleanupBestEffortAsync(
                            page,
                            options.Generation,
                            TimeSpan.FromSeconds(5),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (cleaned || page.IsClosed)
                    {
                        _pageRegistry.RemovePendingCleanupUpTo(page, options.Generation);
                    }
                    else
                    {
                        _pageRegistry.TrackPendingCleanup(page, options.Generation);
                    }
                }
            }
            finally
            {
                await DisposeHandleBestEffortAsync(preparedHandle, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task DisposeHandleBestEffortAsync(
        IJSHandle? handle,
        CancellationToken cancellationToken)
    {
        if (handle is null)
        {
            return;
        }

        Task? disposeTask = null;
        using var disposeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        disposeCancellation.CancelAfter(BrowserResourceCleanupTimeout);
        try
        {
            disposeTask = handle.DisposeAsync().AsTask();
            await disposeTask.WaitAsync(disposeCancellation.Token).ConfigureAwait(false);
        }
        catch (PuppeteerException)
        {
        }
        catch (OperationCanceledException)
            when (disposeCancellation.IsCancellationRequested)
        {
            if (disposeTask is not null)
            {
                _ = ObserveBrowserResourceCleanupAsync(disposeTask);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async Task ObserveBrowserResourceCleanupAsync(Task cleanupTask)
    {
        try
        {
            await cleanupTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The bounded cleanup path has returned; observe late browser cleanup failure.
        }
    }

    private static string WrapActivateExpression(string activateExpression) =>
        $"(async () => {{ const result = await ({activateExpression}); return result?.applied === true; }})()";

    private Task<Task?> StopCoreAsync(
        bool observeHeartbeat = true,
        long preservedFaultGeneration = 0)
    {
        return StopCoreLifecycleAsync(
            observeHeartbeat,
            preservedFaultGeneration,
            preserveCompatibilityState: false);
    }

    private Task<Task?> StopCorePreservingCompatibilityAsync()
    {
        return StopCoreLifecycleAsync(
            observeHeartbeat: true,
            preservedFaultGeneration: 0,
            preserveCompatibilityState: true);
    }

    private Task<Task?> StopCoreAfterApplyFailureAsync(bool preserveCompatibilityState)
    {
        return preserveCompatibilityState
            ? StopCorePreservingCompatibilityAsync()
            : StopCoreAsync();
    }

    private async Task<Task?> StopCoreLifecycleAsync(
        bool observeHeartbeat,
        long preservedFaultGeneration,
        bool preserveCompatibilityState)
    {
        var heartbeatCancellation = _heartbeatCancellation;
        var heartbeatTask = _heartbeatTask;
        _heartbeatCancellation = null;
        _heartbeatTask = null;
        heartbeatCancellation?.Cancel();
        heartbeatCancellation?.Dispose();
        if (observeHeartbeat)
        {
            ObserveHeartbeatInBackground(heartbeatTask);
        }

        try
        {
            await _pageRegistry.CleanupAllAsync().ConfigureAwait(false);
        }
        finally
        {
            _pageRegistry.Reset();
            _pageSelector.Reset();
            _endpoint = null;
            _paused = false;
            if (!preserveCompatibilityState)
            {
                _options = null;
                _capabilityState.Reset();
                _presentationContractState.Reset();
            }

            Interlocked.Exchange(ref _faultedGeneration, preservedFaultGeneration);
            var browser = _browser;
            _browser = null;
            if (browser is not null)
            {
                try
                {
                    browser.Disconnect();
                }
                catch (PuppeteerException)
                {
                    // Disconnect never asks Chromium to close; a broken socket needs no more work.
                }
            }
        }

        return heartbeatTask;
    }

    private static void ObserveHeartbeatInBackground(Task? heartbeatTask)
    {
        if (heartbeatTask is not null)
        {
            _ = ObserveHeartbeatCompletionAsync(heartbeatTask);
        }
    }

    private static async Task ObserveHeartbeatCompletionAsync(Task? heartbeatTask)
    {
        if (heartbeatTask is null)
        {
            return;
        }

        try
        {
            await heartbeatTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Observe cancellation or a broken browser connection outside the lifecycle gate.
        }
    }

    private static async Task<IBrowser> ConnectWithoutOwningBrowserAsync(
        ConnectOptions options,
        CancellationToken cancellationToken)
    {
        var connectTask = Puppeteer.ConnectAsync(options);
        try
        {
            return await connectTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = DisconnectLateConnectionAsync(connectTask);
            throw;
        }
    }

    private void PublishHealthFault(long generation, string detail)
    {
        if (generation == 0)
        {
            return;
        }

        var eventArgs = new WallpaperInjectionHealthFaultedEventArgs(generation, detail);
        var handlers = HealthFaulted;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<WallpaperInjectionHealthFaultedEventArgs> handler in
                 handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception)
            {
                // Health observers cannot be allowed to fault the lease-maintenance task.
            }
        }
    }

    private void PublishCapabilitiesChanged(
        long generation,
        CompatibilityCapabilities previous,
        CompatibilityCapabilities current)
    {
        var presentationContract = _presentationContractState.Current;
        if (previous == current &&
            presentationContract == _publishedPresentationContract)
        {
            return;
        }

        _publishedPresentationContract = presentationContract;
        var eventArgs = new WallpaperInjectionCapabilitiesChangedEventArgs(
            generation,
            previous,
            current,
            presentationContract);
        var handlers = CapabilitiesChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs> handler in
                 handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception)
            {
                // Capability observers cannot interrupt fail-closed page cleanup.
            }
        }
    }

    internal static void ResetHeartbeatFailureWindow(
        long observedGeneration,
        ref long failureGeneration,
        ref int consecutiveFailures)
    {
        if (observedGeneration <= 0 || observedGeneration == failureGeneration)
        {
            return;
        }

        failureGeneration = observedGeneration;
        consecutiveFailures = 0;
    }

    private static async Task DisconnectLateConnectionAsync(Task<IBrowser> connectTask)
    {
        try
        {
            var browser = await connectTask.ConfigureAwait(false);
            browser.Disconnect();
        }
        catch (Exception)
        {
            // Observe connection failures. Never call CloseAsync on the Codex-owned browser.
        }
    }

}

public class WallpaperInjectionException : InvalidOperationException
{
    public WallpaperInjectionException(string message)
        : base(message)
    {
    }

    public WallpaperInjectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WallpaperBrowserHandshakeException : WallpaperInjectionException
{
    public WallpaperBrowserHandshakeException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WallpaperMediaLoadException : WallpaperInjectionException
{
    public WallpaperMediaLoadException(string message)
        : base(message)
    {
    }
}

public sealed class WallpaperTargetAmbiguityException : WallpaperInjectionException
{
    public WallpaperTargetAmbiguityException(string message)
        : base(message)
    {
    }
}

public sealed class WallpaperPresentationContractException : WallpaperInjectionException
{
    public WallpaperPresentationContractException(string message)
        : base(message)
    {
    }
}
