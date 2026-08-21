using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEngineControlCommandTests
{
    [Fact]
    public void OpenWindowArgumentsKeepPathsAndOwnedNamesAsSeparateTokens()
    {
        var name = WallpaperEngineOwnedWindowName.Create(generation: 42);

        var arguments = WallpaperEngineControlCommandBuilder.BuildOpenWindow(
            @"C:\Wallpaper Engine\projects\web project\index.html",
            name,
            new WallpaperEngineWindowOptions(1920, 1080),
            x: -4096,
            y: -2160);

        Assert.Equal(
            [
                "-control",
                "openWallpaper",
                "-file",
                @"C:\Wallpaper Engine\projects\web project\index.html",
                "-playInWindow",
                name.Value,
                "-width",
                "1920",
                "-height",
                "1080",
                "-x",
                "-4096",
                "-y",
                "-2160",
                "-borderless",
            ],
            arguments);
    }

    [Fact]
    public void QueryAndCloseArgumentsCanOnlyAddressTheOwnedWindowName()
    {
        var name = WallpaperEngineOwnedWindowName.Create(generation: 7);

        Assert.Equal(
            ["-control", "getWallpaper", "-location", name.Value],
            WallpaperEngineControlCommandBuilder.BuildQueryWindow(name));
        Assert.Equal(
            ["-control", "closeWallpaper", "-location", name.Value],
            WallpaperEngineControlCommandBuilder.BuildCloseWindow(name));
    }

    [Fact]
    public void OwnedWindowNameIsHighEntropyBoundedAndRedacted()
    {
        var first = WallpaperEngineOwnedWindowName.Create(generation: 1);
        var second = WallpaperEngineOwnedWindowName.Create(generation: 1);

        Assert.NotEqual(first, second);
        Assert.StartsWith("BackdropForCodex-1-", first.Value, StringComparison.Ordinal);
        Assert.InRange(first.Value.Length, 40, WallpaperEngineOwnedWindowName.MaximumLength);
        Assert.DoesNotContain(first.Value, first.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("BackdropForCodex-invalid name")]
    [InlineData("BackdropForCodex-1-../../desktop")]
    public void OwnedWindowNameRejectsUntrustedText(string value)
    {
        Assert.Throws<ArgumentException>(() => new WallpaperEngineOwnedWindowName(value));
    }
}
