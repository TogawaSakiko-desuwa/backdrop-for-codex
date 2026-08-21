using System.Text.Json;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Tests.Infrastructure;
using PuppeteerSharp;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

[Collection("Reviewed selector browser contracts")]
public sealed class PuppeteerDynamicWallpaperPageSessionBrowserContractTests
{
    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task HiddenPageKeepsPlayingUntilHostPauseOrReducedMotionRequestsPause()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await InstallFixtureAsync(page);
            Assert.True(await page.EvaluateExpressionAsync<bool>(
                "globalThis.__backdropPlaybackFixture.setDocumentHidden(true)"));

            const long generation = 904;
            var descriptor = new EncodedWallpaperStreamDescriptor(
                generation,
                "video/mp4; codecs=\"avc1.640028\"",
                width: 1280,
                height: 720,
                frameRate: 15);
            await using var sink = new PuppeteerEncodedWallpaperPageSink(page);

            var prepared = await sink.PrepareAsync(
                new DynamicWallpaperInjectionOptions(generation),
                descriptor);
            Assert.True(prepared.Prepared);
            _ = await sink.AppendAsync(new EncodedWallpaperSegment(
                generation,
                sequence: 0,
                EncodedWallpaperSegmentKind.Initialization,
                isKeyFrame: false,
                new byte[] { 1 }));
            var published = await sink.AppendAsync(new EncodedWallpaperSegment(
                generation,
                sequence: 1,
                EncodedWallpaperSegmentKind.Media,
                isKeyFrame: true,
                new byte[] { 2 }));
            Assert.True(published.Published);

            await AssertPlaybackCallsAsync(page, expectedPlayCalls: 1, expectedPauseCalls: 0);

            Assert.True(await sink.SetPausedAsync(generation, paused: true));
            await AssertPlaybackCallsAsync(page, expectedPlayCalls: 1, expectedPauseCalls: 1);

            Assert.True(await sink.SetPausedAsync(generation, paused: false));
            await AssertPlaybackCallsAsync(page, expectedPlayCalls: 2, expectedPauseCalls: 1);

            Assert.True(await page.EvaluateExpressionAsync<bool>(
                "globalThis.__backdropPlaybackFixture.setReducedMotion(true)"));
            await AssertPlaybackCallsAsync(page, expectedPlayCalls: 2, expectedPauseCalls: 2);

            Assert.False(await page.EvaluateExpressionAsync<bool>(
                "globalThis.__backdropPlaybackFixture.setReducedMotion(false)"));
            await AssertPlaybackCallsAsync(page, expectedPlayCalls: 3, expectedPauseCalls: 2);

            await page.EvaluateExpressionAsync(
                "document.dispatchEvent(new Event('visibilitychange'))");
            await AssertPlaybackCallsAsync(page, expectedPlayCalls: 3, expectedPauseCalls: 2);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task ProductionSessionIgnoresDecoyFailsOnAmbiguityAndOnlyDisconnects()
    {
        await EdgeBrowserContractHarness.WithPageAndWebSocketEndpointAsync(async (
            page,
            browserWebSocketUri) =>
        {
            await InstallFixtureAsync(page);
            var decoy = await page.Browser.NewPageAsync();
            try
            {
                await InstallFixtureAsync(decoy);
                var primaryTarget = await DescribeTargetAsync(page);
                var decoyTarget = await DescribeTargetAsync(decoy);
                var uniqueEndpoint = Endpoint(
                    page.Browser,
                    browserWebSocketUri,
                    (primaryTarget, CdpTargetClassification.CodexPage),
                    (decoyTarget, CdpTargetClassification.OtherPage));
                var factory = new PuppeteerDynamicWallpaperPageSessionFactory();
                var readinessSource = Assert.IsAssignableFrom<
                    IDynamicWallpaperInitialPresentationReadinessSource>(factory);
                var olderReadiness = await readinessSource
                    .WaitForInitialPresentationAsync(uniqueEndpoint)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(primaryTarget.Id, olderReadiness.TargetIdentity);

                await using var olderLease = (await factory.StartAsync(
                        uniqueEndpoint,
                        BufferWithStartup(generation: 901),
                        PreparedOptions(generation: 901, olderReadiness))
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10))).Lease;

                Assert.True(await page.EvaluateExpressionAsync<bool>(
                    $"Boolean(document.getElementById({JsonSerializer.Serialize(InjectionScriptBuilder.RootElementId)}))"));
                var newerReadiness = await readinessSource
                    .WaitForInitialPresentationAsync(uniqueEndpoint)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(primaryTarget.Id, newerReadiness.TargetIdentity);
                await using var newerLease = (await factory.StartAsync(
                        uniqueEndpoint,
                        BufferWithStartup(generation: 902),
                        PreparedOptions(generation: 902, newerReadiness))
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10))).Lease;
                Assert.Equal(902, await ReadActiveGenerationAsync(page));

