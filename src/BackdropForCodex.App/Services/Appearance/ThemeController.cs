using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BackdropForCodex.App.Services.Appearance;

/// <summary>
/// The workbench is a dark editor shell. The controller applies the dark Fluent theme, yields to
/// Windows high contrast, and publishes the resolved state through
/// <see cref="WorkbenchAppearance.IsDarkProperty"/> so the editor palette follows it.
/// </summary>
public sealed class ThemeController : IDisposable
{
    private readonly Window _window;
    private int _disposeState;

    public ThemeController(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        ApplicationThemeManager.Changed += ApplicationThemeManager_Changed;
    }

    public void Apply()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeState) != 0,
            this);

        ApplicationThemeManager.Apply(
            SystemParameters.HighContrast
                ? ApplicationTheme.HighContrast
                : ApplicationTheme.Dark,
            WindowBackdropType.None,
            updateAccent: true);
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
        GC.SuppressFinalize(this);
    }

    private void SystemParameters_StaticPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Apply();
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
