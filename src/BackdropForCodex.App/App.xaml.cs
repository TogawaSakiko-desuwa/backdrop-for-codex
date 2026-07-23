using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Windows;
using BackdropForCodex.App.Services.Errors;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Preferences;
using BackdropForCodex.App.Services.Wallpaper;
using BackdropForCodex.App.ViewModels;
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
    private WallpaperApplicationService? _wallpaperService;
    private AppPreferencesStore? _preferencesStore;
    private UserFacingErrorMapper? _errorMapper;
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
                        "Backdrop for Codex is already running, but its notification-area instance could not be reached. Open it from the notification area and retry.",
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
            var text = new AppTextProvider();
            _errorMapper = new UserFacingErrorMapper(text);
            _preferencesStore = AppPreferencesStore.CreateForCurrentUser();
            _wallpaperService = new WallpaperApplicationService(
                WallpaperCoordinator.CreateDefault(settingsPath));
            var viewModel = new MainWindowViewModel(
                _wallpaperService,
                _preferencesStore,
                _errorMapper,
                text);
            _mainWindow = new MainWindow(viewModel, text);
            _trayController = new TrayController(
                _mainWindow,
                viewModel.DisableAsync,
                ShutdownSafelyAsync,
                text);

            if (HasLaunchArgument(e.Args))
            {
                _mainWindow.ContentRendered += AutoLaunchAfterRender;
            }

            MainWindow = _mainWindow;
            _mainWindow.Show();
            _trayController.Register();
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

            if (_wallpaperService is not null)
            {
                var wallpaperService = _wallpaperService;
                _wallpaperService = null;
                await AttemptAsync(() => wallpaperService.DisableAsync());
                await AttemptAsync(async () => await wallpaperService.DisposeAsync());
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

            if (_preferencesStore is not null)
            {
                try
                {
                    _preferencesStore.Dispose();
                }
                catch (Exception)
                {
                    cleanupFailed = true;
                }

                _preferencesStore = null;
            }

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
                    "Cleanup could not be fully confirmed. The page lease will keep trying to restore the background; exit Codex completely to close the debugging endpoint immediately.",
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

    private string ToStartupMessage(Exception exception)
    {
        if (_errorMapper is not null)
        {
            var error = _errorMapper.Map(exception);
            return $"{error.Message} {error.Recovery}";
        }

        return exception is PlatformNotSupportedException
            ? "Backdrop for Codex requires Windows 11 on x64 hardware."
            : "Backdrop for Codex could not start safely. Confirm this is Windows 11 x64 and retry.";
    }

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
