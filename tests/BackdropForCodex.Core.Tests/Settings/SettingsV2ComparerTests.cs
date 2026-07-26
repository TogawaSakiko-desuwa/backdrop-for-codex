using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Settings;

public sealed class SettingsV2ComparerTests
{
    [Fact]
    public void CreateSnapshotNormalizesAndIsolatesEveryCollection()
    {
        var mediaId = Guid.CreateVersion7();
        var profile = WallpaperProfile.CreateDefault("  Trim me  ") with
        {
            MediaId = mediaId,
        };
        var media = CreateMedia(mediaId, Path.Combine("media", "..", "wallpaper.png"));
        var profiles = new List<WallpaperProfile> { profile };
        var catalog = new List<MediaReference> { media };
        var recents = new List<Guid> { mediaId };
        var bindings = new Dictionary<SemanticRegion, Guid>
        {
            [SemanticRegion.Global] = profile.ProfileId,
        };
        var settings = new SettingsV2
        {
            Profiles = profiles,
            MediaCatalog = catalog,
            RecentMediaIds = recents,
            RegionBindings = bindings,
        };

        var snapshot = settings.CreateSnapshot();

        profiles.Clear();
        catalog.Clear();
        recents.Clear();
        bindings.Clear();

        Assert.Equal("Trim me", Assert.Single(snapshot.Profiles).Name);
        Assert.Equal(Path.GetFullPath("wallpaper.png"), Assert.Single(snapshot.MediaCatalog).SourceIdentifier);
        Assert.Equal(mediaId, Assert.Single(snapshot.RecentMediaIds));
        Assert.Equal(profile.ProfileId, snapshot.RegionBindings[SemanticRegion.Global]);
        Assert.Throws<NotSupportedException>(
            () => ((IList<WallpaperProfile>)snapshot.Profiles).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<SemanticRegion, Guid>)snapshot.RegionBindings).Clear());
    }

    [Fact]
    public void DurableEqualityIncludesEveryPersistedValue()
    {
        var firstMedia = CreateMedia(Guid.CreateVersion7(), "first.png");
        var secondMedia = CreateMedia(Guid.CreateVersion7(), "second.png");
        var profile = WallpaperProfile.CreateDefault() with
        {
            MediaId = firstMedia.MediaId,
        };
        var original = WithLegacyCompatibilityProfileId(
            new SettingsV2
            {
                Profiles = [profile],
                MediaCatalog = [firstMedia, secondMedia],
                RecentMediaIds = [firstMedia.MediaId, secondMedia.MediaId],
                RegionBindings = new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] = profile.ProfileId,
                },
                AcceptedCdpRisk = true,
            }.CreateSnapshot(),
            "legacy-marker");

        Assert.True(SettingsV2Comparer.DurableEquals(original, original.CreateSnapshot()));
        Assert.False(
            SettingsV2Comparer.DurableEquals(
                original,
                original with { AcceptedCdpRisk = false }));
        Assert.False(
            SettingsV2Comparer.DurableEquals(
                original,
                original with
                {
                    RecentMediaIds =
                    [
                        secondMedia.MediaId,
                        firstMedia.MediaId,
                    ],
                }));
        Assert.False(
            SettingsV2Comparer.DurableEquals(
                original,
                WithLegacyCompatibilityProfileId(original, "changed-marker")));
    }

    [Fact]
    public void UiDirtyEqualityIgnoresIndependentlyPersistedMetadata()
    {
        var firstMedia = CreateMedia(Guid.CreateVersion7(), "first.png");
        var secondMedia = CreateMedia(Guid.CreateVersion7(), "second.png");
        var profile = WallpaperProfile.CreateDefault() with
        {
            MediaId = firstMedia.MediaId,
        };
        var original = WithLegacyCompatibilityProfileId(
            new SettingsV2
            {
                Profiles = [profile],
                MediaCatalog = [firstMedia, secondMedia],
                RecentMediaIds = [firstMedia.MediaId, secondMedia.MediaId],
                RegionBindings = new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] = profile.ProfileId,
                },
                AcceptedCdpRisk = true,
            }.CreateSnapshot(),
            "legacy-marker");
        var metadataOnly = WithLegacyCompatibilityProfileId(
            original with
            {
                AcceptedCdpRisk = false,
                RecentMediaIds =
                [
                    secondMedia.MediaId,
                    firstMedia.MediaId,
                ],
            },
            "different-marker");

        Assert.True(SettingsV2Comparer.UiDirtyEquals(original, metadataOnly));
        Assert.False(
            SettingsV2Comparer.UiDirtyEquals(
                original,
                original with
                {
                    Profiles =
                    [
                        profile with { Name = "Renamed" },
                    ],
                }));
        Assert.False(
            SettingsV2Comparer.UiDirtyEquals(
                original,
                original with
                {
                    RegionBindings = new Dictionary<SemanticRegion, Guid>
                    {
                        [SemanticRegion.Global] = profile.ProfileId,
                        [SemanticRegion.Home] = profile.ProfileId,
                    },
                }));
    }

    [Fact]
    public void RuntimeEqualityUsesOnlyEffectiveGlobalMediaAndVisualSettings()
    {
        var mediaPath = Path.GetFullPath("same.png");
        var firstMedia = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = mediaPath,
            LastKnownKind = MediaKind.Image,
        };
        var firstGlobal = WallpaperProfile.CreateDefault("First") with
        {
            MediaId = firstMedia.MediaId,
            SoundEnabled = false,
            Volume = 0.1,
            PerformancePolicy = PerformancePolicy.PreferQuality,
        };
        var first = new SettingsV2
        {
            Profiles = [firstGlobal],
            MediaCatalog = [firstMedia],
            RecentMediaIds = [firstMedia.MediaId],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = firstGlobal.ProfileId,
            },
            AcceptedCdpRisk = false,
        };

        var secondMedia = firstMedia with { MediaId = Guid.CreateVersion7() };
        var hidden = WallpaperProfile.CreateDefault("Hidden future profile");
        var secondGlobal = firstGlobal with
        {
            ProfileId = Guid.CreateVersion7(),
            Name = "Different name",
            MediaId = secondMedia.MediaId,
            SoundEnabled = true,
            Volume = 0.9,
            PerformancePolicy = PerformancePolicy.PreferEfficiency,
        };
        var second = new SettingsV2
        {
            Profiles = [secondGlobal, hidden],
            MediaCatalog = [secondMedia],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = secondGlobal.ProfileId,
                [SemanticRegion.Conversation] = hidden.ProfileId,
            },
            AcceptedCdpRisk = true,
        };

        Assert.True(SettingsV2Comparer.RuntimeEquivalent(first, second));
        Assert.False(
            SettingsV2Comparer.RuntimeEquivalent(
                first,
                second with
                {
                    Profiles =
                    [
                        secondGlobal with { BlurPx = secondGlobal.BlurPx + 1 },
                        hidden,
                    ],
                }));
        Assert.False(
            SettingsV2Comparer.RuntimeEquivalent(
                first,
                second with
                {
                    MediaCatalog =
                    [
                        secondMedia with { LastKnownKind = MediaKind.Video },
                    ],
                }));
    }

    [Fact]
    public void EmptyGlobalProfilesAreRuntimeEquivalentRegardlessOfStyle()
    {
        var first = SettingsV2.CreateDefault();
        var firstProfile = first.ResolveProfile(SemanticRegion.Global);
        var secondProfile = WallpaperProfile.CreateDefault("Official") with
        {
            Fit = WallpaperFit.Stretch,
            FocusX = 0,
            FocusY = 1,
            PanelOpacity = 0.95,
            BlurPx = 0,
            DarkOverlay = 1,
            LightOverlay = 1,
            SoundEnabled = true,
            PerformancePolicy = PerformancePolicy.PreferEfficiency,
        };
        var second = new SettingsV2
        {
            Profiles = [secondProfile],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = secondProfile.ProfileId,
            },
            AcceptedCdpRisk = true,
        };

        Assert.Null(firstProfile.MediaId);
        Assert.True(SettingsV2Comparer.RuntimeEquivalent(first, second));
    }

    private static MediaReference CreateMedia(Guid mediaId, string fileName) =>
        new()
        {
            MediaId = mediaId,
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = Path.GetFullPath(fileName),
            LastKnownKind = MediaKind.Image,
        };

#pragma warning disable CS0618 // Tests intentionally cover the deprecated durable field.
    private static SettingsV2 WithLegacyCompatibilityProfileId(
        SettingsV2 settings,
        string? profileId) =>
        settings with { LastCompatibilityProfileId = profileId };
#pragma warning restore CS0618
}
