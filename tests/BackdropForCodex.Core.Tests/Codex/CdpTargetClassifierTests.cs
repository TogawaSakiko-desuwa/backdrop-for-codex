using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class CdpTargetClassifierTests
{
    private readonly CodexCompatibilityProfile _profile = CodexCompatibilityTests.GetProfile();

    [Theory]
    [InlineData("file:///C:/Program%20Files/WindowsApps/OpenAI.Codex_26.715.10079.0_x64__2p2nqsd0c76g0/app/index.html")]
    [InlineData("file:///C:/Program%20Files/WindowsApps/OpenAI.Codex_26.715.10079.0_x64__2p2nqsd0c76g0/app/index.html?route=home")]
    [InlineData("file:///C:/Program%20Files/WindowsApps/OpenAI.Codex_26.715.10079.0_x64__2p2nqsd0c76g0/app/index.html#conversation")]
    [InlineData("https://chatgpt.com/codex")]
    [InlineData("https://chatgpt.com/CODEX/")]
    [InlineData("https://chatgpt.com/codex/tasks/123?view=workspace#activity")]
    [InlineData("https://chatgpt.com/codex/login-history/authors/oauth-client")]
    [InlineData("https://codex.openai.com/")]
    [InlineData("https://codex.openai.com/codex")]
    [InlineData("https://codex.openai.com/Codex/workspaces/current/?view=files#editor")]
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
    [InlineData("26.721.3996.0")]
    [InlineData("26.721.4000.0")]
    public void Classify_UsesActualPackageRootForExactAndReviewedBandProfiles(string version)
    {
        var parsedVersion = Version.Parse(version);
        var profile = CodexCompatibilityTests.GetProfile(parsedVersion);
        var matchingUrl =
            $"file:///C:/Program%20Files/WindowsApps/" +
            $"OpenAI.Codex_{parsedVersion}_x64__2p2nqsd0c76g0/app/index.html";
        const string olderPackageUrl =
            "file:///C:/Program%20Files/WindowsApps/" +
            "OpenAI.Codex_26.721.3404.0_x64__2p2nqsd0c76g0/app/index.html";

        Assert.Equal(
            CdpTargetClassification.CodexPage,
            CdpTargetClassifier.Classify(
                Target("page", "Codex", matchingUrl),
                profile));
        Assert.Equal(
            CdpTargetClassification.OtherPage,
            CdpTargetClassifier.Classify(
                Target("page", "Codex", olderPackageUrl),
                profile));
    }

    [Fact]
    public void Classify_RejectsPackagedFilePageWithoutObservedPackageRoot()
    {
        var version = CodexCompatibilityCatalog.SupportedPackageVersion;
        var packageFullName =
            $"OpenAI.Codex_{version}_x64__2p2nqsd0c76g0";
        var profileWithoutRoot = CodexCompatibilityCatalog.Evaluate(
            new CodexPackageDescriptor(
                CodexCompatibilityCatalog.OfficialPackageName,
                CodexCompatibilityCatalog.OfficialPackageFamilyName,
                version,
                CodexPackageArchitecture.X64,
                CodexCompatibilityCatalog.OfficialApplicationId,
                packageFullName),
            new CodexRuntimeDescriptor(
                IsWindows: true,
                new Version(10, 0, 26100, 0),
                CodexPackageArchitecture.X64)).Profile!;
        var target = Target(
            "page",
            "Codex",
            $"file:///C:/Program%20Files/WindowsApps/{packageFullName}/app/index.html");

        Assert.Equal(
            CdpTargetClassification.OtherPage,
            CdpTargetClassifier.Classify(target, profileWithoutRoot));
    }

    [Theory]
    [InlineData("file:///C:/Users/Alice/Codex/index.html")]
    [InlineData("app://evil/index.html")]
    [InlineData("app://codex/auth/index.html")]
    [InlineData("codex://evil/index.html")]
    [InlineData("http://127.0.0.2/app")]
    [InlineData("http://127.0.0.1/auth")]
    [InlineData("http://127.0.0.1:4100/app")]
    [InlineData("https://127.0.0.1:4100/index.html")]
    [InlineData("https://chatgpt.com/auth")]
    [InlineData("https://chatgpt.com/codexevil")]
    [InlineData("https://chatgpt.com/codex-evil")]
    [InlineData("https://chatgpt.com/codex/login")]
    [InlineData("https://chatgpt.com/codex/AUTH/callback")]
    [InlineData("https://chatgpt.com/codex/oauth/callback")]
    [InlineData("https://chatgpt.com/codex/%6Cogin")]
    [InlineData("https://codex.openai.com/anything")]
    [InlineData("https://codex.openai.com/login")]
    [InlineData("https://codex.openai.com/AUTH/callback")]
    [InlineData("https://codex.openai.com/oauth/callback")]
    [InlineData("https://codex.openai.com/codexevil")]
    [InlineData("https://codex.openai.com/codex/login")]
    [InlineData("https://codex.openai.com/codex/%61uth")]
    [InlineData("file:///C:/tmp/Program%20Files/WindowsApps/OpenAI.Codex_26.715.10079.0_x64__2p2nqsd0c76g0/app/index.html")]
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
