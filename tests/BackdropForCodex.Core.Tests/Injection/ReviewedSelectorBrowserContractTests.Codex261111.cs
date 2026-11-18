using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Tests.Infrastructure;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed partial class ReviewedSelectorBrowserContractTests
{
    [BrowserContractFact]
    [Trait("Category", "BrowserContract")]
    public async Task Codex261111_ThreadAndSettingsFadesPreserveControlsAndUnrelatedFooters()
    {
        await EdgeBrowserContractHarness.WithPageAsync(async page =>
        {
            await LoadFixtureAsync(page, """
                <html><head><style>
                  .fade, .footer::before { background-image: linear-gradient(black, transparent); }
                  .footer::before, .footer::after { content: ""; position: absolute; height: 16px; }
                  .footer::after, button { background-color: rgb(41, 42, 43); }
                </style></head><body><div id="root"><main data-app-shell-main-surface="default">
                  <div data-app-shell-main-content-top-fade>
                    <div aria-hidden="true" class="fade MainContentTopFade-example"></div>
                    <div aria-hidden="true" class="fade OtherMainContentTopFade-example"></div>
                  </div>
                  <div class="thread-scroll-container">
                    <div id="footer" class="footer pointer-events-none absolute inset-x-0 bottom-0">
                      <div data-thread-scroll-footer="true"><button>Compose</button></div>
                    </div>
                    <div id="other" class="footer pointer-events-none absolute inset-x-0 bottom-0"></div>
                  </div>
                </main></div></body></html>
                """);
            const string decorations = """
                [
                  getComputedStyle(document.querySelector('.fade')).backgroundImage,
                  getComputedStyle(document.querySelector('#footer'), '::before').backgroundImage,
                  getComputedStyle(document.querySelector('#footer'), '::after').backgroundColor
                ]
                """;
            const string protectedProperties = """
                [
                  getComputedStyle(document.querySelector('button')).backgroundColor,
                  getComputedStyle(document.querySelector('#footer'), '::after').height,
                  getComputedStyle(document.querySelector('#other'), '::after').backgroundColor,
                  getComputedStyle(document.querySelector('.OtherMainContentTopFade-example')).backgroundImage
                ]
                """;
            var original = await page.EvaluateExpressionAsync<string[]>(decorations);
            var controls = await page.EvaluateExpressionAsync<string[]>(protectedProperties);
            await AddOwnedStyleAsync(page, BuildStyleSheet(), initializeOwnership: true);
            Assert.Equal(["none", "none", TransparentBackground],
                await page.EvaluateExpressionAsync<string[]>(decorations));
            Assert.Equal(controls, await page.EvaluateExpressionAsync<string[]>(protectedProperties));

            Assert.True(await page.EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCapabilityDowngrade(
                    Generation, CreateCapabilities(glassAvailable: true, advancedAvailable: false))));
            Assert.Equal(original, await page.EvaluateExpressionAsync<string[]>(decorations));
        });
    }
}
