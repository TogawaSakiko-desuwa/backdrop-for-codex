using BackdropForCodex.Core.Codex;
using PuppeteerSharp;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Resolves Puppeteer pages back to the immutable target snapshot accepted by CDP verification.
/// A page that cannot prove both its target id and reviewed document path is never eligible.
/// </summary>
internal sealed class VerifiedCodexPageSelector
{
    private static readonly TimeSpan SessionDetachTimeout = TimeSpan.FromSeconds(1);

    private readonly Dictionary<IPage, string> _targetIds =
        new(ReferenceEqualityComparer.Instance);

    public async Task<VerifiedCodexPageScan> ScanAsync(
        IBrowser browser,
        VerifiedCdpEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(endpoint);

        var pages = await browser.PagesAsync(includeAll: true).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var activePages = pages
            .Where(page => !page.IsClosed)
            .ToHashSet((IEqualityComparer<IPage>)ReferenceEqualityComparer.Instance);

        foreach (var oldPage in _targetIds.Keys.Where(page => !activePages.Contains(page)).ToArray())
        {
            _targetIds.Remove(oldPage);
        }

        var eligiblePages = await FindEligiblePagesAsync(activePages, endpoint, cancellationToken)
            .ConfigureAwait(false);
        // Codex can replace its primary window during startup. A matching URL is only a
        // reason to repeat discovery. The new target ID and socket need their own proof.
        if (eligiblePages.Count == 0 && endpoint.DiscoverySource is { } discovery &&
            activePages.Any(page => Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) &&
                endpoint.InjectableTargets.Any(target => IsSameReviewedDocument(uri, target.Url))))
        {
            var refreshed = await discovery.RefreshAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (refreshed is not null &&
                Uri.TryCreate(refreshed.InjectableTargets[0].Url, UriKind.Absolute, out var replacement) &&
                endpoint.InjectableTargets.Any(target => IsSameReviewedDocument(replacement, target.Url)))
            {
                endpoint = refreshed;
                eligiblePages = await FindEligiblePagesAsync(activePages, endpoint, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new VerifiedCodexPageScan(activePages, eligiblePages, endpoint);
    }

    private async Task<List<IPage>> FindEligiblePagesAsync(
        IReadOnlySet<IPage> activePages,
        VerifiedCdpEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var eligiblePages = new List<IPage>();
        foreach (var page in activePages)
        {
            if (await IsEligibleVerifiedPageAsync(page, endpoint, cancellationToken)
                    .ConfigureAwait(false))
            {
                eligiblePages.Add(page);
            }
        }

        return eligiblePages;
    }

    public void Reset() => _targetIds.Clear();

    internal static bool TrySelectSoleEligiblePage(
        IReadOnlyList<IPage> eligiblePages,
        [NotNullWhen(true)] out IPage? selectedPage)
    {
        ArgumentNullException.ThrowIfNull(eligiblePages);
        if (eligiblePages.Count == 1 && eligiblePages[0] is not null)
        {
            selectedPage = eligiblePages[0];
            return true;
        }

        selectedPage = null;
        return false;
    }

    internal static bool IsReviewedTargetDocument(
        string targetId,
        string pageUrl,
        VerifiedCdpEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(targetId) ||
            !Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
        {
            return false;
        }

        if (CdpTargetClassifier.IsAuxiliaryApplicationPage(pageUri))
        {
            return false;
        }

        return endpoint.InjectableTargets.Any(target =>
            string.Equals(target.Id, targetId, StringComparison.Ordinal) &&
            IsSameReviewedDocument(pageUri, target.Url));
    }

    internal static bool IsEligibleTargetDocument(
        string targetId,
        string pageUrl,
        VerifiedCdpEndpoint endpoint) =>
        IsReviewedTargetDocument(targetId, pageUrl, endpoint);

    internal static bool IsSameReviewedDocument(Uri pageUri, string reviewedTargetUrl)
    {
        ArgumentNullException.ThrowIfNull(pageUri);
        if (!Uri.TryCreate(reviewedTargetUrl, UriKind.Absolute, out var reviewedUri))
        {
            return false;
        }

        if (CdpTargetClassifier.IsAuxiliaryApplicationPage(pageUri) ||
            CdpTargetClassifier.IsAuxiliaryApplicationPage(reviewedUri))
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

    public async Task<bool> IsEligibleVerifiedPageAsync(
        IPage page,
        VerifiedCdpEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (page.IsClosed)
        {
            return false;
        }

        var targetId = await GetTargetIdAsync(page, cancellationToken).ConfigureAwait(false);
        // Conversation switches may change only the title. Keep requiring the original verified
        // target id and document, including rejection of auxiliary initialRoute navigation.
        return targetId is not null &&
               !page.IsClosed &&
               IsEligibleTargetDocument(targetId, page.Url, endpoint);
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
                Task? detachTask = null;
                using var detachCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                detachCancellation.CancelAfter(SessionDetachTimeout);
                try
                {
                    detachTask = session.DetachAsync();
                    await detachTask.WaitAsync(detachCancellation.Token).ConfigureAwait(false);
                }
                catch (PuppeteerException)
                {
                }
                catch (OperationCanceledException)
                    when (detachCancellation.IsCancellationRequested)
                {
                    if (detachTask is not null)
                    {
                        _ = ObserveDetachCompletionAsync(detachTask);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
    }

    private static async Task ObserveDetachCompletionAsync(Task detachTask)
    {
        try
        {
            await detachTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The bounded cleanup path has already returned; observe late detach failure.
        }
    }
}

internal sealed class VerifiedCodexPageScan
{
    public VerifiedCodexPageScan(
        IReadOnlySet<IPage> activePages,
        IReadOnlyList<IPage> eligiblePages,
        VerifiedCdpEndpoint endpoint)
    {
        ActivePages = activePages ?? throw new ArgumentNullException(nameof(activePages));
        EligiblePages = eligiblePages ?? throw new ArgumentNullException(nameof(eligiblePages));
        Endpoint = endpoint;
    }

    public IReadOnlySet<IPage> ActivePages { get; }

    public IReadOnlyList<IPage> EligiblePages { get; }

    public VerifiedCdpEndpoint Endpoint { get; }
}
