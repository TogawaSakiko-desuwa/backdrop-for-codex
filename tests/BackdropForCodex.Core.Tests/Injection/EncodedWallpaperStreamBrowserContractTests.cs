using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Tests.Infrastructure;
using PuppeteerSharp;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

[Collection("Reviewed selector browser contracts")]
public sealed class EncodedWallpaperStreamBrowserContractTests
{
    [Fact]
    public void StreamScriptsSerializePayloadsWithoutPublishingCodeTokens()
    {
        var descriptor = Descriptor(generation: 701);
        var options = Options(generation: 701);
        var segment = Segment(
            generation: 701,
            sequence: 0,
            EncodedWallpaperSegmentKind.Initialization,
            isKeyFrame: false,
            new byte[] { 0, 1, 2, 255 });

        var prepare = InjectionScriptBuilder.BuildPrepareEncodedStream(options, descriptor);
        var append = InjectionScriptBuilder.BuildAppendEncodedStream(segment);

        Assert.Contains("MediaSource", prepare, StringComparison.Ordinal);
        Assert.Contains("appendBuffer", append, StringComparison.Ordinal);
        Assert.DoesNotContain("__BACKDROP_FOR_CODEX_", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("__BACKDROP_FOR_CODEX_", append, StringComparison.Ordinal);
        Assert.Contains("AAEC/w==", append, StringComparison.Ordinal);
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task StartupPublishesOnlyAfterAcknowledgedInitializationAndKeyFrame()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await InstallDeterministicMediaSourceAsync(page);
            var generation = 701L;

            var prepared = await page.EvaluateExpressionAsync<PrepareReceipt>(
                InjectionScriptBuilder.BuildPrepareEncodedStream(
                    Options(generation),
                    Descriptor(generation)));
            var beforeAppend = await ReadStateAsync(page);
            var initialization = await page.EvaluateExpressionAsync<AppendReceipt>(
                InjectionScriptBuilder.BuildAppendEncodedStream(
                    Segment(
                        generation,
                        sequence: 0,
                        EncodedWallpaperSegmentKind.Initialization,
                        isKeyFrame: false,
                        new byte[] { 1, 2, 3 })));
            var beforeKeyFrame = await ReadStateAsync(page);
            var keyFrame = await page.EvaluateExpressionAsync<AppendReceipt>(
                InjectionScriptBuilder.BuildAppendEncodedStream(
                    Segment(
                        generation,
                        sequence: 1,
                        EncodedWallpaperSegmentKind.Media,
                        isKeyFrame: true,
                        new byte[] { 4, 5, 6 })));
            var published = await ReadStateAsync(page);

            Assert.True(prepared.Prepared);
            Assert.False(beforeAppend.RootPresent);
            Assert.Equal(generation, beforeAppend.PendingGeneration);
            Assert.True(initialization.Appended);
            Assert.False(initialization.Published);
            Assert.False(beforeKeyFrame.RootPresent);
            Assert.True(keyFrame.Appended);
            Assert.True(keyFrame.Published);
            Assert.True(published.RootPresent);
            Assert.Equal(generation, published.ActiveGeneration);
            Assert.Null(published.PendingGeneration);
            Assert.Equal(2, published.AppendCount);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task ReplacementRejectsStaleSegmentsAndAppendFailureCleansExactGeneration()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await InstallDeterministicMediaSourceAsync(page);
            await PublishAsync(page, generation: 801);

            await page.EvaluateExpressionAsync<PrepareReceipt>(
                InjectionScriptBuilder.BuildPrepareEncodedStream(
                    Options(generation: 802),
                    Descriptor(generation: 802)));
            Assert.Equal(801, (await ReadStateAsync(page)).ActiveGeneration);
            await AppendStartupAsync(page, generation: 802);
            Assert.Equal(802, (await ReadStateAsync(page)).ActiveGeneration);

            var stale = await page.EvaluateExpressionAsync<AppendReceipt>(
                InjectionScriptBuilder.BuildAppendEncodedStream(
                    Segment(
                        generation: 801,
                        sequence: 2,
                        EncodedWallpaperSegmentKind.Media,
                        isKeyFrame: false,
                        new byte[] { 7 })));
            Assert.False(stale.Appended);
            Assert.Equal("generation-not-owned", stale.Reason);
            Assert.Equal(802, (await ReadStateAsync(page)).ActiveGeneration);

            var supersededCleanup = await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCleanupEncodedStream(generation: 801));
            Assert.True(supersededCleanup);
            Assert.Equal(802, (await ReadStateAsync(page)).ActiveGeneration);

            await AddOwnedDomResidualAsync(page, generation: 801);
            var residualCleanup = await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCleanupEncodedStream(generation: 801));
            Assert.False(residualCleanup);
            Assert.Equal(802, (await ReadStateAsync(page)).ActiveGeneration);
            await RemoveOwnedDomResidualAsync(page);

