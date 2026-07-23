using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed class WallpaperInjectionOptionsTests
{
    [Fact]
    public void ToString_RedactsMediaLocationWhileRetainingSafeConfigurationSummary()
    {
        const string SecretToken = "private-source-token-do-not-log";
        const string LocalMediaPath = @"C:\Users\tester\Pictures\private-wallpaper-do-not-log.png";
        var source = new Uri($"http://127.0.0.1:49152/media/{SecretToken}");
        var options = new WallpaperInjectionOptions(
            generation: 42,
            source,
            LocalMediaPath,
            expectedContentLength: 123_456,
            WallpaperMediaKind.Image,
            WallpaperObjectFit.Contain,
            mediaOpacity: 0.75,
            new GlassEffectOptions(opacity: 0.4, blurPixels: 12));

        var summary = options.ToString();

        Assert.DoesNotContain(source.AbsoluteUri, summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretToken, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(LocalMediaPath, summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Source = <redacted>", summary, StringComparison.Ordinal);
        Assert.Contains("LocalMediaPath = <redacted>", summary, StringComparison.Ordinal);
        Assert.Contains("Generation = 42", summary, StringComparison.Ordinal);
        Assert.Contains("ExpectedContentLength = 123456", summary, StringComparison.Ordinal);
        Assert.Contains("MediaKind = Image", summary, StringComparison.Ordinal);
        Assert.Contains("ObjectFit = Contain", summary, StringComparison.Ordinal);
        Assert.Contains("MediaOpacity = 0.75", summary, StringComparison.Ordinal);
        Assert.Contains("Glass = GlassEffectOptions", summary, StringComparison.Ordinal);
        Assert.Contains("Composition = WallpaperCompositionOptions", summary, StringComparison.Ordinal);
        Assert.Equal(new WallpaperCompositionOptions(), options.Composition);
    }

    [Theory]
    [InlineData("FocusX", -0.01)]
    [InlineData("FocusY", 1.01)]
    [InlineData("DarkOverlay", -0.01)]
    [InlineData("DarkOverlay", 0.61)]
    [InlineData("LightOverlay", double.PositiveInfinity)]
    [InlineData("FocusX", double.NaN)]
    public void CompositionRejectsNonFiniteAndOutOfRangeValues(
        string propertyName,
        double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => propertyName switch
        {
            "FocusX" => new WallpaperCompositionOptions(focusX: value),
            "FocusY" => new WallpaperCompositionOptions(focusY: value),
            "DarkOverlay" => new WallpaperCompositionOptions(darkOverlay: value),
            "LightOverlay" => new WallpaperCompositionOptions(lightOverlay: value),
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
        });
    }

    [Fact]
    public void CompositionAcceptsInclusiveBoundaries()
    {
        var composition = new WallpaperCompositionOptions(
            focusX: 0,
            focusY: 1,
            darkOverlay: 0,
            lightOverlay: WallpaperCompositionOptions.MaximumOverlayOpacity);

        Assert.Equal(0, composition.FocusX);
        Assert.Equal(1, composition.FocusY);
        Assert.Equal(0, composition.DarkOverlay);
        Assert.Equal(WallpaperCompositionOptions.MaximumOverlayOpacity, composition.LightOverlay);
    }
}
