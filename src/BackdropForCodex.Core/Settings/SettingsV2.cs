using System.Collections.ObjectModel;
using System.ComponentModel;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Settings;

/// <summary>
/// Stable application regions. Compatibility-specific routes and selectors must never be persisted here.
/// </summary>
public enum SemanticRegion
{
    Global = 0,
    Home,
    Conversation,
    CodeAndDiff,
    SettingsAndOther,
}

public enum PerformancePolicy
{
    Automatic = 0,
    PreferQuality,
    Balanced,
    PreferEfficiency,
}

/// <summary>
/// A durable wallpaper configuration. Runtime capability degradation does not mutate this contract.
/// </summary>
public sealed record WallpaperProfile
{
    public const int MaximumNameLength = 128;

    public Guid ProfileId { get; init; } = Guid.CreateVersion7();

    public string Name { get; init; } = "Global";

    public Guid? MediaId { get; init; }

    public WallpaperFit Fit { get; init; } = WallpaperFit.Cover;

    public double FocusX { get; init; } = 0.5;

    public double FocusY { get; init; } = 0.5;

    public double PanelOpacity { get; init; } = 0.78;

    public double BlurPx { get; init; } = 14;

    public double DarkOverlay { get; init; } = 0.30;

    public double LightOverlay { get; init; } = 0.18;

    public bool SoundEnabled { get; init; }

    public double Volume { get; init; } = 0.5;

    public PerformancePolicy PerformancePolicy { get; init; } = PerformancePolicy.Automatic;

    public static WallpaperProfile CreateDefault(string name = "Global")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new WallpaperProfile
        {
            Name = name.Trim(),
        };
    }

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

        SettingsContractValidation.ValidateVersion7Identifier(ProfileId, nameof(ProfileId), errors);

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Name cannot be empty.");
        }
        else if (Name.Length > MaximumNameLength)
        {
            errors.Add($"Name cannot exceed {MaximumNameLength} characters.");
        }

        if (MediaId is { } mediaId)
        {
            SettingsContractValidation.ValidateVersion7Identifier(mediaId, nameof(MediaId), errors);
        }

        if (!Enum.IsDefined(Fit))
        {
            errors.Add("Fit is not supported.");
        }

        if (!Enum.IsDefined(PerformancePolicy))
        {
            errors.Add("PerformancePolicy is not supported.");
        }

        SettingsContractValidation.ValidateRange(FocusX, 0, 1, nameof(FocusX), errors);
        SettingsContractValidation.ValidateRange(FocusY, 0, 1, nameof(FocusY), errors);
        SettingsContractValidation.ValidateRange(PanelOpacity, 0.60, 0.95, nameof(PanelOpacity), errors);
        SettingsContractValidation.ValidateRange(BlurPx, 0, 24, nameof(BlurPx), errors);
        SettingsContractValidation.ValidateRange(DarkOverlay, 0, 1, nameof(DarkOverlay), errors);
        SettingsContractValidation.ValidateRange(LightOverlay, 0, 1, nameof(LightOverlay), errors);
        SettingsContractValidation.ValidateRange(Volume, 0, 1, nameof(Volume), errors);

        return new ReadOnlyCollection<string>(errors);
    }

    internal WallpaperProfile Snapshot()
    {
        Validate();
        return this with
        {
            Name = Name.Trim(),
        };
    }
}

/// <summary>
/// Version two of the durable settings contract.
/// </summary>
public sealed record SettingsV2
{
    public const int CurrentSchemaVersion = 2;

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
    /// Retained only to deserialize and round-trip settings written by older releases.
    /// It no longer participates in compatibility selection or runtime behavior.
    /// </summary>
    [Obsolete(
        "Retained only for backward-compatible settings round-tripping. " +
        "Do not use this property for runtime behavior.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public string? LastCompatibilityProfileId { get; init; }

    public static SettingsV2 CreateDefault()
    {
        var profile = WallpaperProfile.CreateDefault();
        return new SettingsV2
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
    /// Validates this document and returns a normalized, deeply isolated snapshot.
    /// Callers may safely retain the returned value even when the source collections
    /// were backed by mutable arrays, lists, or dictionaries.
    /// </summary>
    public SettingsV2 CreateSnapshot()
    {
        Validate();

        var profiles = Profiles
            .Select(profile => profile.Snapshot())
            .ToArray();
        var mediaCatalog = MediaCatalog
            .Select(media => media.Snapshot())
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

    internal SettingsV2 Snapshot() => CreateSnapshot();

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
}

internal static class SettingsContractValidation
{
    internal static void ValidateRange(
        double value,
        double minimum,
        double maximum,
        string propertyName,
        List<string> errors)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            errors.Add($"{propertyName} must be between {minimum} and {maximum}, inclusive.");
        }
    }

    internal static void ValidateVersion7Identifier(
        Guid identifier,
        string propertyName,
        List<string> errors)
    {
        if (identifier == Guid.Empty || identifier.Version != 7)
        {
            errors.Add($"{propertyName} must be an opaque UUIDv7 identifier.");
        }
    }
}
