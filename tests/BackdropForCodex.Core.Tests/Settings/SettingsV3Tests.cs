using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Settings;

public sealed class SettingsV3Tests
{
    [Fact]
    public void StableEnumsKeepReviewedValues()
    {
        Assert.Equal(0, (int)SemanticRegion.Global);
        Assert.Equal(1, (int)SemanticRegion.Home);
        Assert.Equal(2, (int)SemanticRegion.Conversation);
        Assert.Equal(3, (int)SemanticRegion.CodeAndDiff);
        Assert.Equal(4, (int)SemanticRegion.SettingsAndOther);

        Assert.Equal(0, (int)PerformancePolicy.Automatic);
        Assert.Equal(1, (int)PerformancePolicy.PreferQuality);
        Assert.Equal(2, (int)PerformancePolicy.Balanced);
        Assert.Equal(3, (int)PerformancePolicy.PreferEfficiency);
    }

    [Fact]
    public void CreateDefaultBuildsAValidGlobalOnlyContract()
    {
        var settings = SettingsV3.CreateDefault();

        Assert.Equal(3, settings.SchemaVersion);
        var profile = Assert.Single(settings.Profiles);
        Assert.Equal(7, profile.ProfileId.Version);
        Assert.Equal("Global", profile.Name);
        Assert.Null(profile.MediaId);
        Assert.False(profile.SoundEnabled);
        Assert.Equal(0.5, profile.Volume);
        Assert.Equal(PerformancePolicy.Automatic, profile.PerformancePolicy);
        Assert.Empty(settings.MediaCatalog);
        Assert.Empty(settings.RecentMediaIds);
        Assert.Equal(profile.ProfileId, settings.RegionBindings[SemanticRegion.Global]);
        Assert.False(settings.AcceptedCdpRisk);
        Assert.Null(GetLegacyCompatibilityProfileId(settings));
        settings.Validate();
    }

    [Fact]
    public void CreateSnapshotPromotesLegacyMediaKindAndDisplayNameToV3Metadata()
    {
        var media = CreateMedia(Guid.CreateVersion7(), "legacy-image.png");
        var profile = WallpaperProfile.CreateDefault() with { MediaId = media.MediaId };
        var settings = new SettingsV3
        {
            Profiles = [profile],
            MediaCatalog = [media],
            RecentMediaIds = [media.MediaId],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = profile.ProfileId,
            },
        };

        var snapshot = settings.CreateSnapshot();

