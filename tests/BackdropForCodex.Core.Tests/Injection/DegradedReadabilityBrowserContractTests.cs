using System.Globalization;
using System.Text.RegularExpressions;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Tests.Infrastructure;
using PuppeteerSharp;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

[Collection("Reviewed selector browser contracts")]
public sealed partial class DegradedReadabilityBrowserContractTests
{
    private const long InitialGeneration = 401;
    private const string FallbackAttribute =
        "data-codex-wallpaper-contrast-fallback";

    [Fact]
    public void DegradedReadabilityScripts_KeepFallbackOwnedAndThemeAware()
    {
        var install = BuildInstall(
            InitialGeneration,
            glassAvailable: false,
            advancedAvailable: false);
        var styleSheet = InjectionScriptPayloadTestHelper.ExtractStyleSheet(install);
        var downgrade = InjectionScriptBuilder.BuildCapabilityDowngrade(
            InitialGeneration,
            CreateCapabilities(glassAvailable: false, advancedAvailable: true));

        Assert.Contains(
            "root.dataset.codexWallpaperContrastFallback = \"true\"",
            install,
            StringComparison.Ordinal);
        Assert.Contains(
            $"#{InjectionScriptBuilder.RootElementId}[{FallbackAttribute}=\"true\"]",
            styleSheet,
            StringComparison.Ordinal);
        Assert.Contains(
            "--codex-wallpaper-contrast-fallback-overlay-dark: rgb(0 0 0 / 0.84)",
            styleSheet,
            StringComparison.Ordinal);
        Assert.Contains(
            "--codex-wallpaper-contrast-fallback-overlay-light: rgb(255 255 255 / 0.94)",
            styleSheet,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.root.dataset.codexWallpaperContrastFallback = \"true\"",
            downgrade,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.overlay.parentElement === state.root",
            downgrade,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.overlay.hasAttribute(\"data-codex-wallpaper-overlay\")",
            downgrade,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"body[{FallbackAttribute}",
            styleSheet,
            StringComparison.Ordinal);
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task InitiallyUnavailableGlass_AddsAReadableOwnedFallbackForBothThemes()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await AssertInitialFallbackAsync(
                page,
                themeClass: "electron-dark",
                colorScheme: "dark",
                wallpaperChannel: 255,
                nativeTextChannel: 153,
                expectedOverlayChannel: 0,
                minimumOverlayAlpha: 0.84);

            await AssertInitialFallbackAsync(
                page,
                themeClass: "electron-light",
                colorScheme: "light",
                wallpaperChannel: 0,
                nativeTextChannel: 102,
                expectedOverlayChannel: 255,
                minimumOverlayAlpha: 0.94);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task RuntimeDowngrade_MarksOnlyGlassLoss_AndNewGenerationRecovers()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await LoadFixtureAsync(
                page,
                themeClass: "electron-dark",
                colorScheme: "dark",
                nativeTextChannel: 153);
            await InstallAsync(
                page,
                BuildInstall(
                    InitialGeneration,
                    glassAvailable: true,
                    advancedAvailable: true));

            var initial = await ReadSnapshotAsync(page);
            Assert.Null(initial.FallbackValue);
            Assert.Equal(0, initial.MarkedCount);

            Assert.True(await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCapabilityDowngrade(
                    InitialGeneration,
                    CreateCapabilities(glassAvailable: false, advancedAvailable: true))));
            var glassDowngraded = await ReadSnapshotAsync(page);
            Assert.Equal("true", glassDowngraded.FallbackValue);
            Assert.Equal(1, glassDowngraded.MarkedCount);
            Assert.False(glassDowngraded.GlassEnabled);

            await InstallAsync(
                page,
                BuildInstall(
                    InitialGeneration + 1,
                    glassAvailable: true,
                    advancedAvailable: true));
            var recovered = await ReadSnapshotAsync(page);
            Assert.Null(recovered.FallbackValue);
            Assert.Equal(0, recovered.MarkedCount);
            Assert.True(recovered.GlassEnabled);

            Assert.True(await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCapabilityDowngrade(
                    InitialGeneration + 1,
                    CreateCapabilities(glassAvailable: true, advancedAvailable: false))));
            var advancedOnly = await ReadSnapshotAsync(page);
            Assert.Null(advancedOnly.FallbackValue);
            Assert.Equal(0, advancedOnly.MarkedCount);
            Assert.True(advancedOnly.GlassEnabled);
            Assert.False(advancedOnly.AdvancedSurfacesEnabled);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task RuntimeDowngrade_RejectsADamagedOwnedGraphWithoutPartialMutation()
    {
        (string Name, string DamageScript)[] cases =
        [
            ("detached root", "state.root.remove()"),
            ("detached overlay", "state.overlay.remove()"),
            (
                "foreign overlay owner",
                "state.overlay.dataset.codexWallpaperOwner = 'foreign'"),
        ];

        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            foreach (var testCase in cases)
            {
                await LoadFixtureAsync(
                    page,
                    themeClass: "electron-dark",
                    colorScheme: "dark",
                    nativeTextChannel: 153);
                await InstallAsync(
                    page,
                    BuildInstall(
                        InitialGeneration,
                        glassAvailable: true,
                        advancedAvailable: true));
                Assert.True(await page.EvaluateExpressionAsync<bool>(
                    $$"""
                    (() => {
                      const state = globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionScriptBuilder.StateProperty)}}];
                      {{testCase.DamageScript}};
                      return true;
                    })()
                    """));

                var downgradeApplied = await page.EvaluateExpressionAsync<bool>(
                    InjectionScriptBuilder.BuildCapabilityDowngrade(
                        InitialGeneration,
                        CreateCapabilities(
                            glassAvailable: false,
                            advancedAvailable: true)));
                var stateAfter = await page.EvaluateExpressionAsync<DowngradeState>(
                    $$"""
                    (() => {
                      const state = globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionScriptBuilder.StateProperty)}}];
                      return {
                        glassEnabled: state.glassEnabled,
                        fallbackValue:
                          state.root.dataset.codexWallpaperContrastFallback || null,
                        glassMarkersPresent:
                          state.style.textContent.includes("codex-wallpaper-glass:start") &&
                          state.style.textContent.includes("codex-wallpaper-glass:end")
                      };
                    })()
                    """);

                Assert.False(
                    downgradeApplied,
                    $"Damaged graph case '{testCase.Name}' must fail closed.");
                Assert.True(stateAfter.GlassEnabled);
                Assert.Null(stateAfter.FallbackValue);
                Assert.True(stateAfter.GlassMarkersPresent);
            }
        });
    }

    private static async Task AssertInitialFallbackAsync(
        IPage page,
        string themeClass,
        string colorScheme,
        int wallpaperChannel,
        int nativeTextChannel,
        int expectedOverlayChannel,
        double minimumOverlayAlpha)
    {
        await LoadFixtureAsync(page, themeClass, colorScheme, nativeTextChannel);
        await InstallAsync(
            page,
            BuildInstall(
                InitialGeneration,
                glassAvailable: false,
                advancedAvailable: false));
        await page.EvaluateExpressionAsync<bool>(
            $$"""
            (() => {
              const state = globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionScriptBuilder.StateProperty)}}];
              state.media.style.backgroundColor =
                "rgb({{wallpaperChannel}} {{wallpaperChannel}} {{wallpaperChannel}})";
              return true;
            })()
            """);

        var snapshot = await ReadSnapshotAsync(page);
        var overlay = ParseCssColor(snapshot.OverlayColor);
        var text = ParseCssColor(snapshot.TextColor);
        var compositedBackground = CompositeChannel(
            expectedOverlayChannel,
            wallpaperChannel,
            overlay.Alpha);
        var contrast = ContrastRatio(
            RelativeLuminance(text.Red, text.Green, text.Blue),
            RelativeLuminance(
                compositedBackground,
                compositedBackground,
                compositedBackground));

        Assert.Equal(InjectionScriptBuilder.RootElementId, snapshot.RootId);
        Assert.Equal(InjectionScriptBuilder.Owner, snapshot.Owner);
        Assert.Equal("true", snapshot.FallbackValue);
        Assert.Equal(1, snapshot.MarkedCount);
        Assert.Equal(expectedOverlayChannel, overlay.Red);
        Assert.Equal(expectedOverlayChannel, overlay.Green);
        Assert.Equal(expectedOverlayChannel, overlay.Blue);
        Assert.True(
            overlay.Alpha >= minimumOverlayAlpha,
            $"Expected fallback alpha >= {minimumOverlayAlpha}, got {overlay.Alpha}.");
        Assert.Equal(nativeTextChannel, text.Red);
        Assert.Equal(nativeTextChannel, text.Green);
        Assert.Equal(nativeTextChannel, text.Blue);
        Assert.True(
            contrast >= 4.5,
            $"Fallback contrast {contrast:F2}:1 did not meet the 4.5:1 readability floor.");
        Assert.False(snapshot.GlassEnabled);
    }

    private static string BuildInstall(
        long generation,
        bool glassAvailable,
        bool advancedAvailable) =>
        InjectionScriptBuilder.BuildInstall(
            new WallpaperInjectionOptions(
                generation,
                @"C:\Wallpapers\wallpaper.png",
                1234,
                WallpaperMediaKind.Image,
                WallpaperObjectFit.Cover,
                mediaOpacity: 1,
                glass: null,
                composition: new WallpaperCompositionOptions(
                    focusX: 0.5,
                    focusY: 0.5,
                    darkOverlay: 0,
                    lightOverlay: 0)),
            CreateCapabilities(glassAvailable, advancedAvailable));

    private static CompatibilityCapabilities CreateCapabilities(
        bool glassAvailable,
        bool advancedAvailable)
    {
        var declared = PresentationContractCatalog.CreateFullySupportedCapabilities();
        return declared.DowngradeWith(new CompatibilityCapabilities(
            declared.Global,
            declared.Regions,
            glassAvailable
                ? declared.Glass
                : CompatibilityCapability.Disabled(
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed),
            declared.Audio,
            advancedAvailable
                ? declared.Advanced
                : CompatibilityCapability.Disabled(
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed)));
    }

    private static Task LoadFixtureAsync(
        IPage page,
        string themeClass,
        string colorScheme,
        int nativeTextChannel) =>
        page.SetContentAsync(
            $$"""
            <!doctype html>
            <html class="{{themeClass}}" style="color-scheme: {{colorScheme}}">
              <head>
                <style>
                  html, body { margin: 0; width: 100%; height: 100%; }
                  #root { min-height: 100%; }
                  #native-text {
                    color: rgb({{nativeTextChannel}} {{nativeTextChannel}} {{nativeTextChannel}});
                  }
                </style>
              </head>
              <body>
                <div id="root"><main><span id="native-text">Readable text</span></main></div>
              </body>
            </html>
            """);

    private static async Task InstallAsync(IPage page, string installScript)
    {
        Assert.True(await page.EvaluateExpressionAsync<bool>(
            $"Boolean({installScript})"));
        Assert.True(await page.EvaluateExpressionAsync<bool>(
            "matchMedia('(forced-colors: none)').matches"));
    }

    private static Task<ReadabilitySnapshot> ReadSnapshotAsync(IPage page) =>
        page.EvaluateExpressionAsync<ReadabilitySnapshot>(
            $$"""
            (() => {
              const state = globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionScriptBuilder.StateProperty)}}];
              const root = state.root;
              const overlay = state.overlay;
              const text = document.getElementById("native-text");
              return {
                rootId: root.id,
                owner: root.dataset.codexWallpaperOwner,
                fallbackValue: root.dataset.codexWallpaperContrastFallback || null,
                markedCount: document.querySelectorAll("[{{FallbackAttribute}}]").length,
                overlayColor: getComputedStyle(overlay).backgroundColor,
                textColor: getComputedStyle(text).color,
                glassEnabled: state.glassEnabled,
                advancedSurfacesEnabled: state.advancedSurfacesEnabled
              };
            })()
            """);

    private static CssColor ParseCssColor(string value)
    {
        var match = CssColorRegex().Match(value);
        Assert.True(match.Success, $"Unsupported computed CSS color '{value}'.");
        return new CssColor(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
            match.Groups[4].Success
                ? double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture)
                : 1);
    }

    private static int CompositeChannel(int overlay, int wallpaper, double alpha) =>
        (int)Math.Round((overlay * alpha) + (wallpaper * (1 - alpha)));

    private static double ContrastRatio(double first, double second)
    {
        var lighter = Math.Max(first, second);
        var darker = Math.Min(first, second);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(int red, int green, int blue) =>
        (0.2126 * Linearize(red)) +
        (0.7152 * Linearize(green)) +
        (0.0722 * Linearize(blue));

    private static double Linearize(int channel)
    {
        var normalized = channel / 255d;
        return normalized <= 0.04045
            ? normalized / 12.92
            : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }

    [GeneratedRegex(
        @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([\d.]+))?\s*\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CssColorRegex();

    private sealed record ReadabilitySnapshot(
        string RootId,
        string Owner,
        string? FallbackValue,
        int MarkedCount,
        string OverlayColor,
        string TextColor,
        bool GlassEnabled,
        bool AdvancedSurfacesEnabled);

    private sealed record DowngradeState(
        bool GlassEnabled,
        string? FallbackValue,
        bool GlassMarkersPresent);

    private readonly record struct CssColor(
        int Red,
        int Green,
        int Blue,
        double Alpha);
}
