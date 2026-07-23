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
    }
}
