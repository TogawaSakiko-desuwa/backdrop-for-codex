using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed class WallpaperInjectionOptionsCompatibilityTests
{
    [Fact]
    public void CompositionOptionalConstructorUsesDefaultWithoutRetainingUnusedSource()
    {
        Type[] signature =
        [
            typeof(long),
            typeof(string),
            typeof(long),
            typeof(WallpaperMediaKind),
            typeof(WallpaperObjectFit),
            typeof(double),
            typeof(GlassEffectOptions),
        ];

        var constructor = typeof(WallpaperInjectionOptions).GetConstructor(signature);

        Assert.NotNull(constructor);

        var options = Assert.IsType<WallpaperInjectionOptions>(
            constructor.Invoke(
            [
                7L,
                @"C:\Wallpapers\legacy.png",
                4_096L,
                WallpaperMediaKind.Image,
                WallpaperObjectFit.Contain,
                0.75,
                new GlassEffectOptions(),
            ]));

        Assert.Equal(new WallpaperCompositionOptions(), options.Composition);
        Assert.DoesNotContain(
            typeof(WallpaperInjectionOptions).GetConstructors(),
            candidate => candidate
                .GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(Uri)));
        Assert.Null(typeof(WallpaperInjectionOptions).GetProperty("Source"));
    }
}
