using System.Collections.ObjectModel;
using System.ComponentModel;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Settings;

/// <summary>
/// Version three of the durable settings contract. Provider discovery paths and runtime process,
/// window, renderer, and thumbnail state are intentionally excluded from this document.
/// </summary>
public sealed record SettingsV3
{
    public const int CurrentSchemaVersion = 3;

    public const int MaximumRecentMediaIds = 8;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<WallpaperProfile> Profiles { get; init; } =
        Array.Empty<WallpaperProfile>();

    public IReadOnlyList<MediaReference> MediaCatalog { get; init; } =
        Array.Empty<MediaReference>();

    public IReadOnlyList<Guid> RecentMediaIds { get; init; } = Array.Empty<Guid>();

    public IReadOnlyDictionary<SemanticRegion, Guid> RegionBindings { get; init; } =
        new ReadOnlyDictionary<SemanticRegion, Guid>(
            new Dictionary<SemanticRegion, Guid>());

    public bool AcceptedCdpRisk { get; init; }

    /// <summary>
    /// Retained only to migrate and round-trip settings written by older releases.
    /// It does not participate in compatibility selection or runtime behavior.
    /// </summary>
    [Obsolete(
        "Retained only for backward-compatible settings round-tripping. " +
        "Do not use this property for runtime behavior.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? LastCompatibilityProfileId { get; init; }

    public static SettingsV3 CreateDefault()
    {
        var profile = WallpaperProfile.CreateDefault();
        return new SettingsV3
        {
            Profiles = new ReadOnlyCollection<WallpaperProfile>([profile]),
            RegionBindings = new ReadOnlyDictionary<SemanticRegion, Guid>(
                new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] = profile.ProfileId,
                }),
        };
    }

    public WallpaperProfile ResolveProfile(SemanticRegion region)
    {
        if (!Enum.IsDefined(region))
        {
            region = SemanticRegion.Global;
        }

        var profileId = RegionBindings.TryGetValue(region, out var boundProfileId)
            ? boundProfileId
            : RegionBindings[SemanticRegion.Global];

        return Profiles.Single(profile => profile.ProfileId == profileId);
    }

    public MediaReference? FindMedia(Guid mediaId) =>
        MediaCatalog.FirstOrDefault(media => media.MediaId == mediaId);

    public void Validate()
    {
        var errors = GetValidationErrors();
        if (errors.Count != 0)
        {
            throw new SettingsValidationException(errors);
        }
    }

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add($"SchemaVersion must be {CurrentSchemaVersion}.");
        }

        var profileIds = ValidateProfiles(errors);
        var mediaIds = ValidateMediaCatalog(errors);
        ValidateRecentMedia(mediaIds, errors);
        ValidateRegionBindings(profileIds, errors);

#pragma warning disable CS0618 // Validate the deprecated value solely for safe round-tripping.
        if (LastCompatibilityProfileId is not null)
        {
            if (string.IsNullOrWhiteSpace(LastCompatibilityProfileId))
            {
                errors.Add("LastCompatibilityProfileId cannot be empty.");
            }
            else if (LastCompatibilityProfileId.Length > 128)
            {
                errors.Add("LastCompatibilityProfileId cannot exceed 128 characters.");
            }
        }
