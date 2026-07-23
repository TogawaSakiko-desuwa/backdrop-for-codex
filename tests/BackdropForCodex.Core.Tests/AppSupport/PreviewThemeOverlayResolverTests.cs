using BackdropForCodex.App.Models;
using Wpf.Ui.Appearance;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class PreviewThemeOverlayResolverTests
{
    [Theory]
    [InlineData(ApplicationTheme.Light, SystemTheme.Dark, true, 0.42)]
    [InlineData(ApplicationTheme.Dark, SystemTheme.Light, false, 0.17)]
    [InlineData(ApplicationTheme.Unknown, SystemTheme.Light, true, 0.42)]
    [InlineData(ApplicationTheme.Unknown, SystemTheme.Dark, false, 0.17)]
    [InlineData(ApplicationTheme.HighContrast, SystemTheme.HCWhite, true, 0.42)]
    [InlineData(ApplicationTheme.HighContrast, SystemTheme.Dark, false, 0.17)]
    public void EffectiveThemeSelectsTheMatchingOverlay(
        ApplicationTheme applicationTheme,
        SystemTheme systemTheme,
        bool expectedLight,
        double expectedOpacity)
    {
        var overlay = PreviewThemeOverlayResolver.Resolve(
            applicationTheme,
            systemTheme,
            darkOpacity: 0.17,
            lightOpacity: 0.42);

        Assert.Equal(expectedLight, overlay.IsLight);
        Assert.Equal(expectedOpacity, overlay.Opacity);
    }
}
