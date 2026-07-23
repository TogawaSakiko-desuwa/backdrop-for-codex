using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BackdropForCodex.App.Services.Localization;
using Wpf.Ui.Tray.Controls;

namespace BackdropForCodex.App;

/// <summary>
/// Owns the pure-WPF notification-area icon and routes actions back to the composition root.
/// </summary>
internal sealed class TrayController : IAsyncDisposable
{
    private readonly MainWindow _window;
    private readonly Func<Task> _disableWallpaper;
    private readonly Func<Task> _shutdown;
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public TrayController(
        MainWindow window,
        Func<Task> disableWallpaper,
        Func<Task> shutdown,
        IAppTextProvider text)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _disableWallpaper =
            disableWallpaper ?? throw new ArgumentNullException(nameof(disableWallpaper));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        ArgumentNullException.ThrowIfNull(text);

        var menu = new ContextMenu();
        var openItem = new MenuItem
        {
            Header = Localize(text, "Tray_Open", "Open Backdrop for Codex"),
        };
        openItem.Click += (_, _) => ShowWindow();
        var restoreItem = new MenuItem
        {
            Header = Localize(text, "Tray_Restore", "Restore official background"),
        };
        restoreItem.Click += (_, _) => _ = RunSafelyAsync(_disableWallpaper);
        var exitItem = new MenuItem
        {
            Header = Localize(text, "Tray_Exit", "Exit"),
        };
        exitItem.Click += (_, _) => _ = RunSafelyAsync(_shutdown);
        _ = menu.Items.Add(openItem);
        _ = menu.Items.Add(restoreItem);
        _ = menu.Items.Add(new Separator());
        _ = menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            TooltipText = "Backdrop for Codex",
            Icon = (ImageSource)System.Windows.Application.Current.FindResource("AppIconImage"),
            Menu = menu,
            MenuOnRightClick = true,
            FocusOnLeftClick = true,
        };
        // WPF-UI.Tray 4.3 exposes this public event through a delegate whose sender type is
        // internal, so consumers cannot spell its annotated signature exactly.
#pragma warning disable CS8622
        _notifyIcon.LeftDoubleClick += (_, _) => ShowWindow();
#pragma warning restore CS8622
        _notifyIcon.Register();
    }

    internal void ShowWindow()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _window.Show();
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
        }
        catch (InvalidOperationException)
        {
            // The app is already closing its final window.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        TryCleanup(_notifyIcon.Unregister);
        TryCleanup(_notifyIcon.Dispose);
        TryCleanup(_window.CloseForShutdown);
        return ValueTask.CompletedTask;
    }

    private async Task RunSafelyAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            try
            {
                _window.ReportUnexpectedError(exception);
            }
            catch (Exception)
            {
                // The window may already be gone; tray event exceptions must never escape.
            }
        }
    }

    private static string Localize(
        IAppTextProvider text,
        string key,
        string fallback)
    {
        var value = text.GetString(key);
        return string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception)
        {
            // Tray teardown is best effort; the application owns final process shutdown.
        }
    }
}