            await page.EvaluateExpressionAsync<bool>(
                "globalThis.__testMse.failNextAppend = true; true");
            var failure = await Record.ExceptionAsync(() =>
                page.EvaluateExpressionAsync<AppendReceipt>(
                    InjectionScriptBuilder.BuildAppendEncodedStream(
                        Segment(
                            generation: 802,
                            sequence: 2,
                            EncodedWallpaperSegmentKind.Media,
                            isKeyFrame: false,
                            new byte[] { 8 }))));
            var cleaned = await ReadStateAsync(page);

            Assert.NotNull(failure);
            Assert.False(cleaned.RootPresent);
            Assert.Null(cleaned.ActiveGeneration);
            Assert.Null(cleaned.PendingGeneration);
            Assert.True(cleaned.RevocationCount >= 2);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task ActiveStreamPrunesMediaSourceHistoryToABoundedPlaybackWindow()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await InstallDeterministicMediaSourceAsync(page);
            const long generation = 901;
            await PublishAsync(page, generation);
            await page.EvaluateExpressionAsync<bool>(
                $$"""
                (() => {
                  const state = globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionScriptBuilder.StateProperty)}}];
                  Object.defineProperty(state.media, "currentTime", {
                    configurable: true,
                    value: 40,
                    writable: true
                  });
                  globalThis.__testMse.ranges = [[0, 40]];
                  return true;
                })()
                """);

            var append = await page.EvaluateExpressionAsync<AppendReceipt>(
                InjectionScriptBuilder.BuildAppendEncodedStream(
                    Segment(
                        generation,
                        sequence: 2,
                        EncodedWallpaperSegmentKind.Media,
                        isKeyFrame: false,
                        new byte[] { 3 })));
            var state = await ReadStateAsync(page);

            Assert.True(append.Appended);
            Assert.Equal(1, state.RemoveCount);
            Assert.InRange(state.LastRemovedEnd, 27.9, 28.1);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task QuotaExceededRetriesOnceAfterRemovingOwnedPlaybackHistory()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await InstallDeterministicMediaSourceAsync(page);
            const long generation = 902;
            await PublishAsync(page, generation);
            await page.EvaluateExpressionAsync<bool>(
                $$"""
                (() => {
                  const state = globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionScriptBuilder.StateProperty)}}];
                  Object.defineProperty(state.media, "currentTime", {
                    configurable: true,
                    value: 0,
                    writable: true
                  });
                  globalThis.__testMse.ranges = [[0, 40]];
                  globalThis.__testMse.failNextAppendWithQuota = true;
                  globalThis.__testMse.advanceTimeOnQuota = () => {
                    state.media.currentTime = 40;
                  };
                  return true;
                })()
                """);

            var append = await page.EvaluateExpressionAsync<AppendReceipt>(
                InjectionScriptBuilder.BuildAppendEncodedStream(
                    Segment(
                        generation,
                        sequence: 2,
                        EncodedWallpaperSegmentKind.Media,
                        isKeyFrame: false,
                        new byte[] { 3 })));
            var state = await ReadStateAsync(page);

            Assert.True(append.Appended);
            Assert.Equal(3, state.AppendCount);
            Assert.Equal(1, state.RemoveCount);
            Assert.Equal(1, state.QuotaFailureCount);
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task HostPauseFreezesTheOwnedVideoAndResumeRestartsPlayback()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await InstallDeterministicMediaSourceAsync(page);
            const long generation = 903;
            await PublishAsync(page, generation);
            var before = await ReadStateAsync(page);

