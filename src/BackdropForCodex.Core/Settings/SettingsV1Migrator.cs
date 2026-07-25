using System.Collections.ObjectModel;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Settings;

internal static class SettingsV1Migrator
{
    internal static SettingsV2 Migrate(SettingsV1 version1)
    {
        ArgumentNullException.ThrowIfNull(version1);
        version1.Validate();

        var mediaByPath = new Dictionary<string, MediaReference>(
            StringComparer.OrdinalIgnoreCase);
        var mediaCatalog = new List<MediaReference>();

        MediaReference AddMedia(string sourcePath, MediaKind lastKnownKind)
        {
            var normalizedPath = Path.GetFullPath(sourcePath);
            if (mediaByPath.TryGetValue(normalizedPath, out var existing))
            {
                return existing;
            }

            var media = new MediaReference
            {
                MediaId = Guid.CreateVersion7(),
                SourceKind = MediaSourceKind.LocalFile,
                SourceIdentifier = normalizedPath,
                LastKnownKind = lastKnownKind,
            }.Snapshot();
            mediaByPath.Add(normalizedPath, media);
            mediaCatalog.Add(media);
            return media;
        }

        MediaReference? selectedMedia = null;
        if (version1.MediaPath is not null)
        {
            selectedMedia = AddMedia(version1.MediaPath, version1.MediaKind);
        }

        var recentMediaIds = new List<Guid>(version1.RecentMediaPaths.Count);
        var seenRecentMediaIds = new HashSet<Guid>();
        foreach (var recentPath in version1.RecentMediaPaths)
        {
            var mediaId = AddMedia(recentPath, MediaKind.None).MediaId;
            if (seenRecentMediaIds.Add(mediaId))
            {
                recentMediaIds.Add(mediaId);
            }
        }

        var profile = new WallpaperProfile
        {
            ProfileId = Guid.CreateVersion7(),
            Name = "Global",
            MediaId = selectedMedia?.MediaId,
            Fit = version1.Fit,
            FocusX = version1.FocusX,
            FocusY = version1.FocusY,
            PanelOpacity = version1.PanelOpacity,
            BlurPx = version1.BlurPx,
            DarkOverlay = version1.DarkOverlay,
            LightOverlay = version1.LightOverlay,
            SoundEnabled = false,
            Volume = 0.5,
            PerformancePolicy = PerformancePolicy.Automatic,
        };

        return new SettingsV2
        {
            Profiles = new ReadOnlyCollection<WallpaperProfile>([profile]),
            MediaCatalog = new ReadOnlyCollection<MediaReference>(mediaCatalog),
            RecentMediaIds = new ReadOnlyCollection<Guid>(recentMediaIds),
            RegionBindings = new ReadOnlyDictionary<SemanticRegion, Guid>(
                new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] = profile.ProfileId,
                }),
            AcceptedCdpRisk = version1.AcceptedCdpRisk,
#pragma warning disable CS0618 // Preserve the deprecated persisted value during V1 migration.
            LastCompatibilityProfileId = version1.LastCompatibilityProfileId,
#pragma warning restore CS0618
        }.Snapshot();
    }
}
