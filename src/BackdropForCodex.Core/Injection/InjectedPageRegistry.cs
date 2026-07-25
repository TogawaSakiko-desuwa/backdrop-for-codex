using PuppeteerSharp;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Owns the page mutations and pending cleanup obligations created by one injection session.
/// </summary>
internal sealed class InjectedPageRegistry
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly Dictionary<IPage, long> _mutatedPages =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IPage, long> _preparedPages =
        new(ReferenceEqualityComparer.Instance);

    public int MutatedCount => _mutatedPages.Count;

    public IPage[] GetMutatedPages() => [.. _mutatedPages.Keys];

    public bool IsMutatedForGeneration(IPage page, long generation) =>
        _mutatedPages.TryGetValue(page, out var trackedGeneration) &&
        trackedGeneration == generation;

    public void MarkMutated(IPage page, long generation)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        _mutatedPages[page] = generation;
    }

    public bool RemoveMutation(IPage page, out long generation) =>
        _mutatedPages.Remove(page, out generation);

    public void PruneClosedPages(IReadOnlySet<IPage> activePages)
    {
        ArgumentNullException.ThrowIfNull(activePages);
        foreach (var oldPage in _mutatedPages.Keys.Where(page => !activePages.Contains(page))
                     .ToArray())
        {
            _mutatedPages.Remove(oldPage);
        }

        foreach (var oldPage in _preparedPages.Keys.Where(page => !activePages.Contains(page))
                     .ToArray())
        {
            _preparedPages.Remove(oldPage);
        }
    }

    public async Task CleanupTrackedPageAsync(
        IPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
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
            await CleanupOrTrackPendingAsync(
                    page,
                    cleanupGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<bool> CleanupOrTrackPendingAsync(
        IPage page,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        bool cleaned;
        try
        {
            cleaned = await PuppeteerPageScriptExecutor
                .CleanupBestEffortAsync(
                    page,
                    generation,
                    CleanupTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TrackPendingCleanup(page, generation);
            throw;
        }

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

    public void TrackPendingCleanup(IPage page, long generation)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        if (_preparedPages.TryGetValue(page, out var existingGeneration))
        {
            generation = Math.Max(generation, existingGeneration);
        }

        _preparedPages[page] = generation;
    }

    public void RemovePendingCleanupUpTo(IPage page, long generation)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        if (_preparedPages.TryGetValue(page, out var pendingGeneration) &&
            pendingGeneration <= generation)
        {
            _preparedPages.Remove(page);
        }
    }

    public async Task CleanupAllAsync()
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

        if (pagesToClean.Count == 0)
        {
            return;
        }

        using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
        foreach (var entry in pagesToClean)
        {
            try
            {
                await PuppeteerPageScriptExecutor.TryEvaluateAsync(
                        entry.Key,
                        InjectionScriptBuilder.BuildCleanup(entry.Value),
                        cleanupCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public void Reset()
    {
        _mutatedPages.Clear();
        _preparedPages.Clear();
    }

    internal bool TryGetPendingGeneration(IPage page, out long generation) =>
        _preparedPages.TryGetValue(page, out generation);
}

internal static class PuppeteerPageScriptExecutor
{
    public static async Task<bool> TryEvaluateAsync(
        IPage page,
        string expression,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
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

    public static async Task<bool> CleanupBestEffortAsync(
        IPage page,
        long generation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var cleanupCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cleanupCancellation.CancelAfter(timeout);
        try
        {
            return await TryEvaluateAsync(
                    page,
                    InjectionScriptBuilder.BuildCleanup(generation),
                    cleanupCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }
}