        var canonicalMedia = Assert.Single(snapshot.MediaCatalog);
        Assert.Equal(WallpaperContentKind.Image, canonicalMedia.LastKnownContentKind);
        Assert.Equal("legacy-image.png", canonicalMedia.LastKnownDisplayName);
    }

    [Fact]
    public void CreateSnapshotSanitizesPersistedLastKnownDisplayNames()
    {
        var media = CreateMedia(Guid.CreateVersion7(), "safe-image.png") with
        {
            LastKnownDisplayName = "  壁纸\r\n\u0001\u202e 🌌安全\u0085\u2066  ",
        };
        var profile = WallpaperProfile.CreateDefault() with { MediaId = media.MediaId };
        var settings = new SettingsV3
        {
            Profiles = [profile],
            MediaCatalog = [media],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = profile.ProfileId,
            },
        };

        var snapshot = settings.CreateSnapshot();

        Assert.Equal("壁纸 🌌安全", Assert.Single(snapshot.MediaCatalog)
            .LastKnownDisplayName);
    }

    [Fact]
    public void ValidateRejectsApplicationProjectsAndOversizedDisplayNames()
    {
        var application = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "123456",
            LastKnownContentKind = WallpaperContentKind.Application,
            LastKnownDisplayName = "Application wallpaper",
        };
        var profile = WallpaperProfile.CreateDefault() with
        {
            MediaId = application.MediaId,
        };
        var settings = new SettingsV3
        {
            Profiles = [profile],
            MediaCatalog = [application],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = profile.ProfileId,
            },
        };
        var oversizedName = application with
        {
            LastKnownContentKind = WallpaperContentKind.Scene,
            LastKnownDisplayName = new string('x', MediaReference.MaximumDisplayNameLength + 1),
        };

        Assert.Contains(
            Assert.Throws<SettingsValidationException>(settings.Validate).Errors,
            error => error.Contains("Application", StringComparison.Ordinal));
        Assert.Contains(
            oversizedName.GetValidationErrors(),
            error => error.Contains("256", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveProfileFallsBackToGlobalForUnboundAndUnknownRegions()
    {
        var settings = SettingsV3.CreateDefault();
        var global = Assert.Single(settings.Profiles);

        Assert.Same(global, settings.ResolveProfile(SemanticRegion.Conversation));
        Assert.Same(global, settings.ResolveProfile((SemanticRegion)999));
    }

    [Fact]
    public void ValidateRejectsNonVersion7AndBrokenReferences()
    {
        var profile = WallpaperProfile.CreateDefault() with
        {
            ProfileId = Guid.NewGuid(),
            MediaId = Guid.NewGuid(),
        };
        var settings = new SettingsV3
        {
            Profiles = [profile],
            RecentMediaIds = [Guid.NewGuid()],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = Guid.NewGuid(),
            },
        };

        var exception = Assert.Throws<SettingsValidationException>(settings.Validate);

        Assert.Contains(
            exception.Errors,
            error => error.Contains("UUIDv7", StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            error => error.Contains("MediaCatalog", StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            error => error.Contains("existing profile", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRejectsDuplicateIdentifiersAndMissingGlobalFallback()
    {
        var profileId = Guid.CreateVersion7();
        var mediaId = Guid.CreateVersion7();
        var firstProfile = WallpaperProfile.CreateDefault("First") with
        {
            ProfileId = profileId,
            MediaId = mediaId,
        };
        var duplicateProfile = firstProfile with { Name = "Duplicate" };
        var firstMedia = CreateMedia(mediaId, "first.png");
        var duplicateMedia = firstMedia with
        {
            SourceIdentifier = Path.GetFullPath("second.png"),
        };
        var settings = new SettingsV3
        {
            Profiles = [firstProfile, duplicateProfile],
            MediaCatalog = [firstMedia, duplicateMedia],
            RecentMediaIds = [mediaId, mediaId],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Home] = profileId,
            },
        };

        var exception = Assert.Throws<SettingsValidationException>(settings.Validate);

        Assert.Contains(
            exception.Errors,
            error => error.Contains("duplicate identifiers", StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            error => error.Contains("duplicates", StringComparison.Ordinal));
        Assert.Contains(
            exception.Errors,
            error => error.Contains("Global fallback", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("FocusX", -0.01)]
    [InlineData("FocusY", 1.01)]
    [InlineData("PanelOpacity", 0.59)]
    [InlineData("BlurPx", 24.01)]
    [InlineData("DarkOverlay", double.NaN)]
    [InlineData("LightOverlay", 1.01)]
    [InlineData("Volume", double.PositiveInfinity)]
    public void WallpaperProfileRejectsInvalidNumericValues(
        string propertyName,
        double value)
    {
        var profile = propertyName switch
        {
            "FocusX" => WallpaperProfile.CreateDefault() with { FocusX = value },
            "FocusY" => WallpaperProfile.CreateDefault() with { FocusY = value },
            "PanelOpacity" => WallpaperProfile.CreateDefault() with { PanelOpacity = value },
            "BlurPx" => WallpaperProfile.CreateDefault() with { BlurPx = value },
            "DarkOverlay" => WallpaperProfile.CreateDefault() with { DarkOverlay = value },
            "LightOverlay" => WallpaperProfile.CreateDefault() with { LightOverlay = value },
            "Volume" => WallpaperProfile.CreateDefault() with { Volume = value },
            _ => throw new ArgumentOutOfRangeException(nameof(propertyName)),
        };

        var exception = Assert.Throws<SettingsValidationException>(profile.Validate);

        Assert.Contains(propertyName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAcceptsAllStableRegionsBoundToSharedProfiles()
    {
        var global = WallpaperProfile.CreateDefault("Shared");
        var settings = new SettingsV3
        {
            Profiles = [global],
            RegionBindings = Enum.GetValues<SemanticRegion>()
                .ToDictionary(region => region, _ => global.ProfileId),
        };

        settings.Validate();
    }

    private static MediaReference CreateMedia(Guid mediaId, string fileName) =>
        new()
        {
            MediaId = mediaId,
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = Path.GetFullPath(fileName),
            LastKnownKind = MediaKind.Image,
        };

#pragma warning disable CS0618 // Tests intentionally exercise the deprecated persistence field.
    private static SettingsV3 WithLegacyCompatibilityProfileId(
        SettingsV3 settings,
        string? profileId) =>
        settings with { LastCompatibilityProfileId = profileId };

    private static string? GetLegacyCompatibilityProfileId(SettingsV3 settings) =>
        settings.LastCompatibilityProfileId;
#pragma warning restore CS0618
}
