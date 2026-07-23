using System.ComponentModel;
using System.Windows;
using BackdropForCodex.App.Services.Preferences;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using AppThemeMode = BackdropForCodex.App.Services.Preferences.ThemeMode;

namespace BackdropForCodex.App.Services.Appearance;

/// <summary>
/// Applies the persisted app theme while always yielding to Windows high contrast.
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
            return;
        }

        if (mode == AppThemeMode.System)
        {
            SystemThemeWatcher.Watch(
                _window,
                WindowBackdropType.Mica,
                updateAccents: true);
            return;
        }

        ApplicationThemeManager.Apply(
            mode == AppThemeMode.Dark
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light,
            WindowBackdropType.Mica,
            updateAccent: true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        SystemThemeWatcher.UnWatch(_window);
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
}
