using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace BackdropForCodex.App;

internal sealed class TrayController : IAsyncDisposable
{
    private readonly MainWindow _window;
    private readonly Func<Task> _shutdown;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ContextMenuStrip _menu;
    private bool _disposed;

    public TrayController(MainWindow window, Func<Task> shutdown)
    {
        _window = window;
        _shutdown = shutdown;

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add("打开设置", null, (_, _) => ShowWindow());
        _menu.Items.Add("关闭壁纸并恢复背景", null, DisableWallpaperFromTray);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("退出", null, ExitFromTray);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Backdrop for Codex",
            Icon = SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        _window.Closing += (_, args) =>
        {
            if (!_disposed)
            {
                args.Cancel = true;
                _window.Hide();
            }
        };
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
            _window.WindowState = WindowState.Normal;
            _window.Activate();
        }
        catch (InvalidOperationException)
        {
            // The application is already closing the settings window.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        TryCleanup(() => _notifyIcon.Visible = false);
        TryCleanup(_notifyIcon.Dispose);
        TryCleanup(_menu.Dispose);
        TryCleanup(_window.Close);
        return ValueTask.CompletedTask;
    }

    private void DisableWallpaperFromTray(object? sender, EventArgs e) =>
        _ = DisableWallpaperFromTraySafelyAsync();

    private async Task DisableWallpaperFromTraySafelyAsync()
    {
        try
        {
            await _window.DisableWallpaperAsync();
        }
        catch (Exception exception)
        {
            TryReportUnexpectedError(exception);
        }
    }

    private void ExitFromTray(object? sender, EventArgs e) => _ = ExitFromTraySafelyAsync();

    private async Task ExitFromTraySafelyAsync()
    {
        try
        {
            await _shutdown();
        }
        catch (Exception exception)
        {
            TryReportUnexpectedError(exception);
        }
    }

    private void TryReportUnexpectedError(Exception exception)
    {
        try
        {
            _window.ReportUnexpectedError(exception);
        }
        catch (Exception)
        {
            // The window may already be closed; no async event exception may escape the tray.
        }
    }

    private static void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception)
        {
            // Tray teardown is best effort; App owns the final shutdown and mutex release.
        }
    }
}
