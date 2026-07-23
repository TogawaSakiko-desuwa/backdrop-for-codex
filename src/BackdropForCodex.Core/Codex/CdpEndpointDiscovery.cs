using System.Net;
using System.Text;
using System.Text.Json;

namespace BackdropForCodex.Core.Codex;

public interface ICdpJsonTransport
{
    ValueTask<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default);
}

public sealed class HttpCdpJsonTransport : ICdpJsonTransport, IDisposable
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(2);
    public const int DefaultMaxResponseBytes = 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxResponseBytes;
    private readonly bool _ownsHttpClient;
    private int _disposed;

    /// <summary>
    /// Creates a transport whose handler cannot follow redirects or use a proxy. This is the
    /// preferred production constructor for the loopback-only discovery channel.
    /// </summary>
    public HttpCdpJsonTransport(
        TimeSpan? requestTimeout = null,
        int maxResponseBytes = DefaultMaxResponseBytes)
        : this(
            CreateLoopbackHttpClient(),
            requestTimeout,
            maxResponseBytes,
            ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Creates a transport over an externally managed client. Callers must disable automatic
    /// redirects and proxies; the response URI is still verified before any body is accepted.
    /// </summary>
    public HttpCdpJsonTransport(
        HttpClient httpClient,
        TimeSpan? requestTimeout = null,
        int maxResponseBytes = DefaultMaxResponseBytes)
        : this(httpClient, requestTimeout, maxResponseBytes, ownsHttpClient: false)
    {
    }

    private HttpCdpJsonTransport(
        HttpClient httpClient,
        TimeSpan? requestTimeout,
        int maxResponseBytes,
        bool ownsHttpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The CDP request timeout must be finite and positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBytes);

        _maxResponseBytes = maxResponseBytes;
        _ownsHttpClient = ownsHttpClient;
    }

    public async ValueTask<string> GetStringAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsSafeDiscoveryDocumentUri(uri))
        {
            throw new ArgumentException(
                "Only fixed CDP discovery documents on 127.0.0.1 may be requested.",
                nameof(uri));
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCancellation.Token).ConfigureAwait(false);

        if (response.RequestMessage?.RequestUri is not { } responseUri ||
            !HasSameOriginAndPath(uri, responseUri))
        {
            throw new HttpRequestException(
                "The CDP endpoint redirected outside the requested loopback document.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0 and var contentLength &&
            contentLength > _maxResponseBytes)
        {
            throw new CdpResponseTooLargeException(_maxResponseBytes);
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(timeoutCancellation.Token)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream(Math.Min(_maxResponseBytes, 16 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream
                .ReadAsync(chunk.AsMemory(), timeoutCancellation.Token)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > _maxResponseBytes)
            {
                throw new CdpResponseTooLargeException(_maxResponseBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static HttpClient CreateLoopbackHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = DefaultRequestTimeout,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static bool IsSafeDiscoveryDocumentUri(Uri uri) =>
        CdpEndpointIdentityVerifier.IsStrictIpv4LoopbackHttp(uri, requireRootPath: false) &&
        (string.Equals(uri.AbsolutePath, "/json/version", StringComparison.Ordinal) ||
         string.Equals(uri.AbsolutePath, "/json/list", StringComparison.Ordinal));

    private static bool HasSameOriginAndPath(Uri expected, Uri actual) =>
        IsSafeDiscoveryDocumentUri(actual) &&
        Uri.Compare(
            expected,
            actual,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.UriEscaped,
            StringComparison.Ordinal) == 0;
}

public sealed class CdpResponseTooLargeException : IOException
{
    public CdpResponseTooLargeException(int maxResponseBytes)
        : base($"The CDP discovery response exceeded {maxResponseBytes} bytes.")
    {
    }
}

public sealed record CdpEndpointIdentityResult(
    CdpEndpointRejection Rejection,
    string Detail,
    VerifiedCdpEndpoint? Endpoint)
{
    public bool IsVerified => Rejection == CdpEndpointRejection.None && Endpoint is not null;
}

public static class CdpEndpointIdentityVerifier
{
    public static CdpEndpointIdentityResult Verify(
        CdpEndpointCandidate candidate,
        CodexCompatibilityProfile profile,
        CdpBrowserVersion browser,
        IReadOnlyList<CdpTargetDescriptor> targets)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(targets);

        if (!IsStrictIpv4LoopbackHttp(candidate.BaseUri, requireRootPath: true))
        {
            return Reject(
                CdpEndpointRejection.NonLoopbackEndpoint,
                "The CDP HTTP endpoint is not a loopback-only endpoint.");
        }

        if (candidate.ProcessId <= 0 ||
            !profile.IsKnownExecutable(candidate.ExecutableName) ||
            !string.Equals(
                candidate.PackageFamilyName,
                profile.PackageFamilyName,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.PackageFullName,
                profile.PackageFullName,
                StringComparison.Ordinal) ||
            candidate.StartTimeUtc == default ||
            candidate.SessionId != WindowsCodexProcessSnapshotSource.CurrentSessionId)
        {
            return Reject(
                CdpEndpointRejection.ProcessIdentityMismatch,
                "The endpoint owner does not match the reviewed Codex package process identity.");
        }

        if (!IsReviewedChromiumProduct(browser.Browser) ||
            !string.Equals(browser.ProtocolVersion, "1.3", StringComparison.Ordinal))
        {
            return Reject(
                CdpEndpointRejection.UnexpectedBrowser,
                "The version response is not a reviewed Chromium CDP product.");
        }

        if (!TryValidateSocket(
                browser.WebSocketDebuggerUrl,
                candidate.BaseUri,
                "/devtools/browser/",
                expectedTargetId: null,
                out var browserSocket))
        {
            return Reject(
                CdpEndpointRejection.BrowserSocketMismatch,
                "The browser WebSocket endpoint is not on the same loopback port.");
        }

        if (targets.Any(target =>
                target is null ||
                string.IsNullOrWhiteSpace(target.Id) ||
                string.IsNullOrWhiteSpace(target.Type) ||
                target.Title is null ||
                string.IsNullOrWhiteSpace(target.Url)) ||
            targets.Select(target => target.Id).Distinct(StringComparer.Ordinal).Count() != targets.Count)
        {
            return Reject(
                CdpEndpointRejection.MalformedResponse,
                "The target list contains missing fields or duplicate target identifiers.");
        }

        var classified = targets
            .Select(target => new ClassifiedCdpTarget(
                target,
                CdpTargetClassifier.Classify(target, profile)))
            .ToArray();
        var codexTargets = classified
            .Where(target => target.Classification == CdpTargetClassification.CodexPage)
            .ToArray();

        if (codexTargets.Length == 0)
        {
            return Reject(
                CdpEndpointRejection.NoCodexTarget,
                "No reviewed Codex page target was exposed by this endpoint.");
        }

        if (codexTargets.Any(target =>
                !TryValidateSocket(
                    target.Target.WebSocketDebuggerUrl,
                    candidate.BaseUri,
                    "/devtools/page/",
                    target.Target.Id,
                    out _)))
        {
            return Reject(
                CdpEndpointRejection.TargetSocketMismatch,
                "A Codex page target points outside the candidate loopback endpoint.");
        }

        return new CdpEndpointIdentityResult(
            CdpEndpointRejection.None,
            "The endpoint is owned by the reviewed Codex package and exposes a Codex page.",
            new VerifiedCdpEndpoint(candidate, browser, browserSocket!, classified));
    }

    internal static bool IsStrictIpv4LoopbackHttp(Uri uri, bool requireRootPath) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, IPAddress.Loopback.ToString(), StringComparison.Ordinal) &&
        uri.Port is > 0 and <= ushort.MaxValue &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        (!requireRootPath || string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal));

    private static bool IsReviewedChromiumProduct(string? browser) =>
        !string.IsNullOrWhiteSpace(browser) &&
        (browser.StartsWith("Chrome/", StringComparison.Ordinal) ||
         browser.StartsWith("HeadlessChrome/", StringComparison.Ordinal));

    internal static bool TryValidateSocket(
        string? value,
        Uri expectedHttpBaseUri,
        string expectedPathPrefix,
        string? expectedTargetId,
        out Uri? socket)
    {
        socket = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, "ws", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parsed.Host, expectedHttpBaseUri.Host, StringComparison.Ordinal) ||
            parsed.Port != expectedHttpBaseUri.Port ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            !parsed.AbsolutePath.StartsWith(expectedPathPrefix, StringComparison.Ordinal) ||
            parsed.AbsolutePath.Length == expectedPathPrefix.Length ||
            parsed.AbsolutePath[expectedPathPrefix.Length..].Contains('/', StringComparison.Ordinal) ||
            (expectedTargetId is not null &&
             !string.Equals(
                 parsed.AbsolutePath,
                 expectedPathPrefix + Uri.EscapeDataString(expectedTargetId),
                 StringComparison.Ordinal)))
        {
            return false;
        }

        socket = parsed;
        return true;
    }

    private static CdpEndpointIdentityResult Reject(
        CdpEndpointRejection rejection,
        string detail) => new(rejection, detail, null);
}

