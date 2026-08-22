using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class VerifiedCdpEndpointTests
{
    [Fact]
    public void Constructor_DefensivelyCopiesMutableTargetCollections()
    {
        var reviewed = ClassifiedTarget(
            "codex-page",
            "app://codex/index.html",
            CdpTargetClassification.CodexPage);
        var replacement = ClassifiedTarget(
            "replacement-page",
            "app://codex/replacement.html",
            CdpTargetClassification.CodexPage);
        ClassifiedCdpTarget[] array = [reviewed];
        var list = new List<ClassifiedCdpTarget> { reviewed };
        var endpointFromArray = CreateEndpoint(array);
        var endpointFromList = CreateEndpoint(list);

        array[0] = replacement;
        list[0] = replacement;

        AssertRetainsReviewedAuthorization(endpointFromArray);
        AssertRetainsReviewedAuthorization(endpointFromList);
    }

    [Fact]
    public void ExposedTargetCollections_CannotBeModified()
    {
        var endpoint = CreateEndpoint([
            ClassifiedTarget(
                "codex-page",
                "app://codex/index.html",
                CdpTargetClassification.CodexPage),
        ]);
        var replacement = ClassifiedTarget(
            "replacement-page",
            "app://codex/replacement.html",
            CdpTargetClassification.CodexPage);
        var exposedTargets = Assert.IsAssignableFrom<IList<ClassifiedCdpTarget>>(
            endpoint.Targets);
        var exposedInjectableTargets = Assert.IsAssignableFrom<IList<CdpTargetDescriptor>>(
            endpoint.InjectableTargets);

        Assert.Throws<NotSupportedException>(() => exposedTargets[0] = replacement);
        Assert.Throws<NotSupportedException>(
            () => exposedInjectableTargets[0] = replacement.Target);

        AssertRetainsReviewedAuthorization(endpoint);
    }

    private static void AssertRetainsReviewedAuthorization(VerifiedCdpEndpoint endpoint)
    {
        Assert.Equal("codex-page", Assert.Single(endpoint.InjectableTargets).Id);
        Assert.True(VerifiedCodexPageSelector.IsReviewedTargetDocument(
            "codex-page",
            "app://codex/index.html?thread=1",
            endpoint));
        Assert.False(VerifiedCodexPageSelector.IsReviewedTargetDocument(
            "replacement-page",
            "app://codex/replacement.html",
            endpoint));
    }

    private static VerifiedCdpEndpoint CreateEndpoint(
        IReadOnlyList<ClassifiedCdpTarget> targets)
    {
        var identity = CodexSecurityValidatorTests.GetIdentity();
        return new VerifiedCdpEndpoint(
            new CdpEndpointCandidate(
                1234,
                "ChatGPT.exe",
                identity.PackageFamilyName,
                identity.PackageFullName,
                new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
                WindowsCodexProcessSnapshotSource.CurrentSessionId,
                new Uri("http://127.0.0.1:9222/")),
            new CdpBrowserVersion(
                "Chrome/140.0.0.0",
                "1.3",
                null,
                null,
                "ws://127.0.0.1:9222/devtools/browser/browser-id"),
            new Uri("ws://127.0.0.1:9222/devtools/browser/browser-id"),
            targets,
            identity);
    }

    private static ClassifiedCdpTarget ClassifiedTarget(
        string id,
        string url,
        CdpTargetClassification classification) => new(
        new CdpTargetDescriptor(
            id,
            "page",
            "Codex",
            url,
            $"ws://127.0.0.1:9222/devtools/page/{id}"),
        classification);
}
