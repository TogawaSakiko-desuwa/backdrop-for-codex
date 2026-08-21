using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.App.Models;

/// <summary>
/// UI-facing immutable projection of the canonical V2 workspace state.
/// </summary>
public sealed class WallpaperConfigurationState
{
    private WallpaperConfigurationState(
        SettingsV3 draft,
        SettingsV3 savedDesired,
        SettingsV3? activeSnapshot,
        WallpaperRuntimeSurface surface)
    {
        Draft = draft.CreateSnapshot();
        SavedDesired = savedDesired.CreateSnapshot();
        ActiveSnapshot = activeSnapshot?.CreateSnapshot();
        Surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }

    public SettingsV3 Draft { get; }

    public SettingsV3 SavedDesired { get; }

    public SettingsV3? ActiveSnapshot { get; }

    public WallpaperRuntimeSurface Surface { get; }

    public bool IsRuntimeActive =>
        Surface.Kind == WallpaperRuntimeSurfaceKind.MediaActive;

    public bool HasUnsavedChanges =>
        !SettingsV3Comparer.UiDirtyEquals(Draft, SavedDesired);

    public bool HasPendingApply =>
        ActiveSnapshot is null ||
        !SettingsV3Comparer.RuntimeEquivalent(Draft, ActiveSnapshot);

    public bool IsSavedButNotActive =>
        ActiveSnapshot is null ||
        !SettingsV3Comparer.RuntimeEquivalent(SavedDesired, ActiveSnapshot);

    public static WallpaperConfigurationState FromPersisted(SettingsV3 persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        return new WallpaperConfigurationState(
            persisted,
            persisted,
            activeSnapshot: null,
            WallpaperRuntimeSurface.Disconnected());
    }

    public static WallpaperConfigurationState FromWorkspace(
        WallpaperWorkspaceState workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new WallpaperConfigurationState(
            workspace.Draft,
            workspace.SavedDesired,
            workspace.ActiveSnapshot,
            workspace.RuntimeSurface);
    }

    public WallpaperConfigurationState WithDraft(SettingsV3 draft) =>
        new(draft, SavedDesired, ActiveSnapshot, Surface);

    public WallpaperConfigurationState WithPersisted(
        SettingsV3 persisted,
        bool synchronizeDraft = true) =>
        new(
            synchronizeDraft ? persisted : Draft,
            persisted,
            ActiveSnapshot,
            Surface);

    public WallpaperConfigurationState WithActive(
        SettingsV3 active,
        WallpaperRuntimeSurface? surface = null)
    {
        ArgumentNullException.ThrowIfNull(active);
        var selectedProfile = active.ResolveProfile(SemanticRegion.Global);
        var selectedMediaId = selectedProfile.MediaId;
        var activeSurface = surface ??
            (selectedMediaId is { } mediaId
                ? WallpaperRuntimeSurface.MediaActive(
                    generation: Math.Max(1, Surface.Generation ?? 1),
                    mediaId,
                    Surface.PlaybackOwnership ?? PlaybackOwnershipToken.Create())
                : WallpaperRuntimeSurface.Official());
        return new WallpaperConfigurationState(
            Draft,
            SavedDesired,
            active,
            activeSurface);
    }

    public WallpaperConfigurationState WithoutActive(
        WallpaperRuntimeSurface? surface = null) =>
        new(
            Draft,
            SavedDesired,
            activeSnapshot: null,
            surface ?? WallpaperRuntimeSurface.Official());

    public static bool AreEquivalent(SettingsV3 left, SettingsV3 right) =>
        SettingsV3Comparer.DurableEquals(left, right);

    public static bool AreRuntimeEquivalent(SettingsV3 left, SettingsV3 right) =>
        SettingsV3Comparer.RuntimeEquivalent(left, right);
}
