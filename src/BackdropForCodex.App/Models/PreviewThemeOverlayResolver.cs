using Wpf.Ui.Appearance;

namespace BackdropForCodex.App.Models;

/// <summary>
/// Selects the preview overlay that matches the effective application theme.
/// Explicit application themes take precedence; automatic and high-contrast modes
/// follow the current system theme.
/// </summary>
public static class PreviewThemeOverlayResolver
{
    public static PreviewThemeOverlayState Resolve(
        ApplicationTheme applicationTheme,
        SystemTheme systemTheme,
        double darkOpacity,
        double lightOpacity)
    {
        var isLight = applicationTheme == ApplicationTheme.Light ||
                      ((applicationTheme is ApplicationTheme.Unknown or
                           ApplicationTheme.HighContrast) &&
                       (systemTheme is SystemTheme.Light or SystemTheme.HCWhite));

        return new PreviewThemeOverlayState(
            isLight,
            isLight ? lightOpacity : darkOpacity);
    }
}

public readonly record struct PreviewThemeOverlayState(
    bool IsLight,
    double Opacity);
