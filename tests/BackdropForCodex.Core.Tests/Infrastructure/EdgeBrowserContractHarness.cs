using PuppeteerSharp;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace BackdropForCodex.Core.Tests.Infrastructure;

internal static class EdgeBrowserContractHarness
{
    internal static Task WithPageAsync(Func<IPage, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        return WithPageCoreAsync((page, _) => test(page));
    }

    internal static async Task WithPageAndWebSocketEndpointAsync(
        Func<IPage, Uri, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        await WithPageCoreAsync(test);
    }

    private static async Task WithPageCoreAsync(
        Func<IPage, Uri, Task> test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var port = ReserveLoopbackPort();
        var userDataDirectory = Path.Combine(
            Path.GetTempPath(),
            "BackdropForCodex.BrowserContract",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDirectory);
        Process? edge = null;
        IBrowser? browser = null;
        try
        {
            edge = Process.Start(CreateEdgeStartInfo(
                FindEdge(),
                port,
                userDataDirectory)) ?? throw new InvalidOperationException(
                    "Microsoft Edge did not return a process handle.");
            var endpoint = await WaitForWebSocketEndpointAsync(port);
            browser = await Puppeteer.ConnectAsync(new ConnectOptions
            {
                BrowserWSEndpoint = endpoint.AbsoluteUri,
                DefaultViewport = null,
                ProtocolTimeout = 5_000,
                AcceptInsecureCerts = false,
                NetworkEnabled = false,
            });
            // Edge may expose packaged-app or extension targets before the requested startup tab.
            // Always create the ordinary page owned by this harness so DOM/CSSOM contracts never
            // execute inside a protected browser surface.
            var page = await browser.NewPageAsync();
            await page.SetViewportAsync(new ViewPortOptions
            {
                Width = 1280,
                Height = 900,
            });
            await test(page, endpoint);
        }
        finally
        {
            try
            {
                browser?.Disconnect();
            }
            finally
            {
                try
                {
                    if (edge is { HasExited: false })
                    {
                        edge.Kill(entireProcessTree: true);
                        await edge.WaitForExitAsync();
                    }
                }
                finally
                {
                    edge?.Dispose();
                    await DeleteDirectoryWithRetryAsync(userDataDirectory);
                }
            }
        }
    }

    private static ProcessStartInfo CreateEdgeStartInfo(
        string edgePath,
        int port,
        string userDataDirectory)
    {
        var startInfo = new ProcessStartInfo(edgePath)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--headless=new");
        startInfo.ArgumentList.Add("--disable-extensions");
        startInfo.ArgumentList.Add("--disable-gpu");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--edge-skip-compat-layer-relaunch");
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add($"--remote-debugging-port={port}");
        startInfo.ArgumentList.Add($"--user-data-dir={userDataDirectory}");
        startInfo.ArgumentList.Add("about:blank");
        return startInfo;
    }

    private static async Task DeleteDirectoryWithRetryAsync(string directory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }

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

    private static async Task<Uri> WaitForWebSocketEndpointAsync(int port)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(1),
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("json/version", deadline.Token);
                response.EnsureSuccessStatusCode();
                await using var content = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var document = await JsonDocument.ParseAsync(
                    content,
                    cancellationToken: deadline.Token);
                var endpoint = document.RootElement
                    .GetProperty("webSocketDebuggerUrl")
                    .GetString();
                if (Uri.TryCreate(endpoint, UriKind.Absolute, out var websocket) &&
                    string.Equals(websocket.Scheme, "ws", StringComparison.Ordinal) &&
                    string.Equals(websocket.Host, "127.0.0.1", StringComparison.Ordinal) &&
                    websocket.Port == port)
                {
                    return websocket;
                }
            }
            catch (HttpRequestException) when (!deadline.IsCancellationRequested)
            {
            }
            catch (TaskCanceledException) when (!deadline.IsCancellationRequested)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
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
