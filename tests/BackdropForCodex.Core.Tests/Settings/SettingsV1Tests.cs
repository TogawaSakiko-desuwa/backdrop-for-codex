using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Settings;

public sealed class SettingsV1Tests
{
    [Fact]
    public void CreateDefaultUsesReviewedDefaultsAndIsValid()
    {
        var settings = SettingsV1.CreateDefault();

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
        Assert.Empty(settings.RecentMediaPaths);
        Assert.False(settings.AcceptedCdpRisk);
        Assert.Null(settings.LastCompatibilityProfileId);
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
            "FocusX" => SettingsV1.CreateDefault() with { FocusX = value },
            "FocusY" => SettingsV1.CreateDefault() with { FocusY = value },
            "PanelOpacity" => SettingsV1.CreateDefault() with { PanelOpacity = value },
            "BlurPx" => SettingsV1.CreateDefault() with { BlurPx = value },
            "DarkOverlay" => SettingsV1.CreateDefault() with { DarkOverlay = value },
            "LightOverlay" => SettingsV1.CreateDefault() with { LightOverlay = value },
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
        };

        var exception = Assert.Throws<SettingsValidationException>(settings.Validate);

        Assert.Contains(propertyName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAcceptsInclusiveNumericBoundaries()
    {
        var settings = SettingsV1.CreateDefault() with
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
        var pathWithoutKind = SettingsV1.CreateDefault() with
        {
            MediaPath = Path.GetFullPath("wallpaper.png"),
        };
        var kindWithoutPath = SettingsV1.CreateDefault() with
        {
            MediaKind = MediaKind.Image,
        };

        Assert.Throws<SettingsValidationException>(pathWithoutKind.Validate);
        Assert.Throws<SettingsValidationException>(kindWithoutPath.Validate);
    }

    [Fact]
    public void AddRecentMediaPathDeduplicatesAndKeepsOnlyEightNewest()
    {
        var settings = SettingsV1.CreateDefault();
        var paths = Enumerable.Range(0, 10)
            .Select(index => Path.GetFullPath($"wallpaper-{index}.png"))
            .ToArray();

        foreach (var path in paths)
        {
            settings = settings.AddRecentMediaPath(path);
        }

        settings = settings.AddRecentMediaPath(paths[9].ToUpperInvariant());

        Assert.Equal(SettingsV1.MaximumRecentMediaPaths, settings.RecentMediaPaths.Count);
        Assert.Equal(paths[9], settings.RecentMediaPaths[0], ignoreCase: true);
        Assert.DoesNotContain(paths[0], settings.RecentMediaPaths);
        Assert.Equal(
            settings.RecentMediaPaths.Count,
            settings.RecentMediaPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        settings.Validate();
    }

    [Fact]
    public void RemoveRecentMediaPathUsesWindowsPathComparisonAndPreservesOrder()
    {
        var firstPath = Path.GetFullPath("first.png");
        var removedPath = Path.GetFullPath("removed.png");
        var lastPath = Path.GetFullPath("last.png");
        var settings = SettingsV1.CreateDefault() with
        {
            RecentMediaPaths = [firstPath, removedPath, lastPath],
        };

        var updated = settings.RemoveRecentMediaPath(removedPath.ToUpperInvariant());

        Assert.Equal(new[] { firstPath, lastPath }, updated.RecentMediaPaths);
        Assert.Equal(new[] { firstPath, removedPath, lastPath }, settings.RecentMediaPaths);
        updated.Validate();
    }

    [Fact]
    public void ClearRecentMediaPathsDoesNotChangeTheSelectedWallpaper()
    {
        var selectedPath = Path.GetFullPath("selected.png");
        var settings = SettingsV1.CreateDefault() with
        {
            MediaPath = selectedPath,
            MediaKind = MediaKind.Image,
            RecentMediaPaths = [selectedPath, Path.GetFullPath("other.png")],
        };

        var updated = settings.ClearRecentMediaPaths();

        Assert.Empty(updated.RecentMediaPaths);
        Assert.Equal(selectedPath, updated.MediaPath);
        Assert.Equal(MediaKind.Image, updated.MediaKind);
        updated.Validate();
    }

    [Fact]
    public void ValidateRejectsMoreThanEightRecentPathsWithoutEchoingPaths()
    {
        var privatePath = Path.GetFullPath("private-wallpaper.png");
        var settings = SettingsV1.CreateDefault() with
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
        var settings = SettingsV1.CreateDefault() with
        {
            RecentMediaPaths = ["relative.png", duplicatePath, duplicatePath.ToUpperInvariant()],
        };

        var exception = Assert.Throws<SettingsValidationException>(settings.Validate);

        Assert.Contains(exception.Errors, error => error.Contains("absolute", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, error => error.Contains("duplicates", StringComparison.Ordinal));
    }
}