public sealed class CdpEndpointDiscovery
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly ICdpEndpointCandidateSource _candidateSource;
    private readonly ICdpJsonTransport _transport;
    public CdpEndpointDiscovery(
        ICdpEndpointCandidateSource candidateSource,
        ICdpJsonTransport transport)
    {
        _candidateSource = candidateSource ?? throw new ArgumentNullException(nameof(candidateSource));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async ValueTask<CdpDiscoveryResult> DiscoverAsync(
        CodexCompatibilityProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var candidates = await _candidateSource
            .GetCandidatesAsync(profile, cancellationToken)
            .ConfigureAwait(false);
        var endpoints = new List<VerifiedCdpEndpoint>();
        var rejections = new List<CdpEndpointProbe>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeCandidate(candidate))
            {
                rejections.Add(new CdpEndpointProbe(
                    candidate,
                    CdpEndpointRejection.NonLoopbackEndpoint,
                    "Only root-level loopback HTTP candidates are probed."));
                continue;
            }

            try
            {
                var versionJson = await _transport.GetStringAsync(
                    new Uri(candidate.BaseUri, "json/version"),
                    cancellationToken).ConfigureAwait(false);
                var targetsJson = await _transport.GetStringAsync(
                    new Uri(candidate.BaseUri, "json/list"),
                    cancellationToken).ConfigureAwait(false);

                var currentCandidates = await _candidateSource
                    .GetCandidatesAsync(profile, cancellationToken)
                    .ConfigureAwait(false);
                if (!currentCandidates.Any(current => IsSameCandidate(candidate, current)))
                {
                    rejections.Add(new CdpEndpointProbe(
                        candidate,
                        CdpEndpointRejection.ProcessIdentityMismatch,
                        "The CDP listener ownership changed while its identity was being verified."));
                    continue;
                }

                var browser = JsonSerializer.Deserialize<CdpBrowserVersion>(
                    versionJson,
                    SerializerOptions);
                var targets = JsonSerializer.Deserialize<CdpTargetDescriptor[]>(
                    targetsJson,
                    SerializerOptions);
                if (browser is null || targets is null)
                {
                    rejections.Add(new CdpEndpointProbe(
                        candidate,
                        CdpEndpointRejection.MalformedResponse,
                        "The CDP discovery documents were empty."));
                    continue;
                }

                var identity = CdpEndpointIdentityVerifier.Verify(candidate, profile, browser, targets);
                if (identity.IsVerified)
                {
                    endpoints.Add(identity.Endpoint!);
                }
                else
                {
                    rejections.Add(new CdpEndpointProbe(
                        candidate,
                        identity.Rejection,
                        identity.Detail));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                rejections.Add(new CdpEndpointProbe(
                    candidate,
                    CdpEndpointRejection.Unreachable,
                    "The CDP endpoint timed out."));
            }
            catch (HttpRequestException exception)
            {
                rejections.Add(new CdpEndpointProbe(
                    candidate,
                    CdpEndpointRejection.Unreachable,
                    exception.Message));
            }
            catch (JsonException exception)
            {
                rejections.Add(new CdpEndpointProbe(
                    candidate,
                    CdpEndpointRejection.MalformedResponse,
                    exception.Message));
            }
            catch (CdpResponseTooLargeException exception)
            {
                rejections.Add(new CdpEndpointProbe(
                    candidate,
                    CdpEndpointRejection.MalformedResponse,
                    exception.Message));
            }
        }

        return new CdpDiscoveryResult(endpoints, rejections);
    }

    private static bool IsSafeCandidate(CdpEndpointCandidate candidate) =>
        CdpEndpointIdentityVerifier.IsStrictIpv4LoopbackHttp(
            candidate.BaseUri,
            requireRootPath: true);

    private static bool IsSameCandidate(
        CdpEndpointCandidate expected,
        CdpEndpointCandidate current) =>
        expected.ProcessId == current.ProcessId &&
        expected.StartTimeUtc == current.StartTimeUtc &&
        expected.SessionId == current.SessionId &&
        string.Equals(expected.ExecutableName, current.ExecutableName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            expected.PackageFamilyName,
            current.PackageFamilyName,
            StringComparison.Ordinal) &&
        string.Equals(
            expected.PackageFullName,
            current.PackageFullName,
            StringComparison.Ordinal) &&
        expected.BaseUri == current.BaseUri;
}
