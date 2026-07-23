using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.App.Models;

/// <summary>
/// Keeps editable, persisted-desired, and currently active wallpaper settings distinct.
/// </summary>
public sealed class WallpaperConfigurationState
{
    private WallpaperConfigurationState(
        SettingsV1 draft,
        SettingsV1 savedDesired,
        SettingsV1? activeSnapshot,
        bool isRuntimeActive)
    {
        Draft = Copy(draft);
        SavedDesired = Copy(savedDesired);
        ActiveSnapshot = activeSnapshot is null ? null : Copy(activeSnapshot);
        IsRuntimeActive = isRuntimeActive;
    }

    public SettingsV1 Draft { get; }

    public SettingsV1 SavedDesired { get; }

    public SettingsV1? ActiveSnapshot { get; }

    public bool IsRuntimeActive { get; }

    public bool HasUnsavedChanges => !AreEquivalent(Draft, SavedDesired);

    public bool HasPendingApply =>
        Draft.MediaPath is not null &&
        (!IsRuntimeActive ||
         ActiveSnapshot is null ||
         !AreEquivalent(Draft, ActiveSnapshot));

    public bool IsSavedButNotActive =>
        SavedDesired.MediaPath is not null &&
        (!IsRuntimeActive ||
         ActiveSnapshot is null ||
         !AreEquivalent(SavedDesired, ActiveSnapshot));

    public static WallpaperConfigurationState FromPersisted(SettingsV1 persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        return new WallpaperConfigurationState(
            persisted,
            persisted,
            activeSnapshot: null,
            isRuntimeActive: false);
    }

    public WallpaperConfigurationState WithDraft(SettingsV1 draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new WallpaperConfigurationState(
            draft,
            SavedDesired,
            ActiveSnapshot,
            IsRuntimeActive);
    }

    /// <summary>
    /// Replaces the durable desired snapshot. Draft synchronization is useful after reloading the
    /// settings file at the end of an apply attempt.
    /// </summary>
    public WallpaperConfigurationState WithPersisted(
        SettingsV1 persisted,
        bool synchronizeDraft = true)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        return new WallpaperConfigurationState(
            synchronizeDraft ? persisted : Draft,
            persisted,
            ActiveSnapshot,
            IsRuntimeActive);
    }

    public WallpaperConfigurationState WithActive(SettingsV1 active)
    {
        ArgumentNullException.ThrowIfNull(active);
        return new WallpaperConfigurationState(
            Draft,
            SavedDesired,
            active,
            isRuntimeActive: true);
    }

    public WallpaperConfigurationState WithoutActive() =>
        new(Draft, SavedDesired, activeSnapshot: null, isRuntimeActive: false);

    public static bool AreEquivalent(SettingsV1 left, SettingsV1 right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        left.Validate();
        right.Validate();

        return left.SchemaVersion == right.SchemaVersion &&
               string.Equals(
                   left.MediaPath,
                   right.MediaPath,
                   StringComparison.OrdinalIgnoreCase) &&
               left.MediaKind == right.MediaKind &&
               left.Fit == right.Fit &&
               left.FocusX.Equals(right.FocusX) &&
               left.FocusY.Equals(right.FocusY) &&
               left.PanelOpacity.Equals(right.PanelOpacity) &&
               left.BlurPx.Equals(right.BlurPx) &&
               left.DarkOverlay.Equals(right.DarkOverlay) &&
               left.LightOverlay.Equals(right.LightOverlay) &&
               left.AcceptedCdpRisk == right.AcceptedCdpRisk &&
               string.Equals(
                   left.LastCompatibilityProfileId,
                   right.LastCompatibilityProfileId,
                   StringComparison.Ordinal) &&
               left.RecentMediaPaths.SequenceEqual(
                   right.RecentMediaPaths,
                   StringComparer.OrdinalIgnoreCase);
    }

    private static SettingsV1 Copy(SettingsV1 settings)
    {
        settings.Validate();
        return settings with
        {
            RecentMediaPaths = Array.AsReadOnly(settings.RecentMediaPaths.ToArray()),
        };
    }
}
