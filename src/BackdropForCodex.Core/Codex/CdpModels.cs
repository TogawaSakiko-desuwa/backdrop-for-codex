using System.Text.Json.Serialization;

namespace BackdropForCodex.Core.Codex;

public sealed record CodexProcessSnapshot(
    int ProcessId,
    string ExecutableName,
    string PackageFamilyName,
    string PackageFullName,
    DateTimeOffset StartTimeUtc,
    int SessionId,
    string? CommandLine);

public sealed record CdpEndpointCandidate(
    int ProcessId,
    string ExecutableName,
    string PackageFamilyName,
    string PackageFullName,
    DateTimeOffset StartTimeUtc,
    int SessionId,
    Uri BaseUri);

public sealed record CdpBrowserVersion(
    [property: JsonPropertyName("Browser")] string Browser,
    [property: JsonPropertyName("Protocol-Version")] string ProtocolVersion,
    [property: JsonPropertyName("User-Agent")] string? UserAgent,
    [property: JsonPropertyName("V8-Version")] string? V8Version,
    [property: JsonPropertyName("webSocketDebuggerUrl")] string WebSocketDebuggerUrl);

public sealed record CdpTargetDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);

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

public sealed record ClassifiedCdpTarget(
    CdpTargetDescriptor Target,
    CdpTargetClassification Classification);

public sealed record VerifiedCdpEndpoint
{
    internal VerifiedCdpEndpoint(
        CdpEndpointCandidate candidate,
        CdpBrowserVersion browser,
        Uri browserWebSocketUri,
        IReadOnlyList<ClassifiedCdpTarget> targets)
    {
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        Browser = browser ?? throw new ArgumentNullException(nameof(browser));
        BrowserWebSocketUri = browserWebSocketUri ??
            throw new ArgumentNullException(nameof(browserWebSocketUri));
        Targets = targets ?? throw new ArgumentNullException(nameof(targets));
        VerifiedAtUtc = DateTimeOffset.UtcNow;
    }

    public CdpEndpointCandidate Candidate { get; }

    public CdpBrowserVersion Browser { get; }

    public Uri BrowserWebSocketUri { get; }

    public IReadOnlyList<ClassifiedCdpTarget> Targets { get; }

    public DateTimeOffset VerifiedAtUtc { get; }

    public IReadOnlyList<CdpTargetDescriptor> InjectableTargets => Targets
        .Where(target => target.Classification == CdpTargetClassification.CodexPage)
        .Select(target => target.Target)
        .ToArray();
}

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
