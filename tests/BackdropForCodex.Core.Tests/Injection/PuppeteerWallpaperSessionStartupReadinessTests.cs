using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Tests.Infrastructure;
using PuppeteerSharp;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed class PuppeteerWallpaperSessionStartupReadinessTests
{
    private const string OptInVariable = "BACKDROP_FOR_CODEX_RUN_STARTUP_RACE_TESTS";

    [IntegrationFact(OptInVariable)]
    [Trait("Category", "Integration")]
    public async Task ApplyAsync_WaitsForDelayedMainAndRetriesTransientPreparation_WhenOptedIn()
    {
        var edgePath = FindEdge();
        var port = ReserveLoopbackPort();
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "BackdropForCodex.StartupReadiness",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var pagePath = Path.Combine(testDirectory, "index.html");
        var mediaPath = Path.Combine(testDirectory, "wallpaper.png");
        await File.WriteAllTextAsync(
            pagePath,
            """
            <!doctype html>
            <html>
              <head>
                <meta charset="utf-8">
                <title>Codex</title>
                <script>
                  addEventListener("DOMContentLoaded", () => {
                    const rejectWallpaperInputsUntil = performance.now() + 5500;
                    new MutationObserver(() => {
                      if (performance.now() >= rejectWallpaperInputsUntil) return;
                      document.querySelectorAll(
                        'input[type="file"][data-codex-wallpaper-owner]'
                      ).forEach(node => node.remove());
                    }).observe(document.documentElement, { childList: true, subtree: true });
                    setTimeout(() => {
                      const main = document.createElement("main");
                      main.textContent = "ready";
                      document.querySelector("#root").appendChild(main);
                    }, 4000);
                  });
                </script>
              </head>
              <body>
                <div id="root">
                  <header class="app-header-tint"></header>
                  <div data-home-ambient-suggestions></div>
                </div>
              </body>
            </html>
            """);
        await WriteTestPngAsync(mediaPath);

        Process? edge = null;
        await using var session = new PuppeteerWallpaperSession();
        try
        {
            edge = Process.Start(CreateEdgeStartInfo(edgePath, port, testDirectory, pagePath));
            Assert.NotNull(edge);

            var endpoint = await WaitForEndpointAsync(
                port,
                pagePath,
                TimeSpan.FromSeconds(8),
                new Version(26, 721, 4000, 0));
            var options = new WallpaperInjectionOptions(
                generation: 1,
                source: new Uri("http://127.0.0.1:9/wallpaper.png"),
                localMediaPath: mediaPath,
                expectedContentLength: new FileInfo(mediaPath).Length,
                WallpaperMediaKind.Image);

            await session.ApplyAsync(endpoint, options);

            Assert.True(session.IsActive);
            Assert.Equal(
                CompatibilityProbePackageKind.ReviewedBand,
                endpoint.Profile.ProbePackageKind);
            Assert.True(session.Capabilities.Glass.IsAvailable);
            Assert.True(session.Capabilities.Advanced.IsAvailable);

            var conversation = await AddConversationAndReadRenderingAsync(endpoint);

            Assert.NotEqual("rgba(0, 0, 0, 0)", conversation.AssistantBackground);
            Assert.NotEqual("rgba(0, 0, 0, 0)", conversation.UserBackground);
            Assert.NotEqual("rgba(0, 0, 0, 0)", conversation.ActivityBackground);
            Assert.NotEqual("0px", conversation.AssistantBorderWidth);
            Assert.NotEqual("0px", conversation.UserBorderWidth);
            Assert.NotEqual("none", conversation.AssistantBackdropFilter);

            var shellSurfaces = await AddShellSurfacesAndReadRenderingAsync(endpoint);
            var launcher = shellSurfaces.EmptyLauncher;
            Assert.Equal(1, launcher.Generation);
            Assert.Equal(1, launcher.OwnedStyleCount);
            Assert.Equal(1, launcher.EmptyStateMatchCount);
            Assert.NotEqual("rgba(0, 0, 0, 0)", launcher.ShellBackground);
            Assert.NotEqual("rgb(24, 24, 24)", launcher.ShellBackground);
            Assert.Contains("blur(", launcher.ShellBackdropFilter, StringComparison.Ordinal);
            Assert.Equal("rgb(24, 24, 24)", launcher.PrimarySiblingBackground);
            Assert.Equal("none", launcher.PrimarySiblingBackdropFilter);
            Assert.Equal("rgba(0, 0, 0, 0)", launcher.TabsBackground);
            Assert.Equal("rgba(0, 0, 0, 0)", launcher.ToolbarBackground);
            Assert.Equal("rgba(0, 0, 0, 0)", launcher.ZeroSizeStickyBackground);
            Assert.Equal("rgba(0, 0, 0, 0)", launcher.ScrollBackground);
            Assert.Equal("rgba(0, 0, 0, 0)", launcher.CenterStickyBackground);
            Assert.All(
                launcher.ChromeBackdropFilters,
                backdropFilter => Assert.Equal("none", backdropFilter));
            Assert.Equal(5, launcher.ActionCardBackgrounds.Length);
            Assert.All(
                launcher.ActionCardBackgrounds,
                background => Assert.Equal("rgb(41, 42, 43)", background));

            var populatedPanel = shellSurfaces.PopulatedPanel;
            Assert.Equal(1, populatedPanel.Generation);
            Assert.Equal(1, populatedPanel.OwnedStyleCount);
            Assert.Equal(0, populatedPanel.EmptyStateMatchCount);
            Assert.Equal(1, populatedPanel.RightControllerCount);
            Assert.NotEqual("rgba(0, 0, 0, 0)", populatedPanel.ShellBackground);
            Assert.Contains("blur(", populatedPanel.ShellBackdropFilter, StringComparison.Ordinal);
            Assert.Equal("rgba(0, 0, 0, 0)", populatedPanel.TabsBackground);
            Assert.Equal("rgba(0, 0, 0, 0)", populatedPanel.ToolbarBackground);
            Assert.Equal("rgba(0, 0, 0, 0)", populatedPanel.FileLayoutBackground);
            Assert.Equal("rgb(24, 24, 24)", populatedPanel.EditorBackground);
            Assert.Equal("auto", populatedPanel.CloseButtonPointerEvents);

            var headers = shellSurfaces.Headers;
            Assert.NotEqual("rgba(0, 0, 0, 0)", headers.GlobalBackground);
            Assert.Contains("blur(", headers.GlobalBackdropFilter, StringComparison.Ordinal);
            Assert.Equal("rgba(0, 0, 0, 0)", headers.EdgeBackground);
            Assert.Equal("none", headers.EdgeBackdropFilter);
            Assert.Equal("rgba(0, 0, 0, 0)", headers.ContextBackground);
            Assert.Equal("none", headers.ContextBackdropFilter);
            Assert.Equal("rgba(0, 0, 0, 0)", headers.ContextBorderColor);
            Assert.Equal("rgb(52, 53, 54)", headers.RightSlotBackground);
            Assert.Equal("rgb(71, 72, 73)", headers.CloseButtonBackground);
            Assert.Equal("auto", headers.CloseButtonPointerEvents);
            Assert.True(shellSurfaces.StyleElementPreserved);
        }
        finally
        {
            await session.StopAsync();
            if (edge is { HasExited: false })
            {
                edge.Kill(entireProcessTree: true);
                await edge.WaitForExitAsync();
            }

            edge?.Dispose();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [IntegrationFact(OptInVariable)]
    [Trait("Category", "Integration")]
    public async Task ApplyAsync_ReportsSuccessOnlyAfterCspRestrictedLoopbackImageLoads_WhenOptedIn()
    {
        var edgePath = FindEdge();
        var port = ReserveLoopbackPort();
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            "BackdropForCodex.StartupReadiness",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var pagePath = Path.Combine(testDirectory, "index.html");
        var mediaPath = Path.Combine(testDirectory, "wallpaper.png");
        var replacementMediaPath = Path.Combine(testDirectory, "wallpaper-replacement.png");
        await File.WriteAllTextAsync(
            pagePath,
            """
            <!doctype html>
            <html>
              <head>
                <meta charset="utf-8">
                <meta http-equiv="Content-Security-Policy"
                      content="default-src 'none'; img-src 'self' app: blob: data: https:; media-src 'self' app: blob: data:; style-src 'self' 'unsafe-inline'">
                <title>Codex</title>
                <style>
                  button {
                    transition: none !important;
                  }
                  #home-unrelated-button {
                    background-color: rgb(7 61 109);
                    -webkit-backdrop-filter: none;
                    backdrop-filter: none;
                  }
                  #home-list-button {
                    background-color: rgb(139 83 17);
                    -webkit-backdrop-filter: none;
                    backdrop-filter: none;
                  }
                </style>
              </head>
              <body>
                <div id="root">
                  <aside><nav>sidebar</nav></aside>
                  <main role="main"
                        style="--color-token-main-surface-primary: rgb(24 24 24)">
                    <div data-response-annotation-conversation="conversation"
                         data-response-annotation-target="message">assistant</div>
                    <div data-user-message-bubble="true">user</div>
                    <div data-local-conversation-item-target-ids="activity">activity</div>
                    <div data-home-ambient-suggestions></div>
                    <section class="group/home-suggestions">
                      <span id="home-card-focus-sentinel" tabindex="0">focus sentinel</span>
                      <button id="home-card"
                              type="button"
                              aria-labelledby="home-card-label">
                        <span id="home-card-label">target</span>
                      </button>
                      <button id="home-disabled-card"
                              type="button"
                              aria-labelledby="home-disabled-card-label"
                              disabled>
                        <span id="home-disabled-card-label">disabled</span>
                      </button>
                      <div data-expanded-home-suggestion-list>
                        <button id="home-list-button" type="button">list item</button>
                      </div>
                    </section>
                    <button id="home-unrelated-button"
                            type="button"
                            aria-labelledby="home-unrelated-button-label">
                      <span id="home-unrelated-button-label">unrelated</span>
                    </button>
                  </main>
                </div>
              </body>
            </html>
            """);
        await WriteTestPngAsync(mediaPath);
        await WriteTestPngAsync(replacementMediaPath);

        Process? edge = null;
        try
        {
            edge = Process.Start(CreateEdgeStartInfo(edgePath, port, testDirectory, pagePath));
            Assert.NotNull(edge);

            var endpoint = await WaitForEndpointAsync(port, pagePath, TimeSpan.FromSeconds(8));
            var sourceProvider = new LocalFileWallpaperSourceProvider();
            await using var mediaLease = await sourceProvider.AcquireLeaseAsync(
                CreateLocalMediaReference(mediaPath));
            await using var session = new PuppeteerWallpaperSession();
            var glass = new GlassEffectOptions(
                opacity: 0.78,
                blurPixels: 18,
                saturation: 1.2);
            var options = new WallpaperInjectionOptions(
                generation: 1,
                source: CreateFileUri(mediaLease.ResolvedPath),
                localMediaPath: mediaLease.ResolvedPath,
                expectedContentLength: mediaLease.Metadata.ContentLength,
                WallpaperMediaKind.Image,
                glass: glass);

            await session.ApplyAsync(endpoint, options);

            var rendered = await ReadOwnedImageFromIndependentConnectionAsync(
                endpoint,
                TimeSpan.FromSeconds(5),
                inspectHomeSuggestions: true);
            Assert.True(
                rendered.NaturalWidth > 0,
                "ApplyAsync returned successfully even though the owned image did not load.");
            Assert.StartsWith("blob:", rendered.MediaSource, StringComparison.Ordinal);
            Assert.Equal("rgba(0, 0, 0, 0)", rendered.AppBackground);
            Assert.Equal("rgba(0, 0, 0, 0)", rendered.MainBackground);
            Assert.NotEqual("rgba(0, 0, 0, 0)", rendered.AsideBackground);
            Assert.Equal("rgba(0, 0, 0, 0)", rendered.NestedNavigationBackground);
            Assert.NotEqual("rgba(0, 0, 0, 0)", rendered.AssistantBubbleBackground);
            Assert.NotEqual("rgba(0, 0, 0, 0)", rendered.UserBubbleBackground);
            Assert.NotEqual("rgba(0, 0, 0, 0)", rendered.ActivityBackground);
            Assert.NotNull(rendered.HomeSuggestions);
            var homeSuggestions = rendered.HomeSuggestions!;
            AssertRgba([24, 24, 24, 199], homeSuggestions.DarkBase);
            AssertRgba([24, 24, 24, 219], homeSuggestions.DarkHover);
            AssertRgba([24, 24, 24, 219], homeSuggestions.DarkFocus);
            AssertRgba([24, 24, 24, 199], homeSuggestions.DisabledHover);
            AssertRgba([255, 255, 255, 199], homeSuggestions.LightBase);
            AssertRgba([255, 255, 255, 219], homeSuggestions.LightHover);
            AssertRgba([7, 61, 109, 255], homeSuggestions.Unrelated);
            AssertRgba([139, 83, 17, 255], homeSuggestions.List);
            Assert.True(homeSuggestions.FocusVisible);
            Assert.Contains(
                "blur(18px)",
                homeSuggestions.TargetBackdropFilter,
                StringComparison.Ordinal);
            Assert.Contains(
                "saturate(1.2)",
                homeSuggestions.TargetBackdropFilter,
                StringComparison.Ordinal);
            Assert.Contains(
                "blur(18px)",
                homeSuggestions.DisabledBackdropFilter,
                StringComparison.Ordinal);
            Assert.Contains(
                "saturate(1.2)",
                homeSuggestions.DisabledBackdropFilter,
                StringComparison.Ordinal);
            Assert.Equal("none", homeSuggestions.UnrelatedBackdropFilter);
            Assert.Equal("none", homeSuggestions.ListBackdropFilter);

            await using var replacementMediaLease = await sourceProvider.AcquireLeaseAsync(
                CreateLocalMediaReference(replacementMediaPath));
            var replacementOptions = new WallpaperInjectionOptions(
                generation: 2,
                source: CreateFileUri(replacementMediaLease.ResolvedPath),
                localMediaPath: replacementMediaLease.ResolvedPath,
                expectedContentLength: replacementMediaLease.Metadata.ContentLength,
                WallpaperMediaKind.Image,
                glass: glass);

            await session.ApplyAsync(endpoint, replacementOptions);

            var replacement = await ReadOwnedImageFromIndependentConnectionAsync(
                endpoint,
                TimeSpan.FromSeconds(5));
            Assert.Equal(2, replacement.Generation);
            Assert.StartsWith("blob:", replacement.MediaSource, StringComparison.Ordinal);
            Assert.NotEqual(rendered.MediaSource, replacement.MediaSource);
            Assert.NotEqual("rgba(0, 0, 0, 0)", replacement.AssistantBubbleBackground);
            Assert.NotEqual("rgba(0, 0, 0, 0)", replacement.UserBubbleBackground);
            Assert.NotEqual("rgba(0, 0, 0, 0)", replacement.ActivityBackground);
        }
        finally
        {
            if (edge is { HasExited: false })
            {
                edge.Kill(entireProcessTree: true);
                await edge.WaitForExitAsync();
            }

            edge?.Dispose();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static MediaReference CreateLocalMediaReference(string mediaPath) => new()
    {
        MediaId = Guid.CreateVersion7(),
        SourceKind = MediaSourceKind.LocalFile,
        SourceIdentifier = mediaPath,
        LastKnownKind = MediaKind.Image,
    };

    private static Uri CreateFileUri(string mediaPath) =>
        new UriBuilder(Uri.UriSchemeFile, string.Empty)
        {
            Path = mediaPath,
        }.Uri;

    private static string FindEdge()
    {
        var candidates = new[]
        {
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
        };

        return Assert.Single(candidates, File.Exists);
    }

    private static async Task<RenderedWallpaperState> ReadOwnedImageFromIndependentConnectionAsync(
        VerifiedCdpEndpoint endpoint,
        TimeSpan timeout,
        bool inspectHomeSuggestions = false)
    {
        var browser = await Puppeteer.ConnectAsync(new ConnectOptions
        {
            BrowserWSEndpoint = endpoint.BrowserWebSocketUri.AbsoluteUri,
            DefaultViewport = null,
            ProtocolTimeout = 5_000,
            AcceptInsecureCerts = false,
            NetworkEnabled = false,
        });
        try
        {
            var reviewedTarget = Assert.Single(endpoint.InjectableTargets);
            var pages = await browser.PagesAsync(includeAll: true);
            var page = Assert.Single(
                pages,
                candidate =>
                    !candidate.IsClosed &&
                    Uri.TryCreate(candidate.Url, UriKind.Absolute, out var candidateUri) &&
                    VerifiedCodexPageSelector.IsSameReviewedDocument(
                        candidateUri,
                        reviewedTarget.Url));
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < timeout)
            {
                var complete = await page.EvaluateExpressionAsync<bool>(
                    $"Boolean(document.querySelector('#{InjectionScriptBuilder.RootElementId} > img')?.complete)");
                if (complete)
                {
                    var rendered = await page.EvaluateExpressionAsync<RenderedWallpaperState>(
                        $$"""
                        (() => {
                          const background = selector => {
                            const element = document.querySelector(selector);
                            return element ? getComputedStyle(element).backgroundColor : null;
                          };
                          return {
                            naturalWidth: document.querySelector(
                              '#{{InjectionScriptBuilder.RootElementId}} > img')?.naturalWidth ?? 0,
                            generation: Number(document.querySelector(
                              '#{{InjectionScriptBuilder.RootElementId}}')?.dataset
                                .codexWallpaperGeneration ?? 0),
                            mediaSource: document.querySelector(
                              '#{{InjectionScriptBuilder.RootElementId}} > img')?.currentSrc ?? '',
                            appBackground: background('body > #root'),
                            mainBackground: background('main'),
                            asideBackground: background('aside'),
                            nestedNavigationBackground: background('aside nav'),
                            assistantBubbleBackground: background(
                              '[data-response-annotation-conversation][data-response-annotation-target]'),
                            userBubbleBackground: background('[data-user-message-bubble="true"]'),
                            activityBackground: background('[data-local-conversation-item-target-ids]')
                          };
                        })()
                        """);
                    if (!inspectHomeSuggestions)
                    {
                        return rendered;
                    }

                    var homeSuggestions = await ReadHomeSuggestionRenderingAsync(page);
                    return rendered with { HomeSuggestions = homeSuggestions };
                }

                await Task.Delay(50);
            }

            throw new TimeoutException("The owned image never reached a completed load state.");
        }
        finally
        {
            browser.Disconnect();
        }
    }

    private static async Task<ConversationRendering> AddConversationAndReadRenderingAsync(
        VerifiedCdpEndpoint endpoint)
    {
        var browser = await Puppeteer.ConnectAsync(new ConnectOptions
        {
            BrowserWSEndpoint = endpoint.BrowserWebSocketUri.AbsoluteUri,
            DefaultViewport = null,
            ProtocolTimeout = 5_000,
            AcceptInsecureCerts = false,
            NetworkEnabled = false,
        });
        try
        {
            var reviewedTarget = Assert.Single(endpoint.InjectableTargets);
            var pages = await browser.PagesAsync(includeAll: true);
            var page = Assert.Single(
                pages,
                candidate =>
                    !candidate.IsClosed &&
                    Uri.TryCreate(candidate.Url, UriKind.Absolute, out var candidateUri) &&
                    VerifiedCodexPageSelector.IsSameReviewedDocument(
                        candidateUri,
                        reviewedTarget.Url));

            return await page.EvaluateExpressionAsync<ConversationRendering>(
                """
                (() => {
                  const main = document.querySelector("main");
                  if (!main) throw new Error("Missing fixture main element.");

                  const assistant = document.createElement("div");
                  assistant.dataset.responseAnnotationConversation = "conversation";
                  assistant.dataset.responseAnnotationTarget = "message";
                  const user = document.createElement("div");
                  user.dataset.userMessageBubble = "true";
                  const activity = document.createElement("div");
                  activity.dataset.localConversationItemTargetIds = "activity";
                  main.append(assistant, user, activity);

                  const style = element => getComputedStyle(element);
                  return {
                    assistantBackground: style(assistant).backgroundColor,
                    userBackground: style(user).backgroundColor,
                    activityBackground: style(activity).backgroundColor,
                    assistantBorderWidth: style(assistant).borderTopWidth,
                    userBorderWidth: style(user).borderTopWidth,
                    assistantBackdropFilter: style(assistant).backdropFilter
                  };
                })()
                """);
        }
        finally
        {
            browser.Disconnect();
        }
    }

    private static async Task<ShellSurfaceTransitionRendering>
        AddShellSurfacesAndReadRenderingAsync(VerifiedCdpEndpoint endpoint)
    {
        var browser = await Puppeteer.ConnectAsync(new ConnectOptions
        {
            BrowserWSEndpoint = endpoint.BrowserWebSocketUri.AbsoluteUri,
            DefaultViewport = null,
            ProtocolTimeout = 5_000,
            AcceptInsecureCerts = false,
            NetworkEnabled = false,
        });
        try
        {
            var reviewedTarget = Assert.Single(endpoint.InjectableTargets);
            var pages = await browser.PagesAsync(includeAll: true);
            var page = Assert.Single(
                pages,
                candidate =>
                    !candidate.IsClosed &&
                    Uri.TryCreate(candidate.Url, UriKind.Absolute, out var candidateUri) &&
                    VerifiedCodexPageSelector.IsSameReviewedDocument(
                        candidateUri,
                        reviewedTarget.Url));

            return await page.EvaluateExpressionAsync<ShellSurfaceTransitionRendering>(
                $$"""
                (async () => {
                  const host = document.querySelector("#root");
                  if (!host) throw new Error("Missing fixture root element.");

                  const nativeStyles = document.createElement("style");
                  nativeStyles.textContent = `
                    .bg-token-main-surface-primary {
                      background-color: rgb(24 24 24);
                    }
                    .launcher-action-card {
                      background-color: rgb(41 42 43);
                    }
                    #edge-header,
                    #header-context {
                      background-color: rgb(24 24 24);
                      -webkit-backdrop-filter: blur(2px);
                      backdrop-filter: blur(2px);
                    }
                    #header-context {
                      border: 1px solid rgb(90 91 92);
                    }
                    #right-header-slot {
                      background-color: rgb(52 53 54);
                    }
                    #right-header-close-button,
                    #right-tab-close-button {
                      background-color: rgb(71 72 73);
                      pointer-events: auto;
                    }
                  `;
                  document.head.append(nativeStyles);

                  const globalHeader = document.querySelector("header.app-header-tint");
                  if (!globalHeader) throw new Error("Missing fixture global header.");
                  globalHeader.id = "global-header";

                  const edgeHeader = document.createElement("header");
                  edgeHeader.id = "edge-header";
                  edgeHeader.className = "app-header-tint";
                  edgeHeader.dataset.appShellHeaderEdgeScroll = "false";
                  const headerContext = document.createElement("div");
                  headerContext.id = "header-context";
                  headerContext.dataset.testid = "app-shell-header-context-menu-surface";
                  const headerMenuButton = document.createElement("button");
                  headerMenuButton.textContent = "Plan";
                  headerContext.append(headerMenuButton);
                  const rightHeaderSlot = document.createElement("div");
                  rightHeaderSlot.id = "right-header-slot";
                  const rightHeaderCloseButton = document.createElement("button");
                  rightHeaderCloseButton.id = "right-header-close-button";
                  rightHeaderCloseButton.textContent = "Close";
                  rightHeaderSlot.append(rightHeaderCloseButton);
                  edgeHeader.append(headerContext, rightHeaderSlot);

                  const rightAside = document.createElement("aside");
                  rightAside.dataset.appShellFocusArea = "right-panel";
                  const positioner = document.createElement("div");
                  const primarySibling = document.createElement("div");
                  primarySibling.id = "launcher-primary-sibling";
                  primarySibling.className = "bg-token-main-surface-primary";
                  const shell = document.createElement("div");
                  shell.id = "launcher-shell";
                  shell.className = "bg-token-main-surface-primary";
                  const shellLayout = document.createElement("div");
                  const tabs = document.createElement("div");
                  tabs.id = "launcher-tabs";
                  tabs.className = "bg-token-main-surface-primary";
                  tabs.dataset.appShellTabs = "true";
                  const toolbar = document.createElement("div");
                  toolbar.id = "launcher-toolbar";
                  toolbar.className = "bg-token-main-surface-primary";
                  const tabStrip = document.createElement("div");
                  tabStrip.dataset.appShellTabStripController = "right";
                  const closeButton = document.createElement("button");
                  closeButton.id = "right-tab-close-button";
                  closeButton.dataset.appShellTabCloseButton = "true";
                  closeButton.textContent = "Close tab";
                  tabStrip.append(closeButton);
                  const zeroSizeSticky = document.createElement("div");
                  zeroSizeSticky.id = "launcher-zero-size-sticky";
                  zeroSizeSticky.className = "bg-token-main-surface-primary";
                  toolbar.append(tabStrip, zeroSizeSticky);

                  const launcherContent = document.createElement("div");
                  launcherContent.id = "launcher-content";
                  const scrollContent = document.createElement("div");
                  scrollContent.id = "launcher-scroll-content";
                  scrollContent.className = "bg-token-main-surface-primary";
                  const centerLayout = document.createElement("div");
                  const centerSticky = document.createElement("div");
                  centerSticky.id = "launcher-center-sticky";
                  centerSticky.className = "bg-token-main-surface-primary";
                  for (const name of ["Review", "Terminal", "Browser", "Files", "Tasks"]) {
                    const card = document.createElement("button");
                    card.className = "launcher-action-card";
                    card.textContent = name;
                    centerSticky.append(card);
                  }
                  centerLayout.append(centerSticky);
                  scrollContent.append(centerLayout);
                  launcherContent.append(scrollContent);
                  tabs.append(toolbar, launcherContent);
                  shellLayout.append(tabs);
                  shell.append(shellLayout);
                  positioner.append(primarySibling, shell);
                  rightAside.append(positioner);
                  host.append(edgeHeader, rightAside);

                  const style = element => getComputedStyle(element);
                  const background = element => style(element).backgroundColor;
                  const filter = element => style(element).backdropFilter;
                  const generation = () => Number(document.querySelector(
                    "#{{InjectionScriptBuilder.RootElementId}}"
                  )?.dataset.codexWallpaperGeneration ?? 0);
                  const ownedStyleCount = () => document.querySelectorAll(
                    "#{{InjectionScriptBuilder.StyleElementId}}"
                  ).length;
                  const emptyStateMatchCount = () => document.querySelectorAll(
                    'aside[data-app-shell-focus-area="right-panel"] ' +
                    '[data-app-shell-tabs="true"]' +
                    ':not(:has([data-app-shell-tab-panel-controller]))'
                  ).length;
                  const ownedStyle = document.querySelector(
                    "#{{InjectionScriptBuilder.StyleElementId}}"
                  );

                  await new Promise(resolve => requestAnimationFrame(resolve));
                  const emptyLauncher = {
                    generation: generation(),
                    ownedStyleCount: ownedStyleCount(),
                    emptyStateMatchCount: emptyStateMatchCount(),
                    shellBackground: background(shell),
                    shellBackdropFilter: filter(shell),
                    primarySiblingBackground: background(primarySibling),
                    primarySiblingBackdropFilter: filter(primarySibling),
                    tabsBackground: background(tabs),
                    toolbarBackground: background(toolbar),
                    zeroSizeStickyBackground: background(zeroSizeSticky),
                    scrollBackground: background(scrollContent),
                    centerStickyBackground: background(centerSticky),
                    chromeBackdropFilters: [
                      tabs,
                      toolbar,
                      zeroSizeSticky,
                      scrollContent,
                      centerSticky
                    ].map(filter),
                    actionCardBackgrounds: Array.from(
                      centerSticky.querySelectorAll(".launcher-action-card"),
                      background)
                  };
                  const headers = {
                    globalBackground: background(globalHeader),
                    globalBackdropFilter: filter(globalHeader),
                    edgeBackground: background(edgeHeader),
                    edgeBackdropFilter: filter(edgeHeader),
                    contextBackground: background(headerContext),
                    contextBackdropFilter: filter(headerContext),
                    contextBorderColor: style(headerContext).borderTopColor,
                    rightSlotBackground: background(rightHeaderSlot),
                    closeButtonBackground: background(rightHeaderCloseButton),
                    closeButtonPointerEvents: style(rightHeaderCloseButton).pointerEvents
                  };

                  launcherContent.remove();
                  const tabpanel = document.createElement("div");
                  tabpanel.setAttribute("role", "tabpanel");
                  tabpanel.dataset.appShellTabPanelController = "right";
                  const fileLayout = document.createElement("div");
                  fileLayout.id = "right-file-layout";
                  fileLayout.className = "bg-token-main-surface-primary";
                  const editor = document.createElement("div");
                  editor.id = "right-editor";
                  editor.className = "monaco-editor bg-token-main-surface-primary";
                  fileLayout.append(editor);
                  tabpanel.append(fileLayout);
                  tabs.append(tabpanel);

                  await new Promise(resolve => requestAnimationFrame(
                    () => requestAnimationFrame(resolve)
                  ));
                  const populatedPanel = {
                    generation: generation(),
                    ownedStyleCount: ownedStyleCount(),
                    emptyStateMatchCount: emptyStateMatchCount(),
                    rightControllerCount: document.querySelectorAll(
                      '[data-app-shell-tab-panel-controller="right"]'
                    ).length,
                    shellBackground: background(shell),
                    shellBackdropFilter: filter(shell),
                    tabsBackground: background(tabs),
                    toolbarBackground: background(toolbar),
                    fileLayoutBackground: background(fileLayout),
                    editorBackground: background(editor),
                    closeButtonPointerEvents: style(closeButton).pointerEvents
                  };

                  return {
                    emptyLauncher,
                    populatedPanel,
                    headers,
                    styleElementPreserved:
                      ownedStyle !== null &&
                      document.querySelector(
                        "#{{InjectionScriptBuilder.StyleElementId}}"
                      ) === ownedStyle
                  };
                })()
                """);
        }
        finally
        {
            browser.Disconnect();
        }
    }

    private static async Task<HomeSuggestionRendering> ReadHomeSuggestionRenderingAsync(
        IPage page)
    {
        const string targetSelector = "#home-card";
        const string disabledSelector = "#home-disabled-card";
        const string unrelatedSelector = "#home-unrelated-button";
        const string listSelector = "#home-list-button";

        var darkBase = await ReadNormalizedBackgroundAsync(page, targetSelector);
        var unrelated = await ReadNormalizedBackgroundAsync(page, unrelatedSelector);
        var list = await ReadNormalizedBackgroundAsync(page, listSelector);
        var targetBackdropFilter = await ReadBackdropFilterAsync(page, targetSelector);
        var unrelatedBackdropFilter = await ReadBackdropFilterAsync(page, unrelatedSelector);
        var listBackdropFilter = await ReadBackdropFilterAsync(page, listSelector);

        await page.HoverAsync(targetSelector);
        var darkHover = await ReadNormalizedBackgroundAsync(page, targetSelector);

        await page.HoverAsync(disabledSelector);
        var disabledHover = await ReadNormalizedBackgroundAsync(page, disabledSelector);
        var disabledBackdropFilter = await ReadBackdropFilterAsync(page, disabledSelector);

        await page.HoverAsync(unrelatedSelector);
        await page.FocusAsync("#home-card-focus-sentinel");
        await page.Keyboard.PressAsync("Tab");
        var focusVisible = await page.EvaluateExpressionAsync<bool>(
            """
            document.activeElement?.id === "home-card" &&
              document.activeElement.matches(":focus-visible")
            """);
        var darkFocus = await ReadNormalizedBackgroundAsync(page, targetSelector);

        await page.HoverAsync(unrelatedSelector);
        await page.EvaluateExpressionAsync<bool>(
            """
            (() => {
              document.activeElement?.blur();
              document.querySelector('[role="main"]').style.setProperty(
                "--color-token-main-surface-primary",
                "rgb(255 255 255)");
              return true;
            })()
            """);
        var lightBase = await ReadNormalizedBackgroundAsync(page, targetSelector);

        await page.HoverAsync(targetSelector);
        var lightHover = await ReadNormalizedBackgroundAsync(page, targetSelector);

        return new HomeSuggestionRendering(
            darkBase,
            darkHover,
            darkFocus,
            lightBase,
            lightHover,
            disabledHover,
            unrelated,
            list,
            focusVisible,
            targetBackdropFilter,
            disabledBackdropFilter,
            unrelatedBackdropFilter,
            listBackdropFilter);
    }

    private static Task<int[]> ReadNormalizedBackgroundAsync(IPage page, string selector)
    {
        var serializedSelector = JsonSerializer.Serialize(selector);
        return page.EvaluateExpressionAsync<int[]>(
            $$"""
            (() => {
              const element = document.querySelector({{serializedSelector}});
              if (!element) {
                throw new Error(`Missing fixture element: ${{serializedSelector}}`);
              }
              const canvas = new OffscreenCanvas(1, 1);
              const context = canvas.getContext("2d", { willReadFrequently: true });
              if (!context) {
                throw new Error("OffscreenCanvas 2D context is unavailable.");
              }
              context.clearRect(0, 0, 1, 1);
              context.fillStyle = "rgba(0, 0, 0, 0)";
              context.fillStyle = getComputedStyle(element).backgroundColor;
              context.fillRect(0, 0, 1, 1);
              return Array.from(context.getImageData(0, 0, 1, 1).data);
            })()
            """);
    }

    private static Task<string> ReadBackdropFilterAsync(IPage page, string selector)
    {
        var serializedSelector = JsonSerializer.Serialize(selector);
        return page.EvaluateExpressionAsync<string>(
            $$"""
            (() => {
              const element = document.querySelector({{serializedSelector}});
              if (!element) {
                throw new Error(`Missing fixture element: ${{serializedSelector}}`);
              }
              return getComputedStyle(element).backdropFilter;
            })()
            """);
    }

    private static void AssertRgba(
        int[] expected,
        int[] actual,
        int tolerance = 1)
    {
        Assert.Equal(4, expected.Length);
        Assert.Equal(expected.Length, actual.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.InRange(
                actual[index],
                expected[index] - tolerance,
                expected[index] + tolerance);
        }
    }

    private sealed record RenderedWallpaperState(
        int NaturalWidth,
        long Generation,
        string MediaSource,
        string? AppBackground,
        string? MainBackground,
        string? AsideBackground,
        string? NestedNavigationBackground,
        string? AssistantBubbleBackground,
        string? UserBubbleBackground,
        string? ActivityBackground)
    {
        public HomeSuggestionRendering? HomeSuggestions { get; init; }
    }

    private sealed record HomeSuggestionRendering(
        int[] DarkBase,
        int[] DarkHover,
        int[] DarkFocus,
        int[] LightBase,
        int[] LightHover,
        int[] DisabledHover,
        int[] Unrelated,
        int[] List,
        bool FocusVisible,
        string TargetBackdropFilter,
        string DisabledBackdropFilter,
        string UnrelatedBackdropFilter,
        string ListBackdropFilter);

    private sealed record ConversationRendering(
        string AssistantBackground,
        string UserBackground,
        string ActivityBackground,
        string AssistantBorderWidth,
        string UserBorderWidth,
        string AssistantBackdropFilter);

    private sealed record ShellSurfaceTransitionRendering(
        EmptyLauncherRendering EmptyLauncher,
        PopulatedRightPanelRendering PopulatedPanel,
        HeaderSurfaceRendering Headers,
        bool StyleElementPreserved);

    private sealed record EmptyLauncherRendering(
        long Generation,
        int OwnedStyleCount,
        int EmptyStateMatchCount,
        string ShellBackground,
        string ShellBackdropFilter,
        string PrimarySiblingBackground,
        string PrimarySiblingBackdropFilter,
        string TabsBackground,
        string ToolbarBackground,
        string ZeroSizeStickyBackground,
        string ScrollBackground,
        string CenterStickyBackground,
        string[] ChromeBackdropFilters,
        string[] ActionCardBackgrounds);

    private sealed record PopulatedRightPanelRendering(
        long Generation,
        int OwnedStyleCount,
        int EmptyStateMatchCount,
        int RightControllerCount,
        string ShellBackground,
        string ShellBackdropFilter,
        string TabsBackground,
        string ToolbarBackground,
        string FileLayoutBackground,
        string EditorBackground,
        string CloseButtonPointerEvents);

    private sealed record HeaderSurfaceRendering(
        string GlobalBackground,
        string GlobalBackdropFilter,
        string EdgeBackground,
        string EdgeBackdropFilter,
        string ContextBackground,
        string ContextBackdropFilter,
        string ContextBorderColor,
        string RightSlotBackground,
        string CloseButtonBackground,
        string CloseButtonPointerEvents);

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Task WriteTestPngAsync(string mediaPath) => File.WriteAllBytesAsync(
        mediaPath,
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

    private static ProcessStartInfo CreateEdgeStartInfo(
        string edgePath,
        int port,
        string userDataDirectory,
        string pagePath)
    {
        var startInfo = new ProcessStartInfo(edgePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--headless=new");
        startInfo.ArgumentList.Add("--disable-gpu");
        startInfo.ArgumentList.Add("--disable-extensions");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add($"--remote-debugging-port={port}");
        startInfo.ArgumentList.Add($"--user-data-dir={userDataDirectory}");
        startInfo.ArgumentList.Add(new Uri(pagePath).AbsoluteUri);
        return startInfo;
    }

    private static async Task<VerifiedCdpEndpoint> WaitForEndpointAsync(
        int port,
        string pagePath,
        TimeSpan timeout,
        Version? packageVersion = null)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
        })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(1),
        };
        using var timeoutCancellation = new CancellationTokenSource(timeout);

        while (true)
        {
            timeoutCancellation.Token.ThrowIfCancellationRequested();
            try
            {
                var browser = await client.GetFromJsonAsync<CdpBrowserVersion>(
                    "json/version",
                    timeoutCancellation.Token);
                var targets = await client.GetFromJsonAsync<CdpTargetDescriptor[]>(
                    "json/list",
                    timeoutCancellation.Token);
                var expectedUrl = new Uri(pagePath);
                var target = targets?.SingleOrDefault(item =>
                    string.Equals(item.Title, "Codex", StringComparison.Ordinal) &&
                    Uri.TryCreate(item.Url, UriKind.Absolute, out var targetUri) &&
                    Uri.Compare(
                        targetUri,
                        expectedUrl,
                        UriComponents.SchemeAndServer | UriComponents.Path,
                        UriFormat.SafeUnescaped,
                        StringComparison.OrdinalIgnoreCase) == 0);
                if (browser is not null &&
                    target is not null &&
                    Uri.TryCreate(
                        browser.WebSocketDebuggerUrl,
                        UriKind.Absolute,
                        out var browserWebSocketUri))
                {
                    var candidate = new CdpEndpointCandidate(
                        ProcessId: 1,
                        ExecutableName: "msedge.exe",
                        PackageFamilyName: "test",
                        PackageFullName: "test",
                        StartTimeUtc: DateTimeOffset.UtcNow,
                        SessionId: 1,
                        BaseUri: client.BaseAddress);
                    return new VerifiedCdpEndpoint(
                        candidate,
                        browser,
                        browserWebSocketUri,
                        [new ClassifiedCdpTarget(target, CdpTargetClassification.CodexPage)],
                        BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile(
                            packageVersion ?? new Version(26, 721, 3996, 0)));
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!timeoutCancellation.IsCancellationRequested)
            {
            }

            await Task.Delay(50, timeoutCancellation.Token);
        }
    }
}
