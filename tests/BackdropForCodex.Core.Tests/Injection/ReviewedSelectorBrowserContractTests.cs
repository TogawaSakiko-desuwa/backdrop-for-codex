using System.Text.Json;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Tests.Infrastructure;
using PuppeteerSharp;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

[CollectionDefinition("Reviewed selector browser contracts", DisableParallelization = true)]
public sealed class ReviewedSelectorBrowserContractGroup
{
}

[Collection("Reviewed selector browser contracts")]
public sealed class ReviewedSelectorBrowserContractTests
{
    private const long Generation = 7;
    private const string NativeBackground = "rgb(41, 42, 43)";
    private const string TransparentBackground = "rgba(0, 0, 0, 0)";

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task ShellSelectors_MatchReviewedSurfacesAndPreserveNearMisses()
    {
        await WithEdgePageAsync(async page =>
        {
            await LoadFixtureAsync(page, ShellFixture);
            await AddOwnedStyleAsync(page, BuildStyleSheet());

            var snapshots = await ReadSnapshotsAsync(page);
            AssertGlass(snapshots, "left-panel-lookalike");
            AssertGlass(snapshots, "right-panel-glass-shell");
            AssertGlass(snapshots, "launcher-glass-shell");
            AssertGlass(snapshots, "top-app-bar");

            AssertClear(snapshots, "current-tabs-root");
            AssertClear(snapshots, "current-toolbar");
            AssertClear(snapshots, "file-layout-shell");
            AssertClear(snapshots, "markdown-shell-legacy");
            AssertClear(snapshots, "markdown-shell-module");
            AssertClear(snapshots, "launcher-tabs-root");
            AssertClear(snapshots, "launcher-toolbar");
            AssertClear(snapshots, "launcher-scroll-content");
            AssertClear(snapshots, "edge-scroll-header");
            AssertClear(snapshots, "main-header-context");

            AssertNative(
                snapshots,
                "editor-surface",
                "diff-surface",
                "code-surface",
                "table-surface",
                "popcorn-surface",
                "markdown-substring-near-miss",
                "rounded-surface-without-markdown",
                "right-panel-near-miss",
                "current-selected-tab",
                "launcher-review-card",
                "left-launcher-glass-shell",
                "wrong-controller-glass-shell",
                "populated-editor-surface",
                "wrong-orientation-topbar",
                "css-module-header-without-data-markers");

            Assert.Equal("2", snapshots["browser-host-current"].ZIndex);
            Assert.Equal("2", snapshots["browser-host-legacy"].ZIndex);
            Assert.Equal("auto", snapshots["browser-host-current"].PointerEvents);
            Assert.Equal("visible", snapshots["browser-host-current"].Visibility);
            Assert.Equal(NativeBackground, snapshots["browser-host-current"].BackgroundColor);
            Assert.Equal("none", snapshots["browser-host-current-hidden"].Display);
            Assert.Equal("auto", snapshots["browser-host-current-nested"].ZIndex);
            Assert.Equal("auto", snapshots["browser-host-legacy-near-miss"].ZIndex);

            var cssom = await ReadCssomProbeAsync(page);
            Assert.Empty(cssom.InvalidSelectors);
            Assert.True(cssom.RuleCount > 20);
            Assert.Contains("right-panel-glass-shell", cssom.MatchedFixtureIds);
            Assert.Contains("launcher-glass-shell", cssom.MatchedFixtureIds);
            Assert.Contains("top-app-bar", cssom.MatchedFixtureIds);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task AdvancedSelectors_UseNativeCssForMessagesFadesAndWideTables()
    {
        await WithEdgePageAsync(async page =>
        {
            await LoadFixtureAsync(page, AdvancedFixture);
            await AddOwnedStyleAsync(page, BuildStyleSheet());

            var snapshots = await ReadSnapshotsAsync(page);
            Assert.Equal("none", snapshots["main-content-top-fade"].BackgroundImage);
            Assert.Equal("none", snapshots["composer-surface-fade"].BackgroundImage);
            Assert.Equal("none", snapshots["changed-files-composer-fade"].BackgroundImage);
            AssertGlass(snapshots, "fallback-assistant-message");
            AssertGlass(snapshots, "annotated-assistant-message");
            AssertGlass(snapshots, "user-message-bubble");

            AssertClear(snapshots, "wide-fallback-assistant");
            AssertPseudoGlass(snapshots, "wide-fallback-assistant");
            AssertClear(snapshots, "wide-annotated-assistant-rtl");
            AssertPseudoGlass(snapshots, "wide-annotated-assistant-rtl");
            Assert.NotEqual("auto", snapshots["wide-annotated-assistant-rtl"].BeforeRight);
            Assert.Equal("none", snapshots["wide-annotated-assistant-rtl"].BeforeTransform);

            AssertNative(
                snapshots,
                "ordinary-selected-text-container",
                "non-adjacent-assistant-lookalike",
                "assistant-heading-without-sr-only",
                "assistant-wrapper-without-required-classes",
                "assistant-target-not-direct-child",
                "assistant-message-outside-main",
                "wide-table-outside-assistant",
                "ordinary-table",
                "composer-surface-chrome");
            Assert.NotEqual(
                NativeBackground,
                snapshots["activity-surface"].BackgroundColor);

            var cssom = await ReadCssomProbeAsync(page);
            Assert.Empty(cssom.InvalidSelectors);
            Assert.Contains("fallback-assistant-message", cssom.MatchedFixtureIds);
            Assert.Contains("wide-fallback-assistant", cssom.MatchedFixtureIds);
            Assert.Contains("changed-files-composer-fade", cssom.MatchedFixtureIds);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task RouteSelectors_ClearOnlyReviewedChromeAndKeepContentOpaque()
    {
        await WithEdgePageAsync(async page =>
        {
            await LoadFixtureAsync(page, RouteFixture);
            await AddOwnedStyleAsync(page, BuildStyleSheet());

            var snapshots = await ReadSnapshotsAsync(page);
            AssertClearWithAfter(snapshots, "plugins-search-sticky");
            AssertClearWithAfter(snapshots, "scheduled-search-sticky");
            AssertGlass(snapshots, "sites-route-root");
            AssertClearWithAfter(snapshots, "sites-search-sticky");
            AssertGlass(snapshots, "pull-request-list-root");
            AssertClearWithAfter(snapshots, "pull-request-search-sticky");
            AssertGlass(snapshots, "pull-request-detail-shell");
            AssertGlass(snapshots, "pull-request-ordinary-tab-shell");
            AssertClear(snapshots, "pull-request-detail-section");
            AssertClear(snapshots, "pull-request-detail-root");
            AssertGlass(snapshots, "settings-content-canvas");
            AssertClearWithAfter(snapshots, "keyboard-search-sticky");
            Assert.Equal("none", snapshots["changed-files-route-fade"].BackgroundImage);

            AssertNative(
                snapshots,
                "plugins-featured-card",
                "plugins-search-sticky-wrong-id",
                "scheduled-task-row",
                "scheduled-search-sticky-wrong-id",
                "sites-card",
                "sites-route-root-wrong-id",
                "sites-search-sticky-outside-route",
                "pull-request-card",
                "pull-request-list-root-wrong-id",
                "pull-request-detail-root-near-miss",
                "pull-request-editor",
                "pull-request-diff",
                "pull-request-code",
                "settings-permissions-card",
                "settings-general-card",
                "settings-browser-canvas",
                "settings-canvas-without-data-anchor",
                "keyboard-search-input",
                "keyboard-shortcut-row",
                "keyboard-sticky-non-text-input",
                "changed-files-summary-button",
                "composer-surface-chrome-route");

            var cssom = await ReadCssomProbeAsync(page);
            Assert.Empty(cssom.InvalidSelectors);
            Assert.Contains("plugins-search-sticky", cssom.MatchedFixtureIds);
            Assert.Contains("sites-route-root", cssom.MatchedFixtureIds);
            Assert.Contains("pull-request-detail-shell", cssom.MatchedFixtureIds);
            Assert.Contains("settings-content-canvas", cssom.MatchedFixtureIds);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task CapabilityDowngrade_RemovesOnlyTheOwnedGlassOrAdvancedRules()
    {
        await WithEdgePageAsync(async page =>
        {
            await LoadFixtureAsync(page, DowngradeFixture);
            await AddOwnedStyleAsync(page, BuildStyleSheet(), initializeOwnership: true);

            var initial = await ReadSnapshotsAsync(page);
            AssertGlass(initial, "downgrade-glass-surface");
            Assert.Equal("none", initial["downgrade-advanced-fade"].BackgroundImage);

            var glassDowngradeApplied = await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCapabilityDowngrade(
                    Generation,
                    CreateCapabilities(glassAvailable: false, advancedAvailable: true)));
            Assert.True(glassDowngradeApplied);

            var glassDowngraded = await ReadSnapshotsAsync(page);
            AssertNative(glassDowngraded, "downgrade-glass-surface");
            Assert.Equal(
                "none",
                glassDowngraded["downgrade-advanced-fade"].BackgroundImage);
            var glassMarkers = await ReadCapabilityMarkersAsync(page);
            Assert.False(glassMarkers.GlassPresent);
            Assert.True(glassMarkers.AdvancedPresent);

            await LoadFixtureAsync(page, DowngradeFixture);
            await AddOwnedStyleAsync(page, BuildStyleSheet(), initializeOwnership: true);
            var advancedDowngradeApplied = await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCapabilityDowngrade(
                    Generation,
                    CreateCapabilities(glassAvailable: true, advancedAvailable: false)));
            Assert.True(advancedDowngradeApplied);

            var advancedDowngraded = await ReadSnapshotsAsync(page);
            AssertGlass(advancedDowngraded, "downgrade-glass-surface");
            Assert.NotEqual(
                "none",
                advancedDowngraded["downgrade-advanced-fade"].BackgroundImage);
            var advancedMarkers = await ReadCapabilityMarkersAsync(page);
            Assert.True(advancedMarkers.GlassPresent);
            Assert.False(advancedMarkers.AdvancedPresent);
        });
    }

    private static async Task WithEdgePageAsync(Func<IPage, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            ExecutablePath = FindEdge(),
            Headless = true,
            Timeout = 15_000,
            Args =
            [
                "--disable-extensions",
                "--disable-gpu",
                "--no-default-browser-check",
                "--no-first-run",
            ],
        });
        try
        {
            var page = await browser.NewPageAsync();
            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = 1280,
                Height = 900,
            });
            await test(page);
        }
        finally
        {
            await browser.CloseAsync();
        }
    }

    private static string FindEdge()
    {
        var configuredPath = Environment.GetEnvironmentVariable(
            "BACKDROP_FOR_CODEX_EDGE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        string[] candidates =
        [
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft",
                "Edge",
                "Application",
                "msedge.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft",
                "Edge",
                "Application",
                "msedge.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Edge",
                "Application",
                "msedge.exe"),
        ];
        var edgePath = candidates.FirstOrDefault(File.Exists);
        if (edgePath is null)
        {
            throw new FileNotFoundException(
                "Microsoft Edge is required for Category=BrowserContract. " +
                "Install Edge or set BACKDROP_FOR_CODEX_EDGE_PATH to msedge.exe.");
        }

        return edgePath;
    }

    private static Task LoadFixtureAsync(IPage page, string body) =>
        page.SetContentAsync(
            $$"""
            <!doctype html>
            {{body}}
            """);

    private static async Task AddOwnedStyleAsync(
        IPage page,
        string styleSheet,
        bool initializeOwnership = false)
    {
        var script = $$"""
            (() => {
              const style = document.createElement("style");
              style.id = {{JsonSerializer.Serialize(InjectionScriptBuilder.StyleElementId)}};
              style.dataset.codexWallpaperOwner =
                {{JsonSerializer.Serialize(InjectionScriptBuilder.Owner)}};
              style.dataset.codexWallpaperGeneration = "{{Generation}}";
              style.textContent = {{JsonSerializer.Serialize(styleSheet)}};
              document.head.append(style);
              if ({{(initializeOwnership ? "true" : "false")}}) {
                globalThis[{{JsonSerializer.Serialize(InjectionScriptBuilder.StateProperty)}}] = {
                  cleaned: false,
                  generation: {{Generation}},
                  style,
                  glassEnabled: true,
                  advancedSurfacesEnabled: true
                };
              }
              return style.sheet?.cssRules.length > 0;
            })()
            """;

        Assert.True(await page.EvaluateExpressionAsync<bool>(script));
        Assert.True(await page.EvaluateExpressionAsync<bool>(
            "matchMedia('(forced-colors: none)').matches"));
    }

    private static string BuildStyleSheet()
    {
        var script = InjectionScriptBuilder.BuildInstall(
            new WallpaperInjectionOptions(
                Generation,
                @"C:\Wallpapers\wallpaper.png",
                1234,
                WallpaperMediaKind.Image),
            PresentationContractCatalog.CreateFullySupportedCapabilities());
        return InjectionScriptPayloadTestHelper.ExtractStyleSheet(script);
    }

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

    private static async Task<IReadOnlyDictionary<string, ComputedStyleSnapshot>>
        ReadSnapshotsAsync(IPage page)
    {
        var snapshots = await page.EvaluateExpressionAsync<ComputedStyleSnapshot[]>(
            """
            (() => Array.from(document.querySelectorAll("[data-fixture-id]"), node => {
              const style = getComputedStyle(node);
              const before = getComputedStyle(node, "::before");
              const after = getComputedStyle(node, "::after");
              return {
                id: node.dataset.fixtureId,
                backgroundColor: style.backgroundColor,
                backgroundImage: style.backgroundImage,
                backdropFilter: style.backdropFilter || style.webkitBackdropFilter,
                zIndex: style.zIndex,
                display: style.display,
                visibility: style.visibility,
                pointerEvents: style.pointerEvents,
                beforeBackgroundColor: before.backgroundColor,
                beforeBackdropFilter:
                  before.backdropFilter || before.webkitBackdropFilter,
                beforeLeft: before.left,
                beforeRight: before.right,
                beforeTransform: before.transform,
                afterBackgroundImage: after.backgroundImage
              };
            }))()
            """);
        return snapshots.ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
    }

    private static Task<CssomProbe> ReadCssomProbeAsync(IPage page) =>
        page.EvaluateExpressionAsync<CssomProbe>(
            $$"""
            (() => {
              const style = document.querySelector(
                "#{{InjectionScriptBuilder.StyleElementId}}"
              );
              if (!style?.sheet) throw new Error("Missing owned stylesheet.");
              const selectors = [];
              const visit = rules => {
                for (const rule of rules) {
                  if (rule instanceof CSSStyleRule) selectors.push(rule.selectorText);
                  if (rule.cssRules) visit(rule.cssRules);
                }
              };
              visit(style.sheet.cssRules);
              const invalidSelectors = [];
              const matchedFixtureIds = new Set();
              for (const selector of selectors) {
                try {
                  for (const node of document.querySelectorAll(selector)) {
                    if (node.dataset.fixtureId) {
                      matchedFixtureIds.add(node.dataset.fixtureId);
                    }
                  }
                } catch (error) {
                  invalidSelectors.push(`${selector}: ${error}`);
                }
              }
              return {
                ruleCount: selectors.length,
                invalidSelectors,
                matchedFixtureIds: Array.from(matchedFixtureIds).sort()
              };
            })()
            """);

    private static Task<CapabilityMarkers> ReadCapabilityMarkersAsync(IPage page) =>
        page.EvaluateExpressionAsync<CapabilityMarkers>(
            $$"""
            (() => {
              const css = document.querySelector(
                "#{{InjectionScriptBuilder.StyleElementId}}"
              )?.textContent ?? "";
              return {
                glassPresent:
                  css.includes("codex-wallpaper-glass:start") &&
                  css.includes("codex-wallpaper-glass:end"),
                advancedPresent:
                  css.includes("codex-wallpaper-advanced:start") &&
                  css.includes("codex-wallpaper-advanced:end")
              };
            })()
            """);

    private static void AssertGlass(
        IReadOnlyDictionary<string, ComputedStyleSnapshot> snapshots,
        string id)
    {
        var snapshot = snapshots[id];
        Assert.NotEqual(NativeBackground, snapshot.BackgroundColor);
        Assert.NotEqual(TransparentBackground, snapshot.BackgroundColor);
        Assert.Contains("blur(", snapshot.BackdropFilter, StringComparison.Ordinal);
    }

    private static void AssertPseudoGlass(
        IReadOnlyDictionary<string, ComputedStyleSnapshot> snapshots,
        string id)
    {
        var snapshot = snapshots[id];
        Assert.NotEqual(TransparentBackground, snapshot.BeforeBackgroundColor);
        Assert.Contains("blur(", snapshot.BeforeBackdropFilter, StringComparison.Ordinal);
    }

    private static void AssertClear(
        IReadOnlyDictionary<string, ComputedStyleSnapshot> snapshots,
        string id)
    {
        Assert.Equal(TransparentBackground, snapshots[id].BackgroundColor);
    }

    private static void AssertClearWithAfter(
        IReadOnlyDictionary<string, ComputedStyleSnapshot> snapshots,
        string id)
    {
        AssertClear(snapshots, id);
        Assert.Equal("none", snapshots[id].AfterBackgroundImage);
    }

    private static void AssertNative(
        IReadOnlyDictionary<string, ComputedStyleSnapshot> snapshots,
        params string[] ids)
    {
        Assert.All(ids, id => Assert.Equal(NativeBackground, snapshots[id].BackgroundColor));
    }

    private sealed record ComputedStyleSnapshot(
        string Id,
        string BackgroundColor,
        string BackgroundImage,
        string BackdropFilter,
        string ZIndex,
        string Display,
        string Visibility,
        string PointerEvents,
        string BeforeBackgroundColor,
        string BeforeBackdropFilter,
        string BeforeLeft,
        string BeforeRight,
        string BeforeTransform,
        string AfterBackgroundImage);

    private sealed record CssomProbe(
        int RuleCount,
        string[] InvalidSelectors,
        string[] MatchedFixtureIds);

    private sealed record CapabilityMarkers(
        bool GlassPresent,
        bool AdvancedPresent);

    private const string NativeStyles =
        """
        <style>
          [data-fixture-id] {
            background-color: rgb(41 42 43);
            background-image: none;
            -webkit-backdrop-filter: none;
            backdrop-filter: none;
            border-color: rgb(90 91 92);
            box-shadow: none;
          }
          [data-native-fade] {
            background-image: linear-gradient(to bottom, rgb(41 42 43), transparent);
          }
          [data-native-sticky]::after {
            content: "";
            position: absolute;
            inset: 100% 0 auto;
            height: 32px;
            background-image: linear-gradient(to bottom, rgb(41 42 43), transparent);
          }
        </style>
        """;

    private const string ShellFixture =
        $$"""
        <html class="electron-dark"
              data-codex-window-type="electron"
              data-codex-window-chrome="application-menu">
          <head>{{NativeStyles}}</head>
          <body>
            <div id="root">
              <div>
                <div data-fixture-id="top-app-bar">
                  <div role="menubar" data-orientation="horizontal"></div>
                </div>
                <div data-fixture-id="wrong-orientation-topbar">
                  <div role="menubar" data-orientation="vertical"></div>
                </div>
                <main>
                  <header data-app-shell-application-menu-bar
                          data-app-shell-header-edge-scroll
                          data-fixture-id="edge-scroll-header">
                    <div data-testid="app-shell-header-context-menu-surface"
                         data-fixture-id="main-header-context"></div>
                  </header>
                  <header class="_Header_fixture"
                          data-fixture-id="css-module-header-without-data-markers"></header>
                </main>
              </div>
            </div>

            <aside data-app-shell-focus-area="left-panel"
                   data-fixture-id="left-panel-lookalike"></aside>

            <aside data-app-shell-focus-area="right-panel">
              <div>
                <div class="bg-token-main-surface-primary"
                     data-fixture-id="right-panel-glass-shell">
                  <div class="bg-token-main-surface-primary"
                       data-app-shell-tabs="true"
                       data-fixture-id="current-tabs-root">
                    <div class="bg-token-main-surface-primary"
                         data-fixture-id="current-toolbar">
                      <div data-app-shell-tab-strip-controller="right">
                        <button class="bg-token-main-surface-primary"
                                data-fixture-id="current-selected-tab"></button>
                      </div>
                    </div>
                    <div role="tabpanel"
                         data-app-shell-tab-panel-controller="right">
                      <div class="bg-token-main-surface-primary"
                           data-fixture-id="file-layout-shell">
                        <div class="monaco-editor bg-token-main-surface-primary"
                             data-fixture-id="editor-surface"></div>
                        <div class="bg-token-main-surface-primary"
                             data-diff-view="unified"
                             data-fixture-id="diff-surface"></div>
                        <pre class="bg-token-main-surface-primary"
                             data-fixture-id="code-surface"><code>code</code></pre>
                        <table class="bg-token-main-surface-primary"
                               data-fixture-id="table-surface"><tbody></tbody></table>
                        <div class="bg-token-main-surface-primary"
                             data-popcorn-root
                             data-fixture-id="popcorn-surface"></div>
                      </div>
                      <section>
                        <div class="relative rounded-lg bg-token-main-surface-primary"
                             data-fixture-id="markdown-shell-legacy">
                          <article class="markdown"></article>
                        </div>
                        <div class="relative rounded-lg bg-token-main-surface-primary"
                             data-fixture-id="markdown-shell-module">
                          <article class="_markdownContent_fixture"></article>
                        </div>
                        <div class="relative rounded-lg bg-token-main-surface-primary"
                             data-fixture-id="markdown-substring-near-miss">
                          <article class="prefix_markdownContent_fixture"></article>
                        </div>
                        <div class="relative rounded-lg bg-token-main-surface-primary"
                             data-fixture-id="rounded-surface-without-markdown"></div>
                      </section>
                    </div>
                  </div>
                </div>
              </div>
            </aside>

            <aside data-app-shell-focus-area="right-panel">
              <div><div><div class="bg-token-main-surface-primary"
                   data-fixture-id="right-panel-near-miss">
                <div><div role="tabpanel"
                          data-app-shell-tab-panel-controller="right"></div></div>
              </div></div></div>
            </aside>

            <aside data-app-shell-focus-area="right-panel">
              <div>
                <div class="bg-token-main-surface-primary"
                     data-fixture-id="launcher-glass-shell">
                  <div class="bg-token-main-surface-primary"
                       data-app-shell-tabs="true"
                       data-fixture-id="launcher-tabs-root">
                    <div class="bg-token-main-surface-primary"
                         data-fixture-id="launcher-toolbar"></div>
                    <div class="bg-token-main-surface-primary"
                         data-fixture-id="launcher-scroll-content">
                      <button class="bg-token-main-surface-secondary"
                              data-fixture-id="launcher-review-card"></button>
                    </div>
                  </div>
                </div>
              </div>
            </aside>

            <aside data-app-shell-focus-area="left-panel">
              <div><div class="bg-token-main-surface-primary"
                        data-fixture-id="left-launcher-glass-shell">
                <div data-app-shell-tabs="true"></div>
              </div></div>
            </aside>
            <aside data-app-shell-focus-area="right-panel">
              <div><div class="bg-token-main-surface-primary"
                        data-fixture-id="wrong-controller-glass-shell">
                <div data-app-shell-tabs="true">
                  <div role="tabpanel"
                       data-app-shell-tab-panel-controller="left"></div>
                </div>
              </div></div>
            </aside>
            <aside data-app-shell-focus-area="right-panel">
              <div><div class="bg-token-main-surface-primary">
                <div data-app-shell-tabs="true">
                  <div role="tabpanel"
                       data-app-shell-tab-panel-controller="right">
                    <div>
                      <div class="monaco-editor bg-token-main-surface-primary"
                           data-fixture-id="populated-editor-surface"></div>
                    </div>
                  </div>
                </div>
              </div></div>
            </aside>

            <div data-browser-sidebar-webview-host-root
                 data-fixture-id="browser-host-current"></div>
            <div data-browser-sidebar-webview-host-root
                 data-fixture-id="browser-host-current-hidden"
                 style="display:none"></div>
            <div data-browser-sidebar-webview
                 data-app-shell-focus-area="right-panel"
                 data-fixture-id="browser-host-legacy"></div>
            <section><div data-browser-sidebar-webview-host-root
                          data-fixture-id="browser-host-current-nested"></div></section>
            <div data-browser-sidebar-webview
                 data-fixture-id="browser-host-legacy-near-miss"></div>
          </body>
        </html>
        """;

    private const string AdvancedFixture =
        $$"""
        <html class="electron-dark">
          <head>{{NativeStyles}}</head>
          <body>
            <main>
              <div data-app-shell-main-content-top-fade
                   data-native-fade
                   data-fixture-id="main-content-top-fade"></div>
              <div class="thread-scroll-container">
                <div class="bg-gradient-to-t from-token-main-surface-primary via-token-main-surface-primary"
                     data-native-fade
                     data-fixture-id="composer-surface-fade"></div>
              </div>
              <div data-codex-composer-root>
                <div data-above-composer-portal>
                  <div data-in-progress-fixed-content>
                    <div class="absolute inset-x-0 bottom-1 flex min-h-7 items-center justify-center gap-2 pb-1">
                      <button data-fixture-id="changed-files-summary-button"></button>
                      <div class="pointer-events-none absolute inset-x-0 -bottom-1 h-7 bg-gradient-to-t from-token-main-surface-primary to-transparent"
                           data-native-fade
                           data-fixture-id="changed-files-composer-fade"></div>
                    </div>
                  </div>
                </div>
                <div data-fixture-id="composer-surface-chrome"></div>
              </div>

              <div data-content-search-unit-key="fallback-only">
                <h4 class="sr-only select-none"></h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="fallback-assistant-message">
                  <div data-selected-text-overlay-target></div>
                </div>
              </div>
              <div data-response-annotation-conversation="conversation"
                   data-response-annotation-target="response"
                   data-fixture-id="annotated-assistant-message"></div>
              <div data-user-message-bubble="true"
                   data-fixture-id="user-message-bubble"></div>

              <div data-content-search-unit-key="wide-fallback">
                <h4 class="sr-only select-none"></h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="wide-fallback-assistant">
                  <div data-selected-text-overlay-target></div>
                  <div class="_tableContainer_fixture _tableWideBlock_fixture">
                    <div class="_tableScroller_fixture">
                      <div class="_tableWrapper_fixture">
                        <table class="_table_fixture"></table>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
              <div dir="rtl"
                   data-response-annotation-conversation="conversation-rtl"
                   data-response-annotation-target="response-rtl"
                   data-fixture-id="wide-annotated-assistant-rtl">
                <div class="_tableContainer_fixture _tableWideBlock_fixture">
                  <div class="_tableScroller_fixture">
                    <div class="_tableWrapper_fixture">
                      <table class="_table_fixture"></table>
                    </div>
                  </div>
                </div>
              </div>

              <article data-fixture-id="ordinary-selected-text-container">
                <div data-selected-text-overlay-target></div>
              </article>
              <div data-content-search-unit-key="non-adjacent">
                <h4 class="sr-only select-none"></h4><span></span>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="non-adjacent-assistant-lookalike">
                  <div data-selected-text-overlay-target></div>
                </div>
              </div>
              <div data-content-search-unit-key="wrong-heading">
                <h4 class="select-none"></h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="assistant-heading-without-sr-only">
                  <div data-selected-text-overlay-target></div>
                </div>
              </div>
              <div data-content-search-unit-key="wrong-wrapper">
                <h4 class="sr-only select-none"></h4>
                <div data-fixture-id="assistant-wrapper-without-required-classes">
                  <div data-selected-text-overlay-target></div>
                </div>
              </div>
              <div data-content-search-unit-key="nested-target">
                <h4 class="sr-only select-none"></h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="assistant-target-not-direct-child">
                  <section><div data-selected-text-overlay-target></div></section>
                </div>
              </div>
              <article data-fixture-id="wide-table-outside-assistant">
                <div class="_tableContainer_fixture _tableWideBlock_fixture">
                  <div class="_tableScroller_fixture"><div class="_tableWrapper_fixture">
                    <table class="_table_fixture"></table>
                  </div></div>
                </div>
              </article>
              <table data-fixture-id="ordinary-table"></table>
              <div data-local-conversation-item-target-ids
                   data-fixture-id="activity-surface"></div>
            </main>
            <aside>
              <div data-content-search-unit-key="outside-main">
                <h4 class="sr-only select-none"></h4>
                <div class="group flex min-w-0 flex-col"
                     data-fixture-id="assistant-message-outside-main">
                  <div data-selected-text-overlay-target></div>
                </div>
              </div>
            </aside>
          </body>
        </html>
        """;

    private const string RouteFixture =
        $$"""
        <html class="electron-dark">
          <head>{{NativeStyles}}</head>
          <body>
            <div class="sticky z-30 bg-token-main-surface-primary"
                 data-native-sticky data-fixture-id="plugins-search-sticky">
              <input id="plugins-page-search">
            </div>
            <div class="sticky z-30 bg-token-main-surface-primary"
                 data-native-sticky data-fixture-id="plugins-search-sticky-wrong-id">
              <input id="plugins-page-search-near-miss">
            </div>
            <article data-fixture-id="plugins-featured-card"></article>

            <div class="sticky z-30 bg-token-main-surface-primary"
                 data-native-sticky data-fixture-id="scheduled-search-sticky">
              <input id="scheduled-page-search">
            </div>
            <div class="sticky z-30 bg-token-main-surface-primary"
                 data-native-sticky data-fixture-id="scheduled-search-sticky-wrong-id">
              <input id="scheduled-page-search-near-miss">
            </div>
            <article data-fixture-id="scheduled-task-row"></article>

            <div class="flex h-full min-h-0 flex-col bg-token-main-surface-primary"
                 data-fixture-id="sites-route-root">
              <div class="sticky z-30 bg-token-main-surface-primary"
                   data-native-sticky data-fixture-id="sites-search-sticky">
                <input id="appgen-site-search">
              </div>
              <article data-fixture-id="sites-card"></article>
            </div>
            <div class="flex h-full min-h-0 flex-col bg-token-main-surface-primary"
                 data-fixture-id="sites-route-root-wrong-id">
              <input id="appgen-site-search-near-miss">
            </div>
            <section><div class="sticky z-30 bg-token-main-surface-primary"
                          data-native-sticky
                          data-fixture-id="sites-search-sticky-outside-route">
              <input id="appgen-site-search">
            </div></section>

            <main>
              <div class="flex h-full min-h-0 w-full flex-col bg-token-main-surface-primary"
                   data-fixture-id="pull-request-list-root">
                <div class="sticky z-30 bg-token-main-surface-primary"
                     data-native-sticky data-fixture-id="pull-request-search-sticky">
                  <input id="pull-request-inbox-search">
                </div>
                <article data-fixture-id="pull-request-card"></article>
              </div>
              <aside data-app-shell-focus-area="right-panel">
                <div class="absolute inset-0 min-h-0 min-w-0 overflow-hidden">
                  <div class="absolute top-0 bottom-0 left-0 min-w-0 bg-token-main-surface-primary"
                       data-fixture-id="pull-request-detail-shell">
                    <div class="h-full min-h-0 min-w-0 overflow-hidden"><div class="h-full">
                      <section class="h-full min-h-0 min-w-0 bg-token-main-surface-primary"
                               data-fixture-id="pull-request-detail-section">
                        <div class="@container/app-shell-detail-panel flex h-full min-h-0 flex-col bg-token-main-surface-primary"
                             data-fixture-id="pull-request-detail-root">
                          <div class="monaco-editor" data-fixture-id="pull-request-editor"></div>
                          <div data-diff-view data-fixture-id="pull-request-diff"></div>
                          <pre data-fixture-id="pull-request-code"></pre>
                        </div>
                        <div class="@container/app-shell-detail-pane flex h-full min-h-0 flex-col bg-token-main-surface-primary"
                             data-fixture-id="pull-request-detail-root-near-miss"></div>
                      </section>
                    </div></div>
                  </div>
                </div>
              </aside>
              <aside data-app-shell-focus-area="right-panel">
                <div class="absolute inset-0 min-h-0 min-w-0 overflow-hidden">
                  <div class="absolute top-0 bottom-0 left-0 min-w-0 bg-token-main-surface-primary"
                       data-fixture-id="pull-request-ordinary-tab-shell">
                    <div role="tabpanel" data-app-shell-tab-panel-controller="right"></div>
                  </div>
                </div>
              </aside>
            </main>
            <div class="flex h-full min-h-0 w-full flex-col bg-token-main-surface-primary"
                 data-fixture-id="pull-request-list-root-wrong-id">
              <input id="pull-request-inbox-search-near-miss">
            </div>

            <div class="relative isolate flex max-h-full min-h-0 w-full flex-1">
              <aside class="app-shell-left-panel"><button data-settings-panel-slug="general"></button></aside>
              <main data-app-shell-main-surface="default">
                <div class="relative isolate flex min-h-0 flex-1 overflow-hidden">
                  <div data-app-shell-main-content-layout data-app-shell-right-panel-full-width>
                    <div data-app-shell-thread-edge-divider>
                      <div class="relative flex min-h-0 flex-1">
                        <div class="h-full min-h-0 min-w-0 flex-1">
                          <div class="h-full min-w-0 overflow-visible">
                            <div class="flex h-full min-h-0 flex-col electron:overflow-hidden electron:bg-token-main-surface-primary electron:elevation-prominent windows:rounded-tl-lg"
                                 data-fixture-id="settings-content-canvas">
                              <section data-fixture-id="settings-permissions-card"></section>
                              <section data-fixture-id="settings-general-card"></section>
                            </div>
                          </div>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </main>
            </div>
            <div class="relative isolate flex max-h-full min-h-0 w-full flex-1">
              <aside class="app-shell-left-panel"><button data-settings-panel-slug="general"></button></aside>
              <main data-app-shell-main-surface="browser">
                <div class="relative isolate flex min-h-0 flex-1 overflow-hidden">
                  <div data-app-shell-main-content-layout data-app-shell-right-panel-full-width>
                    <div data-app-shell-thread-edge-divider><div class="relative flex min-h-0 flex-1">
                      <div class="h-full min-h-0 min-w-0 flex-1"><div class="h-full min-w-0 overflow-visible">
                        <div class="flex h-full min-h-0 flex-col electron:overflow-hidden electron:bg-token-main-surface-primary electron:elevation-prominent windows:rounded-tl-lg"
                             data-fixture-id="settings-browser-canvas"></div>
                      </div></div>
                    </div></div>
                  </div>
                </div>
              </main>
            </div>
            <div class="relative isolate flex max-h-full min-h-0 w-full flex-1">
              <aside class="app-shell-left-panel"></aside>
              <main data-app-shell-main-surface="default">
                <div class="relative isolate flex min-h-0 flex-1 overflow-hidden">
                  <div data-app-shell-main-content-layout data-app-shell-right-panel-full-width>
                    <div data-app-shell-thread-edge-divider><div class="relative flex min-h-0 flex-1">
                      <div class="h-full min-h-0 min-w-0 flex-1"><div class="h-full min-w-0 overflow-visible">
                        <div class="flex h-full min-h-0 flex-col electron:overflow-hidden electron:bg-token-main-surface-primary electron:elevation-prominent windows:rounded-tl-lg"
                             data-fixture-id="settings-canvas-without-data-anchor"></div>
                      </div></div>
                    </div></div>
                  </div>
                </div>
              </main>
            </div>

            <aside class="app-shell-left-panel">
              <button data-settings-panel-slug="keyboard-shortcuts" aria-current="page"></button>
            </aside>
            <main>
              <div class="sticky z-30 bg-token-main-surface-primary"
                   data-native-sticky data-fixture-id="keyboard-search-sticky">
                <input type="text" data-fixture-id="keyboard-search-input">
              </div>
              <div class="sticky z-30 bg-token-main-surface-primary"
                   data-native-sticky data-fixture-id="keyboard-sticky-non-text-input">
                <input type="search">
              </div>
              <section data-fixture-id="keyboard-shortcut-row"></section>
              <div data-codex-composer-root>
                <div data-above-composer-portal><div data-in-progress-fixed-content>
                  <div class="absolute inset-x-0 bottom-1 flex min-h-7 items-center justify-center gap-2 pb-1">
                    <button data-fixture-id="changed-files-summary-button"></button>
                    <div class="pointer-events-none absolute inset-x-0 -bottom-1 h-7 bg-gradient-to-t from-token-main-surface-primary to-transparent"
                         data-native-fade data-fixture-id="changed-files-route-fade"></div>
                  </div>
                </div></div>
                <div data-fixture-id="composer-surface-chrome-route"></div>
              </div>
            </main>
          </body>
        </html>
        """;

    private const string DowngradeFixture =
        $$"""
        <html class="electron-dark">
          <head>{{NativeStyles}}</head>
          <body>
            <aside data-app-shell-focus-area="left-panel"
                   data-fixture-id="downgrade-glass-surface"></aside>
            <main>
              <div data-app-shell-main-content-top-fade
                   data-native-fade
                   data-fixture-id="downgrade-advanced-fade"></div>
            </main>
          </body>
        </html>
        """;
}
