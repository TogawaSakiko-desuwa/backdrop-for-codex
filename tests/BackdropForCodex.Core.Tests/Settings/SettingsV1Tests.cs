using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Settings;

public sealed class SettingsV1Tests
{
    [Fact]
    public void WallpaperFit_PreservesLegacyValuesAndAppendsStretch()
    {
        Assert.Equal(0, (int)WallpaperFit.Cover);
        Assert.Equal(1, (int)WallpaperFit.Contain);
        Assert.Equal(2, (int)WallpaperFit.Stretch);
    }

    [Fact]
    public void DeserializedDefaultsUseReviewedValuesAndAreValid()
    {
        var settings = new SettingsV1();

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Null(settings.MediaPath);
        Assert.Equal(MediaKind.None, settings.MediaKind);
        Assert.Equal(WallpaperFit.Cover, settings.Fit);
        Assert.Equal(0.5, settings.FocusX);
        Assert.Equal(0.5, settings.FocusY);
        Assert.Equal(0.78, settings.PanelOpacity);
        Assert.Equal(14, settings.BlurPx);
        Assert.Equal(0.30, settings.DarkOverlay);
        Assert.Equal(0.18, settings.LightOverlay);
        Assert.Equal(0.60, SettingsV1.MaximumEffectiveOverlay);
        Assert.Empty(settings.RecentMediaPaths);
        Assert.False(settings.AcceptedCdpRisk);
#pragma warning disable CS0618 // Verify the deprecated persistence field's default.
        Assert.Null(settings.LastCompatibilityProfileId);
#pragma warning restore CS0618
        settings.Validate();
    }

    [Theory]
    [InlineData("FocusX", -0.01)]
    [InlineData("FocusY", 1.01)]
    [InlineData("PanelOpacity", 0.59)]
    [InlineData("PanelOpacity", 0.96)]
    [InlineData("BlurPx", -0.01)]
    [InlineData("BlurPx", 24.01)]
    [InlineData("DarkOverlay", -0.01)]
    [InlineData("LightOverlay", 1.01)]
    [InlineData("FocusX", double.NaN)]
    [InlineData("BlurPx", double.PositiveInfinity)]
    public void ValidateRejectsInvalidNumericValues(string propertyName, double value)
    {
        var settings = propertyName switch
        {
            "FocusX" => new SettingsV1 { FocusX = value },
            "FocusY" => new SettingsV1 { FocusY = value },
            "PanelOpacity" => new SettingsV1 { PanelOpacity = value },
            "BlurPx" => new SettingsV1 { BlurPx = value },
            "DarkOverlay" => new SettingsV1 { DarkOverlay = value },
            "LightOverlay" => new SettingsV1 { LightOverlay = value },
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
        };

        var exception = Assert.Throws<SettingsValidationException>(settings.Validate);

        Assert.Contains(propertyName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAcceptsInclusiveNumericBoundaries()
    {
        var settings = new SettingsV1
        {
            FocusX = 0,
            FocusY = 1,
            PanelOpacity = 0.60,
            BlurPx = 24,
            DarkOverlay = 0,
            LightOverlay = 1,
        };

        settings.Validate();
    }

    [Fact]
    public void ValidateRequiresMediaPathAndKindToAgree()
    {
        var pathWithoutKind = new SettingsV1
        {
            MediaPath = Path.GetFullPath("wallpaper.png"),
        };
        var kindWithoutPath = new SettingsV1
        {
            MediaKind = MediaKind.Image,
        };

        Assert.Throws<SettingsValidationException>(pathWithoutKind.Validate);
        Assert.Throws<SettingsValidationException>(kindWithoutPath.Validate);
    }

    [Fact]
    public void ValidateRejectsMoreThanEightRecentPathsWithoutEchoingPaths()
    {
        var privatePath = Path.GetFullPath("private-wallpaper.png");
        var settings = new SettingsV1
        {
            RecentMediaPaths = Enumerable.Range(0, 8)
                .Select(index => Path.GetFullPath($"recent-{index}.png"))
                .Append(privatePath)
                .ToArray(),
        };

        var exception = Assert.Throws<SettingsValidationException>(settings.Validate);

        Assert.DoesNotContain(privatePath, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsRelativeAndDuplicateRecentPaths()
    {
        var duplicatePath = Path.GetFullPath("duplicate.png");
        var settings = new SettingsV1
        {
            RecentMediaPaths = ["relative.png", duplicatePath, duplicatePath.ToUpperInvariant()],
        };

        var exception = Assert.Throws<SettingsValidationException>(settings.Validate);

        Assert.Contains(exception.Errors, error => error.Contains("absolute", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, error => error.Contains("duplicates", StringComparison.Ordinal));
    }
}
