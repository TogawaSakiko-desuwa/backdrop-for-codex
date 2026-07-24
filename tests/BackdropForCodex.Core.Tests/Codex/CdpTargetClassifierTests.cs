using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class CdpTargetClassifierTests
{
    private readonly CodexCompatibilityProfile _profile = CodexCompatibilityTests.GetProfile();

    [Theory]
    [InlineData("file:///C:/Program%20Files/WindowsApps/OpenAI.Codex_26.715.10079.0_x64__2p2nqsd0c76g0/app/index.html")]
    [InlineData("http://127.0.0.1:4100/app")]
    [InlineData("https://chatgpt.com/codex")]
    [InlineData("codex://desktop/index.html")]
    [InlineData("app://codex/index.html")]
    public void Classify_AcceptsReviewedCodexPages(string url)
    {
        var target = Target("page", "Codex", url);

        Assert.Equal(CdpTargetClassification.CodexPage, CdpTargetClassifier.Classify(target, _profile));
    }

    [Fact]
    public void Classify_AcceptsOnlyPackagedFilePageForMatchingReviewedProfile()
    {
        const string legacyUrl =
            "file:///C:/Program%20Files/WindowsApps/" +
            "OpenAI.Codex_26.715.10079.0_x64__2p2nqsd0c76g0/app/index.html";
        const string currentUrl =
            "file:///C:/Program%20Files/WindowsApps/" +
            "OpenAI.Codex_26.721.3404.0_x64__2p2nqsd0c76g0/app/index.html";
        var currentProfile = CodexCompatibilityTests.GetProfile(new Version(26, 721, 3404, 0));

        Assert.Equal(
            CdpTargetClassification.CodexPage,
            CdpTargetClassifier.Classify(Target("page", "Codex", legacyUrl), _profile));
        Assert.Equal(
            CdpTargetClassification.OtherPage,
            CdpTargetClassifier.Classify(Target("page", "Codex", currentUrl), _profile));
        Assert.Equal(
            CdpTargetClassification.CodexPage,
            CdpTargetClassifier.Classify(Target("page", "Codex", currentUrl), currentProfile));
        Assert.Equal(
            CdpTargetClassification.OtherPage,
            CdpTargetClassifier.Classify(Target("page", "Codex", legacyUrl), currentProfile));
    }

    [Theory]
    [InlineData("file:///C:/Users/Alice/Codex/index.html")]
    [InlineData("app://evil/index.html")]
    [InlineData("app://codex/auth/index.html")]
    [InlineData("codex://evil/index.html")]
    [InlineData("http://127.0.0.2/app")]
    [InlineData("http://127.0.0.1/auth")]
    [InlineData("https://chatgpt.com/auth")]
    public void Classify_RejectsLookalikeOrAuthenticationPages(string url)
    {
        var target = Target("page", "Codex", url);

        Assert.Equal(CdpTargetClassification.OtherPage, CdpTargetClassifier.Classify(target, _profile));
    }

    [Theory]
    [InlineData("page", "Codex", "https://evil.example/codex", CdpTargetClassification.OtherPage)]
    [InlineData("page", "Not Codex", "file:///C:/app/index.html", CdpTargetClassification.OtherPage)]
    [InlineData("page", "DevTools", "devtools://devtools/bundled/inspector.html", CdpTargetClassification.DeveloperTools)]
    [InlineData("page", "Extension", "chrome-extension://abc/index.html", CdpTargetClassification.Extension)]
    [InlineData("service_worker", "Codex", "https://chatgpt.com/sw.js", CdpTargetClassification.Worker)]
    public void Classify_SeparatesNonInjectableTargets(
        string type,
        string title,
        string url,
        CdpTargetClassification expected)
    {
        Assert.Equal(expected, CdpTargetClassifier.Classify(Target(type, title, url), _profile));
    }

    private static CdpTargetDescriptor Target(string type, string title, string url) =>
        new("target", type, title, url, "ws://127.0.0.1:9222/devtools/page/target");
}
