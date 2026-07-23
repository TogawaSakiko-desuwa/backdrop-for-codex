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
                      document.body.appendChild(main);
                    }, 4000);
                  });
                </script>
              </head>
              <body><div id="root"></div></body>
            </html>
            """);
        await WriteTestPngAsync(mediaPath);

        Process? edge = null;
        await using var session = new PuppeteerWallpaperSession();
        try
        {
            edge = Process.Start(CreateEdgeStartInfo(edgePath, port, testDirectory, pagePath));
            Assert.NotNull(edge);

            var endpoint = await WaitForEndpointAsync(port, pagePath, TimeSpan.FromSeconds(8));
            var options = new WallpaperInjectionOptions(
                generation: 1,
                source: new Uri("http://127.0.0.1:9/wallpaper.png"),
                localMediaPath: mediaPath,
                expectedContentLength: new FileInfo(mediaPath).Length,
                WallpaperMediaKind.Image);

            await session.ApplyAsync(endpoint, options);

            Assert.True(session.IsActive);
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
            await using var mediaServer = new LoopbackMediaServer();
            var mediaEndpoint = await mediaServer.StartAsync(mediaPath);
            await using var session = new PuppeteerWallpaperSession();
            var glass = new GlassEffectOptions(
                opacity: 0.78,
                blurPixels: 18,
                saturation: 1.2);
            var options = new WallpaperInjectionOptions(
                generation: 1,
                source: mediaEndpoint.Uri,
                localMediaPath: mediaPath,
                expectedContentLength: mediaEndpoint.ContentLength,
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

            await using var replacementMediaServer = new LoopbackMediaServer();
            var replacementEndpoint = await replacementMediaServer.StartAsync(replacementMediaPath);
            var replacementOptions = new WallpaperInjectionOptions(
                generation: 2,
                source: replacementEndpoint.Uri,
                localMediaPath: replacementMediaPath,
                expectedContentLength: replacementEndpoint.ContentLength,
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
                    PuppeteerWallpaperSession.IsSameReviewedDocument(
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
        TimeSpan timeout)
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
                        [new ClassifiedCdpTarget(target, CdpTargetClassification.CodexPage)]);
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
