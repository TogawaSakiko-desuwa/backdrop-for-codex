using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed class WallpaperInjectionOptionsCompatibilityTests
{
    [Fact]
    public void LegacyEightParameterConstructor_RemainsAvailableAndUsesDefaultComposition()
    {
        Type[] legacySignature =
        [
            typeof(long),
            typeof(Uri),
            typeof(string),
            typeof(long),
            typeof(WallpaperMediaKind),
            typeof(WallpaperObjectFit),
            typeof(double),
            typeof(GlassEffectOptions),
        ];

        var constructor = typeof(WallpaperInjectionOptions).GetConstructor(legacySignature);

        Assert.NotNull(constructor);

        var options = Assert.IsType<WallpaperInjectionOptions>(
            constructor.Invoke(
            [
                7L,
                new Uri("file:///C:/Wallpapers/legacy.png"),
                @"C:\Wallpapers\legacy.png",
                4_096L,
                WallpaperMediaKind.Image,
                WallpaperObjectFit.Contain,
                0.75,
                new GlassEffectOptions(),
            ]));

        Assert.Equal(new WallpaperCompositionOptions(), options.Composition);
    }
}
