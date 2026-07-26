using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.App.Models;

/// <summary>
/// UI-facing immutable projection of the canonical V2 workspace state.
/// </summary>
public sealed class WallpaperConfigurationState
{
    private WallpaperConfigurationState(
        SettingsV2 draft,
        SettingsV2 savedDesired,
        SettingsV2? activeSnapshot,
        WallpaperRuntimeSurface surface)
    {
        Draft = draft.CreateSnapshot();
        SavedDesired = savedDesired.CreateSnapshot();
        ActiveSnapshot = activeSnapshot?.CreateSnapshot();
        Surface = surface ?? throw new ArgumentNullException(nameof(surface));
    }

    public SettingsV2 Draft { get; }

    public SettingsV2 SavedDesired { get; }

    public SettingsV2? ActiveSnapshot { get; }

    public WallpaperRuntimeSurface Surface { get; }

    public bool IsRuntimeActive =>
        Surface.Kind == WallpaperRuntimeSurfaceKind.MediaActive;

    public bool HasUnsavedChanges =>
        !SettingsV2Comparer.UiDirtyEquals(Draft, SavedDesired);

    public bool HasPendingApply =>
        ActiveSnapshot is null ||
        !SettingsV2Comparer.RuntimeEquivalent(Draft, ActiveSnapshot);

    public bool IsSavedButNotActive =>
        ActiveSnapshot is null ||
        !SettingsV2Comparer.RuntimeEquivalent(SavedDesired, ActiveSnapshot);

    public static WallpaperConfigurationState FromPersisted(SettingsV2 persisted)
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

    public WallpaperConfigurationState WithDraft(SettingsV2 draft) =>
        new(draft, SavedDesired, ActiveSnapshot, Surface);

    public WallpaperConfigurationState WithPersisted(
        SettingsV2 persisted,
        bool synchronizeDraft = true) =>
        new(
            synchronizeDraft ? persisted : Draft,
            persisted,
            ActiveSnapshot,
            Surface);

    public WallpaperConfigurationState WithActive(
        SettingsV2 active,
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

    public static bool AreEquivalent(SettingsV2 left, SettingsV2 right) =>
        SettingsV2Comparer.DurableEquals(left, right);

    public static bool AreRuntimeEquivalent(SettingsV2 left, SettingsV2 right) =>
        SettingsV2Comparer.RuntimeEquivalent(left, right);
}
