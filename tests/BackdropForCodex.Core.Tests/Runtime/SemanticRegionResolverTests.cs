using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Runtime;

public sealed class SemanticRegionResolverTests
{
    [Fact]
    public void Resolve_MapsReviewedRouteAndDomFeatureTokens()
    {
        var resolver = new ReviewedSemanticRegionResolver(
        [
            new(
                "generic-v1",
                "conversation",
                SemanticRegion.Conversation,
                ["composer", "message-list"]),
        ]);
        var evidence = new SemanticRegionEvidence(
            "GENERIC-V1",
            "Conversation",
            ["message-list", "composer", "sidebar"]);

        var region = resolver.Resolve(evidence);

        Assert.Equal(SemanticRegion.Conversation, region);
    }

    [Theory]
    [InlineData("unknown-package", "conversation")]
    [InlineData("generic-v1", "unknown-route")]
    public void Resolve_UnknownObservationFallsBackToGlobal(
        string packageId,
        string routeFeature)
    {
        var resolver = new ReviewedSemanticRegionResolver(
        [
            new(
                "generic-v1",
                "conversation",
                SemanticRegion.Conversation,
                ["message-list"]),
        ]);

        var region = resolver.Resolve(
            new SemanticRegionEvidence(
                packageId,
                routeFeature,
                ["message-list"]));

        Assert.Equal(SemanticRegion.Global, region);
    }

    [Fact]
    public void Resolve_AmbiguousReviewedRulesFallBackToGlobal()
    {
        var resolver = new ReviewedSemanticRegionResolver(
        [
            new("generic-v1", "work", SemanticRegion.Conversation),
            new("generic-v1", "work", SemanticRegion.CodeAndDiff),
        ]);

        var region = resolver.Resolve(
            new SemanticRegionEvidence("generic-v1", "work"));

        Assert.Equal(SemanticRegion.Global, region);
    }

    [Fact]
    public void Evidence_RejectsSelectorLikeTokens()
    {
        Assert.Throws<ArgumentException>(
            () => new SemanticRegionEvidence(
                "generic-v1",
                "conversation",
                ["div[data-testid=chat]"]));
    }

    [Fact]
    public void GlobalOnlyResolverAlwaysUsesGlobalFallback()
    {
        var evidence = new SemanticRegionEvidence(
            "generic-v1",
            "settings",
            ["settings-root"]);

        Assert.Equal(
            SemanticRegion.Global,
            GlobalSemanticRegionResolver.Instance.Resolve(evidence));
    }
}
