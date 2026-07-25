using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class CdpTargetClassifierTests
{
    private readonly VerifiedCodexIdentity _identity = CodexSecurityValidatorTests.GetIdentity();

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

        Assert.Equal(
            CdpTargetClassification.CodexPage,
            CdpTargetClassifier.Classify(target, _identity));
    }

    [Fact]
    public void Classify_AcceptsOnlyPackagedFilePageForMatchingVerifiedIdentity()
    {
        const string legacyUrl =
            "file:///C:/Program%20Files/WindowsApps/" +
            "OpenAI.Codex_26.715.10079.0_x64__2p2nqsd0c76g0/app/index.html";
        const string currentUrl =
            "file:///C:/Program%20Files/WindowsApps/" +
            "OpenAI.Codex_26.721.3404.0_x64__2p2nqsd0c76g0/app/index.html";
        var currentIdentity =
            CodexSecurityValidatorTests.GetIdentity(new Version(26, 721, 3404, 0));

        Assert.Equal(
            CdpTargetClassification.CodexPage,
            CdpTargetClassifier.Classify(Target("page", "Codex", legacyUrl), _identity));
        Assert.Equal(
            CdpTargetClassification.OtherPage,
            CdpTargetClassifier.Classify(Target("page", "Codex", currentUrl), _identity));
        Assert.Equal(
            CdpTargetClassification.CodexPage,
            CdpTargetClassifier.Classify(
                Target("page", "Codex", currentUrl),
                currentIdentity));
        Assert.Equal(
            CdpTargetClassification.OtherPage,
            CdpTargetClassifier.Classify(
                Target("page", "Codex", legacyUrl),
                currentIdentity));
    }

    [Theory]
    [InlineData("26.721.3996.0")]
    [InlineData("26.721.4000.0")]
    public void Classify_UsesActualPackageRootForAnyVerifiedVersion(string version)
    {
        var parsedVersion = Version.Parse(version);
        var identity = CodexSecurityValidatorTests.GetIdentity(parsedVersion);
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
                identity));
        Assert.Equal(
            CdpTargetClassification.OtherPage,
            CdpTargetClassifier.Classify(
                Target("page", "Codex", olderPackageUrl),
                identity));
    }

    [Fact]
    public void Classify_RejectsPackagedFilePageWithoutObservedPackageRoot()
    {
        var version = CodexSecurityValidatorTests.ReferencePackageVersion;
        var packageFullName =
            $"OpenAI.Codex_{version}_x64__2p2nqsd0c76g0";
        var identityWithoutRoot = CodexSecurityValidator.Validate(
            new CodexPackageDescriptor(
                CodexSecurityValidator.OfficialPackageName,
                CodexSecurityValidator.OfficialPackageFamilyName,
                version,
                CodexPackageArchitecture.X64,
                CodexSecurityValidator.OfficialApplicationId,
                packageFullName),
            new CodexRuntimeDescriptor(
                IsWindows: true,
                new Version(10, 0, 26100, 0),
                CodexPackageArchitecture.X64)).Identity!;
        var target = Target(
            "page",
            "Codex",
            $"file:///C:/Program%20Files/WindowsApps/{packageFullName}/app/index.html");

        Assert.Equal(
            CdpTargetClassification.OtherPage,
            CdpTargetClassifier.Classify(target, identityWithoutRoot));
    }

    [Theory]
    [InlineData("file:///C:/Users/Alice/Codex/index.html")]
    [InlineData("file:///C:/Program%20Files/WindowsApps/OpenAI.Codex_26.715.10079.0_x64__2p2nqsd0c76g0/app/index.html?initialRoute=%2Favatar-overlay")]
    [InlineData("app://evil/index.html")]
    [InlineData("app://codex/auth/index.html")]
    [InlineData("app://-/index.html?initialRoute=/avatar-overlay")]
    [InlineData("app://-/index.html?initialRoute=%2Favatar-overlay")]
    [InlineData("app://-/index.html?initialRoute=/home&initialRoute=%2Favatar-overlay")]
    [InlineData("app://codex/index.html?initialRoute=%2Favatar-overlay")]
    [InlineData("app://codex/index.html?%69nitialRoute=%2Favatar-overlay")]
    [InlineData("codex://evil/index.html")]
    [InlineData("codex://desktop/index.html?initialRoute=%2Favatar-overlay")]
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

        Assert.Equal(
            CdpTargetClassification.OtherPage,
            CdpTargetClassifier.Classify(target, _identity));
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
        Assert.Equal(
            expected,
            CdpTargetClassifier.Classify(Target(type, title, url), _identity));
    }

    private static CdpTargetDescriptor Target(string type, string title, string url) =>
        new("target", type, title, url, "ws://127.0.0.1:9222/devtools/page/target");
}
