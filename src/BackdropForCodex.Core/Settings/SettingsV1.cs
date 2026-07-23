using System.Collections.ObjectModel;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Settings;

public enum WallpaperFit
{
    Cover = 0,
    Contain,
}

/// <summary>
/// Version one of the durable wallpaper settings contract.
/// Video playback is intentionally always muted and looping, so neither option is persisted.
/// </summary>
public sealed record SettingsV1
{
    public const int CurrentSchemaVersion = 1;

    public const int MaximumRecentMediaPaths = 8;

    public const int MaximumPathLength = 32767;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? MediaPath { get; init; }

    public MediaKind MediaKind { get; init; } = MediaKind.None;

    public WallpaperFit Fit { get; init; } = WallpaperFit.Cover;

    public double FocusX { get; init; } = 0.5;

    public double FocusY { get; init; } = 0.5;

    public double PanelOpacity { get; init; } = 0.78;

    public double BlurPx { get; init; } = 14;

    public double DarkOverlay { get; init; } = 0.30;

    public double LightOverlay { get; init; } = 0.18;

    public IReadOnlyList<string> RecentMediaPaths { get; init; } = Array.Empty<string>();

    public bool AcceptedCdpRisk { get; init; }

    public string? LastCompatibilityProfileId { get; init; }

    public static SettingsV1 CreateDefault() => new();

    /// <summary>
    /// Returns a copy with <paramref name="mediaPath"/> at the front of the bounded recent list.
    /// Paths are compared case-insensitively because this application targets Windows.
    /// </summary>
    public SettingsV1 AddRecentMediaPath(string mediaPath)
    {
        var normalizedPath = ValidateAndNormalizePath(mediaPath, nameof(mediaPath));
        var currentPaths = RecentMediaPaths ?? Array.Empty<string>();
        var paths = new List<string>(MaximumRecentMediaPaths) { normalizedPath };

        foreach (var candidate in currentPaths)
        {
            if (paths.Count == MaximumRecentMediaPaths)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(candidate) &&
                !paths.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(candidate);
            }
        }

        return this with
        {
            RecentMediaPaths = new ReadOnlyCollection<string>(paths),
        };
    }

    /// <summary>
    /// Returns a copy without <paramref name="mediaPath"/> in the recent list.
    /// Paths are compared case-insensitively because this application targets Windows.
    /// </summary>
    public SettingsV1 RemoveRecentMediaPath(string mediaPath)
    {
        var normalizedPath = ValidateAndNormalizePath(mediaPath, nameof(mediaPath));
        var currentPaths = RecentMediaPaths ?? Array.Empty<string>();
        var paths = currentPaths
            .Where(candidate =>
                !string.Equals(candidate, normalizedPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return this with
        {
            RecentMediaPaths = new ReadOnlyCollection<string>(paths),
        };
    }

    /// <summary>
    /// Returns a copy with an empty recent-media list.
    /// </summary>
    public SettingsV1 ClearRecentMediaPaths() => this with
    {
        RecentMediaPaths = Array.Empty<string>(),
    };

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

        if (!Enum.IsDefined(MediaKind))
        {
            errors.Add("MediaKind is not supported.");
        }

        if (!Enum.IsDefined(Fit))
        {
            errors.Add("Fit is not supported.");
        }

        if (MediaPath is null)
        {
            if (MediaKind != MediaKind.None)
            {
                errors.Add("MediaKind must be None when MediaPath is not set.");
            }
        }
        else
        {
            ValidatePath(MediaPath, nameof(MediaPath), errors);
            if (MediaKind == MediaKind.None)
            {
                errors.Add("MediaKind must identify an image or video when MediaPath is set.");
            }
        }

        ValidateRange(FocusX, 0, 1, nameof(FocusX), errors);
        ValidateRange(FocusY, 0, 1, nameof(FocusY), errors);
        ValidateRange(PanelOpacity, 0.60, 0.95, nameof(PanelOpacity), errors);
        ValidateRange(BlurPx, 0, 24, nameof(BlurPx), errors);
        ValidateRange(DarkOverlay, 0, 1, nameof(DarkOverlay), errors);
        ValidateRange(LightOverlay, 0, 1, nameof(LightOverlay), errors);

        if (RecentMediaPaths is null)
        {
            errors.Add("RecentMediaPaths is required.");
        }
        else
        {
            if (RecentMediaPaths.Count > MaximumRecentMediaPaths)
            {
                errors.Add($"RecentMediaPaths cannot contain more than {MaximumRecentMediaPaths} entries.");
            }

            var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var recentPath in RecentMediaPaths)
            {
                ValidatePath(recentPath, "RecentMediaPaths entry", errors);
                if (!string.IsNullOrWhiteSpace(recentPath) && !uniquePaths.Add(recentPath))
                {
                    errors.Add("RecentMediaPaths cannot contain duplicates.");
                }
            }
        }

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

        return new ReadOnlyCollection<string>(errors);
    }

    internal SettingsV1 Snapshot()
    {
        Validate();
        return this with
        {
            RecentMediaPaths = new ReadOnlyCollection<string>(RecentMediaPaths.ToArray()),
        };
    }

    private static void ValidateRange(
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

    private static void ValidatePath(string? path, string propertyName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{propertyName} cannot be empty.");
        }
        else if (!Path.IsPathFullyQualified(path))
        {
            errors.Add($"{propertyName} must be an absolute path.");
        }
        else if (path.Length > MaximumPathLength)
        {
            errors.Add($"{propertyName} cannot exceed {MaximumPathLength} characters.");
        }
    }

    private static string ValidateAndNormalizePath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The media path must be absolute.", parameterName);
        }

        return Path.GetFullPath(path);
    }
}

public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(IReadOnlyList<string> errors)
        : base(CreateMessage(errors))
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = new ReadOnlyCollection<string>(errors.ToArray());
    }

    public IReadOnlyList<string> Errors { get; }

    private static string CreateMessage(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return errors.Count == 0
            ? "Settings validation failed."
            : $"Settings validation failed: {string.Join(" ", errors)}";
    }
}
