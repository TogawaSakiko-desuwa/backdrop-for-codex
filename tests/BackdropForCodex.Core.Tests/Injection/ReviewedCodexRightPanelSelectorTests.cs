using System.Text.RegularExpressions;
using System.Xml.Linq;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed partial class ReviewedCodexRightPanelSelectorTests
{
    private static readonly string[] ProtectedSurfaceIds =
    [
        "code-surface",
        "diff-surface",
        "editor-surface",
        "markdown-substring-near-miss",
        "popcorn-surface",
        "table-surface",
    ];

    [Fact]
    public void ReviewedSelectors_MatchOnlyTheIntendedRightPanelShells()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(ReviewedRightPanelFixture);

        var glassRule = Assert.Single(rules, IsReviewedRightPanelGlassRule);
        var clearRule = Assert.Single(rules, IsReviewedRightPanelClearRule);
        var generalGlassRule = Assert.Single(rules, IsGeneralGlassRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body aside[data-app-shell-focus-area="right-panel"]
                      > div:has([role="tabpanel"][data-app-shell-tab-panel-controller="right"])
                      > div[class~="bg-token-main-surface-primary"]
                    """),
            ],
            glassRule.Selectors);
        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body [role="tabpanel"][data-app-shell-tab-panel-controller="right"]
                      > [class~="bg-token-main-surface-primary"]
                    """),
                CanonicalizeSelector(
                    """
                    body [role="tabpanel"][data-app-shell-tab-panel-controller="right"]
                      [class~="relative"][class~="rounded-lg"][class~="bg-token-main-surface-primary"]:has(:is(.markdown, [class^="_markdownContent_"], [class*=" _markdownContent_"]))
                    """),
            ],
            clearRule.Selectors);

        Assert.Equal(
            ["right-panel-glass-shell"],
            SelectFixtureIds(fixture, glassRule.Selectors));
        Assert.Equal(
            ["file-layout-shell"],
            SelectFixtureIds(fixture, [clearRule.Selectors[0]]));
        Assert.Equal(
            ["markdown-shell-3996", "markdown-shell-legacy"],
            SelectFixtureIds(fixture, [clearRule.Selectors[1]]));
        Assert.Equal(
            ["file-layout-shell", "markdown-shell-3996", "markdown-shell-legacy"],
            SelectFixtureIds(fixture, clearRule.Selectors));
        Assert.Equal(
            ["left-panel-lookalike"],
            SelectFixtureIds(fixture, generalGlassRule.Selectors));
    }

    [Fact]
    public void ReviewedSelectors_KeepContentSurfacesOutOfGlassAndClearRules()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(ReviewedRightPanelFixture);
        var glassRule = Assert.Single(rules, IsReviewedRightPanelGlassRule);
        var clearRule = Assert.Single(rules, IsReviewedRightPanelClearRule);
        var modifiedIds = SelectFixtureIds(
            fixture,
            [.. glassRule.Selectors, .. clearRule.Selectors]);

        Assert.All(
            ProtectedSurfaceIds,
            protectedId =>
            {
                Assert.NotNull(FindFixtureNode(fixture, protectedId));
                Assert.DoesNotContain(protectedId, modifiedIds);
            });
        Assert.DoesNotContain("left-panel-lookalike", modifiedIds);
        Assert.DoesNotContain("right-panel-near-miss", modifiedIds);
        Assert.DoesNotContain("rounded-surface-without-markdown", modifiedIds);
    }

    [Fact]
    public void ReviewedSelectors_ClearOnlyTheCurrentRightPanelChrome()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentRightPanelFixture);

        var chromeRule = Assert.Single(rules, IsReviewedRightPanelChromeClearRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body aside[data-app-shell-focus-area="right-panel"]
                      [data-app-shell-tabs="true"][class~="bg-token-main-surface-primary"]:has([role="tabpanel"][data-app-shell-tab-panel-controller="right"])
                    """),
                CanonicalizeSelector(
                    """
                    body aside[data-app-shell-focus-area="right-panel"]
                      [data-app-shell-tabs="true"][class~="bg-token-main-surface-primary"]:has([role="tabpanel"][data-app-shell-tab-panel-controller="right"])
                      > [class~="bg-token-main-surface-primary"]:has([data-app-shell-tab-strip-controller="right"])
                    """),
            ],
            chromeRule.Selectors);
        Assert.Equal(
            ["current-tabs-root", "current-toolbar"],
            SelectFixtureIds(fixture, chromeRule.Selectors));
        Assert.DoesNotContain("current-selected-tab", SelectFixtureIds(fixture, chromeRule.Selectors));
        Assert.DoesNotContain("current-close-button", SelectFixtureIds(fixture, chromeRule.Selectors));
        Assert.DoesNotContain("current-add-button", SelectFixtureIds(fixture, chromeRule.Selectors));
        Assert.DoesNotContain("current-file-layout", SelectFixtureIds(fixture, chromeRule.Selectors));
        Assert.DoesNotContain("left-tabs-root", SelectFixtureIds(fixture, chromeRule.Selectors));
        Assert.DoesNotContain("wrong-controller-tabs-root", SelectFixtureIds(fixture, chromeRule.Selectors));
    }

    [Fact]
    public void ReviewedSelectors_GlassOnlyTheEmptyRightLauncherShellAndClearItsPrimaryChrome()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentRightPanelLauncherFixture);

        var launcherGlassRule = Assert.Single(rules, IsReviewedRightLauncherGlassRule);
        var launcherClearRule = Assert.Single(rules, IsReviewedRightLauncherClearRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body aside[data-app-shell-focus-area="right-panel"]:not(:has([data-app-shell-tab-panel-controller]))
                      > div
                      > div[class~="bg-token-main-surface-primary"]:has([data-app-shell-tabs="true"])
                    """),
            ],
            launcherGlassRule.Selectors);
        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body aside[data-app-shell-focus-area="right-panel"]
                      [data-app-shell-tabs="true"][class~="bg-token-main-surface-primary"]:not(:has([data-app-shell-tab-panel-controller]))
                    """),
                CanonicalizeSelector(
                    """
                    body aside[data-app-shell-focus-area="right-panel"]
                      [data-app-shell-tabs="true"]:not(:has([data-app-shell-tab-panel-controller]))
                      [class~="bg-token-main-surface-primary"]
                    """),
            ],
            launcherClearRule.Selectors);

        Assert.Equal(
            ["launcher-glass-shell"],
            SelectFixtureIds(fixture, launcherGlassRule.Selectors));
        Assert.Equal(
            [
                "launcher-center-sticky",
                "launcher-scroll-content",
                "launcher-tabs-root",
                "launcher-toolbar",
                "launcher-zero-size-sticky",
            ],
            SelectFixtureIds(fixture, launcherClearRule.Selectors));

        var changedIds = SelectFixtureIds(
            fixture,
            [.. launcherGlassRule.Selectors, .. launcherClearRule.Selectors]);
        Assert.DoesNotContain("launcher-review-card", changedIds);
        Assert.DoesNotContain("launcher-terminal-card", changedIds);
        Assert.DoesNotContain("launcher-primary-sibling", changedIds);
        Assert.DoesNotContain("left-launcher-glass-shell", changedIds);
        Assert.DoesNotContain("left-launcher-tabs-root", changedIds);
        Assert.DoesNotContain("wrong-controller-glass-shell", changedIds);
        Assert.DoesNotContain("wrong-controller-tabs-root", changedIds);
        Assert.DoesNotContain("populated-glass-shell", changedIds);
        Assert.DoesNotContain("populated-tabs-root", changedIds);
        Assert.DoesNotContain("populated-tabpanel", changedIds);
        Assert.DoesNotContain("editor-surface", changedIds);
    }

    [Fact]
    public void ReviewedHeaderSelectors_GlassOnlyTheNativeApplicationMenuTopBarAndClearTheEdgeLayer()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentHeaderFixture);

        var applicationMenuTopBarGlassRule = Assert.Single(
            rules,
            IsReviewedApplicationMenuTopBarGlassRule);
        var edgeHeaderResetRule = Assert.Single(rules, IsReviewedEdgeHeaderResetRule);
        var contextClearRule = Assert.Single(rules, IsReviewedHeaderContextClearRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    html[data-codex-window-type="electron"][data-codex-window-chrome="application-menu"]
                      body > #root > div > div:has(> [role="menubar"][data-orientation="horizontal"])
                    """),
            ],
            applicationMenuTopBarGlassRule.Selectors);
        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body header[data-app-shell-application-menu-bar][data-app-shell-header-edge-scroll]
                    """),
            ],
            edgeHeaderResetRule.Selectors);
        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body header[data-app-shell-application-menu-bar][data-app-shell-header-edge-scroll]
                      > [data-testid="app-shell-header-context-menu-surface"]
                    """),
            ],
            contextClearRule.Selectors);

        Assert.Equal(
            ["top-app-bar"],
            SelectFixtureIds(fixture, applicationMenuTopBarGlassRule.Selectors));
        Assert.Equal(
            ["edge-scroll-header"],
            SelectFixtureIds(fixture, edgeHeaderResetRule.Selectors));
        Assert.Equal(
            ["main-header-context"],
            SelectFixtureIds(fixture, contextClearRule.Selectors));

        var changedIds = SelectFixtureIds(
            fixture,
            [
                .. applicationMenuTopBarGlassRule.Selectors,
                .. edgeHeaderResetRule.Selectors,
                .. contextClearRule.Selectors,
            ]);
        Assert.DoesNotContain("main-header-menu-button", changedIds);
        Assert.DoesNotContain("top-app-leading-controls", changedIds);
        Assert.DoesNotContain("top-app-menubar", changedIds);
        Assert.DoesNotContain("top-file-menu-item", changedIds);
        Assert.DoesNotContain("right-header-slot", changedIds);
        Assert.DoesNotContain("right-tab-close-button", changedIds);
        Assert.DoesNotContain("right-panel", changedIds);
        Assert.DoesNotContain("css-module-header-without-data-markers", changedIds);
        Assert.DoesNotContain("css-module-header-context", changedIds);
        Assert.DoesNotContain("nested-horizontal-menubar-container", changedIds);
        Assert.DoesNotContain("wrong-orientation-topbar", changedIds);
    }

    [Fact]
    public void ReviewedConversationSelectors_ClearOnlyTheComposerSurfaceFade()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentComposerFixture);

        var composerFadeRule = Assert.Single(rules, IsReviewedComposerFadeClearRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body main .thread-scroll-container
                      [class~="bg-gradient-to-t"][class~="from-token-main-surface-primary"][class~="via-token-main-surface-primary"]
                    """),
            ],
            composerFadeRule.Selectors);
        Assert.Equal(
            ["composer-surface-fade"],
            SelectFixtureIds(fixture, composerFadeRule.Selectors));
    }

    [Fact]
    public void ReviewedConversationSelectors_GlassOnlyUnannotatedChatGptAssistantMessages()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentConversationMessagesFixture);

        var fallbackGlassRule = Assert.Single(
            rules,
            IsReviewedChatGptAssistantFallbackGlassRule);
        var fallbackPaddingRule = Assert.Single(
            rules,
            IsReviewedChatGptAssistantFallbackPaddingRule);
        var fallbackSelector = CanonicalizeSelector(
            """
            body main [data-content-search-unit-key] > h4[class~="sr-only"][class~="select-none"] + div[class~="group"][class~="flex"][class~="min-w-0"][class~="flex-col"]:not([data-response-annotation-target]):has(> [data-selected-text-overlay-target])
            """);

        Assert.Contains(fallbackSelector, fallbackGlassRule.Selectors);
        Assert.Contains(fallbackSelector, fallbackPaddingRule.Selectors);
        Assert.Equal(
            ["chatgpt-assistant-message"],
            SelectFixtureIds(fixture, [fallbackSelector]));

        var changedIds = SelectFixtureIds(fixture, [fallbackSelector]);
        Assert.DoesNotContain("annotated-codex-assistant-message", changedIds);
        Assert.DoesNotContain("ordinary-selected-text-container", changedIds);
        Assert.DoesNotContain("non-adjacent-assistant-lookalike", changedIds);
        Assert.DoesNotContain("assistant-heading-without-sr-only", changedIds);
        Assert.DoesNotContain("assistant-heading-without-select-none", changedIds);
        Assert.DoesNotContain("assistant-wrapper-without-required-classes", changedIds);
        Assert.DoesNotContain("assistant-unit-without-search-key", changedIds);
        Assert.DoesNotContain("assistant-target-not-direct-child", changedIds);
        Assert.DoesNotContain("user-message-bubble", changedIds);
        Assert.DoesNotContain("assistant-message-outside-main", changedIds);
    }

    [Fact]
    public void ReviewedBrowserHostSelectors_LiftOnlyBodyLevelHostsWithoutChangingContentOrInteraction()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentBrowserHostFixture);

        var stackingRule = Assert.Single(rules, IsReviewedBrowserHostStackingRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body > [data-browser-sidebar-webview-host-root]
                    """),
                CanonicalizeSelector(
                    """
                    body > [data-browser-sidebar-webview][data-app-shell-focus-area]
                    """),
            ],
            stackingRule.Selectors);
        Assert.Equal(
            [
                "browser-host-current",
                "browser-host-current-hidden",
                "browser-host-legacy",
            ],
            SelectFixtureIds(fixture, stackingRule.Selectors));

        Assert.DoesNotContain("background", stackingRule.Declarations, StringComparison.Ordinal);
        Assert.DoesNotContain("opacity", stackingRule.Declarations, StringComparison.Ordinal);
        Assert.DoesNotContain("filter", stackingRule.Declarations, StringComparison.Ordinal);
        Assert.DoesNotContain("pointer-events", stackingRule.Declarations, StringComparison.Ordinal);
        Assert.DoesNotContain("display", stackingRule.Declarations, StringComparison.Ordinal);
        Assert.DoesNotContain("visibility", stackingRule.Declarations, StringComparison.Ordinal);

        var changedIds = SelectFixtureIds(fixture, stackingRule.Selectors);
        Assert.DoesNotContain("app-root", changedIds);
        Assert.DoesNotContain("browser-host-current-nested", changedIds);
        Assert.DoesNotContain("browser-host-legacy-without-focus-area", changedIds);
        Assert.DoesNotContain("browser-webview-current", changedIds);
        Assert.DoesNotContain("browser-webview-legacy", changedIds);
    }

    [Fact]
    public void ReviewedMainContentSelectors_ClearOnlyTheNativeTopFade()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentMainContentTopFadeFixture);

        var topFadeRule = Assert.Single(rules, IsReviewedMainContentTopFadeClearRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body main
                      [data-app-shell-main-content-top-fade]
                    """),
            ],
            topFadeRule.Selectors);
        Assert.Equal(
            ["main-content-top-fade", "top-fade-without-class"],
            SelectFixtureIds(fixture, topFadeRule.Selectors));
        Assert.DoesNotContain(
            "css-module-top-fade-without-data-marker",
            SelectFixtureIds(fixture, topFadeRule.Selectors));
    }

    [Fact]
    public void ReviewedPluginsPageSelectors_ClearOnlyTheSearchStickyShell()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentPluginsPageFixture);

        var stickyClearRule = Assert.Single(rules, IsReviewedPluginsPageStickyClearRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="plugins-page-search"])
                    """),
            ],
            stickyClearRule.Selectors);
        Assert.Equal(
            ["plugins-search-sticky"],
            SelectFixtureIds(fixture, stickyClearRule.Selectors));

        var changedIds = SelectFixtureIds(fixture, stickyClearRule.Selectors);
        Assert.DoesNotContain("plugins-search-sticky-wrong-id", changedIds);
        Assert.DoesNotContain("plugins-featured-card", changedIds);
    }

    [Fact]
    public void ReviewedScheduledPageSelectors_ClearOnlyTheSearchStickyShell()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentScheduledPageFixture);

        var stickyClearRule = Assert.Single(rules, IsReviewedScheduledPageStickyClearRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="scheduled-page-search"])
                    """),
            ],
            stickyClearRule.Selectors);
        Assert.Equal(
            ["scheduled-search-sticky"],
            SelectFixtureIds(fixture, stickyClearRule.Selectors));

        var changedIds = SelectFixtureIds(fixture, stickyClearRule.Selectors);
        Assert.DoesNotContain("scheduled-search-sticky-wrong-id", changedIds);
        Assert.DoesNotContain("scheduled-task-row", changedIds);
    }

    [Fact]
    public void ReviewedSitesPageSelectors_GlassTheRouteRootAndClearOnlyItsSearchSticky()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentSitesPageFixture);

        var rootGlassRule = Assert.Single(rules, IsReviewedSitesPageRootGlassRule);
        var stickyClearRule = Assert.Single(rules, IsReviewedSitesPageStickyClearRule);

        const string RootSelector =
            """
            body [class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="bg-token-main-surface-primary"]:has([id="appgen-site-search"])
            """;
        Assert.Equal(
            [CanonicalizeSelector(RootSelector)],
            rootGlassRule.Selectors);
        Assert.Equal(
            [
                CanonicalizeSelector(
                    $"""
                    {RootSelector}
                      [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="appgen-site-search"])
                    """),
            ],
            stickyClearRule.Selectors);

        Assert.Equal(
            ["sites-route-root"],
            SelectFixtureIds(fixture, rootGlassRule.Selectors));
        Assert.Equal(
            ["sites-search-sticky"],
            SelectFixtureIds(fixture, stickyClearRule.Selectors));

        var changedIds = SelectFixtureIds(
            fixture,
            [.. rootGlassRule.Selectors, .. stickyClearRule.Selectors]);
        Assert.DoesNotContain("sites-card", changedIds);
        Assert.DoesNotContain("sites-route-root-wrong-id", changedIds);
        Assert.DoesNotContain("sites-search-sticky-outside-route", changedIds);
    }

    [Fact]
    public void ReviewedPullRequestSelectors_GlassOnlyTheListAndDetailPanes()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentPullRequestFixture);

        var paneGlassRule = Assert.Single(rules, IsReviewedPullRequestPaneGlassRule);
        var stickyClearRule = Assert.Single(rules, IsReviewedPullRequestStickyClearRule);
        var detailInternalClearRule = Assert.Single(
            rules,
            IsReviewedPullRequestDetailInternalClearRule);

        const string ListRootSelector =
            """
            body [class~="flex"][class~="h-full"][class~="min-h-0"][class~="w-full"][class~="flex-col"][class~="bg-token-main-surface-primary"]:has([id="pull-request-inbox-search"])
            """;
        const string DetailEvidenceSelector =
            """
            section[class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"]
              > div[class~="@container/app-shell-detail-panel"][class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="bg-token-main-surface-primary"]
            """;
        var detailAsideSelector =
            $"""
            body main:has([id="pull-request-inbox-search"])
              aside[data-app-shell-focus-area="right-panel"]:has(
                {DetailEvidenceSelector}
              )
            """;
        var detailShellSelector =
            $"""
            {detailAsideSelector}
              > div[class~="absolute"][class~="inset-0"][class~="min-h-0"][class~="min-w-0"][class~="overflow-hidden"]
              > div[class~="absolute"][class~="top-0"][class~="bottom-0"][class~="left-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"]
            """;
        var detailSectionSelector =
            $"""
            {detailShellSelector}
              > div[class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="overflow-hidden"]
              > div[class~="h-full"]
              > section[class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="bg-token-main-surface-primary"]
            """;
        Assert.Equal(
            [
                CanonicalizeSelector(ListRootSelector),
                CanonicalizeSelector(detailShellSelector),
            ],
            paneGlassRule.Selectors);
        Assert.Equal(
            [
                CanonicalizeSelector(
                    $"""
                    {ListRootSelector}
                      [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has([id="pull-request-inbox-search"])
                    """),
            ],
            stickyClearRule.Selectors);
        Assert.Equal(
            [
                CanonicalizeSelector(detailSectionSelector),
                CanonicalizeSelector(
                    $"""
                    {detailSectionSelector}
                      > div[class~="@container/app-shell-detail-panel"][class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="bg-token-main-surface-primary"]
                    """),
            ],
            detailInternalClearRule.Selectors);

        Assert.Equal(
            ["pull-request-detail-shell", "pull-request-list-root"],
            SelectFixtureIds(fixture, paneGlassRule.Selectors));
        Assert.Equal(
            ["pull-request-search-sticky"],
            SelectFixtureIds(fixture, stickyClearRule.Selectors));
        Assert.Equal(
            ["pull-request-detail-root", "pull-request-detail-section"],
            SelectFixtureIds(fixture, detailInternalClearRule.Selectors));

        var changedIds = SelectFixtureIds(
            fixture,
            [
                .. paneGlassRule.Selectors,
                .. stickyClearRule.Selectors,
                .. detailInternalClearRule.Selectors,
            ]);
        Assert.DoesNotContain("pull-request-list-root-wrong-id", changedIds);
        Assert.DoesNotContain("pull-request-search-sticky-wrong-id", changedIds);
        Assert.DoesNotContain("pull-request-ordinary-tab-shell", changedIds);
        Assert.DoesNotContain("pull-request-detail-shell-wrong-focus", changedIds);
        Assert.DoesNotContain("pull-request-detail-shell-wrong-id", changedIds);
        Assert.DoesNotContain("pull-request-detail-root-near-miss", changedIds);
        Assert.DoesNotContain("pull-request-editor", changedIds);
        Assert.DoesNotContain("pull-request-diff", changedIds);
        Assert.DoesNotContain("pull-request-code", changedIds);
        Assert.DoesNotContain("pull-request-card", changedIds);
    }

    [Fact]
    public void ReviewedSettingsSelectors_GlassOnlyTheRightContentCanvas()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentSettingsFixture);

        var canvasGlassRule = Assert.Single(rules, IsReviewedSettingsCanvasGlassRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body [class~="relative"][class~="isolate"][class~="flex"][class~="max-h-full"][class~="min-h-0"][class~="w-full"][class~="flex-1"]:has([class~="app-shell-left-panel"] [data-settings-panel-slug])
                      > main[data-app-shell-main-surface="default"]
                      > [class~="relative"][class~="isolate"][class~="flex"][class~="min-h-0"][class~="flex-1"][class~="overflow-hidden"]
                      > [data-app-shell-main-content-layout][data-app-shell-right-panel-full-width]
                      > [data-app-shell-thread-edge-divider]
                      > [class~="relative"][class~="flex"][class~="min-h-0"][class~="flex-1"]
                      > [class~="h-full"][class~="min-h-0"][class~="min-w-0"][class~="flex-1"]
                      > [class~="h-full"][class~="min-w-0"][class~="overflow-visible"]
                      > div[class~="flex"][class~="h-full"][class~="min-h-0"][class~="flex-col"][class~="electron:overflow-hidden"][class~="electron:bg-token-main-surface-primary"][class~="electron:elevation-prominent"][class~="windows:rounded-tl-lg"]
                    """),
            ],
            canvasGlassRule.Selectors);
        Assert.Equal(
            ["settings-content-canvas"],
            SelectFixtureIds(fixture, canvasGlassRule.Selectors));

        var changedIds = SelectFixtureIds(fixture, canvasGlassRule.Selectors);
        Assert.DoesNotContain("ordinary-div-main-surface", changedIds);
        Assert.DoesNotContain("settings-outer-main-surface", changedIds);
        Assert.DoesNotContain("settings-main-main-surface", changedIds);
        Assert.DoesNotContain("settings-canvas-without-data-anchor", changedIds);
        Assert.DoesNotContain("settings-permissions-card", changedIds);
        Assert.DoesNotContain("settings-general-card", changedIds);
        Assert.DoesNotContain("settings-dropdown", changedIds);
        Assert.DoesNotContain("settings-switch", changedIds);
        Assert.DoesNotContain("settings-css-module-only-canvas", changedIds);
        Assert.DoesNotContain("settings-browser-canvas", changedIds);
        Assert.DoesNotContain("settings-legacy-main-surface-canvas", changedIds);
    }

    [Fact]
    public void ReviewedKeyboardShortcutsSelectors_ClearOnlyTheActiveSearchStickyShell()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var activeFixture = XDocument.Parse(CurrentKeyboardShortcutsFixture);
        var inactiveFixture = XDocument.Parse(InactiveKeyboardShortcutsFixture);
        var otherRouteFixture = XDocument.Parse(OtherSettingsRouteFixture);

        var stickyClearRule = Assert.Single(
            rules,
            IsReviewedKeyboardShortcutsStickyClearRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body:has([class~="app-shell-left-panel"] [data-settings-panel-slug="keyboard-shortcuts"][aria-current="page"])
                      [class~="sticky"][class~="z-30"][class~="bg-token-main-surface-primary"]:has(input[type="text"])
                    """),
            ],
            stickyClearRule.Selectors);
        Assert.Equal(
            ["keyboard-search-sticky"],
            SelectFixtureIds(activeFixture, stickyClearRule.Selectors));
        Assert.Empty(SelectFixtureIds(inactiveFixture, stickyClearRule.Selectors));
        Assert.Empty(SelectFixtureIds(otherRouteFixture, stickyClearRule.Selectors));

        var changedIds = SelectFixtureIds(activeFixture, stickyClearRule.Selectors);
        Assert.DoesNotContain("keyboard-search-input", changedIds);
        Assert.DoesNotContain("keyboard-shortcut-row", changedIds);
        Assert.DoesNotContain("keyboard-shortcut-capture", changedIds);
        Assert.DoesNotContain("keyboard-sticky-non-text-input", changedIds);
    }

    [Fact]
    public void ReviewedChangedFilesComposerSelectors_ClearOnlyTheInProgressFade()
    {
        var styleSheet = ExtractGeneratedStyleSheet();
        var forcedColorsNone = ExtractBlock(styleSheet, "@media (forced-colors: none)");
        var rules = ParseLeafRules(forcedColorsNone);
        var fixture = XDocument.Parse(CurrentChangedFilesComposerFixture);

        var fadeClearRule = Assert.Single(
            rules,
            IsReviewedChangedFilesComposerFadeClearRule);

        Assert.Equal(
            [
                CanonicalizeSelector(
                    """
                    body main [data-codex-composer-root] [data-above-composer-portal]
                      > [data-in-progress-fixed-content]
                      > [class~="absolute"][class~="inset-x-0"][class~="bottom-1"][class~="flex"][class~="min-h-7"][class~="items-center"][class~="justify-center"][class~="gap-2"][class~="pb-1"]
                      > [class~="pointer-events-none"][class~="absolute"][class~="inset-x-0"][class~="-bottom-1"][class~="h-7"][class~="bg-gradient-to-t"][class~="from-token-main-surface-primary"][class~="to-transparent"]
                    """),
            ],
            fadeClearRule.Selectors);
        Assert.Equal(
            ["changed-files-composer-fade"],
            SelectFixtureIds(fixture, fadeClearRule.Selectors));

        var changedIds = SelectFixtureIds(fixture, fadeClearRule.Selectors);
        Assert.DoesNotContain("changed-files-summary-button", changedIds);
        Assert.DoesNotContain("composer-surface-chrome", changedIds);
        Assert.DoesNotContain("changed-files-fade-outside-portal", changedIds);
        Assert.DoesNotContain("changed-files-fade-without-in-progress", changedIds);
        Assert.DoesNotContain("composer-via-gradient", changedIds);
    }

    private static bool IsReviewedRightPanelGlassRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: var(--codex-wallpaper-glass) !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: blur(var(--codex-wallpaper-blur))",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector => selector.Contains(
                "aside[data-app-shell-focus-area=\"right-panel\"]",
                StringComparison.Ordinal) &&
                selector.Contains(
                    "[data-app-shell-tab-panel-controller=\"right\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(":has(", StringComparison.Ordinal) &&
                selector.Contains('>'));

    private static bool IsReviewedRightLauncherGlassRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: var(--codex-wallpaper-glass) !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: blur(var(--codex-wallpaper-blur))",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "aside[data-app-shell-focus-area=\"right-panel\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    ":has([data-app-shell-tabs=\"true\"])",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    ":not(:has([data-app-shell-tab-panel-controller]))",
                    StringComparison.Ordinal));

    private static bool IsReviewedRightPanelClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background-color: transparent !important;" &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[data-app-shell-tab-panel-controller=\"right\"]",
                    StringComparison.Ordinal) &&
                !selector.Contains(
                    "[data-app-shell-tabs=\"true\"]",
                    StringComparison.Ordinal));

    private static bool IsReviewedRightPanelChromeClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background-color: transparent !important;" &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[data-app-shell-tabs=\"true\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[data-app-shell-tab-panel-controller=\"right\"]",
                    StringComparison.Ordinal));

    private static bool IsReviewedRightLauncherClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background-color: transparent !important;" &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "aside[data-app-shell-focus-area=\"right-panel\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    ":not(:has([data-app-shell-tab-panel-controller]))",
                    StringComparison.Ordinal));

    private static bool IsReviewedApplicationMenuTopBarGlassRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: var(--codex-wallpaper-glass) !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "-webkit-backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation))",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: blur(var(--codex-wallpaper-blur)) saturate(var(--codex-wallpaper-saturation))",
            StringComparison.Ordinal) &&
        rule.Selectors.Contains(
            CanonicalizeSelector(
                """
                html[data-codex-window-type="electron"][data-codex-window-chrome="application-menu"]
                  body > #root > div > div:has(> [role="menubar"][data-orientation="horizontal"])
                """),
            StringComparer.Ordinal);

    private static bool IsReviewedEdgeHeaderResetRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background: transparent !important; -webkit-backdrop-filter: none !important; backdrop-filter: none !important;" &&
        rule.Selectors.Any(
            selector => selector.Contains(
                "header[data-app-shell-application-menu-bar]" +
                "[data-app-shell-header-edge-scroll]",
                StringComparison.Ordinal));

    private static bool IsReviewedHeaderContextClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background: transparent !important; -webkit-backdrop-filter: none !important; backdrop-filter: none !important; border-color: transparent !important;" &&
        rule.Selectors.Any(
            selector => selector.Contains(
                "[data-testid=\"app-shell-header-context-menu-surface\"]",
                StringComparison.Ordinal));

    private static bool IsReviewedComposerFadeClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background: transparent !important;" &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    ".thread-scroll-container",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"bg-gradient-to-t\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"from-token-main-surface-primary\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"via-token-main-surface-primary\"]",
                    StringComparison.Ordinal));

    private static bool IsReviewedChatGptAssistantFallbackGlassRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: var(--codex-wallpaper-glass) !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: blur(var(--codex-wallpaper-blur))",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "border-radius: var(--codex-wallpaper-radius)",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "box-shadow: 0 8px 28px",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector => selector.Contains(
                "[data-selected-text-overlay-target]",
                StringComparison.Ordinal));

    private static bool IsReviewedChatGptAssistantFallbackPaddingRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) == "padding: 12px 16px;" &&
        rule.Selectors.Any(
            selector => selector.Contains(
                "[data-selected-text-overlay-target]",
                StringComparison.Ordinal));

    private static bool IsReviewedBrowserHostStackingRule(CssRule rule) =>
        rule.Declarations.Contains("z-index: 2", StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector => selector.Contains(
                "[data-browser-sidebar-webview-host-root]",
                StringComparison.Ordinal)) &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[data-browser-sidebar-webview]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[data-app-shell-focus-area]",
                    StringComparison.Ordinal));

    private static bool IsReviewedMainContentTopFadeClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background-image: none !important;" &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[data-app-shell-main-content-top-fade]",
                    StringComparison.Ordinal));

    private static bool IsReviewedPluginsPageStickyClearRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: transparent !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: none !important",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[class~=\"sticky\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"z-30\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    ":has([id=\"plugins-page-search\"])",
                    StringComparison.Ordinal));

    private static bool IsReviewedScheduledPageStickyClearRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: transparent !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: none !important",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[class~=\"sticky\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"z-30\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    ":has([id=\"scheduled-page-search\"])",
                    StringComparison.Ordinal));

    private static bool IsReviewedSitesPageRootGlassRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: var(--codex-wallpaper-glass) !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: blur(var(--codex-wallpaper-blur))",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[class~=\"flex\"][class~=\"h-full\"][class~=\"min-h-0\"]" +
                    "[class~=\"flex-col\"][class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    ":has([id=\"appgen-site-search\"])",
                    StringComparison.Ordinal));

    private static bool IsReviewedSitesPageStickyClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background-color: transparent !important;" &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[class~=\"flex\"][class~=\"h-full\"][class~=\"min-h-0\"]" +
                    "[class~=\"flex-col\"][class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"sticky\"][class~=\"z-30\"]" +
                    "[class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    ":has([id=\"appgen-site-search\"])",
                    StringComparison.Ordinal));

    private static bool IsReviewedPullRequestPaneGlassRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: var(--codex-wallpaper-glass) !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: blur(var(--codex-wallpaper-blur))",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[class~=\"flex\"][class~=\"h-full\"][class~=\"min-h-0\"]" +
                    "[class~=\"w-full\"][class~=\"flex-col\"]" +
                    "[class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    ":has([id=\"pull-request-inbox-search\"])",
                    StringComparison.Ordinal)) &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "body main:has([id=\"pull-request-inbox-search\"]) " +
                    "aside[data-app-shell-focus-area=\"right-panel\"]:has(",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "> div[class~=\"absolute\"][class~=\"inset-0\"]" +
                    "[class~=\"min-h-0\"][class~=\"min-w-0\"]" +
                    "[class~=\"overflow-hidden\"]" +
                    " > div[class~=\"absolute\"][class~=\"top-0\"]" +
                    "[class~=\"bottom-0\"][class~=\"left-0\"]" +
                    "[class~=\"min-w-0\"]" +
                    "[class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal));

    private static bool IsReviewedPullRequestStickyClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background-color: transparent !important;" &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[class~=\"flex\"][class~=\"h-full\"][class~=\"min-h-0\"]" +
                    "[class~=\"w-full\"][class~=\"flex-col\"]" +
                    "[class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"sticky\"][class~=\"z-30\"]" +
                    "[class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    ":has([id=\"pull-request-inbox-search\"])",
                    StringComparison.Ordinal));

    private static bool IsReviewedPullRequestDetailInternalClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background-color: transparent !important;" &&
        rule.Selectors.Count == 2 &&
        rule.Selectors.All(
            selector =>
                selector.Contains(
                    "body main:has([id=\"pull-request-inbox-search\"]) " +
                    "aside[data-app-shell-focus-area=\"right-panel\"]:has(",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "> div[class~=\"absolute\"][class~=\"inset-0\"]" +
                    "[class~=\"min-h-0\"][class~=\"min-w-0\"]" +
                    "[class~=\"overflow-hidden\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "> div[class~=\"h-full\"][class~=\"min-h-0\"]" +
                    "[class~=\"min-w-0\"][class~=\"overflow-hidden\"]" +
                    " > div[class~=\"h-full\"]" +
                    " > section[class~=\"h-full\"][class~=\"min-h-0\"]" +
                    "[class~=\"min-w-0\"]" +
                    "[class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal)) &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "> div[class~=\"@container/app-shell-detail-panel\"]" +
                    "[class~=\"flex\"][class~=\"h-full\"][class~=\"min-h-0\"]" +
                    "[class~=\"flex-col\"]" +
                    "[class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal));

    private static bool IsReviewedSettingsCanvasGlassRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: var(--codex-wallpaper-glass) !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: blur(var(--codex-wallpaper-blur))",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[class~=\"relative\"][class~=\"isolate\"][class~=\"flex\"]" +
                    "[class~=\"max-h-full\"][class~=\"min-h-0\"]" +
                    "[class~=\"w-full\"][class~=\"flex-1\"]" +
                    ":has([class~=\"app-shell-left-panel\"] [data-settings-panel-slug])",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "> main[data-app-shell-main-surface=\"default\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "> [data-app-shell-main-content-layout]" +
                    "[data-app-shell-right-panel-full-width]" +
                    " > [data-app-shell-thread-edge-divider]",
                    StringComparison.Ordinal) &&
                selector.EndsWith(
                    "> div[class~=\"flex\"][class~=\"h-full\"]" +
                    "[class~=\"min-h-0\"][class~=\"flex-col\"]" +
                    "[class~=\"electron:overflow-hidden\"]" +
                    "[class~=\"electron:bg-token-main-surface-primary\"]" +
                    "[class~=\"electron:elevation-prominent\"]" +
                    "[class~=\"windows:rounded-tl-lg\"]",
                    StringComparison.Ordinal));

    private static bool IsReviewedKeyboardShortcutsStickyClearRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: transparent !important",
            StringComparison.Ordinal) &&
        rule.Declarations.Contains(
            "backdrop-filter: none !important",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[class~=\"app-shell-left-panel\"] " +
                    "[data-settings-panel-slug=\"keyboard-shortcuts\"]" +
                    "[aria-current=\"page\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"sticky\"][class~=\"z-30\"]" +
                    "[class~=\"bg-token-main-surface-primary\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    ":has(input[type=\"text\"])",
                    StringComparison.Ordinal));

    private static bool IsReviewedChangedFilesComposerFadeClearRule(CssRule rule) =>
        CanonicalizeWhitespace(rule.Declarations) ==
        "background-image: none !important;" &&
        rule.Selectors.Any(
            selector =>
                selector.Contains(
                    "[data-codex-composer-root] [data-above-composer-portal]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "> [data-in-progress-fixed-content]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"bottom-1\"][class~=\"flex\"][class~=\"min-h-7\"]",
                    StringComparison.Ordinal) &&
                selector.Contains(
                    "[class~=\"-bottom-1\"][class~=\"h-7\"]" +
                    "[class~=\"bg-gradient-to-t\"]" +
                    "[class~=\"from-token-main-surface-primary\"]" +
                    "[class~=\"to-transparent\"]",
                    StringComparison.Ordinal));

    private static bool IsGeneralGlassRule(CssRule rule) =>
        rule.Declarations.Contains(
            "background-color: var(--codex-wallpaper-glass) !important",
            StringComparison.Ordinal) &&
        rule.Selectors.Any(
            selector => selector.Contains(
                "aside:not([data-app-shell-focus-area=\"right-panel\"])",
                StringComparison.Ordinal));

    private static string ExtractGeneratedStyleSheet()
    {
        var script = InjectionScriptBuilder.BuildInstall(
            new WallpaperInjectionOptions(
                1,
                new Uri("https://127.0.0.1:49152/media/wallpaper"),
                @"C:\Wallpapers\wallpaper.png",
                1234,
                WallpaperMediaKind.Image),
            PresentationContractCatalog.CreateFullySupportedCapabilities());
        const string StartMarker = "style.textContent = `";
        var start = script.IndexOf(StartMarker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += StartMarker.Length;
        var end = script.IndexOf("`;", start, StringComparison.Ordinal);
        Assert.True(end > start);

        return script[start..end];
    }

    private static string ExtractBlock(string source, string blockHeader)
    {
        var withoutRuntimeExpressions = JavaScriptInterpolationRegex().Replace(
            source,
            "runtime-value");
        var header = withoutRuntimeExpressions.IndexOf(blockHeader, StringComparison.Ordinal);
        Assert.True(header >= 0);
        var openingBrace = withoutRuntimeExpressions.IndexOf('{', header);
        Assert.True(openingBrace > header);

        var depth = 0;
        for (var index = openingBrace; index < withoutRuntimeExpressions.Length; index++)
        {
            depth += withoutRuntimeExpressions[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return withoutRuntimeExpressions[(openingBrace + 1)..index];
            }
        }

        throw new InvalidOperationException($"CSS block '{blockHeader}' is not closed.");
    }

    private static CssRule[] ParseLeafRules(string css)
    {
        var withoutComments = CssCommentRegex().Replace(css, string.Empty);

        return CssLeafRuleRegex()
            .Matches(withoutComments)
            .Select(match => new CssRule(
                SplitTopLevel(
                        match.Groups["selectors"].Value,
                        ',')
                    .Select(CanonicalizeSelector)
                    .ToArray(),
                match.Groups["declarations"].Value.Trim()))
            .ToArray();
    }

    private static string[] SelectFixtureIds(
        XDocument fixture,
        IReadOnlyCollection<string> selectors) =>
        fixture
            .Descendants()
            .Where(element => selectors.Any(selector => MatchesSelector(element, selector)))
            .Select(element => (string?)element.Attribute("data-fixture-id"))
            .Where(id => id is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static XElement? FindFixtureNode(XDocument fixture, string fixtureId) =>
        fixture
            .Descendants()
            .SingleOrDefault(
                element => (string?)element.Attribute("data-fixture-id") == fixtureId);

    private static bool MatchesSelector(XElement element, string selector)
    {
        var compactSelector = SiblingCombinatorWhitespaceRegex().Replace(
            ChildCombinatorWhitespaceRegex().Replace(
                CanonicalizeWhitespace(selector),
                ">"),
            "+");
        var split = FindLastCombinator(compactSelector);
        if (split is null)
        {
            return MatchesSimpleSelector(element, compactSelector);
        }

        var (index, combinator) = split.Value;
        var left = compactSelector[..index].Trim();
        var right = compactSelector[(index + 1)..].Trim();
        if (!MatchesSimpleSelector(element, right))
        {
            return false;
        }

        return combinator switch
        {
            '>' => element.Parent is not null &&
                MatchesSelector(element.Parent, left),
            '+' => element.ElementsBeforeSelf().LastOrDefault() is { } sibling &&
                MatchesSelector(sibling, left),
            _ => element.Ancestors().Any(ancestor => MatchesSelector(ancestor, left)),
        };
    }

    private static (int Index, char Combinator)? FindLastCombinator(string selector)
    {
        var parentheses = 0;
        var brackets = 0;
        var quote = '\0';

        for (var index = selector.Length - 1; index >= 0; index--)
        {
            var character = selector[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            switch (character)
            {
                case ')':
                    parentheses++;
                    break;
                case '(':
                    parentheses--;
                    break;
                case ']':
                    brackets++;
                    break;
                case '[':
                    brackets--;
                    break;
                case '>' or '+' when parentheses == 0 && brackets == 0:
                    return (index, character);
                case ' ' when parentheses == 0 && brackets == 0:
                    return (index, character);
            }
        }

        return null;
    }

    private static bool MatchesSimpleSelector(XElement element, string selector)
    {
        if (selector.StartsWith(":is(", StringComparison.Ordinal))
        {
            var closingParenthesis = FindMatchingParenthesis(selector, 3);
            if (closingParenthesis != selector.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Unsupported selector after :is(): '{selector}'.");
            }

            return SplitTopLevel(selector[4..closingParenthesis], ',')
                .Any(alternative => MatchesSimpleSelector(element, alternative));
        }

        for (var notIndex = FindTopLevelPseudo(selector, ":not(");
             notIndex >= 0;
             notIndex = FindTopLevelPseudo(selector, ":not("))
        {
            var closingParenthesis = FindMatchingParenthesis(selector, notIndex + 4);
            var excludedSelector = selector[(notIndex + 5)..closingParenthesis];
            if (MatchesSimpleSelector(element, excludedSelector))
            {
                return false;
            }

            selector = string.Concat(
                selector.AsSpan(0, notIndex),
                selector.AsSpan(closingParenthesis + 1));
        }

        for (var hasIndex = FindTopLevelPseudo(selector, ":has(");
             hasIndex >= 0;
             hasIndex = FindTopLevelPseudo(selector, ":has("))
        {
            var closingParenthesis = FindMatchingParenthesis(selector, hasIndex + 4);
            var relativeSelector = selector[(hasIndex + 5)..closingParenthesis].Trim();
            var directChild = relativeSelector.StartsWith('>');
            if (directChild)
            {
                relativeSelector = relativeSelector[1..].TrimStart();
            }

            var candidates = directChild
                ? element.Elements()
                : element.Descendants();
            if (!candidates.Any(candidate => MatchesSelector(candidate, relativeSelector)))
            {
                return false;
            }

            selector = string.Concat(
                selector.AsSpan(0, hasIndex),
                selector.AsSpan(closingParenthesis + 1));
        }

        var attributes = CssAttributeRegex().Matches(selector);
        foreach (Match attributeMatch in attributes)
        {
            var name = attributeMatch.Groups["name"].Value;
            var attribute = element.Attribute(name);
            if (attribute is null)
            {
                return false;
            }

            var operation = attributeMatch.Groups["operation"].Value;
            var expected = attributeMatch.Groups["value"].Value;
            if (operation == "=" && attribute.Value != expected)
            {
                return false;
            }

            if (operation == "~=" &&
                !attribute.Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains(expected, StringComparer.Ordinal))
            {
                return false;
            }

            if (operation == "*=" &&
                !attribute.Value.Contains(expected, StringComparison.Ordinal))
            {
                return false;
            }

            if (operation == "^=" &&
                !attribute.Value.StartsWith(expected, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var selectorWithoutAttributes = CssAttributeRegex().Replace(selector, string.Empty);
        var ids = CssIdRegex().Matches(selectorWithoutAttributes);
        var actualId = (string?)element.Attribute("id");
        if (ids.Any(idMatch => actualId != idMatch.Groups["name"].Value))
        {
            return false;
        }

        selectorWithoutAttributes = CssIdRegex().Replace(selectorWithoutAttributes, string.Empty);
        var classes = CssClassRegex().Matches(selectorWithoutAttributes);
        var actualClasses = ((string?)element.Attribute("class") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (classes.Any(
                classMatch => !actualClasses.Contains(
                    classMatch.Groups["name"].Value,
                    StringComparer.Ordinal)))
        {
            return false;
        }

        var typeSelector = CssClassRegex()
            .Replace(selectorWithoutAttributes, string.Empty)
            .Trim();

        return typeSelector.Length == 0 ||
            typeSelector == "*" ||
            element.Name.LocalName.Equals(typeSelector, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindTopLevelPseudo(string selector, string pseudo)
    {
        var parentheses = 0;
        var brackets = 0;
        var quote = '\0';

        for (var index = 0; index < selector.Length; index++)
        {
            var character = selector[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (parentheses == 0 &&
                brackets == 0 &&
                selector.IndexOf(pseudo, index, StringComparison.Ordinal) == index)
            {
                return index;
            }

            switch (character)
            {
                case '(':
                    parentheses++;
                    break;
                case ')':
                    parentheses--;
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets--;
                    break;
            }
        }

        return -1;
    }

    private static int FindMatchingParenthesis(string source, int openingParenthesis)
    {
        var depth = 0;
        for (var index = openingParenthesis; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException($"Selector has an unclosed parenthesis: '{source}'.");
    }

    private static List<string> SplitTopLevel(string value, char separator)
    {
        var result = new List<string>();
        var start = 0;
        var parentheses = 0;
        var brackets = 0;
        var quote = '\0';

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            switch (character)
            {
                case '(':
                    parentheses++;
                    break;
                case ')':
                    parentheses--;
                    break;
                case '[':
                    brackets++;
                    break;
                case ']':
                    brackets--;
                    break;
                default:
                    if (character == separator && parentheses == 0 && brackets == 0)
                    {
                        result.Add(value[start..index].Trim());
                        start = index + 1;
                    }

                    break;
            }
        }

        result.Add(value[start..].Trim());
        return result;
    }

    private static string CanonicalizeSelector(string selector) =>
        ChildCombinatorWhitespaceRegex().Replace(
            CanonicalizeWhitespace(selector),
            " > ");

    private static string CanonicalizeWhitespace(string value) =>
        WhitespaceRegex().Replace(value, " ").Trim();

    private sealed record CssRule(
        IReadOnlyList<string> Selectors,
        string Declarations);

    [GeneratedRegex(@"\$\{[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptInterpolationRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CssCommentRegex();

    [GeneratedRegex(
        @"(?<selectors>[^{}]+)\{(?<declarations>[^{}]*)\}",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CssLeafRuleRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s*>\s*", RegexOptions.CultureInvariant)]
    private static partial Regex ChildCombinatorWhitespaceRegex();

    [GeneratedRegex(@"\s*\+\s*", RegexOptions.CultureInvariant)]
    private static partial Regex SiblingCombinatorWhitespaceRegex();

    [GeneratedRegex(
        @"\[(?<name>[\w-]+)(?:(?<operation>~=|\*=|\^=|=)""(?<value>[^""]*)"")?\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex CssAttributeRegex();

    [GeneratedRegex(@"\.(?<name>[\w-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CssClassRegex();

    [GeneratedRegex(@"#(?<name>[\w-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CssIdRegex();

    private const string ReviewedRightPanelFixture =
        """
        <html class="electron-dark">
          <body>
            <aside data-app-shell-focus-area="left-panel"
                   data-fixture-id="left-panel-lookalike">
              <div>
                <div class="bg-token-main-surface-primary">
                  <div role="tabpanel"
                       data-app-shell-tab-panel-controller="left" />
                </div>
              </div>
            </aside>

            <aside data-app-shell-focus-area="right-panel"
                   data-fixture-id="reviewed-right-panel">
              <div data-fixture-id="right-panel-controller">
                <div class="bg-token-main-surface-primary"
                     data-fixture-id="right-panel-glass-shell">
                  <header class="bg-token-main-surface-primary"
                          data-fixture-id="right-panel-tab-strip" />
                  <div role="tabpanel"
                       data-app-shell-tab-panel-controller="right"
                       data-fixture-id="right-tabpanel">
                    <div class="bg-token-main-surface-primary"
                         data-fixture-id="file-layout-shell">
                      <div class="monaco-editor bg-token-main-surface-primary"
                           data-fixture-id="editor-surface" />
                      <div class="bg-token-main-surface-primary"
                           data-diff-view="unified"
                           data-fixture-id="diff-surface" />
                      <pre class="bg-token-main-surface-primary"
                           data-fixture-id="code-surface"><code>const answer = 42;</code></pre>
                      <table class="bg-token-main-surface-primary"
                             data-fixture-id="table-surface">
                        <tbody><tr><td>preserve table background</td></tr></tbody>
                      </table>
                      <div class="bg-token-main-surface-primary"
                           data-popcorn-root=""
                           data-fixture-id="popcorn-surface" />
                    </div>

                    <section>
                      <div class="relative rounded-lg bg-token-main-surface-primary"
                           data-fixture-id="markdown-shell-legacy">
                        <article class="markdown">
                          <p>Reviewed file details</p>
                        </article>
                      </div>

                      <div class="relative rounded-lg bg-token-main-surface-primary"
                           data-fixture-id="markdown-shell-3996">
                        <article class="_markdownContent_1dreu_131">
                          <p>Reviewed file details</p>
                        </article>
                      </div>

                      <div class="relative rounded-lg bg-token-main-surface-primary"
                           data-fixture-id="rounded-surface-without-markdown">
                        <article>Not Markdown</article>
                      </div>

                      <div class="relative rounded-lg bg-token-main-surface-primary"
                           data-fixture-id="markdown-substring-near-miss">
                        <article class="prefix_markdownContent_1dreu_131">
                          Not a reviewed Markdown class token
                        </article>
                      </div>
                    </section>
                  </div>
                </div>
              </div>
            </aside>

            <aside data-app-shell-focus-area="right-panel">
              <div>
                <div>
                  <div class="bg-token-main-surface-primary"
                       data-fixture-id="right-panel-near-miss">
                    <div>
                      <div role="tabpanel"
                           data-app-shell-tab-panel-controller="right" />
                    </div>
                  </div>
                </div>
              </div>
            </aside>
          </body>
        </html>
        """;

    private const string CurrentRightPanelFixture =
        """
        <html class="electron-dark">
          <body>
            <aside data-app-shell-focus-area="right-panel">
              <div>
                <div class="bg-token-main-surface-primary"
                     data-fixture-id="current-glass-shell">
                  <div>
                    <div class="isolate bg-token-main-surface-primary"
                         data-app-shell-tabs="true"
                         data-fixture-id="current-tabs-root">
                      <div class="bg-token-main-surface-primary"
                           data-fixture-id="current-toolbar">
                        <div data-app-shell-tab-strip-controller="right">
                          <div class="bg-token-main-surface-primary"
                               data-fixture-id="current-selected-tab">
                            <button data-app-shell-tab-close-button="true"
                                    data-fixture-id="current-close-button" />
                          </div>
                          <div class="bg-token-main-surface-primary">
                            <button title="Open tab"
                                    data-fixture-id="current-add-button" />
                          </div>
                        </div>
                      </div>
                      <div role="tabpanel"
                           data-app-shell-tab-panel-controller="right">
                        <div class="bg-token-main-surface-primary"
                             data-fixture-id="current-file-layout">
                          <div class="monaco-editor bg-token-main-surface-primary"
                               data-fixture-id="current-editor" />
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </aside>

            <aside data-app-shell-focus-area="left-panel">
              <div class="bg-token-main-surface-primary"
                   data-app-shell-tabs="true"
                   data-fixture-id="left-tabs-root">
                <div role="tabpanel"
                     data-app-shell-tab-panel-controller="right" />
              </div>
            </aside>

            <aside data-app-shell-focus-area="right-panel">
              <div class="bg-token-main-surface-primary"
                   data-app-shell-tabs="true"
                   data-fixture-id="wrong-controller-tabs-root">
                <div role="tabpanel"
                     data-app-shell-tab-panel-controller="left" />
              </div>
            </aside>
          </body>
        </html>
        """;

    private const string CurrentRightPanelLauncherFixture =
        """
        <html class="electron-dark">
          <body>
            <aside data-app-shell-focus-area="right-panel">
              <div data-fixture-id="launcher-positioner">
                <div class="bg-token-main-surface-primary"
                     data-fixture-id="launcher-primary-sibling" />
                <div class="absolute bg-token-main-surface-primary"
                     data-fixture-id="launcher-glass-shell">
                  <div class="h-full">
                    <div class="isolate bg-token-main-surface-primary"
                         data-app-shell-tabs="true"
                         data-fixture-id="launcher-tabs-root">
                      <div class="h-toolbar bg-token-main-surface-primary"
                           data-fixture-id="launcher-toolbar">
                        <div class="sticky bg-token-main-surface-primary"
                             data-fixture-id="launcher-zero-size-sticky" />
                      </div>
                      <div class="relative flex-1">
                        <div class="overflow-y-auto bg-token-main-surface-primary"
                             data-fixture-id="launcher-scroll-content">
                          <div class="launcher-center-layout">
                            <div class="sticky bg-token-main-surface-primary"
                                 data-fixture-id="launcher-center-sticky">
                              <button class="bg-token-main-surface-secondary"
                                      data-fixture-id="launcher-review-card">
                                Review
                              </button>
                              <button class="bg-token-main-surface-secondary"
                                      data-fixture-id="launcher-terminal-card">
                                Terminal
                              </button>
                            </div>
                          </div>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </aside>

            <aside data-app-shell-focus-area="left-panel">
              <div>
                <div class="bg-token-main-surface-primary"
                     data-fixture-id="left-launcher-glass-shell">
                  <div class="bg-token-main-surface-primary"
                       data-app-shell-tabs="true"
                       data-fixture-id="left-launcher-tabs-root">
                    <div class="bg-token-main-surface-primary" />
                  </div>
                </div>
              </div>
            </aside>

            <aside data-app-shell-focus-area="right-panel">
              <div>
                <div class="bg-token-main-surface-primary"
                     data-fixture-id="wrong-controller-glass-shell">
                  <div class="bg-token-main-surface-primary"
                       data-app-shell-tabs="true"
                       data-fixture-id="wrong-controller-tabs-root">
                    <div role="tabpanel"
                         data-app-shell-tab-panel-controller="left" />
                  </div>
                </div>
              </div>
            </aside>

            <aside data-app-shell-focus-area="right-panel">
              <div>
                <div class="bg-token-main-surface-primary"
                     data-fixture-id="populated-glass-shell">
                  <div class="bg-token-main-surface-primary"
                       data-app-shell-tabs="true"
                       data-fixture-id="populated-tabs-root">
                    <div role="tabpanel"
                         data-app-shell-tab-panel-controller="right"
                         data-fixture-id="populated-tabpanel">
                      <div class="monaco-editor bg-token-main-surface-primary"
                           data-fixture-id="editor-surface" />
                    </div>
                  </div>
                </div>
              </div>
            </aside>
          </body>
        </html>
        """;

    private const string CurrentHeaderFixture =
        """
        <html class="electron-dark"
              data-codex-window-type="electron"
              data-window-type="electron"
              data-codex-os="win32"
              data-codex-window-chrome="application-menu">
          <body>
            <div id="root">
              <div class="relative flex flex-col" data-fixture-id="app-shell-frame">
                <div class="_ApplicationMenuTopBar_fixture_1"
                     data-fixture-id="top-app-bar">
                  <div class="flex items-center gap-1"
                       data-fixture-id="top-app-leading-controls">
                    <span class="contents" data-state="closed" />
                  </div>
                  <div class="flex items-center gap-0.5 ps-1 pe-2"
                       role="menubar"
                       data-orientation="horizontal"
                       data-fixture-id="top-app-menubar">
                    <button id="application-menu-trigger-file-menu"
                            role="menuitem"
                            data-fixture-id="top-file-menu-item" />
                  </div>
                </div>

                <div data-fixture-id="nested-horizontal-menubar-container">
                  <div>
                    <div role="menubar" data-orientation="horizontal" />
                  </div>
                </div>

                <div data-fixture-id="wrong-orientation-topbar">
                  <div role="menubar" data-orientation="vertical" />
                </div>

                <main>
                  <header class="_Header_fixture_2"
                          data-app-shell-application-menu-bar=""
                          data-app-shell-header-edge-scroll=""
                          data-fixture-id="edge-scroll-header">
                    <div data-testid="app-shell-header-context-menu-surface"
                         data-fixture-id="main-header-context">
                      <button data-fixture-id="main-header-menu-button" />
                    </div>
                    <div data-fixture-id="right-header-slot">
                      <button data-app-shell-tab-close-button="true"
                              data-fixture-id="right-tab-close-button" />
                    </div>
                  </header>

                  <header class="_Header_fixture_3"
                          data-fixture-id="css-module-header-without-data-markers">
                    <div data-testid="app-shell-header-context-menu-surface"
                         data-fixture-id="css-module-header-context" />
                  </header>
                </main>
              </div>
            </div>

            <aside data-app-shell-focus-area="right-panel"
                   data-fixture-id="right-panel" />
          </body>
        </html>
        """;

    private const string CurrentComposerFixture =
        """
        <html class="electron-dark">
          <body>
            <main>
              <div class="thread-scroll-container">
                <div class="bg-gradient-to-t from-token-main-surface-primary via-token-main-surface-primary"
                     data-fixture-id="composer-surface-fade" />
                <div class="bg-gradient-to-t from-token-main-surface-primary"
                     data-fixture-id="missing-via-color" />
                <div class="bg-gradient-to-t via-token-main-surface-primary"
                     data-fixture-id="missing-from-color" />
                <div class="from-token-main-surface-primary via-token-main-surface-primary"
                     data-fixture-id="missing-gradient-direction" />
                <div class="bg-gradient-to-b from-token-main-surface-primary via-token-main-surface-primary"
                     data-fixture-id="different-gradient-direction" />
              </div>
              <div>
                <div class="bg-gradient-to-t from-token-main-surface-primary via-token-main-surface-primary"
                     data-fixture-id="outside-thread-scroll-container" />
              </div>
            </main>
            <aside>
              <div class="thread-scroll-container">
                <div class="bg-gradient-to-t from-token-main-surface-primary via-token-main-surface-primary"
                     data-fixture-id="outside-main" />
              </div>
            </aside>
          </body>
        </html>
        """;

    private const string CurrentConversationMessagesFixture =
        """
        <html class="electron-dark">
          <body>
            <main>
              <div data-content-search-unit-key="assistant-1">
                <h4 class="sr-only select-none">ChatGPT said:</h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="chatgpt-assistant-message">
                  <div data-selected-text-overlay-target="">
                    Assistant response
                  </div>
                </div>
              </div>

              <div data-content-search-unit-key="assistant-2">
                <h4 class="sr-only select-none">ChatGPT said:</h4>
                <div class="group flex min-w-0 flex-col"
                     data-response-annotation-conversation="conversation-1"
                     data-response-annotation-target="response-1"
                     data-fixture-id="annotated-codex-assistant-message">
                  <div data-selected-text-overlay-target="">
                    Annotated Codex response
                  </div>
                </div>
              </div>

              <article data-fixture-id="ordinary-selected-text-container">
                <div data-selected-text-overlay-target="">
                  Ordinary selectable content
                </div>
              </article>

              <div data-content-search-unit-key="assistant-3">
                <h4 class="sr-only select-none">ChatGPT said:</h4>
                <span>Unexpected sibling</span>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="non-adjacent-assistant-lookalike">
                  <div data-selected-text-overlay-target="">
                    Not directly adjacent
                  </div>
                </div>
              </div>

              <div data-content-search-unit-key="assistant-4">
                <h4 class="select-none">ChatGPT said:</h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="assistant-heading-without-sr-only">
                  <div data-selected-text-overlay-target="">
                    Heading is not the accessible role marker
                  </div>
                </div>
              </div>

              <div data-content-search-unit-key="assistant-5">
                <h4 class="sr-only">ChatGPT said:</h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="assistant-heading-without-select-none">
                  <div data-selected-text-overlay-target="">
                    Heading is missing the reviewed companion class
                  </div>
                </div>
              </div>

              <div data-content-search-unit-key="assistant-6">
                <h4 class="sr-only select-none">ChatGPT said:</h4>
                <div data-fixture-id="assistant-wrapper-without-required-classes">
                  <div data-selected-text-overlay-target="">
                    Wrapper is not an assistant message container
                  </div>
                </div>
              </div>

              <div>
                <h4 class="sr-only select-none">ChatGPT said:</h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="assistant-unit-without-search-key">
                  <div data-selected-text-overlay-target="">
                    Unit is missing the reviewed search anchor
                  </div>
                </div>
              </div>

              <div data-content-search-unit-key="assistant-7">
                <h4 class="sr-only select-none">ChatGPT said:</h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="assistant-target-not-direct-child">
                  <div>
                    <div data-selected-text-overlay-target="">
                      Target is nested below the reviewed surface
                    </div>
                  </div>
                </div>
              </div>

              <div data-content-search-unit-key="user-1">
                <h4 class="sr-only select-none">You said:</h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="user-message-bubble">
                  <div data-user-message-bubble="true">
                    <div data-selected-text-overlay-target="">
                      User message
                    </div>
                  </div>
                </div>
              </div>
            </main>

            <aside>
              <div data-content-search-unit-key="assistant-outside-main">
                <h4 class="sr-only select-none">ChatGPT said:</h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="assistant-message-outside-main">
                  <div data-selected-text-overlay-target="">
                    Outside main
                  </div>
                </div>
              </div>
            </aside>
          </body>
        </html>
        """;

    private const string CurrentBrowserHostFixture =
        """
        <html class="electron-dark">
          <body>
            <div id="root"
                 data-fixture-id="app-root">
              <div data-browser-sidebar-webview-host-root=""
                   data-fixture-id="browser-host-current-nested" />
            </div>

            <div data-browser-sidebar-webview-host-root=""
                 data-fixture-id="browser-host-current">
              <webview data-fixture-id="browser-webview-current" />
            </div>

            <div data-browser-sidebar-webview-host-root=""
                 hidden="hidden"
                 data-fixture-id="browser-host-current-hidden" />

            <div data-browser-sidebar-webview=""
                 data-app-shell-focus-area="browser"
                 data-fixture-id="browser-host-legacy">
              <webview data-fixture-id="browser-webview-legacy" />
            </div>

            <div data-browser-sidebar-webview=""
                 data-fixture-id="browser-host-legacy-without-focus-area" />
          </body>
        </html>
        """;

    private const string CurrentMainContentTopFadeFixture =
        """
        <html class="electron-dark">
          <body>
            <main>
              <div class="_MainContentTopFade_fixture_144"
                   data-app-shell-main-content-top-fade="visible"
                   data-fixture-id="main-content-top-fade" />
              <div class="_MainContentTopFade_fixture_145"
                   data-fixture-id="css-module-top-fade-without-data-marker" />
              <div data-app-shell-main-content-top-fade="visible"
                   data-fixture-id="top-fade-without-class" />
              <div class="bg-gradient-to-b from-token-main-surface-primary"
                   data-fixture-id="unrelated-main-gradient" />
            </main>
            <div class="_MainContentTopFade_fixture_146"
                 data-app-shell-main-content-top-fade="visible"
                 data-fixture-id="outside-main-top-fade" />
          </body>
        </html>
        """;

    private const string CurrentPluginsPageFixture =
        """
        <html class="electron-dark">
          <body>
            <div class="sticky z-30 bg-token-main-surface-primary"
                 data-fixture-id="plugins-search-sticky">
              <input id="plugins-page-search" />
            </div>

            <div class="sticky z-30 bg-token-main-surface-primary"
                 data-fixture-id="plugins-search-sticky-wrong-id">
              <input id="plugins-page-search-near-miss" />
            </div>

            <button class="rounded-lg bg-token-main-surface-primary"
                    data-fixture-id="plugins-featured-card">
              Install
            </button>
          </body>
        </html>
        """;

    private const string CurrentScheduledPageFixture =
        """
        <html class="electron-dark">
          <body>
            <div class="sticky z-30 bg-token-main-surface-primary"
                 data-fixture-id="scheduled-search-sticky">
              <input id="scheduled-page-search" />
            </div>

            <div class="sticky z-30 bg-token-main-surface-primary"
                 data-fixture-id="scheduled-search-sticky-wrong-id">
              <input id="scheduled-page-search-near-miss" />
            </div>

            <div class="rounded-lg bg-token-main-surface-primary"
                 data-fixture-id="scheduled-task-row">
              Daily briefing
            </div>
          </body>
        </html>
        """;

    private const string CurrentSitesPageFixture =
        """
        <html class="electron-dark">
          <body>
            <div class="flex h-full min-h-0 flex-col bg-token-main-surface-primary"
                 data-fixture-id="sites-route-root">
              <div class="sticky z-30 bg-token-main-surface-primary"
                   data-fixture-id="sites-search-sticky">
                <input id="appgen-site-search" />
              </div>
              <button class="rounded-lg bg-token-main-surface-primary"
                      data-fixture-id="sites-card">
                Open site
              </button>
            </div>

            <div class="flex h-full min-h-0 flex-col bg-token-main-surface-primary"
                 data-fixture-id="sites-route-root-wrong-id">
              <div class="sticky z-30 bg-token-main-surface-primary">
                <input id="appgen-site-search-near-miss" />
              </div>
            </div>

            <section>
              <div class="sticky z-30 bg-token-main-surface-primary"
                   data-fixture-id="sites-search-sticky-outside-route">
                <input id="appgen-site-search" />
              </div>
            </section>
          </body>
        </html>
        """;

    private const string CurrentPullRequestFixture =
        """
        <html class="electron-dark">
          <body>
            <main>
              <div class="relative isolate flex min-h-0 flex-1 overflow-hidden">
                <div class="_MainContentViewport_fixture_201"
                     data-app-shell-main-content-layout="default"
                     data-app-shell-right-panel-full-width="false">
                  <div class="flex h-full min-h-0 w-full flex-col bg-token-main-surface-primary"
                       data-fixture-id="pull-request-list-root">
                    <div class="sticky z-30 bg-token-main-surface-primary"
                         data-fixture-id="pull-request-search-sticky">
                      <input id="pull-request-inbox-search" />
                    </div>
                    <article class="rounded-lg bg-token-main-surface-primary"
                             data-fixture-id="pull-request-card">
                      Pull request
                    </article>
                  </div>
                </div>
                <aside data-app-shell-focus-area="right-panel">
                  <div class="absolute inset-0 min-h-0 min-w-0 overflow-hidden">
                    <div class="absolute top-0 bottom-0 left-0 min-w-0 bg-token-main-surface-primary border-l border-token-border-default"
                         data-fixture-id="pull-request-detail-shell">
                      <div class="h-full min-h-0 min-w-0 overflow-hidden">
                        <div class="h-full">
                          <section class="h-full min-h-0 min-w-0 bg-token-main-surface-primary"
                                   data-fixture-id="pull-request-detail-section">
                            <div class="@container/app-shell-detail-panel flex h-full min-h-0 flex-col bg-token-main-surface-primary"
                                 data-fixture-id="pull-request-detail-root">
                              <div class="monaco-editor bg-token-main-surface-primary"
                                   data-fixture-id="pull-request-editor" />
                              <div class="bg-token-main-surface-primary"
                                   data-diff-view="unified"
                                   data-fixture-id="pull-request-diff" />
                              <pre class="bg-token-main-surface-primary"
                                   data-fixture-id="pull-request-code"><code>const approved = true;</code></pre>
                            </div>
                            <div class="@container/app-shell-detail-pane flex h-full min-h-0 flex-col bg-token-main-surface-primary"
                                 data-fixture-id="pull-request-detail-root-near-miss" />
                          </section>
                        </div>
                      </div>
                    </div>
                  </div>
                </aside>
                <aside data-app-shell-focus-area="right-panel">
                  <div class="absolute inset-0 min-h-0 min-w-0 overflow-hidden">
                    <div class="absolute top-0 bottom-0 left-0 min-w-0 bg-token-main-surface-primary"
                         data-fixture-id="pull-request-ordinary-tab-shell">
                      <div role="tabpanel"
                           data-app-shell-tab-panel-controller="right" />
                    </div>
                  </div>
                </aside>
                <aside data-app-shell-focus-area="left-panel">
                  <div class="absolute inset-0 min-h-0 min-w-0 overflow-hidden">
                    <div class="absolute top-0 bottom-0 left-0 min-w-0 bg-token-main-surface-primary"
                         data-fixture-id="pull-request-detail-shell-wrong-focus">
                      <section class="h-full min-h-0 min-w-0 bg-token-main-surface-primary">
                        <div class="@container/app-shell-detail-panel flex h-full min-h-0 flex-col bg-token-main-surface-primary" />
                      </section>
                    </div>
                  </div>
                </aside>
              </div>
            </main>

            <main>
              <div class="relative isolate flex min-h-0 flex-1 overflow-hidden">
                <div class="_MainContentViewport_fixture_202"
                     data-app-shell-main-content-layout="default"
                     data-app-shell-right-panel-full-width="false">
                  <div class="flex h-full min-h-0 w-full flex-col bg-token-main-surface-primary"
                       data-fixture-id="pull-request-list-root-wrong-id">
                    <div class="sticky z-30 bg-token-main-surface-primary"
                         data-fixture-id="pull-request-search-sticky-wrong-id">
                      <input id="pull-request-inbox-search-near-miss" />
                    </div>
                  </div>
                </div>
                <aside data-app-shell-focus-area="right-panel">
                  <div class="absolute inset-0 min-h-0 min-w-0 overflow-hidden">
                    <div class="absolute top-0 bottom-0 left-0 min-w-0 bg-token-main-surface-primary"
                         data-fixture-id="pull-request-detail-shell-wrong-id">
                      <div class="h-full min-h-0 min-w-0 overflow-hidden">
                        <div class="h-full">
                          <section class="h-full min-h-0 min-w-0 bg-token-main-surface-primary">
                            <div class="@container/app-shell-detail-panel flex h-full min-h-0 flex-col bg-token-main-surface-primary" />
                          </section>
                        </div>
                      </div>
                    </div>
                  </div>
                </aside>
              </div>
            </main>
          </body>
        </html>
        """;

    private const string CurrentSettingsFixture =
        """
        <html class="electron-dark">
          <body>
            <div class="relative isolate flex max-h-full min-h-0 w-full flex-1">
              <aside class="app-shell-left-panel">
                <button data-settings-panel-slug="general" />
              </aside>
              <main class="_MainSurface_fixture_41"
                    data-app-shell-main-surface="default"
                    data-fixture-id="settings-outer-main-surface">
                <div class="relative isolate flex min-h-0 flex-1 overflow-hidden">
                  <div class="_MainContentViewport_fixture_143"
                       data-app-shell-main-content-layout="default"
                       data-app-shell-right-panel-full-width="false">
                    <div class="_MainContentFrame_fixture_144"
                         data-app-shell-thread-edge-divider="">
                      <div class="relative flex min-h-0 flex-1">
                        <div class="h-full min-h-0 min-w-0 flex-1">
                          <div class="h-full min-w-0 overflow-visible">
                            <div class="flex h-full min-h-0 flex-col electron:overflow-hidden electron:bg-token-main-surface-primary electron:elevation-prominent windows:rounded-tl-lg"
                                 data-fixture-id="settings-content-canvas">
                              <section class="rounded-xl bg-token-main-surface-primary"
                                       data-fixture-id="settings-permissions-card">
                                Permissions
                              </section>
                              <section class="rounded-xl bg-token-main-surface-primary"
                                       data-fixture-id="settings-general-card">
                                <select class="bg-token-main-surface-primary"
                                        data-fixture-id="settings-dropdown">
                                  <option>English</option>
                                </select>
                                <button class="bg-token-main-surface-primary"
                                        role="switch"
                                        data-fixture-id="settings-switch" />
                              </section>
                            </div>
                            <div class="main-surface flex h-full min-h-0 flex-col"
                                 data-fixture-id="settings-legacy-main-surface-canvas" />
                          </div>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </main>
            </div>

            <div class="relative isolate flex max-h-full min-h-0 w-full flex-1">
              <aside class="app-shell-left-panel">
                <button data-settings-panel-slug="general" />
              </aside>
              <main class="_MainSurface_fixture_42"
                    data-app-shell-main-surface="browser">
                <div class="relative isolate flex min-h-0 flex-1 overflow-hidden">
                  <div class="_MainContentViewport_fixture_145"
                       data-app-shell-main-content-layout="default"
                       data-app-shell-right-panel-full-width="false">
                    <div class="_MainContentFrame_fixture_146"
                         data-app-shell-thread-edge-divider="">
                      <div class="relative flex min-h-0 flex-1">
                        <div class="h-full min-h-0 min-w-0 flex-1">
                          <div class="h-full min-w-0 overflow-visible">
                            <div class="flex h-full min-h-0 flex-col electron:overflow-hidden electron:bg-token-main-surface-primary electron:elevation-prominent windows:rounded-tl-lg"
                                 data-fixture-id="settings-browser-canvas" />
                          </div>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </main>
            </div>

            <div class="relative isolate flex max-h-full min-h-0 w-full flex-1">
              <aside class="app-shell-left-panel">
                <button data-settings-panel-slug="general" />
              </aside>
              <main class="_MainSurface_fixture_43">
                <div class="relative isolate flex min-h-0 flex-1 overflow-hidden">
                  <div class="_MainContentViewport_fixture_147">
                    <div class="_MainContentFrame_fixture_148">
                      <div class="relative flex min-h-0 flex-1">
                        <div class="h-full min-h-0 min-w-0 flex-1">
                          <div class="h-full min-w-0 overflow-visible">
                            <div class="flex h-full min-h-0 flex-col electron:overflow-hidden electron:bg-token-main-surface-primary electron:elevation-prominent windows:rounded-tl-lg"
                                 data-fixture-id="settings-css-module-only-canvas" />
                          </div>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </main>
            </div>

            <div class="main-surface flex h-full min-h-0 flex-col"
                 data-fixture-id="ordinary-div-main-surface" />

            <div class="flex h-full min-h-0">
              <aside class="app-shell-left-panel">
                <button data-settings-panel-slug="appearance" />
              </aside>
              <div class="relative isolate min-w-0 flex-1 overflow-visible">
                <main class="main-surface flex h-full min-h-0 flex-col"
                      data-fixture-id="settings-main-main-surface" />
              </div>
            </div>

            <div class="relative isolate flex max-h-full min-h-0 w-full flex-1">
              <aside class="app-shell-left-panel" />
              <main class="_MainSurface_fixture_44"
                    data-app-shell-main-surface="default">
                <div class="relative isolate flex min-h-0 flex-1 overflow-hidden">
                  <div class="_MainContentViewport_fixture_149"
                       data-app-shell-main-content-layout="default"
                       data-app-shell-right-panel-full-width="false">
                    <div class="_MainContentFrame_fixture_150"
                         data-app-shell-thread-edge-divider="">
                      <div class="relative flex min-h-0 flex-1">
                        <div class="h-full min-h-0 min-w-0 flex-1">
                          <div class="h-full min-w-0 overflow-visible">
                            <div class="flex h-full min-h-0 flex-col electron:overflow-hidden electron:bg-token-main-surface-primary electron:elevation-prominent windows:rounded-tl-lg"
                                 data-fixture-id="settings-canvas-without-data-anchor" />
                          </div>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </main>
            </div>
          </body>
        </html>
        """;

    private const string CurrentKeyboardShortcutsFixture =
        """
        <html class="electron-dark">
          <body>
            <aside class="app-shell-left-panel">
              <button data-settings-panel-slug="keyboard-shortcuts"
                      aria-current="page">
                Keyboard shortcuts
              </button>
            </aside>

            <main>
              <div class="sticky z-30 bg-token-main-surface-primary"
                   data-fixture-id="keyboard-search-sticky">
                <div class="relative">
                  <input type="text"
                         data-fixture-id="keyboard-search-input" />
                </div>
              </div>

              <div class="sticky z-30 bg-token-main-surface-primary"
                   data-fixture-id="keyboard-sticky-non-text-input">
                <input type="search" />
              </div>

              <section class="rounded-xl bg-token-main-surface-primary"
                       data-fixture-id="keyboard-shortcut-row">
                <div role="group"
                     data-fixture-id="keyboard-shortcut-capture">
                  <button type="button">Edit shortcut</button>
                </div>
              </section>
            </main>
          </body>
        </html>
        """;

    private const string InactiveKeyboardShortcutsFixture =
        """
        <html class="electron-dark">
          <body>
            <aside class="app-shell-left-panel">
              <button data-settings-panel-slug="keyboard-shortcuts">
                Keyboard shortcuts
              </button>
            </aside>
            <main>
              <div class="sticky z-30 bg-token-main-surface-primary"
                   data-fixture-id="keyboard-search-sticky-inactive">
                <input type="text" />
              </div>
            </main>
          </body>
        </html>
        """;

    private const string OtherSettingsRouteFixture =
        """
        <html class="electron-dark">
          <body>
            <aside class="app-shell-left-panel">
              <button data-settings-panel-slug="general-settings"
                      aria-current="page">
                General
              </button>
            </aside>
            <main>
              <div class="sticky z-30 bg-token-main-surface-primary"
                   data-fixture-id="general-search-sticky">
                <input type="text" />
              </div>
            </main>
          </body>
        </html>
        """;

    private const string CurrentChangedFilesComposerFixture =
        """
        <html class="electron-dark">
          <body>
            <main>
              <div data-codex-composer-root="">
                <div data-above-composer-portal="">
                  <div data-in-progress-fixed-content="">
                    <div class="absolute inset-x-0 bottom-1 flex min-h-7 items-center justify-center gap-2 pb-1">
                      <button class="bg-token-main-surface-primary"
                              data-fixture-id="changed-files-summary-button">
                        View changed files
                      </button>
                      <div class="pointer-events-none absolute inset-x-0 -bottom-1 h-7 bg-gradient-to-t from-token-main-surface-primary to-transparent"
                           data-fixture-id="changed-files-composer-fade" />
                    </div>
                  </div>
                </div>

                <div class="relative bg-token-main-surface-primary"
                     data-fixture-id="composer-surface-chrome" />
              </div>

              <div data-codex-composer-root="">
                <div data-in-progress-fixed-content="">
                  <div class="absolute inset-x-0 bottom-1 flex min-h-7 items-center justify-center gap-2 pb-1">
                    <div class="pointer-events-none absolute inset-x-0 -bottom-1 h-7 bg-gradient-to-t from-token-main-surface-primary to-transparent"
                         data-fixture-id="changed-files-fade-outside-portal" />
                  </div>
                </div>
              </div>

              <div data-codex-composer-root="">
                <div data-above-composer-portal="">
                  <div>
                    <div class="absolute inset-x-0 bottom-1 flex min-h-7 items-center justify-center gap-2 pb-1">
                      <div class="pointer-events-none absolute inset-x-0 -bottom-1 h-7 bg-gradient-to-t from-token-main-surface-primary to-transparent"
                           data-fixture-id="changed-files-fade-without-in-progress" />
                    </div>
                  </div>
                </div>
              </div>

              <div class="thread-scroll-container">
                <div class="bg-gradient-to-t from-token-main-surface-primary via-token-main-surface-primary"
                     data-fixture-id="composer-via-gradient" />
              </div>
            </main>
          </body>
        </html>
        """;
}
