using System.Net;
using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class CdpEndpointDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_VerifiesBrowserAndCodexPageOnSameLoopbackPort()
    {
        var candidate = Candidate("http://127.0.0.1:9222/");
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = VersionJson(9222),
            ["/json/list"] = TargetJson(9222),
        });
        var discovery = new CdpEndpointDiscovery(
            new StubCandidateSource([candidate]),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        var endpoint = Assert.Single(result.Endpoints);
        Assert.Empty(result.Rejections);
        Assert.Single(endpoint.InjectableTargets);
        Assert.Equal(new Uri("ws://127.0.0.1:9222/devtools/browser/browser-id"), endpoint.BrowserWebSocketUri);
    }

    [Fact]
    public async Task DiscoverAsync_VerifiesCurrentReviewedProfileEndToEnd()
    {
        const string packageFullName =
            "OpenAI.Codex_26.721.3404.0_x64__2p2nqsd0c76g0";
        const string pageUrl =
            "file:///C:/Program%20Files/WindowsApps/" +
            "OpenAI.Codex_26.721.3404.0_x64__2p2nqsd0c76g0/app/index.html";
        var profile = CodexCompatibilityTests.GetProfile(new Version(26, 721, 3404, 0));
        var candidate = Candidate("http://127.0.0.1:9222/", packageFullName);
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = VersionJson(9222),
            ["/json/list"] = TargetJson(9222, pageUrl),
        });
        var discovery = new CdpEndpointDiscovery(
            new StubCandidateSource([candidate]),
            transport);

        var result = await discovery.DiscoverAsync(profile);

        var endpoint = Assert.Single(result.Endpoints);
        Assert.Empty(result.Rejections);
        Assert.Equal(packageFullName, endpoint.Candidate.PackageFullName);
        Assert.Single(endpoint.InjectableTargets);
    }

    [Fact]
    public async Task DiscoverAsync_DoesNotProbeNonLoopbackCandidate()
    {
        var transport = new StubTransport(new Dictionary<string, string>());
        var discovery = new CdpEndpointDiscovery(
            new StubCandidateSource([Candidate("http://192.0.2.10:9222/")]),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(result.Endpoints);
        Assert.Equal(CdpEndpointRejection.NonLoopbackEndpoint, Assert.Single(result.Rejections).Rejection);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsCodexPageSocketOnDifferentPort()
    {
        var candidate = Candidate("http://127.0.0.1:9222/");
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = VersionJson(9222),
            ["/json/list"] = TargetJson(6553),
        });
        var discovery = new CdpEndpointDiscovery(
            new StubCandidateSource([candidate]),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(result.Endpoints);
        Assert.Equal(CdpEndpointRejection.TargetSocketMismatch, Assert.Single(result.Rejections).Rejection);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsEndpointWithoutCodexPage()
    {
        var candidate = Candidate("http://127.0.0.1:9222/");
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = VersionJson(9222),
            ["/json/list"] = """
                [{
                  "id":"foreign",
                  "type":"page",
                  "title":"Example",
                  "url":"https://example.com/",
                  "webSocketDebuggerUrl":"ws://127.0.0.1:9222/devtools/page/foreign"
                }]
                """,
        });
        var discovery = new CdpEndpointDiscovery(
            new StubCandidateSource([candidate]),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(result.Endpoints);
        Assert.Equal(CdpEndpointRejection.NoCodexTarget, Assert.Single(result.Rejections).Rejection);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsBrowserSocketOnDifferentLoopbackAddress()
    {
        var candidate = Candidate("http://127.0.0.1:9222/");
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = VersionJson(9222).Replace(
                "ws://127.0.0.1:",
                "ws://127.0.0.2:",
                StringComparison.Ordinal),
            ["/json/list"] = TargetJson(9222),
        });
        var discovery = new CdpEndpointDiscovery(
            new StubCandidateSource([candidate]),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(result.Endpoints);
        Assert.Equal(CdpEndpointRejection.BrowserSocketMismatch, Assert.Single(result.Rejections).Rejection);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsUnreviewedProtocolVersion()
    {
        var candidate = Candidate("http://127.0.0.1:9222/");
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = VersionJson(9222).Replace(
                "\"Protocol-Version\":\"1.3\"",
                "\"Protocol-Version\":\"2.0\"",
                StringComparison.Ordinal),
            ["/json/list"] = TargetJson(9222),
        });
        var discovery = new CdpEndpointDiscovery(
            new StubCandidateSource([candidate]),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(result.Endpoints);
        Assert.Equal(CdpEndpointRejection.UnexpectedBrowser, Assert.Single(result.Rejections).Rejection);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsTargetSocketWhoseIdDoesNotMatchDescriptor()
    {
        var candidate = Candidate("http://127.0.0.1:9222/");
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = VersionJson(9222),
            ["/json/list"] = TargetJson(9222).Replace(
                "/devtools/page/codex-page",
                "/devtools/page/different-target",
                StringComparison.Ordinal),
        });
        var discovery = new CdpEndpointDiscovery(
            new StubCandidateSource([candidate]),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(result.Endpoints);
        Assert.Equal(CdpEndpointRejection.TargetSocketMismatch, Assert.Single(result.Rejections).Rejection);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsMalformedNullTarget()
    {
        var candidate = Candidate("http://127.0.0.1:9222/");
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = VersionJson(9222),
            ["/json/list"] = "[null]",
        });
        var discovery = new CdpEndpointDiscovery(
            new StubCandidateSource([candidate]),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(result.Endpoints);
        Assert.Equal(CdpEndpointRejection.MalformedResponse, Assert.Single(result.Rejections).Rejection);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsListenerWhoseOwnershipChangesDuringProbe()
    {
        var candidate = Candidate("http://127.0.0.1:9222/");
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = VersionJson(9222),
            ["/json/list"] = TargetJson(9222),
        });
        var discovery = new CdpEndpointDiscovery(
            new RotatingCandidateSource([candidate], []),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(result.Endpoints);
        Assert.Equal(CdpEndpointRejection.ProcessIdentityMismatch, Assert.Single(result.Rejections).Rejection);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsPidReuseWhoseStartTimeChangesDuringProbe()
    {
        var candidate = Candidate("http://127.0.0.1:9222/");
        var replacement = candidate with { StartTimeUtc = candidate.StartTimeUtc.AddSeconds(1) };
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = VersionJson(9222),
            ["/json/list"] = TargetJson(9222),
        });
        var discovery = new CdpEndpointDiscovery(
            new RotatingCandidateSource([candidate], [replacement]),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        Assert.Empty(result.Endpoints);
        Assert.Equal(CdpEndpointRejection.ProcessIdentityMismatch, Assert.Single(result.Rejections).Rejection);
    }

    [Fact]
    public async Task DiscoverAsync_RecordsMalformedJsonWithoutThrowing()
    {
        var candidate = Candidate("http://127.0.0.1:9222/");
        var transport = new StubTransport(new Dictionary<string, string>
        {
            ["/json/version"] = "not-json",
            ["/json/list"] = "[]",
        });
        var discovery = new CdpEndpointDiscovery(
            new StubCandidateSource([candidate]),
            transport);

        var result = await discovery.DiscoverAsync(CodexCompatibilityTests.GetProfile());

        Assert.Equal(CdpEndpointRejection.MalformedResponse, Assert.Single(result.Rejections).Rejection);
    }

    private static CdpEndpointCandidate Candidate(
        string baseUri,
        string? packageFullName = null) => new(
        1234,
        "ChatGPT.exe",
        CodexCompatibilityCatalog.OfficialPackageFamilyName,
        packageFullName ?? CodexCompatibilityCatalog.SupportedPackageFullName,
        new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
        WindowsCodexProcessSnapshotSource.CurrentSessionId,
        new Uri(baseUri));

    private static string VersionJson(int port) => $$"""
        {
          "Browser":"Chrome/140.0.0.0",
          "Protocol-Version":"1.3",
          "User-Agent":"Mozilla/5.0",
          "V8-Version":"14.0",
          "webSocketDebuggerUrl":"ws://127.0.0.1:{{port}}/devtools/browser/browser-id"
        }
        """;

    private static string TargetJson(int port, string? pageUrl = null) => $$"""
        [{
          "id":"codex-page",
          "type":"page",
          "title":"Codex",
          "url":"{{pageUrl ?? "file:///C:/Program%20Files/WindowsApps/OpenAI.Codex_26.715.10079.0_x64__2p2nqsd0c76g0/app/index.html"}}",
          "webSocketDebuggerUrl":"ws://127.0.0.1:{{port}}/devtools/page/codex-page"
        }]
        """;

    private sealed class StubCandidateSource(IReadOnlyList<CdpEndpointCandidate> candidates)
        : ICdpEndpointCandidateSource
    {
        public ValueTask<IReadOnlyList<CdpEndpointCandidate>> GetCandidatesAsync(
            CodexCompatibilityProfile profile,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(profile);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(candidates);
        }
    }

    private sealed class RotatingCandidateSource(
        IReadOnlyList<CdpEndpointCandidate> first,
        IReadOnlyList<CdpEndpointCandidate> subsequent)
        : ICdpEndpointCandidateSource
    {
        private int _callCount;

        public ValueTask<IReadOnlyList<CdpEndpointCandidate>> GetCandidatesAsync(
            CodexCompatibilityProfile profile,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(profile);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                Interlocked.Increment(ref _callCount) == 1 ? first : subsequent);
        }
    }

    private sealed class StubTransport(IReadOnlyDictionary<string, string> responses)
        : ICdpJsonTransport
    {
        public int CallCount { get; private set; }

        public ValueTask<string> GetStringAsync(
            Uri uri,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (!responses.TryGetValue(uri.AbsolutePath, out var response))
            {
                throw new HttpRequestException(
                    "No response configured.",
                    null,
                    HttpStatusCode.NotFound);
            }

            return ValueTask.FromResult(response);
        }
    }
}
