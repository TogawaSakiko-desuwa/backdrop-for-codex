using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class WallpaperProfileCardProjectionTests
{
    [Fact]
    public void CreateItemsProjectsOfficialAndLocalProfilesWithoutLeakingPathInAccessibleText()
    {
        var official = WallpaperProfile.CreateDefault("Official");
        var mediaId = Guid.CreateVersion7();
        var photo = WallpaperProfile.CreateDefault("Mountains") with
        {
            MediaId = mediaId,
        };
        var path = Path.GetFullPath(@"C:\private\family\mountains.png");
        var settings = CreateSettings(
            [official, photo],
            [
                new MediaReference
                {
                    MediaId = mediaId,
                    SourceKind = MediaSourceKind.LocalFile,
                    SourceIdentifier = path,
                    LastKnownKind = MediaKind.Image,
                },
            ],
            official.ProfileId);
        var preview = new RecordingPreviewService(isAvailable: true);
        var projection = new WallpaperProfileCardProjection(
            new DictionaryTextProvider(),
            preview);

        var items = projection.CreateItems(settings);

        Assert.Collection(
            items,
            item =>
            {
                Assert.Equal(official.ProfileId, item.ProfileId);
                Assert.True(item.IsOfficial);
                Assert.Null(item.PreviewPath);
                Assert.Equal("Official background", item.Subtitle);
                Assert.Equal("Official, Official background", item.AutomationName);
            },
            item =>
            {
                Assert.Equal(photo.ProfileId, item.ProfileId);
                Assert.False(item.IsOfficial);
                Assert.True(item.IsImagePreviewAvailable);
                Assert.False(item.IsMissing);
                Assert.Equal(path, item.PreviewPath);
                Assert.Equal("mountains.png", item.MediaDisplayName);
                Assert.Equal("Mountains, Image", item.AutomationName);
                Assert.Equal("More actions for Mountains", item.ActionsAutomationName);
                Assert.DoesNotContain(
                    "private",
                    item.AutomationName,
                    StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(
                    "private",
                    item.ActionsAutomationName,
                    StringComparison.OrdinalIgnoreCase);
            });
        Assert.Equal([path], preview.ProbedPaths);
    }

    [Fact]
    public void CreateItemsMarksUnavailableMediaAndSuppressesItsThumbnail()
    {
        var mediaId = Guid.CreateVersion7();
        var profile = WallpaperProfile.CreateDefault("Missing") with
        {
            MediaId = mediaId,
        };
        var settings = CreateSettings(
            [profile],
            [
                new MediaReference
                {
                    MediaId = mediaId,
                    SourceKind = MediaSourceKind.LocalFile,
                    SourceIdentifier = Path.GetFullPath(@"C:\removed\wallpaper.webm"),
                    LastKnownKind = MediaKind.Video,
                },
            ],
            profile.ProfileId);
        var projection = new WallpaperProfileCardProjection(
            new DictionaryTextProvider(),
            new RecordingPreviewService(isAvailable: false));

        var item = Assert.Single(projection.CreateItems(settings));

        Assert.True(item.IsMissing);
        Assert.False(item.IsVideo);
        Assert.False(item.IsImagePreviewAvailable);
        Assert.Equal("Media missing", item.Subtitle);
        Assert.Equal("Missing, Media missing", item.AutomationName);
    }

    [Fact]
    public void CreateItemsUsesLocalizedAccessibleLabels()
    {
        var profile = WallpaperProfile.CreateDefault("工作");
        var text = new DictionaryTextProvider(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Profile_Official"] = "官方背景",
                ["Profile_AutomationName"] = "{0}，{1}",
                ["Profile_ActionsAutomationName"] = "{0} 的更多操作",
            });
        var projection = new WallpaperProfileCardProjection(
            text,
            new RecordingPreviewService(isAvailable: true));

        var item = Assert.Single(
            projection.CreateItems(
                CreateSettings([profile], [], profile.ProfileId)));

        Assert.Equal("工作，官方背景", item.AutomationName);
        Assert.Equal("工作 的更多操作", item.ActionsAutomationName);
    }

    [Fact]
    public void CreateItemsProbesSharedMediaOnceAndSkipsOrphanedCatalogEntries()
    {
        var sharedMediaId = Guid.CreateVersion7();
        var orphanedMediaId = Guid.CreateVersion7();
        var first = WallpaperProfile.CreateDefault("First") with
        {
            MediaId = sharedMediaId,
        };
        var second = WallpaperProfile.CreateDefault("Second") with
        {
            MediaId = sharedMediaId,
        };
        var sharedPath = Path.GetFullPath(@"C:\wallpapers\shared.jpg");
        var settings = CreateSettings(
            [first, second],
            [
                new MediaReference
                {
                    MediaId = sharedMediaId,
                    SourceKind = MediaSourceKind.LocalFile,
                    SourceIdentifier = sharedPath,
                    LastKnownKind = MediaKind.Image,
                },
                new MediaReference
                {
                    MediaId = orphanedMediaId,
                    SourceKind = MediaSourceKind.LocalFile,
                    SourceIdentifier = Path.GetFullPath(@"C:\wallpapers\orphaned.png"),
                    LastKnownKind = MediaKind.Image,
                },
            ],
            first.ProfileId);
        var preview = new RecordingPreviewService(isAvailable: true);
        var projection = new WallpaperProfileCardProjection(
            new DictionaryTextProvider(),
            preview);

        var items = projection.CreateItems(settings);

        Assert.Equal(2, items.Count);
        Assert.Equal([sharedPath], preview.ProbedPaths);
    }

    [Fact]
    public void CreateItemsRejectsInvalidSettingsInsteadOfRenderingPartialState()
    {
        var projection = new WallpaperProfileCardProjection(
            new DictionaryTextProvider(),
            new RecordingPreviewService(isAvailable: true));
        var invalid = new SettingsV2();

        Assert.Throws<SettingsValidationException>(
            () => projection.CreateItems(invalid));
    }

    private static SettingsV2 CreateSettings(
        IReadOnlyList<WallpaperProfile> profiles,
        IReadOnlyList<MediaReference> media,
        Guid globalProfileId) =>
        new()
        {
            Profiles = profiles,
            MediaCatalog = media,
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = globalProfileId,
            },
        };

    private sealed class DictionaryTextProvider(
        IReadOnlyDictionary<string, string>? values = null) : IAppTextProvider
    {
        private readonly IReadOnlyDictionary<string, string> _values =
            values ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Profile_Official"] = "Official background",
                ["Profile_Media"] = "Media",
                ["Profile_MediaMissing"] = "Media missing",
                ["Profile_AutomationName"] = "{0}, {1}",
                ["Profile_ActionsAutomationName"] = "More actions for {0}",
                ["Media_Image"] = "Image",
                ["Media_Video"] = "Video",
            };

        public string GetString(string key) =>
            _values.TryGetValue(key, out var value) ? value : key;
    }

    private sealed class RecordingPreviewService(bool isAvailable)
        : ISafeMediaPreviewService
    {
        public List<string> ProbedPaths { get; } = [];

        public ISafeMediaPreviewLease Acquire(string mediaPath) =>
            throw new NotSupportedException();

        public bool IsAvailable(string mediaPath)
        {
            ProbedPaths.Add(mediaPath);
            return isAvailable;
        }
    }
}
