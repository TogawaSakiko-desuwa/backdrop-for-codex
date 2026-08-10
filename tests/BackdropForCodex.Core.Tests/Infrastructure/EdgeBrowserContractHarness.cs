using PuppeteerSharp;

namespace BackdropForCodex.Core.Tests.Infrastructure;

internal static class EdgeBrowserContractHarness
{
    internal static async Task WithPageAsync(Func<IPage, Task> test)
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
        return candidates.FirstOrDefault(File.Exists) ??
            throw new FileNotFoundException(
                "Microsoft Edge is required for Category=BrowserContract. " +
                "Install Edge or set BACKDROP_FOR_CODEX_EDGE_PATH to msedge.exe.");
    }
}
