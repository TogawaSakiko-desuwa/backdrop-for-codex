using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Settings;

public sealed class SettingsV2Tests
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
        var settings = SettingsV2.CreateDefault();

        Assert.Equal(2, settings.SchemaVersion);
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
        Assert.Null(settings.LastCompatibilityProfileId);
        settings.Validate();
    }

    [Fact]
    public void ResolveProfileFallsBackToGlobalForUnboundAndUnknownRegions()
    {
        var settings = SettingsV2.CreateDefault();
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
        var settings = new SettingsV2
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
        var settings = new SettingsV2
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
        var settings = new SettingsV2
        {
            Profiles = [global],
            RegionBindings = Enum.GetValues<SemanticRegion>()
                .ToDictionary(region => region, _ => global.ProfileId),
        };

        settings.Validate();
    }

    [Fact]
    public void LegacyProjectionRoundTripUpdatesOnlyGlobalEditorState()
    {
        var originalLocal = CreateMedia(Guid.CreateVersion7(), "original.png");
        var workshop = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "123456",
            LastKnownKind = MediaKind.Video,
        };
        var global = WallpaperProfile.CreateDefault("Global custom") with
        {
            MediaId = originalLocal.MediaId,
            SoundEnabled = true,
            Volume = 0.7,
            PerformancePolicy = PerformancePolicy.PreferEfficiency,
        };
        var other = WallpaperProfile.CreateDefault("Other") with
        {
            MediaId = workshop.MediaId,
        };
        var settings = new SettingsV2
        {
            Profiles = [global, other],
            MediaCatalog = [originalLocal, workshop],
            RecentMediaIds = [originalLocal.MediaId, workshop.MediaId],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = global.ProfileId,
                [SemanticRegion.Home] = other.ProfileId,
            },
            AcceptedCdpRisk = true,
            LastCompatibilityProfileId = "keep-this-profile",
        };

        var projected = SettingsV1Projection.ProjectGlobal(settings);
        var replacementPath = Path.GetFullPath("replacement.webm");
        var legacyEdit = projected with
        {
            MediaPath = replacementPath,
            MediaKind = MediaKind.Video,
            Fit = WallpaperFit.Contain,
            FocusX = 0.1,
            FocusY = 0.9,
            RecentMediaPaths = [replacementPath],
            AcceptedCdpRisk = false,
            LastCompatibilityProfileId = "must-not-overwrite",
        };

        var updated = SettingsV1Projection.ApplyGlobal(settings, legacyEdit);

        Assert.Equal(settings.AcceptedCdpRisk, updated.AcceptedCdpRisk);
        Assert.Equal(
            settings.LastCompatibilityProfileId,
            updated.LastCompatibilityProfileId);
        Assert.Equal(settings.RegionBindings, updated.RegionBindings);
        Assert.Equal(other, updated.Profiles.Single(profile => profile.ProfileId == other.ProfileId));
        Assert.Contains(
            updated.MediaCatalog,
            media => media.MediaId == originalLocal.MediaId);
        Assert.Contains(
            updated.MediaCatalog,
            media => media.MediaId == workshop.MediaId);
        var updatedGlobal = updated.ResolveProfile(SemanticRegion.Global);
        Assert.Equal("Global custom", updatedGlobal.Name);
        Assert.True(updatedGlobal.SoundEnabled);
        Assert.Equal(0.7, updatedGlobal.Volume);
        Assert.Equal(PerformancePolicy.PreferEfficiency, updatedGlobal.PerformancePolicy);
        Assert.Equal(WallpaperFit.Contain, updatedGlobal.Fit);
        Assert.Equal(0.1, updatedGlobal.FocusX);
        Assert.Equal(0.9, updatedGlobal.FocusY);
        Assert.Equal(
            replacementPath,
            updated.FindMedia(updatedGlobal.MediaId!.Value)!.SourceIdentifier);
        Assert.Equal(
            new[]
            {
                updatedGlobal.MediaId.Value,
                workshop.MediaId,
            },
            updated.RecentMediaIds);
    }

    [Fact]
    public void LegacyProjectionPreservesHiddenRecentSourcesAtCapacity()
    {
        var localMedia = Enumerable.Range(0, 7)
            .Select(index => CreateMedia(
                Guid.CreateVersion7(),
                $"recent-{index}.png"))
            .ToArray();
        var workshop = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "987654",
            LastKnownKind = MediaKind.Video,
        };
        var global = WallpaperProfile.CreateDefault();
        var settings = new SettingsV2
        {
            Profiles = [global],
            MediaCatalog = [.. localMedia, workshop],
            RecentMediaIds =
            [
                .. localMedia.Select(media => media.MediaId),
                workshop.MediaId,
            ],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = global.ProfileId,
            },
        };
        var projected = SettingsV1Projection.ProjectGlobal(settings);
        var newestPath = Path.GetFullPath("newest.png");

        var updated = SettingsV1Projection.ApplyGlobal(
            settings,
            projected with
            {
                RecentMediaPaths = [newestPath, .. projected.RecentMediaPaths],
            });

        Assert.Equal(SettingsV2.MaximumRecentMediaIds, updated.RecentMediaIds.Count);
        Assert.Contains(workshop.MediaId, updated.RecentMediaIds);
        Assert.Equal(
            SettingsV2.MaximumRecentMediaIds - 1,
            updated.RecentMediaIds.Count(
                mediaId =>
                    updated.FindMedia(mediaId)?.SourceKind ==
                    MediaSourceKind.LocalFile));
        Assert.Equal(
            newestPath,
            updated.FindMedia(updated.RecentMediaIds[0])?.SourceIdentifier);
    }

    [Fact]
    public void LegacyProjectionDeduplicatesPathAliasesAfterNormalization()
    {
        var settings = SettingsV2.CreateDefault();
        var canonicalPath = Path.GetFullPath(Path.Combine("wallpapers", "same.png"));
        var aliasPath = Path.Combine(
            Path.GetDirectoryName(canonicalPath)!,
            "unused",
            "..",
            Path.GetFileName(canonicalPath));
        var legacy = SettingsV1Projection.ProjectGlobal(settings) with
        {
            RecentMediaPaths = [aliasPath, canonicalPath],
        };

        var updated = SettingsV1Projection.ApplyGlobal(settings, legacy);

        var recentId = Assert.Single(updated.RecentMediaIds);
        Assert.Equal(canonicalPath, updated.FindMedia(recentId)?.SourceIdentifier);
    }

    [Fact]
    public void ProjectGlobalRefusesToMisrepresentNonLocalSelectedMedia()
    {
        var workshop = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "42",
            LastKnownKind = MediaKind.Video,
        };
        var profile = WallpaperProfile.CreateDefault() with
        {
            MediaId = workshop.MediaId,
        };
        var settings = new SettingsV2
        {
            Profiles = [profile],
            MediaCatalog = [workshop],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = profile.ProfileId,
            },
        };

        Assert.Throws<SettingsProjectionException>(
            () => SettingsV1Projection.ProjectGlobal(settings));
    }

    [Fact]
    public void ProjectGlobalRefusesToGuessAnUnknownLocalMediaKind()
    {
        var media = CreateMedia(Guid.CreateVersion7(), "unresolved.png") with
        {
            LastKnownKind = MediaKind.None,
        };
        var profile = WallpaperProfile.CreateDefault() with
        {
            MediaId = media.MediaId,
        };
        var settings = new SettingsV2
        {
            Profiles = [profile],
            MediaCatalog = [media],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = profile.ProfileId,
            },
        };

        Assert.Throws<SettingsProjectionException>(
            () => SettingsV1Projection.ProjectGlobal(settings));
    }

    private static MediaReference CreateMedia(Guid mediaId, string fileName) =>
        new()
        {
            MediaId = mediaId,
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = Path.GetFullPath(fileName),
            LastKnownKind = MediaKind.Image,
        };
}
