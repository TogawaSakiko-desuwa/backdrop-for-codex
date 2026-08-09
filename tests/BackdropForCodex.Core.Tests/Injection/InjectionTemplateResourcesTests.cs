using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed partial class InjectionTemplateResourcesTests
{
    private const string InstallScriptResourceName =
        "BackdropForCodex.Core.Injection.Templates.Install.js";
    private const string WallpaperStyleResourceName =
        "BackdropForCodex.Core.Injection.Templates.Wallpaper.css";

    [Fact]
    public void Templates_AreEmbeddedInCoreAssemblyUnderStableLogicalNames()
    {
        var assembly = typeof(InjectionScriptBuilder).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();

        Assert.Equal(
            InstallScriptResourceName,
            InjectionTemplateResources.InstallScriptResourceName);
        Assert.Equal(
            WallpaperStyleResourceName,
            InjectionTemplateResources.WallpaperStyleResourceName);
        Assert.Contains(InstallScriptResourceName, resourceNames);
        Assert.Contains(WallpaperStyleResourceName, resourceNames);

        using var installStream = assembly.GetManifestResourceStream(InstallScriptResourceName);
        using var styleStream = assembly.GetManifestResourceStream(WallpaperStyleResourceName);
        Assert.NotNull(installStream);
        Assert.NotNull(styleStream);
        Assert.True(installStream.Length > 0);
        Assert.True(styleStream.Length > 0);
    }

    [Fact]
    public void InstallTemplate_ContainsSinglePayloadTokenExactlyOnce()
    {
        var template = InjectionTemplateResources.InstallScriptTemplate;

        Assert.Equal(1, CountOccurrences(template, InjectionInstallScriptModule.PayloadToken));
        Assert.Contains("style.textContent = cfg.styleSheet;", template, StringComparison.Ordinal);
        Assert.DoesNotContain("{{payload}}", template, StringComparison.Ordinal);
        Assert.DoesNotContain("__BACKDROP_FOR_CODEX_STYLESHEET__", template, StringComparison.Ordinal);
    }

    [Fact]
    public void StyleTemplate_UsesOnlyWhitelistedValueTokens()
    {
        string[] expectedTokens =
        [
            "__BFC_ADVANCED_BODY_SELECTOR__",
            "__BFC_DARK_OVERLAY__",
            "__BFC_FOCUS_X_PERCENT__",
            "__BFC_FOCUS_Y_PERCENT__",
            "__BFC_GLASS_BLUR_PIXELS__",
            "__BFC_GLASS_BLUE__",
            "__BFC_GLASS_BODY_SELECTOR__",
            "__BFC_GLASS_GREEN__",
            "__BFC_GLASS_OPACITY__",
            "__BFC_GLASS_OPACITY_PERCENT__",
            "__BFC_GLASS_RED__",
            "__BFC_GLASS_SATURATION__",
            "__BFC_HOME_HOVER_OPACITY_PERCENT__",
            "__BFC_LIGHT_OVERLAY__",
            "__BFC_MEDIA_OPACITY__",
            "__BFC_OBJECT_FIT__",
            "__BFC_ROOT_ID__",
        ];
        var actualTokens = StyleTokenRegex()
            .Matches(InjectionTemplateResources.WallpaperStyleTemplate)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedTokens.Length, actualTokens.Length);
        Assert.All(
            expectedTokens,
            expectedToken => Assert.Contains(expectedToken, actualTokens));
    }

    [Fact]
    public void BuildInstall_ConsumesTokensWithoutLeakingLocalPath()
    {
        const string LocalPath = @"C:\Users\Private\template-resource-secret.jpg";
        var options = new WallpaperInjectionOptions(
            9,
            LocalPath,
            321,
            WallpaperMediaKind.Image);

        var script = InjectionScriptBuilder.BuildInstall(options);

        Assert.DoesNotContain(InjectionInstallScriptModule.PayloadToken, script, StringComparison.Ordinal);
        Assert.DoesNotContain("__BACKDROP_FOR_CODEX_", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__BFC_", script, StringComparison.Ordinal);
        Assert.DoesNotContain(LocalPath, script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localMediaPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstall_SerializesStyleSheetInsideSingleJsonPayload()
    {
        const string StyleSheet = "body::before { content: `literal ${notCode}`; }\u2028/* line separator */";
        var options = new WallpaperInjectionOptions(
            9,
            @"C:\Wallpapers\wallpaper.png",
            321,
            WallpaperMediaKind.Image);

        var script = InjectionInstallScriptModule.BuildWithStyleSheet(
            options,
            PresentationContractCatalog.CreateFullySupportedCapabilities(),
            StyleSheet);
        var payloadJson = InjectionScriptPayloadTestHelper.ExtractPayloadJson(script);

        using var payload = JsonDocument.Parse(payloadJson);
        Assert.Equal(StyleSheet, payload.RootElement.GetProperty("styleSheet").GetString());
        Assert.Contains("style.textContent = cfg.styleSheet;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("style.textContent = `", script, StringComparison.Ordinal);
        Assert.DoesNotContain('\u2028', payloadJson);
        Assert.Contains("\\u2028", payloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(InjectionInstallScriptModule.PayloadToken, script, StringComparison.Ordinal);
        Assert.DoesNotContain("__BACKDROP_FOR_CODEX_", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStyleSheet_FormatsValidatedValuesUsingInvariantCulture()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var options = new WallpaperInjectionOptions(
                1,
                @"C:\Wallpapers\wallpaper.png",
                1234,
                WallpaperMediaKind.Image,
                WallpaperObjectFit.Cover,
                0.8,
                new GlassEffectOptions(10, 20, 30, 0.42, 18.5, 1.2),
                new WallpaperCompositionOptions(0.25, 0.75, 0.5, 0.1));

            var styleSheet = InjectionInstallScriptModule.BuildStyleSheet(
                options,
                PresentationContractCatalog.CreateFullySupportedCapabilities());

            Assert.Contains("rgba(10, 20, 30, 0.42)", styleSheet, StringComparison.Ordinal);
            Assert.Contains("object-position:\n        25%\n        75%;", styleSheet, StringComparison.Ordinal);
            Assert.Contains("opacity: 0.8;", styleSheet, StringComparison.Ordinal);
            Assert.Contains("--codex-wallpaper-blur: 18.5px;", styleSheet, StringComparison.Ordinal);
            Assert.DoesNotContain("0,42", styleSheet, StringComparison.Ordinal);
            Assert.DoesNotContain("18,5", styleSheet, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var searchIndex = 0;
        while ((searchIndex = value.IndexOf(token, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            searchIndex += token.Length;
        }

        return count;
    }

    [GeneratedRegex("__BFC_[A-Z0-9_]+__", RegexOptions.CultureInvariant)]
    private static partial Regex StyleTokenRegex();
}
