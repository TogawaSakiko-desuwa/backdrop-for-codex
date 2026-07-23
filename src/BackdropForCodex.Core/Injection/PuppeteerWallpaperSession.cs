using PuppeteerSharp;
using BackdropForCodex.Core.Codex;
using System.Diagnostics;
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

/// <summary>
/// Connects to a previously verified CDP endpoint. Disconnecting this controller never closes
/// Codex; <see cref="IBrowser.Disconnect"/> is used instead of CloseAsync/DisposeAsync.
/// </summary>
public sealed class PuppeteerWallpaperSession :
    IWallpaperInjectionSession,
    IWallpaperInjectionHealthSource
{
    private const int MaximumConsecutiveHeartbeatFailures = 3;
    private static readonly TimeSpan InitialPageReadinessTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialPageReadinessPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<IPage, long> _mutatedPages = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IPage, long> _preparedPages = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IPage, string> _targetIds = new(ReferenceEqualityComparer.Instance);
    private IBrowser? _browser;
    private VerifiedCdpEndpoint? _endpoint;
    private WallpaperInjectionOptions? _options;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatTask;
    private bool _paused;
    private int _disposed;

    public event EventHandler<WallpaperInjectionHealthFaultedEventArgs>? HealthFaulted;

    public bool IsActive =>
        _browser?.IsConnected == true &&
        _options is not null &&
        _heartbeatTask is { IsCompleted: false };

    public long Generation => _options?.Generation ?? 0;

    public async Task ApplyAsync(
        VerifiedCdpEndpoint endpoint,
        WallpaperInjectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_browser is null || !_browser.IsConnected || _endpoint != endpoint)
            {
                ObserveHeartbeatInBackground(await StopCoreAsync().ConfigureAwait(false));
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

            _options = options;
            var applyResult = await ApplyWhenPageIsReadyAsync(cancellationToken).ConfigureAwait(false);
            if (applyResult.AppliedCount == 0)
            {
                ObserveHeartbeatInBackground(await StopCoreAsync().ConfigureAwait(false));
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
        catch (WallpaperInjectionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveHeartbeatInBackground(await StopCoreAsync().ConfigureAwait(false));
            throw;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            throw;
        }
        catch (Exception exception)
        {
            ObserveHeartbeatInBackground(await StopCoreAsync().ConfigureAwait(false));
            throw new WallpaperInjectionException(
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
            if (generation == 0 || _mutatedPages.Count == 0)
            {
                throw new WallpaperInjectionException("No active wallpaper page can be paused.");
            }

            var script = InjectionScriptBuilder.BuildSetPaused(generation, paused);
            var applied = false;
            foreach (var page in _mutatedPages.Keys.ToArray())
            {
                applied |= await TryEvaluateAsync(page, script, cancellationToken).ConfigureAwait(false);
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
                        if (applyResult.AppliedCount != 0)
                        {
                            var heartbeat = InjectionScriptBuilder.BuildHeartbeat(_options.Generation);
                            foreach (var page in _mutatedPages.Keys.ToArray())
                            {
                                var alive = await TryEvaluateAsync(page, heartbeat, cancellationToken)
                                    .ConfigureAwait(false);
                                if (alive)
                                {
                                    healthyPageAvailable = true;
                                }
                                else
                                {
                                    var cleanupGeneration = observedGeneration;
                                    if (_mutatedPages.Remove(page, out var mutatedGeneration))
                                    {
                                        cleanupGeneration = Math.Max(
                                            cleanupGeneration,
                                            mutatedGeneration);
                                    }

                                    await CleanupOrTrackPendingAsync(page, cleanupGeneration)
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

    private async Task<PageApplyResult> ApplyWhenPageIsReadyAsync(
        CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var greatestEligibleCount = 0;
        do
        {
            var result = await ApplyToCurrentPagesAsync(cancellationToken).ConfigureAwait(false);
            greatestEligibleCount = Math.Max(greatestEligibleCount, result.EligibleCount);
            if (result.AppliedCount != 0)
            {
                return new PageApplyResult(greatestEligibleCount, result.AppliedCount);
            }

            var remaining = InitialPageReadinessTimeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new PageApplyResult(greatestEligibleCount, AppliedCount: 0);
            }

            await Task.Delay(
                    remaining < InitialPageReadinessPollInterval
                        ? remaining
                        : InitialPageReadinessPollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        while (true);
    }

    private async Task<PageApplyResult> ApplyToCurrentPagesAsync(
        CancellationToken cancellationToken)
    {
        if (_browser is null || _endpoint is null || _options is null)
        {
            return default;
        }

        var pages = await _browser.PagesAsync(includeAll: true).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var activePages = pages
            .Where(page => !page.IsClosed)
            .ToHashSet((IEqualityComparer<IPage>)ReferenceEqualityComparer.Instance);
        foreach (var oldPage in _mutatedPages.Keys.Where(page => !activePages.Contains(page)).ToArray())
        {
            _mutatedPages.Remove(oldPage);
        }

        foreach (var oldPage in _preparedPages.Keys.Where(page => !activePages.Contains(page)).ToArray())
        {
            _preparedPages.Remove(oldPage);
        }

        foreach (var oldPage in _targetIds.Keys.Where(page => !activePages.Contains(page)).ToArray())
        {
            _targetIds.Remove(oldPage);
        }

        var eligible = 0;
        var applied = 0;
        foreach (var page in activePages)
        {
            if (!await IsEligibleMainPageAsync(page, _endpoint, cancellationToken).ConfigureAwait(false))
            {
                var cleanupGeneration = 0L;
                if (_preparedPages.Remove(page, out var preparedGeneration))
                {
                    cleanupGeneration = preparedGeneration;
                }

                if (_mutatedPages.Remove(page, out var previousGeneration))
                {
                    cleanupGeneration = Math.Max(cleanupGeneration, previousGeneration);
                }

                if (cleanupGeneration != 0)
                {
                    await CleanupOrTrackPendingAsync(page, cleanupGeneration)
                        .ConfigureAwait(false);
                }

                continue;
            }

            eligible++;
            if (!_mutatedPages.TryGetValue(page, out var generation) || generation != _options.Generation)
            {
                var installed = await TryInstallMediaAsync(page, _options, cancellationToken)
                    .ConfigureAwait(false);
                if (!installed)
                {
                    continue;
                }

                _mutatedPages[page] = _options.Generation;
                if (_paused)
                {
                    await TryEvaluateAsync(
                        page,
                        InjectionScriptBuilder.BuildSetPaused(_options.Generation, paused: true),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            applied++;
        }

        return new PageApplyResult(eligible, applied);
    }

    private async Task<bool> TryInstallMediaAsync(
        IPage page,
        WallpaperInjectionOptions options,
        CancellationToken cancellationToken)
    {
        var activated = false;
        TrackPendingCleanup(page, options.Generation);
        try
        {
            var prepared = await TryEvaluateAsync(
                page,
                WrapPrepareExpression(InjectionScriptBuilder.BuildInstall(options)),
                cancellationToken).ConfigureAwait(false);
            if (!prepared ||
                !await IsEligibleMainPageAsync(page, _endpoint!, cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }

            await using var fileInput = await page
                .QuerySelectorAsync(
                    $"input[type=\"file\"]#{InjectionScriptBuilder.FileInputElementId}" +
                    $"[data-codex-wallpaper-owner=\"{InjectionScriptBuilder.Owner}\"]" +
                    $"[data-codex-wallpaper-generation=\"{options.Generation}\"]")
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (fileInput is null)
            {
                return false;
            }

            await fileInput.UploadFileAsync(resolveFilePaths: false, [options.LocalMediaPath])
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            activated = await TryEvaluateAsync(
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
            if (activated)
            {
                RemovePendingCleanupUpTo(page, options.Generation);
            }
            else
            {
                var cleaned = await CleanupPageBestEffortAsync(page, options.Generation)
                    .ConfigureAwait(false);
                if (cleaned || page.IsClosed)
                {
                    RemovePendingCleanupUpTo(page, options.Generation);
                }
                else
                {
                    TrackPendingCleanup(page, options.Generation);
                }
            }
        }
    }

    private async Task<bool> CleanupOrTrackPendingAsync(IPage page, long generation)
    {
        var cleaned = await CleanupPageBestEffortAsync(page, generation).ConfigureAwait(false);
        if (cleaned || page.IsClosed)
        {
            RemovePendingCleanupUpTo(page, generation);
        }
        else
        {
            TrackPendingCleanup(page, generation);
        }

        return cleaned;
    }

    private void TrackPendingCleanup(IPage page, long generation)
    {
        if (_preparedPages.TryGetValue(page, out var existingGeneration))
        {
            generation = Math.Max(generation, existingGeneration);
        }

        _preparedPages[page] = generation;
    }

    private void RemovePendingCleanupUpTo(IPage page, long generation)
    {
        if (_preparedPages.TryGetValue(page, out var pendingGeneration) &&
            pendingGeneration <= generation)
        {
            _preparedPages.Remove(page);
        }
    }

    private static async Task<bool> CleanupPageBestEffortAsync(IPage page, long generation)
    {
        using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            return await TryEvaluateAsync(
                page,
                InjectionScriptBuilder.BuildCleanup(generation),
                cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> IsEligibleMainPageAsync(
        IPage page,
        VerifiedCdpEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var targetId = await GetTargetIdAsync(page, cancellationToken).ConfigureAwait(false);
        if (targetId is null || !IsReviewedTargetDocument(targetId, page.Url, endpoint))
        {
            return false;
        }

        try
        {
            var title = await page.GetTitleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!title.Contains("Codex", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return await page.EvaluateExpressionAsync<bool>(
                    "Boolean(document.documentElement && document.body && document.querySelector('main'))")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PuppeteerException)
        {
            return false;
        }
    }

    private async Task<string?> GetTargetIdAsync(
        IPage page,
        CancellationToken cancellationToken)
    {
        if (_targetIds.TryGetValue(page, out var cached))
        {
            return cached;
        }

        ICDPSession? session = null;
        try
        {
            session = await page.CreateCDPSessionAsync().WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var response = await session.SendAsync<JsonElement>("Target.getTargetInfo")
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!response.TryGetProperty("targetInfo", out var targetInfo) ||
                !targetInfo.TryGetProperty("targetId", out var targetIdElement))
            {
                return null;
            }

            var targetId = targetIdElement.GetString();
            if (string.IsNullOrWhiteSpace(targetId))
            {
                return null;
            }

            _targetIds[page] = targetId;
            return targetId;
        }
        catch (PuppeteerException)
        {
            return null;
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await session.DetachAsync().ConfigureAwait(false);
                }
                catch (PuppeteerException)
                {
                }
            }
        }
    }

    internal static bool IsReviewedTargetDocument(
        string targetId,
        string pageUrl,
        VerifiedCdpEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(targetId) ||
            !Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
        {
            return false;
        }

        return endpoint.InjectableTargets.Any(target =>
            string.Equals(target.Id, targetId, StringComparison.Ordinal) &&
            IsSameReviewedDocument(pageUri, target.Url));
    }

    internal static bool IsSameReviewedDocument(Uri pageUri, string reviewedTargetUrl)
    {
        if (!Uri.TryCreate(reviewedTargetUrl, UriKind.Absolute, out var reviewedUri))
        {
            return false;
        }

        return Uri.Compare(
            pageUri,
            reviewedUri,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static string WrapPrepareExpression(string installExpression) =>
        $"(() => {{ const result = ({installExpression}); return result?.prepared === true; }})()";

    private static string WrapActivateExpression(string activateExpression) =>
        $"(async () => {{ const result = await ({activateExpression}); return result?.applied === true; }})()";

    private static async Task<bool> TryEvaluateAsync(
        IPage page,
        string expression,
        CancellationToken cancellationToken)
    {
        if (page.IsClosed)
        {
            return false;
        }

        try
        {
            return await page.EvaluateExpressionAsync<bool>(expression)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PuppeteerException)
        {
            return false;
        }
    }

    private async Task<Task?> StopCoreAsync()
    {
        var heartbeatCancellation = _heartbeatCancellation;
        var heartbeatTask = _heartbeatTask;
        _heartbeatCancellation = null;
        _heartbeatTask = null;
        heartbeatCancellation?.Cancel();
        heartbeatCancellation?.Dispose();
        ObserveHeartbeatInBackground(heartbeatTask);

        var generation = Generation;
        try
        {
            var pagesToClean = new Dictionary<IPage, long>(ReferenceEqualityComparer.Instance);
            foreach (var entry in _mutatedPages.Concat(_preparedPages))
            {
                pagesToClean[entry.Key] = pagesToClean.TryGetValue(
                    entry.Key,
                    out var existingGeneration)
                    ? Math.Max(existingGeneration, entry.Value)
                    : entry.Value;
            }

            if (generation > 0 || pagesToClean.Count != 0)
            {
                using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var entry in pagesToClean)
                {
                    try
                    {
                        await TryEvaluateAsync(
                            entry.Key,
                            InjectionScriptBuilder.BuildCleanup(entry.Value),
                            cleanupCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (cleanupCancellation.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            _mutatedPages.Clear();
            _preparedPages.Clear();
            _targetIds.Clear();
            _options = null;
            _endpoint = null;
            _paused = false;
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

    private readonly record struct PageApplyResult(int EligibleCount, int AppliedCount);
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

public sealed class WallpaperMediaLoadException : WallpaperInjectionException
{
    public WallpaperMediaLoadException(string message)
        : base(message)
    {
    }
}