            var paused = await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildSetEncodedStreamPaused(generation, paused: true));
            var whilePaused = await ReadStateAsync(page);
            var resumed = await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildSetEncodedStreamPaused(generation, paused: false));
            var after = await ReadStateAsync(page);

            Assert.True(paused);
            Assert.True(resumed);
            Assert.Equal(before.PauseCount + 1, whilePaused.PauseCount);
            Assert.True(whilePaused.HostPaused);
            Assert.Equal(whilePaused.PlayCount + 1, after.PlayCount);
            Assert.False(after.HostPaused);
        });
    }

    private static async Task PublishAsync(IPage page, long generation)
    {
        await page.EvaluateExpressionAsync<PrepareReceipt>(
            InjectionScriptBuilder.BuildPrepareEncodedStream(
                Options(generation),
                Descriptor(generation)));
        await AppendStartupAsync(page, generation);
    }

    private static async Task AppendStartupAsync(IPage page, long generation)
    {
        await page.EvaluateExpressionAsync<AppendReceipt>(
            InjectionScriptBuilder.BuildAppendEncodedStream(
                Segment(
                    generation,
                    sequence: 0,
                    EncodedWallpaperSegmentKind.Initialization,
                    isKeyFrame: false,
                    new byte[] { 1 })));
        await page.EvaluateExpressionAsync<AppendReceipt>(
            InjectionScriptBuilder.BuildAppendEncodedStream(
                Segment(
                    generation,
                    sequence: 1,
                    EncodedWallpaperSegmentKind.Media,
                    isKeyFrame: true,
                    new byte[] { 2 })));
    }

    private static Task<StreamState> ReadStateAsync(IPage page) =>
        page.EvaluateExpressionAsync<StreamState>(
            $$"""
            (() => {
              const active = globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionScriptBuilder.StateProperty)}}];
              const pending = globalThis[{{System.Text.Json.JsonSerializer.Serialize(InjectionScriptBuilder.PendingStreamStateProperty)}}];
              return {
                rootPresent: Boolean(document.getElementById({{System.Text.Json.JsonSerializer.Serialize(InjectionScriptBuilder.RootElementId)}})),
                activeGeneration: active?.generation ?? null,
                pendingGeneration: pending?.generation ?? null,
                appendCount: globalThis.__testMse.appendCount,
                revocationCount: globalThis.__testMse.revocationCount,
                removeCount: globalThis.__testMse.removeCount,
                lastRemovedEnd: globalThis.__testMse.lastRemovedEnd,
                quotaFailureCount: globalThis.__testMse.quotaFailureCount,
                playCount: globalThis.__testMse.playCount,
                pauseCount: globalThis.__testMse.pauseCount,
                hostPaused: active?.hostPaused ?? false
              };
            })()
            """);

    private static Task<bool> AddOwnedDomResidualAsync(IPage page, long generation) =>
        page.EvaluateExpressionAsync<bool>(
            $$"""
            (() => {
              const residual = document.createElement("div");
              residual.id = "encoded-wallpaper-old-generation-residual";
              residual.dataset.codexWallpaperOwner = {{System.Text.Json.JsonSerializer.Serialize(InjectionScriptBuilder.Owner)}};
              residual.dataset.codexWallpaperGeneration = {{System.Text.Json.JsonSerializer.Serialize(generation.ToString(System.Globalization.CultureInfo.InvariantCulture))}};
              document.body.appendChild(residual);
              return true;
            })()
            """);

    private static Task<bool> RemoveOwnedDomResidualAsync(IPage page) =>
        page.EvaluateExpressionAsync<bool>(
            "document.getElementById('encoded-wallpaper-old-generation-residual')?.remove(); true");

    private static Task InstallDeterministicMediaSourceAsync(IPage page) =>
        page.SetContentAsync(
            """
            <!doctype html>
            <html><head></head><body><main id="native">Codex</main>
            <script>
              globalThis.matchMedia = query => ({
                matches: false,
                media: query,
                onchange: null,
                addEventListener() {},
                removeEventListener() {},
                addListener() {},
                removeListener() {},
                dispatchEvent() { return true; }
              });
              globalThis.__testMse = {
                appendCount: 0,
                revocationCount: 0,
                urlCount: 0,
                failNextAppend: false,
                removeCount: 0,
                lastRemovedEnd: 0,
                ranges: [],
                failNextAppendWithQuota: false,
                quotaFailureCount: 0,
                advanceTimeOnQuota: null,
                playCount: 0,
                pauseCount: 0
              };
              class TestSourceBuffer extends EventTarget {
                constructor() { super(); this.updating = false; }
                get buffered() {
                  const ranges = globalThis.__testMse.ranges;
                  return {
                    length: ranges.length,
                    start: index => ranges[index][0],
                    end: index => ranges[index][1]
                  };
                }
                appendBuffer(bytes) {
                  if (globalThis.__testMse.failNextAppendWithQuota) {
                    globalThis.__testMse.failNextAppendWithQuota = false;
                    globalThis.__testMse.quotaFailureCount += 1;
                    globalThis.__testMse.advanceTimeOnQuota?.();
                    throw new DOMException("synthetic quota pressure", "QuotaExceededError");
                  }
                  if (globalThis.__testMse.failNextAppend) {
                    globalThis.__testMse.failNextAppend = false;
                    throw new DOMException("synthetic append failure", "InvalidStateError");
                  }
                  this.updating = true;
                  globalThis.__testMse.appendCount += 1;
                  queueMicrotask(() => {
                    this.updating = false;
                    this.dispatchEvent(new Event("updateend"));
                  });
                }
                remove(start, end) {
                  this.updating = true;
                  globalThis.__testMse.removeCount += 1;
                  globalThis.__testMse.lastRemovedEnd = end;
                  globalThis.__testMse.ranges = globalThis.__testMse.ranges
                    .map(range => range[1] <= end ? null : [Math.max(range[0], end), range[1]])
                    .filter(Boolean);
                  queueMicrotask(() => {
                    this.updating = false;
                    this.dispatchEvent(new Event("updateend"));
                  });
                }
                abort() {
                  this.updating = false;
                  this.dispatchEvent(new Event("abort"));
                }
              }
              class TestMediaSource extends EventTarget {
                static isTypeSupported(mimeType) { return mimeType.startsWith("video/mp4"); }
                constructor() {
                  super();
                  this.readyState = "closed";
                  queueMicrotask(() => {
                    this.readyState = "open";
                    this.dispatchEvent(new Event("sourceopen"));
                  });
                }
                addSourceBuffer() {
                  this.sourceBuffer = new TestSourceBuffer();
                  return this.sourceBuffer;
                }
                endOfStream() { this.readyState = "ended"; }
              }
              globalThis.MediaSource = TestMediaSource;
              URL.createObjectURL = () =>
                "blob:test-" + (++globalThis.__testMse.urlCount);
              URL.revokeObjectURL = () => { globalThis.__testMse.revocationCount += 1; };
              HTMLMediaElement.prototype.play = () => {
                globalThis.__testMse.playCount += 1;
                return Promise.resolve();
              };
              HTMLMediaElement.prototype.pause = () => {
                globalThis.__testMse.pauseCount += 1;
              };
              HTMLMediaElement.prototype.load = () => {};
            </script></body></html>
            """);

    private static DynamicWallpaperInjectionOptions Options(long generation) =>
        new(
            generation,
            WallpaperObjectFit.Cover,
            mediaOpacity: 0.8,
            glass: new GlassEffectOptions(),
            composition: new WallpaperCompositionOptions());

    private static EncodedWallpaperStreamDescriptor Descriptor(long generation) =>
        new(
            generation,
            "video/mp4; codecs=\"avc1.640028\"",
            width: 1920,
            height: 1080,
            frameRate: 30);

    private static EncodedWallpaperSegment Segment(
        long generation,
        long sequence,
        EncodedWallpaperSegmentKind kind,
        bool isKeyFrame,
        ReadOnlyMemory<byte> payload) =>
        new(generation, sequence, kind, isKeyFrame, payload);

    private sealed record PrepareReceipt(bool Prepared, string Reason, long Generation);

    private sealed record AppendReceipt(
        bool Appended,
        bool Published,
        string Reason,
        long Sequence);

    private sealed record StreamState(
        bool RootPresent,
        long? ActiveGeneration,
        long? PendingGeneration,
        int AppendCount,
        int RevocationCount,
        int RemoveCount,
        double LastRemovedEnd,
        int QuotaFailureCount,
        int PlayCount,
        int PauseCount,
        bool HostPaused);
}