                await olderLease.DisposeAsync();
                Assert.Equal(902, await ReadActiveGenerationAsync(page));
                await newerLease.DisposeAsync();
                Assert.False(await page.EvaluateExpressionAsync<bool>(
                    $"Boolean(document.getElementById({JsonSerializer.Serialize(InjectionScriptBuilder.RootElementId)}))"));

                var ambiguousEndpoint = Endpoint(
                    page.Browser,
                    browserWebSocketUri,
                    (primaryTarget, CdpTargetClassification.CodexPage),
                    (decoyTarget, CdpTargetClassification.CodexPage));
                await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
                    factory.StartAsync(
                        ambiguousEndpoint,
                        BufferWithStartup(generation: 903),
                        new DynamicWallpaperInjectionOptions(generation: 903))
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10)));

                Assert.True(page.Browser.IsConnected);
                Assert.False(page.IsClosed);
                Assert.Equal(42, await page.EvaluateExpressionAsync<int>("6 * 7"));
            }
            finally
            {
                await decoy.CloseAsync();
            }
        });
    }

    private static Task<long?> ReadActiveGenerationAsync(IPage page) =>
        page.EvaluateExpressionAsync<long?>(
            $"globalThis[{JsonSerializer.Serialize(InjectionScriptBuilder.StateProperty)}]?.generation ?? null");

    private static Task<int[]> ReadPlaybackCallsAsync(IPage page) =>
        page.EvaluateExpressionAsync<int[]>(
            "[globalThis.__backdropPlaybackFixture.playCalls, " +
            "globalThis.__backdropPlaybackFixture.pauseCalls]");

    private static async Task AssertPlaybackCallsAsync(
        IPage page,
        int expectedPlayCalls,
        int expectedPauseCalls)
    {
        var calls = await ReadPlaybackCallsAsync(page);
        Assert.Equal(expectedPlayCalls, calls[0]);
        Assert.Equal(expectedPauseCalls, calls[1]);
    }

    private static EncodedWallpaperStreamBuffer BufferWithStartup(long generation)
    {
        var buffer = new EncodedWallpaperStreamBuffer(new EncodedWallpaperStreamDescriptor(
            generation,
            "video/mp4; codecs=\"avc1.640028\"",
            width: 1920,
            height: 1080,
            frameRate: 30));
        buffer.TryWrite(new EncodedWallpaperSegment(
            generation,
            sequence: 0,
            EncodedWallpaperSegmentKind.Initialization,
            isKeyFrame: false,
            new byte[] { 1 }));
        buffer.TryWrite(new EncodedWallpaperSegment(
            generation,
            sequence: 1,
            EncodedWallpaperSegmentKind.Media,
            isKeyFrame: true,
            new byte[] { 2 }));
        return buffer;
    }

    private static DynamicWallpaperInjectionOptions PreparedOptions(
        long generation,
        DynamicWallpaperInitialPresentationReadiness readiness) =>
        new(
            generation,
            WallpaperObjectFit.Cover,
            mediaOpacity: 1,
            glass: null,
            composition: null,
            readiness.Presentation,
            readiness.Capabilities,
            readiness.CapabilityState,
            requireCurrentGlobalBaseline: true,
            readiness.TargetIdentity);

    private static async Task<CdpTargetDescriptor> DescribeTargetAsync(IPage page)
    {
        var session = await page.CreateCDPSessionAsync();
        try
        {
            var response = await session.SendAsync<JsonElement>("Target.getTargetInfo");
            var targetId = response.GetProperty("targetInfo").GetProperty("targetId")
                .GetString();
            Assert.False(string.IsNullOrWhiteSpace(targetId));
            return new CdpTargetDescriptor(
                targetId!,
                "page",
                "Codex",
                page.Url,
                $"ws://127.0.0.1/devtools/page/{targetId}");
        }
        finally
        {
            await session.DetachAsync();
        }
    }

    private static VerifiedCdpEndpoint Endpoint(
        IBrowser browser,
        Uri browserWebSocketUri,
        params (CdpTargetDescriptor Target, CdpTargetClassification Classification)[] targets)
    {
        var identity = Codex.CodexSecurityValidatorTests.GetIdentity();
        var baseUriBuilder = new UriBuilder(browserWebSocketUri)
        {
            Scheme = Uri.UriSchemeHttp,
            Path = "/",
            Query = string.Empty,
        };
        return new VerifiedCdpEndpoint(
            new CdpEndpointCandidate(
                browser.Process?.Id ?? 1234,
                "ChatGPT.exe",
                identity.PackageFamilyName,
                identity.PackageFullName,
                DateTimeOffset.UtcNow,
                WindowsCodexProcessSnapshotSource.CurrentSessionId,
                baseUriBuilder.Uri),
            new CdpBrowserVersion(
                "Chrome/140.0.0.0",
                "1.3",
                null,
                null,
                browserWebSocketUri.AbsoluteUri),
            browserWebSocketUri,
            targets.Select(item => new ClassifiedCdpTarget(
                item.Target,
                item.Classification)).ToArray(),
            identity);
    }

    private static Task InstallFixtureAsync(IPage page) =>
        page.SetContentAsync(
            """
            <!doctype html>
            <html>
              <head><title>Codex</title></head>
              <body>
                <div id="root">
                  <main data-app-shell-main-surface="default">
                    <header data-app-shell-application-menu-bar
                            data-app-shell-header-edge-scroll></header>
                    <div data-app-shell-main-content-layout
                         data-app-shell-right-panel-full-width></div>
                  </main>
                </div>
                <script>
                  const playbackFixture = {
                    playCalls: 0,
                    pauseCalls: 0,
                    documentHidden: false,
                    reducedMotion: false,
                    motionListeners: new Set(),
                    setDocumentHidden(value) {
                      this.documentHidden = Boolean(value);
                      return this.documentHidden;
                    },
                    setReducedMotion(value) {
                      this.reducedMotion = Boolean(value);
                      for (const listener of this.motionListeners) {
                        listener(new Event("change"));
                      }
                      return this.reducedMotion;
                    }
                  };
                  globalThis.__backdropPlaybackFixture = playbackFixture;
                  Object.defineProperty(document, "hidden", {
                    configurable: true,
                    get: () => playbackFixture.documentHidden
                  });
                  globalThis.matchMedia = () => ({
                    get matches() { return playbackFixture.reducedMotion; },
                    addEventListener(type, listener) {
                      if (type === "change") playbackFixture.motionListeners.add(listener);
                    },
                    removeEventListener(type, listener) {
                      if (type === "change") playbackFixture.motionListeners.delete(listener);
                    }
                  });
                  class TestSourceBuffer extends EventTarget {
                    constructor() { super(); this.updating = false; }
                    appendBuffer() {
                      this.updating = true;
                      queueMicrotask(() => {
                        this.updating = false;
                        this.dispatchEvent(new Event("updateend"));
                      });
                    }
                    abort() { this.updating = false; }
                  }
                  class TestMediaSource extends EventTarget {
                    static isTypeSupported(value) { return value.startsWith("video/mp4"); }
                    constructor() {
                      super();
                      this.readyState = "closed";
                      queueMicrotask(() => {
                        this.readyState = "open";
                        this.dispatchEvent(new Event("sourceopen"));
                      });
                    }
                    addSourceBuffer() { return new TestSourceBuffer(); }
                    endOfStream() { this.readyState = "ended"; }
                  }
                  globalThis.MediaSource = TestMediaSource;
                  URL.createObjectURL = () => "blob:verified-page-session";
                  URL.revokeObjectURL = () => {};
                  HTMLMediaElement.prototype.play = () => {
                    playbackFixture.playCalls++;
                    return Promise.resolve();
                  };
                  HTMLMediaElement.prototype.pause = () => {
                    playbackFixture.pauseCalls++;
                  };
                  HTMLMediaElement.prototype.load = () => {};
                </script>
              </body>
            </html>
            """);
}
