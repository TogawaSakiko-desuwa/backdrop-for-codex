using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Tests.Infrastructure;
using PuppeteerSharp;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

[Collection("Reviewed selector browser contracts")]
public sealed class DeferredMediaBrowserContractTests
{
    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task HiddenVideoSurvivesLoadingDeadlineAndPlaysWhenShownWithoutReapply()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await page.SetContentAsync("<html><body><div id='root'><main></main></div></body></html>");
            // This contract verifies autoplay; do not inherit the runner's reduced-motion policy.
            var motion = await page.CreateCDPSessionAsync();
            await motion.SendAsync("Emulation.setEmulatedMedia", new
            {
                features = new[] { new { name = "prefers-reduced-motion", value = "no-preference" } },
            });
            Assert.False(await page.EvaluateExpressionAsync<bool>(
                "matchMedia('(prefers-reduced-motion: reduce)').matches"));
            var bytes = await RecordVideoAsync(page);
            var path = Path.Combine(Path.GetTempPath(), $"backdrop-deferred-{Guid.NewGuid():N}.webm");
            await File.WriteAllBytesAsync(path, bytes);
            try
            {
                var foreground = await page.Browser.NewPageAsync();
                await foreground.BringToFrontAsync();
                Assert.True(await page.EvaluateExpressionAsync<bool>("document.hidden"));
                const long generation = 812;
                var receipt = await InstallAsync(page, path, generation);
                Assert.True(receipt.Applied);
                Assert.True(receipt.Pending);
                // Keep the controller alive longer than the old wall-clock load deadline.
                for (var tick = 0; tick < 6; tick++)
                {
                    await Task.Delay(2000);
                    Assert.True(await page.EvaluateExpressionAsync<bool>(
                        InjectionScriptBuilder.BuildHeartbeat(generation)));
                }

                // A headless browser has no native window to expose; CDP visibility emulation
                // drives Chromium's real media suspension/resume path for this fixture.
                var visibility = await page.CreateCDPSessionAsync();
                await visibility.SendAsync("Emulation.setFocusEmulationEnabled", new { enabled = true });
                Assert.False(await page.EvaluateExpressionAsync<bool>("document.hidden"));
                await WaitUntilAsync(page,
                    "globalThis.__codexWallpaperOwnedState_v1?.mediaReady === true");
                await WaitUntilAsync(page,
                    "globalThis.__codexWallpaperOwnedState_v1.media.currentTime > 0 && " +
                    "!globalThis.__codexWallpaperOwnedState_v1.media.paused");
                Assert.True(await page.EvaluateExpressionAsync<bool>(
                    InjectionScriptBuilder.BuildHeartbeat(generation)));
                Assert.True(await page.EvaluateExpressionAsync<bool>(
                    InjectionScriptBuilder.BuildSetPaused(generation, true)));
                Assert.True(await page.EvaluateExpressionAsync<bool>(
                    "globalThis.__codexWallpaperOwnedState_v1.media.paused"));
                Assert.True(await page.EvaluateExpressionAsync<bool>(
                    InjectionScriptBuilder.BuildCleanup(generation)));
                Assert.False(await page.EvaluateExpressionAsync<bool>(
                    "Boolean(globalThis.__codexWallpaperOwnedState_v1)"));
                await visibility.DetachAsync();
                await motion.DetachAsync();
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task DeferredDecodeFailureCleansOwnedResourcesAfterWindowIsShown()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await page.SetContentAsync("<html><body><div id='root'><main></main></div></body></html>");
            var path = Path.Combine(Path.GetTempPath(), $"backdrop-invalid-{Guid.NewGuid():N}.webm");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            try
            {
                var foreground = await page.Browser.NewPageAsync();
                await foreground.BringToFrontAsync();
                var receipt = await InstallAsync(page, path, 813);
                Assert.True(receipt.Applied);
                Assert.True(receipt.Pending);
                var visibility = await page.CreateCDPSessionAsync();
                await visibility.SendAsync("Emulation.setFocusEmulationEnabled", new { enabled = true });
                Assert.False(await page.EvaluateExpressionAsync<bool>("document.hidden"));
                await WaitUntilAsync(page, "!globalThis.__codexWallpaperOwnedState_v1");
                Assert.False(await page.EvaluateExpressionAsync<bool>(
                    InjectionScriptBuilder.BuildHeartbeat(813)));
                Assert.False(await page.EvaluateExpressionAsync<bool>(
                    "Boolean(document.getElementById('codex-wallpaper-owned-root'))"));
                await visibility.DetachAsync();
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    private static async Task<ActivationReceipt> InstallAsync(IPage page, string path, long generation)
    {
        var options = new WallpaperInjectionOptions(
            generation, path, new FileInfo(path).Length, WallpaperMediaKind.Video);
        await using var input = await page.EvaluateExpressionHandleAsync(
            InjectionScriptBuilder.BuildInstall(options));
        await Assert.IsAssignableFrom<IElementHandle>(input).UploadFileAsync(false, path);
        return await page.EvaluateExpressionAsync<ActivationReceipt>(
            InjectionScriptBuilder.BuildActivateMedia(generation));
    }

    private static async Task WaitUntilAsync(IPage page, string condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (await page.EvaluateExpressionAsync<bool>(condition)) return;
            await Task.Delay(100);
        }

        Assert.Fail(await page.EvaluateExpressionAsync<string>("""
            (() => { const s=globalThis.__codexWallpaperOwnedState_v1; return JSON.stringify({
              hidden:document.hidden,pending:s?.activationPending,ready:s?.mediaReady,
              network:s?.media.networkState,mediaState:s?.media.readyState,error:s?.media.error?.message,
              paused:s?.media.paused,time:s?.media.currentTime,reducedMotion:s?.motionQuery?.matches
            }); })()
            """));
    }

    private static async Task<byte[]> RecordVideoAsync(IPage page)
    {
        var base64 = await page.EvaluateExpressionAsync<string>("""
            (async () => {
              const canvas = document.createElement("canvas");
              canvas.width = canvas.height = 32;
              const context = canvas.getContext("2d");
              const stream = canvas.captureStream(10);
              const recorder = new MediaRecorder(stream, { mimeType: "video/webm;codecs=vp8" });
              const chunks = [];
              recorder.ondataavailable = event => chunks.push(event.data);
              const completed = new Promise(resolve => recorder.onstop = resolve);
              recorder.start();
              for (let frame = 0; frame < 5; frame++) {
                context.fillStyle = frame % 2 ? "red" : "blue";
                context.fillRect(0, 0, 32, 32);
                await new Promise(resolve => setTimeout(resolve, 100));
              }
              recorder.stop();
              await completed;
              stream.getTracks().forEach(track => track.stop());
              return btoa(String.fromCharCode(...new Uint8Array(await new Blob(chunks).arrayBuffer())));
            })()
            """);
        return Convert.FromBase64String(base64);
    }

    private sealed record ActivationReceipt(bool Applied, bool Pending);
}
