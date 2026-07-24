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
    private const string MainDocumentReadyExpression =
        "Boolean(document.documentElement && document.body && document.querySelector('main'))";

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

        var eligiblePages = new List<IPage>();
        foreach (var page in activePages)
        {
            if (await IsEligibleMainPageAsync(page, endpoint, cancellationToken)
                    .ConfigureAwait(false))
            {
                eligiblePages.Add(page);
            }
        }

        return new VerifiedCodexPageScan(activePages, eligiblePages);
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

        return Uri.Compare(
            pageUri,
            reviewedUri,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    public async Task<bool> IsEligibleMainPageAsync(
        IPage page,
        VerifiedCdpEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var targetId = await GetTargetIdAsync(page, cancellationToken).ConfigureAwait(false);
        if (targetId is null)
        {
            return false;
        }

        try
        {
            var title = await page.GetTitleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!title.Contains("Codex", StringComparison.OrdinalIgnoreCase) ||
                !IsEligibleTargetDocument(targetId, page.Url, endpoint))
            {
                return false;
            }

            return await page.EvaluateExpressionAsync<bool>(MainDocumentReadyExpression)
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
}

internal sealed class VerifiedCodexPageScan
{
    public VerifiedCodexPageScan(
        IReadOnlySet<IPage> activePages,
        IReadOnlyList<IPage> eligiblePages)
    {
        ActivePages = activePages ?? throw new ArgumentNullException(nameof(activePages));
        EligiblePages = eligiblePages ?? throw new ArgumentNullException(nameof(eligiblePages));
    }

    public IReadOnlySet<IPage> ActivePages { get; }

    public IReadOnlyList<IPage> EligiblePages { get; }
}