#pragma warning restore CS0618

        return new ReadOnlyCollection<string>(errors);
    }

    /// <summary>
    /// Validates and returns a normalized, deeply isolated snapshot. Missing last-known provider
    /// metadata from in-memory legacy callers is populated before the snapshot is persisted.
    /// </summary>
    public SettingsV3 CreateSnapshot()
    {
        Validate();

        var profiles = Profiles
            .Select(profile => profile.Snapshot())
            .ToArray();
        var mediaCatalog = MediaCatalog
            .Select(CreateVersion3MediaSnapshot)
            .ToArray();
        var recentMediaIds = RecentMediaIds.ToArray();
        var regionBindings = RegionBindings
            .OrderBy(binding => binding.Key)
            .ToDictionary(binding => binding.Key, binding => binding.Value);

        var snapshot = this with
        {
            Profiles = new ReadOnlyCollection<WallpaperProfile>(profiles),
            MediaCatalog = new ReadOnlyCollection<MediaReference>(mediaCatalog),
            RecentMediaIds = new ReadOnlyCollection<Guid>(recentMediaIds),
            RegionBindings = new ReadOnlyDictionary<SemanticRegion, Guid>(regionBindings),
        };
        snapshot.Validate();
        return snapshot;
    }

    internal SettingsV3 Snapshot() => CreateSnapshot();

    internal static MediaReference CreateVersion3MediaSnapshot(MediaReference media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var snapshot = media.Snapshot();
        var contentKind = snapshot.LastKnownContentKind == WallpaperContentKind.Unknown
            ? snapshot.LastKnownKind switch
            {
                MediaKind.Image => WallpaperContentKind.Image,
                MediaKind.Video => WallpaperContentKind.Video,
                _ => WallpaperContentKind.Unknown,
            }
            : snapshot.LastKnownContentKind;
        var mediaKind = contentKind switch
        {
            WallpaperContentKind.Image => MediaKind.Image,
            WallpaperContentKind.Video => MediaKind.Video,
            _ => MediaKind.None,
        };
        var displayName = string.IsNullOrWhiteSpace(snapshot.LastKnownDisplayName)
            ? CreateFallbackDisplayName(snapshot)
            : snapshot.LastKnownDisplayName.Trim();

        return (snapshot with
        {
            LastKnownKind = mediaKind,
            LastKnownContentKind = contentKind,
            LastKnownDisplayName = displayName,
        }).Snapshot();
    }

    private HashSet<Guid> ValidateProfiles(List<string> errors)
    {
        var profileIds = new HashSet<Guid>();
        if (Profiles is null)
        {
            errors.Add("Profiles is required.");
            return profileIds;
        }

        if (Profiles.Count == 0)
        {
            errors.Add("Profiles must contain at least one profile.");
            return profileIds;
        }

        foreach (var profile in Profiles)
        {
            if (profile is null)
            {
                errors.Add("Profiles cannot contain null entries.");
                continue;
            }

            errors.AddRange(profile.GetValidationErrors().Select(error => $"Profile: {error}"));
            if (!profileIds.Add(profile.ProfileId))
            {
                errors.Add("Profiles cannot contain duplicate identifiers.");
            }
        }

        return profileIds;
    }

    private HashSet<Guid> ValidateMediaCatalog(List<string> errors)
    {
        var mediaIds = new HashSet<Guid>();
        if (MediaCatalog is null)
        {
            errors.Add("MediaCatalog is required.");
            return mediaIds;
        }

        foreach (var media in MediaCatalog)
        {
            if (media is null)
            {
                errors.Add("MediaCatalog cannot contain null entries.");
                continue;
            }

            errors.AddRange(media.GetValidationErrors().Select(error => $"Media: {error}"));
            ValidateVersion3MediaContract(media, errors);
            if (!mediaIds.Add(media.MediaId))
            {
                errors.Add("MediaCatalog cannot contain duplicate identifiers.");
            }
        }

        if (Profiles is not null)
        {
            foreach (var mediaId in Profiles
                         .Where(profile => profile is not null)
                         .Select(profile => profile.MediaId)
                         .OfType<Guid>())
            {
                if (!mediaIds.Contains(mediaId))
                {
                    errors.Add("Every profile media identifier must exist in MediaCatalog.");
                }
            }
        }

        return mediaIds;
    }

    private static void ValidateVersion3MediaContract(
        MediaReference media,
        List<string> errors)
    {
        if (media.LastKnownContentKind == WallpaperContentKind.Application)
        {
            errors.Add("Media: Application wallpaper projects cannot be persisted.");
        }

        if (media.SourceKind == MediaSourceKind.LocalFile &&
            media.LastKnownContentKind is WallpaperContentKind.Scene or WallpaperContentKind.Web)
        {
            errors.Add("Media: A direct local file cannot identify Scene or Web content.");
        }
    }

    private void ValidateRecentMedia(HashSet<Guid> mediaIds, List<string> errors)
    {
        if (RecentMediaIds is null)
        {
            errors.Add("RecentMediaIds is required.");
            return;
        }

        if (RecentMediaIds.Count > MaximumRecentMediaIds)
        {
            errors.Add($"RecentMediaIds cannot contain more than {MaximumRecentMediaIds} entries.");
        }

        var recentIds = new HashSet<Guid>();
        foreach (var mediaId in RecentMediaIds)
        {
            SettingsContractValidation.ValidateVersion7Identifier(
                mediaId,
                "RecentMediaIds entry",
                errors);
            if (!recentIds.Add(mediaId))
            {
                errors.Add("RecentMediaIds cannot contain duplicates.");
            }

            if (!mediaIds.Contains(mediaId))
            {
                errors.Add("Every recent media identifier must exist in MediaCatalog.");
            }
        }
    }

    private void ValidateRegionBindings(HashSet<Guid> profileIds, List<string> errors)
    {
        if (RegionBindings is null)
        {
            errors.Add("RegionBindings is required.");
            return;
        }

        if (!RegionBindings.ContainsKey(SemanticRegion.Global))
        {
            errors.Add("RegionBindings must contain a Global fallback.");
        }

        foreach (var binding in RegionBindings)
        {
            if (!Enum.IsDefined(binding.Key))
            {
                errors.Add("RegionBindings contains an unsupported region.");
            }

            SettingsContractValidation.ValidateVersion7Identifier(
                binding.Value,
                "RegionBindings profile identifier",
                errors);
            if (!profileIds.Contains(binding.Value))
            {
                errors.Add("Every region binding must refer to an existing profile.");
            }
        }
    }

    private static string CreateFallbackDisplayName(MediaReference media)
    {
        string candidate;
        if (media.SourceKind == MediaSourceKind.WallpaperEngineWorkshopProject)
        {
            candidate = $"Workshop {media.SourceIdentifier}";
        }
        else
        {
            var trimmedPath = media.SourceIdentifier.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            candidate = Path.GetFileName(trimmedPath);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = "Wallpaper";
            }
        }

        return candidate.Length <= MediaReference.MaximumDisplayNameLength
            ? candidate
            : candidate[..MediaReference.MaximumDisplayNameLength];
    }
}
