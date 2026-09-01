using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed class ReviewedCodexRightPanelSelectorTests
{
    [Fact]
    public void GeneratedStyleSheet_RetainsReviewedSelectorAnchors()
    {
        var styleSheet = BuildStyleSheet();
        string[] reviewedAnchors =
        [
            "aside[data-app-shell-focus-area=\"right-panel\"]",
            "[data-app-shell-tab-panel-controller=\"right\"]",
            "[data-app-shell-application-menu-bar]",
            "[data-app-shell-main-content-top-fade]",
            "[data-content-search-unit-key]",
            "[data-response-annotation-conversation]",
            "_tableContainer_",
            "plugins-page-search",
            "scheduled-page-search",
            "appgen-site-search",
            "pull-request-inbox-search",
            "bg-[var(--app-shell-panel-background,var(--color-surface))]",
            "electron:bg-surface",
            "data-settings-panel-slug=\"keyboard-shortcuts\"",
            "[class~=\"sticky\"][class~=\"bottom-0\"][class~=\"z-10\"][class~=\"w-full\"]",
            "[data-above-composer-portal]",
        ];

        Assert.All(
            reviewedAnchors,
            anchor => Assert.Contains(anchor, styleSheet, StringComparison.Ordinal));
    }

    [Fact]
    public void GeneratedStyleSheet_KeepsReviewedRulesInsideOwnedCapabilityBlocks()
    {
        var styleSheet = BuildStyleSheet();

        AssertOwnedBlock(styleSheet, "plugins-page-search", "glass");
        AssertOwnedBlock(styleSheet, "pull-request-inbox-search", "glass");
        AssertOwnedBlock(
            styleSheet,
            "bg-[var(--app-shell-panel-background,var(--color-surface))]",
            "glass");
        AssertOwnedBlock(styleSheet, "electron:bg-surface", "glass");
        AssertOwnedBlock(styleSheet, "data-settings-panel-slug", "glass");
        AssertOwnedBlock(styleSheet, "_tableContainer_", "advanced");
        AssertOwnedBlock(
            styleSheet,
            "[class~=\"sticky\"][class~=\"bottom-0\"][class~=\"z-10\"][class~=\"w-full\"]",
            "advanced");
        AssertOwnedBlock(styleSheet, "[data-above-composer-portal]", "advanced");
    }

    private static string BuildStyleSheet()
    {
        var script = InjectionScriptBuilder.BuildInstall(
            new WallpaperInjectionOptions(
                1,
                @"C:\Wallpapers\wallpaper.png",
                1234,
                WallpaperMediaKind.Image),
            PresentationContractCatalog.CreateFullySupportedCapabilities());
        return InjectionScriptPayloadTestHelper.ExtractStyleSheet(script);
    }

    private static void AssertOwnedBlock(
        string styleSheet,
        string anchor,
        string capability)
    {
        var anchorIndex = styleSheet.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(anchorIndex >= 0, $"Missing reviewed selector anchor '{anchor}'.");
        var startIndex = styleSheet.LastIndexOf(
            $"codex-wallpaper-{capability}:start",
            anchorIndex,
            StringComparison.Ordinal);
        var endIndex = styleSheet.IndexOf(
            $"codex-wallpaper-{capability}:end",
            anchorIndex,
            StringComparison.Ordinal);

        Assert.True(startIndex >= 0, $"'{anchor}' has no {capability} start marker.");
        Assert.True(endIndex > anchorIndex, $"'{anchor}' has no {capability} end marker.");
    }
}
