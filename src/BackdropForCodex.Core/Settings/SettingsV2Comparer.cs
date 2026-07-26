using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Settings;

/// <summary>
/// Defines the three intentionally different equality boundaries used by the V2 workspace.
/// </summary>
public static class SettingsV2Comparer
{
    /// <summary>
    /// Compares every durable schema-two value. Collection order is significant except for
    /// region bindings, whose serialized meaning is a mapping.
    /// </summary>
    public static bool DurableEquals(SettingsV2? left, SettingsV2? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null ||
            left.SchemaVersion != right.SchemaVersion ||
            left.AcceptedCdpRisk != right.AcceptedCdpRisk ||
            !string.Equals(
                GetLastCompatibilityProfileId(left),
                GetLastCompatibilityProfileId(right),
                StringComparison.Ordinal) ||
            !ProfilesEqual(left.Profiles, right.Profiles) ||
            !MediaCatalogEqual(left.MediaCatalog, right.MediaCatalog) ||
            !left.RecentMediaIds.SequenceEqual(right.RecentMediaIds))
        {
            return false;
        }

        return RegionBindingsEqual(left.RegionBindings, right.RegionBindings);
    }

    /// <summary>
    /// Compares values whose divergence makes an editor draft dirty. Risk acceptance,
    /// recent-media ordering, and the deprecated compatibility marker are persisted
    /// independently and therefore do not make the wallpaper draft dirty.
    /// </summary>
    public static bool UiDirtyEquals(SettingsV2? left, SettingsV2? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null ||
            left.SchemaVersion != right.SchemaVersion ||
            !ProfilesEqual(left.Profiles, right.Profiles) ||
            !MediaCatalogEqual(left.MediaCatalog, right.MediaCatalog))
        {
            return false;
        }

        return RegionBindingsEqual(left.RegionBindings, right.RegionBindings);
    }

    /// <summary>
    /// Compares only the currently implemented Global runtime surface. Profile/media
    /// identifiers and future-facing settings are deliberately excluded. Any two empty
    /// Global profiles are equivalent because both produce the official Codex background.
    /// </summary>
    public static bool RuntimeEquivalent(SettingsV2? left, SettingsV2? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (!TryResolveGlobalSurface(left, out var leftProfile, out var leftMedia) ||
            !TryResolveGlobalSurface(right, out var rightProfile, out var rightMedia))
        {
            return false;
        }

        if (leftMedia is null || rightMedia is null)
        {
            return leftMedia is null && rightMedia is null;
        }

        return MediaRuntimeEquals(leftMedia, rightMedia) &&
               leftProfile!.Fit == rightProfile!.Fit &&
               leftProfile.FocusX.Equals(rightProfile.FocusX) &&
               leftProfile.FocusY.Equals(rightProfile.FocusY) &&
               leftProfile.PanelOpacity.Equals(rightProfile.PanelOpacity) &&
               leftProfile.BlurPx.Equals(rightProfile.BlurPx) &&
               leftProfile.DarkOverlay.Equals(rightProfile.DarkOverlay) &&
               leftProfile.LightOverlay.Equals(rightProfile.LightOverlay);
    }

    private static bool ProfilesEqual(
        IReadOnlyList<WallpaperProfile>? left,
        IReadOnlyList<WallpaperProfile>? right)
    {
        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool MediaCatalogEqual(
        IReadOnlyList<MediaReference>? left,
        IReadOnlyList<MediaReference>? right)
    {
        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool RegionBindingsEqual(
        IReadOnlyDictionary<SemanticRegion, Guid>? left,
        IReadOnlyDictionary<SemanticRegion, Guid>? right)
    {
        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var binding in left)
        {
            if (!right.TryGetValue(binding.Key, out var profileId) ||
                profileId != binding.Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveGlobalSurface(
        SettingsV2 settings,
        out WallpaperProfile? profile,
        out MediaReference? media)
    {
        profile = null;
        media = null;

        if (settings.Profiles is null ||
            settings.MediaCatalog is null ||
            settings.RegionBindings is null ||
            !settings.RegionBindings.TryGetValue(
                SemanticRegion.Global,
                out var globalProfileId))
        {
            return false;
        }

        profile = settings.Profiles.FirstOrDefault(
            candidate => candidate?.ProfileId == globalProfileId);
        if (profile is null)
        {
            return false;
        }

        if (profile.MediaId is not { } mediaId)
        {
            return true;
        }

        media = settings.MediaCatalog.FirstOrDefault(
            candidate => candidate?.MediaId == mediaId);
        return media is not null;
    }

    private static bool MediaRuntimeEquals(MediaReference left, MediaReference right)
    {
        if (left.SourceKind != right.SourceKind ||
            left.LastKnownKind != right.LastKnownKind)
        {
            return false;
        }

        var comparison = left.SourceKind is
            MediaSourceKind.LocalFile or MediaSourceKind.WallpaperEngineLocalProject
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            left.SourceIdentifier,
            right.SourceIdentifier,
            comparison);
    }

#pragma warning disable CS0618 // Equality must include the deprecated durable field.
    private static string? GetLastCompatibilityProfileId(SettingsV2 settings) =>
        settings.LastCompatibilityProfileId;
#pragma warning restore CS0618
}
