using System.Collections.ObjectModel;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Settings;

/// <summary>
/// Transitional projection for the existing V1-shaped editor.
/// Disk persistence remains exclusively owned by <see cref="ISettingsRepository"/>.
/// </summary>
public static class SettingsV1Projection
{
    /// <summary>
    /// Projects the Global V2 profile into the legacy editor shape.
    /// Non-local recent entries stay outside the legacy view and are preserved by
    /// <see cref="ApplyGlobal"/>.
    /// </summary>
    public static SettingsV1 ProjectGlobal(SettingsV2 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.Snapshot();
        var profile = snapshot.ResolveProfile(SemanticRegion.Global);

        string? mediaPath = null;
        var mediaKind = MediaKind.None;
        if (profile.MediaId is { } mediaId)
        {
            var media = snapshot.FindMedia(mediaId)
                ?? throw new SettingsProjectionException(
                    "The Global profile refers to missing media.");
            if (media.SourceKind != MediaSourceKind.LocalFile)
            {
                throw new SettingsProjectionException(
                    "The legacy editor can only project a local-file Global wallpaper.");
            }

            if (media.LastKnownKind == MediaKind.None)
            {
                throw new SettingsProjectionException(
                    "The selected local media does not have a known image or video kind.");
            }

            mediaPath = media.SourceIdentifier;
            mediaKind = media.LastKnownKind;
        }

        var recentPaths = new List<string>(SettingsV1.MaximumRecentMediaPaths);
        foreach (var recentMediaId in snapshot.RecentMediaIds)
        {
            var recentMedia = snapshot.FindMedia(recentMediaId);
            if (recentMedia?.SourceKind != MediaSourceKind.LocalFile ||
                recentPaths.Contains(
                    recentMedia.SourceIdentifier,
                    StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            recentPaths.Add(recentMedia.SourceIdentifier);
            if (recentPaths.Count == SettingsV1.MaximumRecentMediaPaths)
            {
                break;
            }
        }

        return new SettingsV1
        {
            MediaPath = mediaPath,
            MediaKind = mediaKind,
            Fit = profile.Fit,
            FocusX = profile.FocusX,
            FocusY = profile.FocusY,
            PanelOpacity = profile.PanelOpacity,
            BlurPx = profile.BlurPx,
            DarkOverlay = profile.DarkOverlay,
            LightOverlay = profile.LightOverlay,
            RecentMediaPaths = new ReadOnlyCollection<string>(recentPaths),
            AcceptedCdpRisk = snapshot.AcceptedCdpRisk,
#pragma warning disable CS0618 // Keep the deprecated value in the legacy projection for round-tripping.
            LastCompatibilityProfileId = snapshot.LastCompatibilityProfileId,
#pragma warning restore CS0618
        }.Snapshot();
    }

    /// <summary>
    /// Applies the legacy editor's wallpaper fields to the Global profile.
    /// Other profiles, existing catalog entries, bindings, non-local recent items,
    /// audio/performance preferences, and top-level risk/compatibility state are preserved.
    /// </summary>
    public static SettingsV2 ApplyGlobal(
        SettingsV2 settings,
        SettingsV1 globalSettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(globalSettings);
        var snapshot = settings.Snapshot();
        var legacySnapshot = globalSettings.SnapshotForSave();

        var mediaCatalog = snapshot.MediaCatalog.ToList();
        var localMediaByPath = new Dictionary<string, MediaReference>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var media in mediaCatalog.Where(
                     media => media.SourceKind == MediaSourceKind.LocalFile))
        {
            localMediaByPath.TryAdd(media.SourceIdentifier, media);
        }

        MediaReference GetOrAddLocalMedia(string sourcePath, MediaKind lastKnownKind)
        {
            var normalizedPath = Path.GetFullPath(sourcePath);
            if (localMediaByPath.TryGetValue(normalizedPath, out var existing))
            {
                if (lastKnownKind != MediaKind.None &&
                    existing.LastKnownKind != lastKnownKind)
                {
                    var updated = existing with { LastKnownKind = lastKnownKind };
                    mediaCatalog[mediaCatalog.IndexOf(existing)] = updated;
                    localMediaByPath[normalizedPath] = updated;
                    return updated;
                }

                return existing;
            }

            var added = new MediaReference
            {
                MediaId = Guid.CreateVersion7(),
                SourceKind = MediaSourceKind.LocalFile,
                SourceIdentifier = normalizedPath,
                LastKnownKind = lastKnownKind,
            }.Snapshot();
            mediaCatalog.Add(added);
            localMediaByPath.Add(normalizedPath, added);
            return added;
        }

        MediaReference? selectedMedia = null;
        if (legacySnapshot.MediaPath is not null)
        {
            selectedMedia = GetOrAddLocalMedia(
                legacySnapshot.MediaPath,
                legacySnapshot.MediaKind);
        }

        var requestedLocalRecentIds = new List<Guid>(SettingsV2.MaximumRecentMediaIds);
        var seenLocalRecentIds = new HashSet<Guid>();
        foreach (var recentPath in legacySnapshot.RecentMediaPaths)
        {
            var mediaId = GetOrAddLocalMedia(recentPath, MediaKind.None).MediaId;
            if (seenLocalRecentIds.Add(mediaId))
            {
                requestedLocalRecentIds.Add(mediaId);
            }
        }

        var hiddenRecentIds = snapshot.RecentMediaIds
            .Where(recentId =>
                snapshot.FindMedia(recentId)?.SourceKind != MediaSourceKind.LocalFile)
            .ToArray();
        var hiddenRecentIdSet = hiddenRecentIds.ToHashSet();
        var localCapacity = SettingsV2.MaximumRecentMediaIds - hiddenRecentIds.Length;
        if (requestedLocalRecentIds.Count > localCapacity)
        {
            requestedLocalRecentIds.RemoveRange(
                localCapacity,
                requestedLocalRecentIds.Count - localCapacity);
        }

        // Keep hidden source entries in their prior relative slots while mapping the V1-visible
        // local order onto the remaining slots. Hidden entries always win capacity so a legacy
        // editor action cannot silently evict data it cannot display.
        var recentMediaIds = new List<Guid>(SettingsV2.MaximumRecentMediaIds);
        var nextLocalIndex = 0;
        foreach (var existingRecentId in snapshot.RecentMediaIds)
        {
            if (hiddenRecentIdSet.Contains(existingRecentId))
            {
                recentMediaIds.Add(existingRecentId);
                continue;
            }

            if (nextLocalIndex < requestedLocalRecentIds.Count)
            {
                recentMediaIds.Add(requestedLocalRecentIds[nextLocalIndex]);
                nextLocalIndex++;
            }
        }

        while (nextLocalIndex < requestedLocalRecentIds.Count)
        {
            recentMediaIds.Add(requestedLocalRecentIds[nextLocalIndex]);
            nextLocalIndex++;
        }

        var globalProfile = snapshot.ResolveProfile(SemanticRegion.Global);
        var updatedGlobalProfile = globalProfile with
        {
            MediaId = selectedMedia?.MediaId,
            Fit = legacySnapshot.Fit,
            FocusX = legacySnapshot.FocusX,
            FocusY = legacySnapshot.FocusY,
            PanelOpacity = legacySnapshot.PanelOpacity,
            BlurPx = legacySnapshot.BlurPx,
            DarkOverlay = legacySnapshot.DarkOverlay,
            LightOverlay = legacySnapshot.LightOverlay,
        };
        var profiles = snapshot.Profiles
            .Select(profile => profile.ProfileId == globalProfile.ProfileId
                ? updatedGlobalProfile
                : profile)
            .ToArray();

        // LastCompatibilityProfileId is deliberately omitted. A V1-shaped editor may
        // round-trip the deprecated field, but it cannot replace the V2 persisted value.
        var updated = snapshot with
        {
            Profiles = new ReadOnlyCollection<WallpaperProfile>(profiles),
            MediaCatalog = new ReadOnlyCollection<MediaReference>(mediaCatalog),
            RecentMediaIds = new ReadOnlyCollection<Guid>(recentMediaIds),
        };
        return updated.Snapshot();
    }
}

public sealed class SettingsProjectionException : InvalidOperationException
{
    public SettingsProjectionException(
        string message,
        bool hasVersion1Backup = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        HasVersion1Backup = hasVersion1Backup;
    }

    public bool HasVersion1Backup { get; }
}
