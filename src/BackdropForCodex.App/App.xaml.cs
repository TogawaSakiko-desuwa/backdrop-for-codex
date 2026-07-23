using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Windows;
using BackdropForCodex.Core.Runtime;

namespace BackdropForCodex.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF lifetime explicitly disposes all owned resources before Shutdown.")]
public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private SingleInstanceCommandServer? _commandServer;
    private TrayController? _trayController;
    private WallpaperCoordinator? _coordinator;
    private MainWindow? _mainWindow;
    private int _shutdownStarted;
    private int _launchCommandRunning;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var identity = SingleInstanceIdentity.ForCurrentUserSession();
            _instanceMutex = new Mutex(initiallyOwned: true, identity.MutexName, out var createdNew);
            if (!createdNew)
            {
                var command = HasLaunchArgument(e.Args)
                    ? SingleInstanceCommand.Launch
                    : SingleInstanceCommand.Show;
                var forwarded = SingleInstanceCommandClient.TrySend(identity.PipeName, command);
                _instanceMutex.Dispose();
                _instanceMutex = null;

                if (!forwarded)
                {
                    ShowMessageSafely(
                        "Backdrop for Codex 已在运行，但暂时无法联系托盘实例。请从系统托盘打开后重试。",
                        MessageBoxImage.Warning);
                }

                Shutdown();
                return;
            }

            _commandServer = new SingleInstanceCommandServer(
                identity.PipeName,
                ReceiveSingleInstanceCommand);

            // This storage identity predates the display-name change and remains stable so
            // existing local settings continue to load after upgrading.
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexWallpaper",
                "settings.json");
            _coordinator = WallpaperCoordinator.CreateDefault(settingsPath);
            _mainWindow = new MainWindow(_coordinator);
            _trayController = new TrayController(_mainWindow, ShutdownSafelyAsync);

            if (HasLaunchArgument(e.Args))
            {
                _mainWindow.ContentRendered += AutoLaunchAfterRender;
            }

            _mainWindow.Show();
        }
        catch (Exception exception)
        {
            ShowMessageSafely(ToStartupMessage(exception), MessageBoxImage.Error);
            _ = ShutdownSafelyAsync();
        }
    }

    private static bool HasLaunchArgument(IEnumerable<string> arguments) =>
        arguments.Contains("--launch", StringComparer.OrdinalIgnoreCase);

    private void AutoLaunchAfterRender(object? sender, EventArgs eventArgs)
    {
        if (sender is MainWindow window)
        {
            window.ContentRendered -= AutoLaunchAfterRender;
            StartAutoLaunchCommand(window);
        }
    }

    private void ReceiveSingleInstanceCommand(SingleInstanceCommand command)
    {
        try
        {
            _ = Dispatcher.InvokeAsync(() => HandleSingleInstanceCommand(command));
        }
        catch (Exception)
        {
            // The dispatcher is already shutting down; the client can retry after the process exits.
        }
    }

    private void HandleSingleInstanceCommand(SingleInstanceCommand command)
    {
        if (Volatile.Read(ref _shutdownStarted) != 0 || _mainWindow is null)
        {
            return;
        }

        _trayController?.ShowWindow();
        if (command == SingleInstanceCommand.Launch)
        {
            StartAutoLaunchCommand(_mainWindow);
        }
    }

    private void StartAutoLaunchCommand(MainWindow window)
    {
        if (Volatile.Read(ref _shutdownStarted) != 0 ||
            Interlocked.CompareExchange(ref _launchCommandRunning, 1, 0) != 0)
        {
            return;
        }

        _ = RunAutoLaunchCommandSafelyAsync(window);
    }

    private async Task RunAutoLaunchCommandSafelyAsync(MainWindow window)
    {
        try
        {
            await window.BeginAutoLaunchAsync();
        }
        catch (Exception exception)
        {
            try
            {
                window.ReportUnexpectedError(exception);
            }
            catch (Exception)
            {
                // The window may have closed while the forwarded launch was awaiting the runtime.
            }
        }
        finally
        {
            Volatile.Write(ref _launchCommandRunning, 0);
        }
    }

    private async Task ShutdownSafelyAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        var cleanupFailed = false;

        async Task AttemptAsync(Func<Task> operation)
        {
            try
            {
                await operation();
            }
            catch (ObjectDisposedException)
            {
                // A preceding best-effort cleanup step already released this resource.
            }
            catch (Exception)
            {
                cleanupFailed = true;
            }
        }

        try
        {
            if (_commandServer is not null)
            {
                var commandServer = _commandServer;
                _commandServer = null;
                await AttemptAsync(async () => await commandServer.DisposeAsync());
            }

            if (_coordinator is not null)
            {
                var coordinator = _coordinator;
                _coordinator = null;
                await AttemptAsync(() => coordinator.DisableAsync());
                await AttemptAsync(async () => await coordinator.DisposeAsync());
            }
        }
        catch (Exception)
        {
            cleanupFailed = true;
        }
        finally
        {
            if (_trayController is not null)
            {
                var trayController = _trayController;
                _trayController = null;
                await AttemptAsync(async () => await trayController.DisposeAsync());
            }

            _mainWindow = null;

            if (_instanceMutex is not null)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch (Exception)
                {
                    cleanupFailed = true;
                }
                finally
                {
                    try
                    {
                        _instanceMutex.Dispose();
                    }
                    catch (Exception)
                    {
                        cleanupFailed = true;
                    }

                    _instanceMutex = null;
                }
            }

            if (cleanupFailed)
            {
                ShowMessageSafely(
                    "退出清理未能被完全确认。页面租约会继续尝试恢复背景；为立即关闭调试端口，请完全退出 Codex。",
                    MessageBoxImage.Warning);
            }

            try
            {
                Shutdown();
            }
            catch (Exception)
            {
                // Shutdown is already underway.
            }
        }
    }

    private static string ToStartupMessage(Exception exception) => exception switch
    {
        PlatformNotSupportedException => "Backdrop for Codex 第一版仅支持 Windows 11 x64。",
        _ => "Backdrop for Codex 无法安全启动。请确认系统为 Windows 11 x64，并重试。",
    };

    private static void ShowMessageSafely(string message, MessageBoxImage image)
    {
        try
        {
            System.Windows.MessageBox.Show(
                message,
                "Backdrop for Codex",
                MessageBoxButton.OK,
                image);
        }
        catch (Exception)
        {
            // A message box is advisory; failure to display it must not block cleanup.
        }
    }
}
