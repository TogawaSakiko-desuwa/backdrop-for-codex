using System.Text.Json.Serialization;

namespace BackdropForCodex.Core.Codex;

/// <summary>
/// One observed Codex package process. This is discovery evidence, not proof that the process
/// still owns a CDP listener.
/// </summary>
public sealed record CodexProcessSnapshot(
    int ProcessId,
    string ExecutableName,
    string PackageFamilyName,
    string PackageFullName,
    DateTimeOffset StartTimeUtc,
    int SessionId,
    string? CommandLine);

/// <summary>
/// A possible CDP address correlated with an observed Codex package process and awaiting endpoint
/// identity and loopback verification.
/// </summary>
public sealed record CdpEndpointCandidate(
    int ProcessId,
    string ExecutableName,
    string PackageFamilyName,
    string PackageFullName,
    DateTimeOffset StartTimeUtc,
    int SessionId,
    Uri BaseUri);

/// <summary>
/// The untrusted browser metadata returned by the CDP <c>/json/version</c> document.
/// </summary>
public sealed record CdpBrowserVersion(
    [property: JsonPropertyName("Browser")] string Browser,
    [property: JsonPropertyName("Protocol-Version")] string ProtocolVersion,
    [property: JsonPropertyName("User-Agent")] string? UserAgent,
    [property: JsonPropertyName("V8-Version")] string? V8Version,
    [property: JsonPropertyName("webSocketDebuggerUrl")] string WebSocketDebuggerUrl);

/// <summary>
/// One untrusted target entry returned by the CDP <c>/json/list</c> document.
/// </summary>
public sealed record CdpTargetDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);

/// <summary>
/// The injection eligibility category assigned to a CDP target after reviewing its type, title,
/// and document URI against a verified Codex identity.
/// </summary>
public enum CdpTargetClassification
{
    Unsupported = 0,
    CodexPage,
    AuthenticationPage,
    DeveloperTools,
    Extension,
    Worker,
    OtherPage,
}

/// <summary>
/// Associates one CDP target descriptor with the classification produced during endpoint
/// verification.
/// </summary>
public sealed record ClassifiedCdpTarget(
    CdpTargetDescriptor Target,
    CdpTargetClassification Classification);

/// <summary>
/// Endpoint evidence whose supplied process identity fields, selected browser fields, socket URIs,
/// and targets passed verification at <see cref="VerifiedAtUtc"/>. Consumers must still revalidate
/// live listener and page ownership before mutation because external state can change afterward.
/// </summary>
public sealed record VerifiedCdpEndpoint
{
    internal VerifiedCdpEndpoint(
        CdpEndpointCandidate candidate,
        CdpBrowserVersion browser,
        Uri browserWebSocketUri,
        IReadOnlyList<ClassifiedCdpTarget> targets,
        VerifiedCodexIdentity identity)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Browser = browser ?? throw new ArgumentNullException(nameof(browser));
        BrowserWebSocketUri = browserWebSocketUri ??
            throw new ArgumentNullException(nameof(browserWebSocketUri));
        ArgumentNullException.ThrowIfNull(targets);
        var targetSnapshot = targets.ToArray();
        Targets = Array.AsReadOnly(targetSnapshot);
        InjectableTargets = Array.AsReadOnly(targetSnapshot
            .Where(target => target.Classification == CdpTargetClassification.CodexPage)
            .Select(target => target.Target)
            .ToArray());
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        VerifiedAtUtc = DateTimeOffset.UtcNow;
    }

    public CdpEndpointCandidate Candidate { get; }

    /// <summary>
    /// Browser metadata whose product and protocol fields passed verification. User-agent and V8
    /// fields are retained as reported and are not independently reviewed.
    /// </summary>
    public CdpBrowserVersion Browser { get; }

    public Uri BrowserWebSocketUri { get; }

    /// <summary>
    /// An immutable snapshot of the target response and classifications retained from
    /// verification.
    /// </summary>
    public IReadOnlyList<ClassifiedCdpTarget> Targets { get; }

    public VerifiedCodexIdentity Identity { get; }

    public DateTimeOffset VerifiedAtUtc { get; }

    /// <summary>
    /// An immutable snapshot of targets classified as reviewed Codex work pages.
    /// </summary>
    public IReadOnlyList<CdpTargetDescriptor> InjectableTargets { get; }
}

/// <summary>
/// Stable reason codes for candidates rejected during CDP endpoint discovery or verification.
/// </summary>
public enum CdpEndpointRejection
{
    None = 0,
    NonLoopbackEndpoint,
    ProcessIdentityMismatch,
    Unreachable,
    MalformedResponse,
    UnexpectedBrowser,
    BrowserSocketMismatch,
    NoCodexTarget,
    TargetSocketMismatch,
}

public sealed record CdpEndpointProbe(
    CdpEndpointCandidate Candidate,
    CdpEndpointRejection Rejection,
    string Detail);

public sealed record CdpDiscoveryResult(
    IReadOnlyList<VerifiedCdpEndpoint> Endpoints,
    IReadOnlyList<CdpEndpointProbe> Rejections)
{
    public bool Found => Endpoints.Count > 0;
}
