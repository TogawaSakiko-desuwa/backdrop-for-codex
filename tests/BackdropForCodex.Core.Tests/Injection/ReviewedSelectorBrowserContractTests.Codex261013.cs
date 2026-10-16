using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Tests.Infrastructure;
using PuppeteerSharp;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed partial class ReviewedSelectorBrowserContractTests
{
    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task Codex261013_WindowGuttersStayOutsideContentAndDoNotInterceptInput()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            string[] themes = ["electron-dark", "electron-light"];
            int[] widths = [720, 1280];
            const string layoutAndHitTargets = """
                JSON.stringify({
                  geometry: Array.from(document.querySelectorAll('[data-layout-probe]'),
                    node => node.getBoundingClientRect().toJSON()),
                  hitTargets: [[2, 40], [innerWidth - 2, 200], [400, innerHeight - 2],
                    [400, 44], [400, 200], [40, 18]].map(([x, y]) =>
                      document.elementFromPoint(x, y)?.id)
                })
                """;
            foreach (var theme in themes)
            {
                foreach (var width in widths)
                {
                    await page.SetViewportAsync(new ViewPortOptions
                    {
                        Width = width,
                        Height = 900,
                        DeviceScaleFactor = 1.25,
                    });
                    await LoadFixtureAsync(page, Codex261013GutterFixture.Replace(
                        "electron-dark", theme, StringComparison.Ordinal));
                    var original = await page.EvaluateExpressionAsync<string>(layoutAndHitTargets);
                    await AddOwnedStyleAsync(page, BuildStyleSheet(), initializeOwnership: true);
                    Assert.True(await page.EvaluateExpressionAsync<bool>("""
                        (() => {
                          const top = getComputedStyle(document.getElementById('workspace'), '::before');
                          const edge = getComputedStyle(document.getElementById('frame'), '::after');
                          return top.content === '""' && top.height === '8px' && top.top === '0px'
                            && top.pointerEvents === 'none' && top.backdropFilter.includes('blur(')
                            && top.backgroundColor !== 'rgba(0, 0, 0, 0)'
                            && edge.content === '""' && edge.pointerEvents === 'none'
                            && edge.borderRightWidth === '4px' && edge.borderBottomWidth === '4px'
                            && edge.borderTopWidth === '0px' && edge.borderLeftWidth === '0px'
                            && edge.backgroundColor === 'rgba(0, 0, 0, 0)'
                            && edge.backdropFilter === 'none'
                            && getComputedStyle(document.getElementById('unrelated-frame'), '::after').content === 'none';
                        })()
                        """));
                    Assert.Equal(original, await page.EvaluateExpressionAsync<string>(layoutAndHitTargets));
                    await page.ClickAsync("#menu-action");
                    Assert.Equal("clicked", await page.EvaluateExpressionAsync<string>(
                        "document.getElementById('menu-action').dataset.result"));

                    Assert.True(await page.EvaluateExpressionAsync<bool>(
                        InjectionScriptBuilder.BuildCapabilityDowngrade(
                            Generation, CreateCapabilities(glassAvailable: false, advancedAvailable: true))));
                    Assert.True(await page.EvaluateExpressionAsync<bool>("""
                        getComputedStyle(document.getElementById('workspace'), '::before').content === 'none'
                        && getComputedStyle(document.getElementById('frame'), '::after').content === 'none'
                        """));
                }
            }
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task Codex261013_RouteGlassPreservesContentAndFrameGeometry()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            string[] themes = ["electron-dark", "electron-light"];
            int[] widths = [720, 1280];
            string[] glassSurfaces = ["settings-1013", "page-1013", "pane-1013", "mcp-shell-1013"];
            string[] nativeSurfaces =
            [
                "settings-card-1013", "settings-without-navigation-1013", "settings-browser-1013",
                "page-card-1013", "page-search-1013", "pane-editor-1013", "pane-web-content-1013",
                "new-tab-action-1013", "mcp-content-1013", "new-tab-outside-pane-1013",
            ];
            const string geometryProbe = """
                JSON.stringify(Array.from(document.querySelectorAll('[data-layout-probe]'), node => {
                  const style = getComputedStyle(node);
                  return [node.getBoundingClientRect().toJSON(), style.position,
                    style.pointerEvents, style.zIndex, style.overflow, style.borderRadius,
                    style.borderLeftWidth];
                }))
                """;
            foreach (var theme in themes)
            {
                foreach (var width in widths)
                {
                    await page.SetViewportAsync(new ViewPortOptions { Width = width, Height = 900 });
                    await LoadFixtureAsync(page, Codex261013RouteFixture.Replace(
                        "electron-dark", theme, StringComparison.Ordinal));
                    var geometry = await page.EvaluateExpressionAsync<string>(geometryProbe);
                    await AddOwnedStyleAsync(page, BuildStyleSheet(), initializeOwnership: true);
                    var snapshots = await ReadSnapshotsAsync(page);
                    foreach (var id in glassSurfaces)
                    {
                        AssertGlass(snapshots, id);
                    }

                    AssertClear(snapshots, "page-list-1013");
                    AssertClear(snapshots, "page-content-1013");
                    Assert.Equal(TransparentBackground, snapshots["page-header-1013"].BeforeBackgroundColor);
                    AssertClear(snapshots, "new-tab-1013");
                    AssertNative(snapshots, nativeSurfaces);
                    Assert.Equal(geometry, await page.EvaluateExpressionAsync<string>(geometryProbe));
                    Assert.Empty((await ReadCssomProbeAsync(page)).InvalidSelectors);

                    Assert.True(await page.EvaluateExpressionAsync<bool>(
                        InjectionScriptBuilder.BuildCapabilityDowngrade(
                            Generation, CreateCapabilities(glassAvailable: true, advancedAvailable: false))));
                    foreach (var id in glassSurfaces)
                    {
                        AssertGlass(await ReadSnapshotsAsync(page), id);
                    }

                    Assert.True(await page.EvaluateExpressionAsync<bool>(
                        InjectionScriptBuilder.BuildCapabilityDowngrade(
                            Generation, CreateCapabilities(glassAvailable: false, advancedAvailable: false))));
                    var downgraded = await ReadSnapshotsAsync(page);
                    AssertNative(downgraded, glassSurfaces);
                    AssertNative(downgraded, "page-list-1013", "page-content-1013", "new-tab-1013");
                    Assert.Equal(NativeBackground, downgraded["page-header-1013"].BeforeBackgroundColor);
                }
            }
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task Codex261013_ComposerModulesClearOnlyDecorativeOwners()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await LoadFixtureAsync(page, Codex261013ComposerFixture);
            var original = await ReadSnapshotsAsync(page);
            await AddOwnedStyleAsync(page, BuildStyleSheet(), initializeOwnership: true);
            var snapshots = await ReadSnapshotsAsync(page);
            Assert.Equal("none", snapshots["module-fade-1013"].BackgroundImage);
            AssertClear(snapshots, "module-surface-1013");
            AssertClear(snapshots, "module-notice-1013");
            AssertNative(snapshots, "module-composer-1013", "module-nested-surface-1013",
                "module-wrong-prefix-1013", "module-outside-thread-1013");
            Assert.Equal(original["module-unreviewed-fade-1013"].BackgroundImage,
                snapshots["module-unreviewed-fade-1013"].BackgroundImage);

            Assert.True(await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCapabilityDowngrade(
                    Generation, CreateCapabilities(glassAvailable: false, advancedAvailable: true))));
            AssertClear(await ReadSnapshotsAsync(page), "module-surface-1013");
            Assert.True(await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCapabilityDowngrade(
                    Generation, CreateCapabilities(glassAvailable: false, advancedAvailable: false))));
            var downgraded = await ReadSnapshotsAsync(page);
            AssertNative(downgraded, "module-surface-1013", "module-notice-1013");
            Assert.Equal(original["module-fade-1013"].BackgroundImage,
                downgraded["module-fade-1013"].BackgroundImage);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task Codex261013_DotGlassPreservesMessagesAndFloatingSurfaces()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await LoadFixtureAsync(page, Codex261013DotFixture);
            await AddOwnedStyleAsync(page, BuildStyleSheet(), initializeOwnership: true);
            var snapshots = await ReadSnapshotsAsync(page);
            AssertGlass(snapshots, "dot-root-1013");
            AssertClear(snapshots, "dot-thread-1013");
            AssertClear(snapshots, "dot-room-1013");
            AssertClear(snapshots, "dot-room-pane-1013");
            AssertClear(snapshots, "dot-disclaimer-1013");
            Assert.True(await page.EvaluateExpressionAsync<bool>("""
                (() => {
                  const style = getComputedStyle(document.getElementById('dot-composer-wrap'));
                  return style.borderBottomColor === 'rgba(0, 0, 0, 0)' && style.borderBottomWidth === '12px';
                })()
                """));
            AssertNative(snapshots, "dot-message-1013", "dot-code-1013", "dot-composer-1013",
                "dot-floating-1013", "dot-attachment-1013", "dot-outside-main-1013");
            Assert.True(await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCapabilityDowngrade(
                    Generation, CreateCapabilities(glassAvailable: false, advancedAvailable: true))));
            AssertNative(await ReadSnapshotsAsync(page), "dot-root-1013",
                "dot-room-1013", "dot-room-pane-1013", "dot-disclaimer-1013");
        });
    }

    // Packaged 26.1013 structures; hashes and content are synthetic, with no account data.
    private const string Codex261013GutterFixture =
        """
        <html class="electron-dark"><head><style>
          html, body { margin: 0; height: 100%; }
          #frame { --spacing: 4px; --app-shell-titlebar-height: 8px; --app-shell-page-inline-end-inset: 4px;
            position: relative; display: flex; flex-direction: column; box-sizing: border-box;
            height: 100vh; padding: 0 4px 4px 0; }
          #menu { height: 36px; flex-shrink: 0; }
          #workspace { position: relative; display: flex; flex: 1; padding-top: 8px; min-height: 0; }
          #sidebar { width: 240px; flex-shrink: 0; }
          #canvas { flex: 1; }
          #menu-action { width: 80px; height: 30px; }
        </style></head><body><div id="root">
          <div id="frame" data-app-shell-frame="true" data-app-shell-page-surface="true" data-app-shell-application-menu-header data-layout-probe>
            <div id="menu" data-layout-probe><button id="menu-action" onclick="this.dataset.result='clicked'">Menu</button></div>
            <div id="workspace" class="relative" data-app-shell-workspace-row="true" data-layout-probe>
              <aside id="sidebar" data-layout-probe></aside><main id="canvas" data-layout-probe></main>
            </div>
          </div>
          <div id="unrelated-frame" data-app-shell-frame="true" data-app-shell-page-surface="true"></div>
        </div></body></html>
        """;

    private const string Codex261013RouteFixture =
        $$"""
        <html class="electron-dark"><head>{{NativeStyles}}
          <style>
            [data-app-shell-pane-frame] { position: relative; height: 300px; border-left: 1px solid gray; border-radius: 12px; overflow: hidden; }
            [data-sticky]::before { content: ''; position: absolute; inset: 0; background-color: rgb(41 42 43); pointer-events: none; }
          </style>
        </head><body><div id="root">
          <div data-app-shell-frame="true" data-app-shell-page-surface="true">
            <aside class="app-shell-left-panel"><button data-settings-panel-slug="general"></button></aside>
            <main data-app-shell-main-surface="default">
              <div data-app-shell-focus-area="main">
                <div data-codex-history-swipe-page class="relative isolate flex h-full min-h-0 min-w-0 flex-col overflow-visible">
                  <div class="flex h-full min-h-0 flex-col electron:overflow-hidden electron:bg-surface windows:rounded-tl-lg" data-fixture-id="settings-1013" data-layout-probe>
                    <section data-fixture-id="settings-card-1013"></section>
                  </div>
                </div>
              </div>
            </main>
            <main data-app-shell-main-surface="browser"><div data-app-shell-focus-area="main"><div data-codex-history-swipe-page>
              <div class="flex h-full min-h-0 flex-col electron:overflow-hidden electron:bg-surface windows:rounded-tl-lg" data-fixture-id="settings-browser-1013"></div>
            </div></div></main>
          </div>
          <div data-app-shell-frame="true"><main data-app-shell-main-surface="default"><div data-app-shell-focus-area="main"><div data-codex-history-swipe-page>
            <div class="flex h-full min-h-0 flex-col electron:overflow-hidden electron:bg-surface windows:rounded-tl-lg" data-fixture-id="settings-without-navigation-1013"></div>
          </div></div></main></div>
          <main data-app-shell-main-surface="default">
            <div data-app-shell-focus-area="main">
              <div class="contents">
                <div data-app-shell-inline-page-header="true" class="h-full min-h-0 flex-1 overflow-x-hidden overflow-y-auto relative" data-fixture-id="page-1013" data-layout-probe>
                  <div class="flex min-h-full w-full flex-col">
                    <div class="_shell_fixture border-transparent" data-sticky data-fixture-id="page-header-1013">
                      <input id="appgen-site-search" data-fixture-id="page-search-1013">
                    </div>
                    <div class="mx-auto flex min-h-0 w-full flex-1 flex-col pb-4 bg-surface" data-fixture-id="page-content-1013">
                      <div class="mx-auto flex min-h-full w-full flex-1 flex-col bg-surface" data-fixture-id="page-list-1013">
                        <article class="bg-surface-card" data-fixture-id="page-card-1013"></article>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
              <div data-mcp-extension-surface class="flex h-full min-h-0 flex-col bg-surface text-default" data-fixture-id="mcp-shell-1013">
                <div data-fixture-id="mcp-content-1013"></div>
              </div>
            </div>
            <aside data-app-shell-focus-area="right-panel">
              <div class="absolute inset-x-0 bottom-0 min-h-0 min-w-0 overflow-visible">
                <div class="absolute inset-0 min-h-0 min-w-0 overflow-hidden">
                  <div data-app-shell-pane-frame="true" class="absolute top-0 bottom-0 min-w-0 left-0" data-fixture-id="pane-1013" data-layout-probe>
                    <div class="flex h-full min-h-0 min-w-0 flex-col overflow-hidden">
                      <div data-new-tab-scroll-root="true" class="@container flex h-full min-h-0 flex-col overflow-x-hidden overflow-y-auto bg-surface-canvas px-panel py-8 select-none" data-fixture-id="new-tab-1013">
                        <button data-fixture-id="new-tab-action-1013"></button>
                      </div>
                      <div class="monaco-editor bg-surface" data-fixture-id="pane-editor-1013"></div>
                      <div class="bg-surface" data-fixture-id="pane-web-content-1013"></div>
                    </div>
                  </div>
                </div>
              </div>
            </aside>
            <div data-new-tab-scroll-root="true" class="flex h-full min-h-0 flex-col overflow-x-hidden overflow-y-auto bg-surface-canvas" data-fixture-id="new-tab-outside-pane-1013"></div>
          </main>
        </div></body></html>
        """;

    private const string Codex261013ComposerFixture =
        $$"""
        <html class="electron-dark"><head>{{NativeStyles}}</head><body><div id="root">
          <main data-app-shell-main-surface="default">
            <div class="thread-scroll-container">
              <div class="[container-name:thread-content]">
                <div aria-hidden="true" class="pointer-events-none sticky bottom-0 z-10 mt-auto w-full shrink-0">
                  <div aria-hidden="true" class="_background_fixture _fade_fixture pointer-events-none absolute inset-x-0 z-0 from-surface" data-native-fade data-fixture-id="module-fade-1013"></div>
                </div>
              </div>
              <div data-thread-scroll-footer="true" class="pointer-events-none absolute inset-x-0 bottom-0 z-20">
                <div aria-hidden="true" class="_background_fixture _surface_fixture pointer-events-none absolute inset-x-0 z-0 bottom-0 bg-surface" data-fixture-id="module-surface-1013"></div>
                <div data-codex-composer-root data-fixture-id="module-composer-1013">
                  <div aria-hidden="true" class="_background_fixture _surface_fixture pointer-events-none absolute inset-x-0 z-0 bottom-0 bg-surface" data-fixture-id="module-nested-surface-1013"></div>
                  <div class="composer-disclaimer-above relative">
                    <div aria-hidden="true" class="_background_fixture _surface_fixture pointer-events-none absolute inset-x-0 z-0 bottom-0 bg-surface" data-fixture-id="module-notice-1013"></div>
                  </div>
                </div>
                <div aria-hidden="true" class="other_background_fixture _surface_fixture pointer-events-none absolute inset-x-0 z-0 bottom-0 bg-surface" data-fixture-id="module-wrong-prefix-1013"></div>
              </div>
            </div>
            <div class="thread-scroll-container"><div class="[container-name:thread-content]">
              <div aria-hidden="true" class="pointer-events-none sticky bottom-0 z-10 mt-auto w-full shrink-0">
                <div aria-hidden="true" class="_background_fixture _fade_fixture pointer-events-none absolute inset-x-0 z-0 from-surface" data-native-fade data-fixture-id="module-unreviewed-fade-1013"></div>
              </div>
            </div></div>
            <div data-thread-scroll-footer="true">
              <div aria-hidden="true" class="_background_fixture _surface_fixture pointer-events-none absolute inset-x-0 z-0 bottom-0 bg-surface" data-fixture-id="module-outside-thread-1013"></div>
            </div>
          </main>
        </div></body></html>
        """;

    private const string Codex261013DotFixture =
        $$"""
        <html class="electron-dark"><head>{{NativeStyles}}<style>
          .composer-wrap:not([data-has-composer-surface]) { border-bottom: 12px solid rgb(41 42 43); }
        </style></head><body><div id="root">
          <main data-app-shell-main-surface="default">
            <div class="messaging-root messaging-embedded" data-fixture-id="dot-root-1013">
              <div class="workspace"><main class="thread-pane _enabled_fixture" data-fixture-id="dot-thread-1013">
                <div class="messaging-root messaging-embedded _enabled_fixture" data-fixture-id="dot-room-1013">
                  <div class="thread-pane flex-1" data-fixture-id="dot-room-pane-1013">
                    <div class="conversation-viewport @container relative flex min-h-0 min-w-0 flex-1">
                      <div class="message-row"><div class="message-bubble" data-fixture-id="dot-message-1013"></div></div>
                      <pre class="message-code-block" data-fixture-id="dot-code-1013"></pre>
                    </div>
                    <div><div class="composer-wrap" id="dot-composer-wrap">
                      <textarea data-fixture-id="dot-composer-1013"></textarea>
                      <p class="composer-disclaimer" data-fixture-id="dot-disclaimer-1013">Notice</p>
                    </div></div>
                  </div>
                </div>
              </main></div>
            </div>
            <div class="messaging-root" data-floating data-fixture-id="dot-floating-1013"></div>
            <div class="messaging-root messaging-attachments" data-fixture-id="dot-attachment-1013"></div>
          </main>
          <div class="messaging-root" data-fixture-id="dot-outside-main-1013"></div>
        </div></body></html>
        """;
}
