using System.Collections.ObjectModel;
using System.IO;
using BackdropForCodex.App.Models;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.App.Services.Preferences;
using BackdropForCodex.App.Services.Wallpaper;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackdropForCodex.App.ViewModels;

public sealed record RecentMediaItem(
    string Path,
    string DisplayName,
    MediaKind Kind,
    bool Exists);

public sealed record WallpaperSettingsInitializationResult(
    SettingsV1 Settings,
    Exception? Error);

/// <summary>
/// Owns management-side persisted state: UI preferences, recent media, and the
/// read-only/recovery state used when Settings V2 cannot be projected safely.
/// </summary>
public sealed class SettingsManagementViewModel : ObservableObject, IDisposable
{
    private readonly IWallpaperApplicationService _wallpaper;
    private readonly IAppPreferencesStore _preferencesStore;
    private readonly WallpaperEditorViewModel _editor;
    private readonly ISafeMediaPreviewService _previewMedia;
    private readonly SemaphoreSlim _preferencesMutationGate = new(1, 1);
    private WallpaperConfigurationState _configurationState =
        WallpaperConfigurationState.FromPersisted(SettingsV1.CreateDefault());
    private AppPreferencesV1 _preferences = AppPreferencesV1.CreateDefault();
    private bool _hasProtectedSettings;
    private bool _hasVersion1Backup;
    private bool _isDisposed;

    public SettingsManagementViewModel(
        IWallpaperApplicationService wallpaper,
        IAppPreferencesStore preferencesStore,
        WallpaperEditorViewModel editor,
        ISafeMediaPreviewService? previewMedia = null)
    {
        _wallpaper = wallpaper ?? throw new ArgumentNullException(nameof(wallpaper));
        _preferencesStore =
            preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _previewMedia = previewMedia ?? SafeMediaPreviewService.Shared;
        _editor.DraftChanged += Editor_DraftChanged;
    }

    public ObservableCollection<RecentMediaItem> Recents { get; } = [];

