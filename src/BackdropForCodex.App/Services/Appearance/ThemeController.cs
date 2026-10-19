using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BackdropForCodex.App.Services.Preferences;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using AppThemeMode = BackdropForCodex.App.Services.Preferences.ThemeMode;

namespace BackdropForCodex.App.Services.Appearance;

/// <summary>
/// Applies the persisted app theme while always yielding to Windows high contrast, and publishes
/// the resolved dark state to the window so the editor palette can follow it.
/// </summary>
public sealed class ThemeController : IDisposable
{
    private readonly Window _window;
    private AppThemeMode _mode;
    private int _disposeState;

    public ThemeController(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        ApplicationThemeManager.Changed += ApplicationThemeManager_Changed;
    }

    public void Apply(AppThemeMode mode)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeState) != 0,
            this);
        _mode = mode;
        SystemThemeWatcher.UnWatch(_window);

        if (SystemParameters.HighContrast)
        {
            ApplicationThemeManager.Apply(
                ApplicationTheme.HighContrast,
                WindowBackdropType.None,
                updateAccent: true);
        }
        else if (mode == AppThemeMode.System)
        {
            SystemThemeWatcher.Watch(
                _window,
                WindowBackdropType.Mica,
                updateAccents: true);
        }
        else
        {
            ApplicationThemeManager.Apply(
                mode == AppThemeMode.Dark
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light,
                WindowBackdropType.Mica,
                updateAccent: true);
        }

        PublishAppearance(ApplicationThemeManager.GetAppTheme());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        ApplicationThemeManager.Changed -= ApplicationThemeManager_Changed;
        if (new WindowInteropHelper(_window).Handle != IntPtr.Zero)
        {
            SystemThemeWatcher.UnWatch(_window);
        }

        GC.SuppressFinalize(this);
    }

    private void SystemParameters_StaticPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Apply(_mode);
        }
    }

    private void ApplicationThemeManager_Changed(
        ApplicationTheme currentApplicationTheme,
        Color systemAccent)
    {
        _ = systemAccent;
        if (_window.Dispatcher.CheckAccess())
        {
            PublishAppearance(currentApplicationTheme);
            return;
        }

        _ = _window.Dispatcher.BeginInvoke(
            () => PublishAppearance(currentApplicationTheme));
    }

    private void PublishAppearance(ApplicationTheme theme) =>
        WorkbenchAppearance.SetIsDark(
            _window,
            theme == ApplicationTheme.Dark && !SystemParameters.HighContrast);
}
