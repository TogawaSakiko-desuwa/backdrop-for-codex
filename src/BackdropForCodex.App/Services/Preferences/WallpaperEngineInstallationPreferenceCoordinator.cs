using BackdropForCodex.App.Services.Media;

namespace BackdropForCodex.App.Services.Preferences;

/// <summary>
/// Validates and publishes a process-local Wallpaper Engine installation selection. Installation
/// roots never enter the preferences document or the durable wallpaper workspace.
/// </summary>
public sealed class WallpaperEngineInstallationPreferenceCoordinator
{
    private readonly IWallpaperEngineInstallationPreferenceManager _manager;

    public WallpaperEngineInstallationPreferenceCoordinator(
        IWallpaperEngineInstallationPreferenceManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public bool HasPreferredWallpaperEngineInstallation =>
        _manager.PreferredInstallRootPath is not null;

    public void ApplyLoadedPreferences(AppPreferencesV1 preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        _manager.ClearPreferredInstallRootPath();
    }

    public async Task<AppPreferencesV1> SelectAsync(
        AppPreferencesV1 current,
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        var snapshot = current.Snapshot();
        var installRootPath = await _manager
            .ValidateSelectionAsync(selectedPath, cancellationToken)
            .ConfigureAwait(false);
        _manager.SetPreferredInstallRootPath(installRootPath);
        return snapshot;
    }

    public Task<AppPreferencesV1> ClearAsync(
        AppPreferencesV1 current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = current.Snapshot();
        _manager.ClearPreferredInstallRootPath();
        return Task.FromResult(snapshot);
    }
}