    public WallpaperConfigurationState ConfigurationState
    {
        get => _configurationState;
        private set
        {
            if (SetProperty(ref _configurationState, value))
            {
                OnPropertyChanged(nameof(SavedDesired));
                OnPropertyChanged(nameof(ActiveSnapshot));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsSavedButInactive));
                OnPropertyChanged(nameof(IsDraftDirty));
            }
        }
    }

    public SettingsV1 SavedDesired => ConfigurationState.SavedDesired;

    public SettingsV1? ActiveSnapshot => ConfigurationState.ActiveSnapshot;

    public bool IsActive => ConfigurationState.IsRuntimeActive;

    public bool IsSavedButInactive => ConfigurationState.IsSavedButNotActive;

    public bool IsDraftDirty => ConfigurationState.HasUnsavedChanges;

    public AppPreferencesV1 Preferences
    {
        get => _preferences;
        private set
        {
            if (SetProperty(ref _preferences, value))
            {
                OnPropertyChanged(nameof(ThemeMode));
                OnPropertyChanged(nameof(HasShownTrayTip));
            }
        }
    }

    public ThemeMode ThemeMode => Preferences.ThemeMode;

    public bool HasShownTrayTip => Preferences.HasShownTrayTip;

    public bool HasProtectedSettings
    {
        get => _hasProtectedSettings;
        private set => SetProperty(ref _hasProtectedSettings, value);
    }

    public bool HasVersion1Backup
    {
        get => _hasVersion1Backup;
        private set => SetProperty(ref _hasVersion1Backup, value);
    }

    public bool SupportsVersion1BackupRestore =>
        _wallpaper is IWallpaperSettingsRecoveryService;

    public void SetPersistedSettings(SettingsV1 settings, bool synchronizeEditor)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ConfigurationState =
            ConfigurationState.WithPersisted(
                settings,
                synchronizeDraft: synchronizeEditor);
        if (synchronizeEditor)
        {
            ApplySavedSettingsToEditor(settings);
            return;
        }

        ConfigurationState =
            ConfigurationState.WithDraft(_editor.ProjectOnto(settings));
    }

    public void ApplySavedSettingsToEditor(SettingsV1 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _editor.ApplySettings(settings);
    }

    public void SetActive(SettingsV1 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ConfigurationState = ConfigurationState.WithActive(settings);
    }

    public void SetRuntimeActivity(bool isActive) =>
        ConfigurationState = isActive
            ? ConfigurationState.WithRuntimeActive(isRuntimeActive: true)
            : ConfigurationState.WithoutActive();

    public async Task LoadPreferencesAsync(CancellationToken cancellationToken)
    {
        await _preferencesMutationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        try
        {
            Preferences = await _preferencesStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            _ = _preferencesMutationGate.Release();
        }
    }

    public void UseDefaultPreferences() =>
        Preferences = AppPreferencesV1.CreateDefault();

    public Task SetThemeModeAsync(
        ThemeMode themeMode,
        CancellationToken cancellationToken) =>
        UpdatePreferencesAsync(
            current => current with { ThemeMode = themeMode },
            cancellationToken);

    public Task MarkTrayTipShownAsync(CancellationToken cancellationToken) =>
        Preferences.HasShownTrayTip
            ? Task.CompletedTask
            : UpdatePreferencesAsync(
                current => current with { HasShownTrayTip = true },
                cancellationToken);

    public async Task ResetPreferencesAsync(CancellationToken cancellationToken)
    {
        await _preferencesMutationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        try
        {
            await _preferencesStore
                .ResetAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            _ = _preferencesMutationGate.Release();
        }
    }

    public async Task<WallpaperSettingsInitializationResult>
        InitializeWallpaperSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await LoadWallpaperSettingsAsync(cancellationToken)
                .ConfigureAwait(true);
            return new WallpaperSettingsInitializationResult(settings, Error: null);
        }
        catch (Exception exception)
        {
            SetProtectionFrom(exception);
            var settings = SettingsV1.CreateDefault();
            RefreshRecents(settings);
            return new WallpaperSettingsInitializationResult(settings, exception);
        }
    }

    public async Task<SettingsV1> LoadWallpaperSettingsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _wallpaper
                .LoadSettingsAsync(cancellationToken)
                .ConfigureAwait(true);
            ClearProtection();
            RefreshRecents(settings);
            return settings;
        }
        catch (Exception exception)
        {
            SetProtectionFrom(exception);
            throw;
        }
    }

    public async Task<SettingsV1> SaveRiskAcceptanceAsync(
        SettingsV1 baseline,
        bool accepted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        return await _wallpaper
            .SaveSettingsAsync(
                WallpaperEditorViewModel.ClampLegacyOverlays(baseline) with
                {
                    AcceptedCdpRisk = accepted,
                },
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<SettingsV1> RemoveRecentAsync(
        SettingsV1 baseline,
        string mediaPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        var saved = await _wallpaper
            .SaveSettingsAsync(
                WallpaperEditorViewModel
                    .ClampLegacyOverlays(baseline)
                    .RemoveRecentMediaPath(mediaPath),
                cancellationToken)
            .ConfigureAwait(true);
        RefreshRecents(saved);
        return saved;
    }

    public async Task<SettingsV1> ClearRecentsAsync(
        SettingsV1 baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        var saved = await _wallpaper
            .SaveSettingsAsync(
                WallpaperEditorViewModel
                    .ClampLegacyOverlays(baseline)
                    .ClearRecentMediaPaths(),
                cancellationToken)
            .ConfigureAwait(true);
        RefreshRecents(saved);
        return saved;
    }

    public async Task<SettingsV1> ResetWallpaperSettingsAsync(
        CancellationToken cancellationToken)
    {
        var saved = _wallpaper is IWallpaperSettingsRecoveryService recovery
            ? await recovery
                .ResetWallpaperSettingsAsync(cancellationToken)
                .ConfigureAwait(true)
            : await _wallpaper
                .SaveSettingsAsync(SettingsV1.CreateDefault(), cancellationToken)
                .ConfigureAwait(true);
        ClearProtection();
        RefreshRecents(saved);
        return saved;
    }

    public async Task<SettingsV1> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken)
    {
        if (_wallpaper is not IWallpaperSettingsRecoveryService recovery)
        {
            throw new InvalidOperationException(
                "The wallpaper settings service does not support V1 backup recovery.");
        }

        var restored = await recovery
            .RestoreVersion1BackupAsync(cancellationToken)
            .ConfigureAwait(true);
        ClearProtection();
        RefreshRecents(restored);
        return restored;
    }

    public void RefreshRecents(SettingsV1 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Recents.Clear();
        foreach (var path in settings.RecentMediaPaths.Take(SettingsV1.MaximumRecentMediaPaths))
        {
            Recents.Add(
                new RecentMediaItem(
                    path,
                    Path.GetFileName(path),
                    WallpaperEditorViewModel.InferMediaKind(path),
                    _previewMedia.IsAvailable(path)));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _editor.DraftChanged -= Editor_DraftChanged;
        GC.SuppressFinalize(this);
    }

    private async Task UpdatePreferencesAsync(
        Func<AppPreferencesV1, AppPreferencesV1> update,
        CancellationToken cancellationToken)
    {
        await _preferencesMutationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        try
        {
            var next = update(Preferences);
            await _preferencesStore
                .SaveAsync(next, cancellationToken)
                .ConfigureAwait(true);
            Preferences = next;
        }
        finally
        {
            _ = _preferencesMutationGate.Release();
        }
    }

    private void SetProtectionFrom(Exception exception)
    {
        HasProtectedSettings =
            exception is SettingsRecoveryRequiredException or
                FutureSettingsVersionException or
                SettingsProjectionException;
        HasVersion1Backup = exception switch
        {
            SettingsRecoveryRequiredException recovery =>
                recovery.HasVersion1Backup,
            FutureSettingsVersionException future =>
                future.HasVersion1Backup,
            SettingsProjectionException projection =>
                projection.HasVersion1Backup,
            _ => false,
        };
    }

    private void ClearProtection()
    {
        HasProtectedSettings = false;
        HasVersion1Backup = false;
    }

    private void Editor_DraftChanged(object? sender, EventArgs eventArgs) =>
        ConfigurationState =
            ConfigurationState.WithDraft(_editor.ProjectOnto(SavedDesired));
}
