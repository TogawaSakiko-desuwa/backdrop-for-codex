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
    bool Exists)
{
    public Guid MediaId { get; init; }
}

public sealed record WallpaperSettingsInitializationResult(
    SettingsV2 Settings,
    Exception? Error);

/// <summary>
/// Projects the canonical V2 workspace into settings-management UI state.
/// Persistence and activation remain serialized by <see cref="IWallpaperApplicationService"/>.
/// </summary>
public sealed class SettingsManagementViewModel : ObservableObject, IDisposable
{
    private readonly IWallpaperApplicationService _wallpaper;
    private readonly IAppPreferencesStore _preferencesStore;
    private readonly WallpaperEditorViewModel _editor;
    private readonly ISafeMediaPreviewService _previewMedia;
    private readonly SynchronizationContext? _uiContext;
    private readonly SemaphoreSlim _preferencesMutationGate = new(1, 1);
    private WallpaperConfigurationState _configurationState =
        WallpaperConfigurationState.FromPersisted(SettingsV2.CreateDefault());
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
        _uiContext = SynchronizationContext.Current;
        _editor.DraftChanged += Editor_DraftChanged;
        _wallpaper.WorkspaceChanged += Wallpaper_WorkspaceChanged;
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

    public SettingsV2 SavedDesired => ConfigurationState.SavedDesired;

    public SettingsV2? ActiveSnapshot => ConfigurationState.ActiveSnapshot;

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

    public bool SupportsVersion1BackupRestore
    {
        get
        {
            _ = _wallpaper;
            return true;
        }
    }

    public void SetPersistedSettings(SettingsV2 settings, bool synchronizeEditor)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ConfigurationState =
            ConfigurationState.WithPersisted(settings, synchronizeEditor);
        RefreshRecents(settings);
        if (synchronizeEditor)
        {
            ApplySavedSettingsToEditor(settings);
        }
    }

    public void ApplySavedSettingsToEditor(SettingsV2 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _editor.ApplySettings(settings);
    }

    public void SetActive(SettingsV2 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ConfigurationState = ConfigurationState.WithActive(
            settings,
            _wallpaper.Workspace.RuntimeSurface);
    }

    public void SetRuntimeActivity(bool isActive)
    {
        _ = isActive;
        SynchronizeWorkspace(_wallpaper.Workspace);
    }

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
            var settings = SettingsV2.CreateDefault();
            RefreshRecents(settings);
            return new WallpaperSettingsInitializationResult(settings, exception);
        }
    }

    public async Task<SettingsV2> LoadWallpaperSettingsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var workspace = await _wallpaper
                .InitializeAsync(cancellationToken)
                .ConfigureAwait(true);
            ClearProtection();
            HasVersion1Backup = _wallpaper.HasVersion1Backup;
            SynchronizeWorkspace(workspace);
            return workspace.SavedDesired;
        }
        catch (Exception exception)
        {
            SetProtectionFrom(exception);
            throw;
        }
    }

    public async Task<SettingsV2> SaveRiskAcceptanceAsync(
        SettingsV2 baseline,
        bool accepted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        return await _wallpaper
            .SetRiskAcceptanceAsync(accepted, cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<SettingsV2> RemoveRecentAsync(
        SettingsV2 baseline,
        string mediaPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        var media = baseline.MediaCatalog.FirstOrDefault(
            candidate =>
                string.Equals(
                    candidate.SourceIdentifier,
                    Path.GetFullPath(mediaPath),
                    StringComparison.OrdinalIgnoreCase));
        if (media is null)
        {
            return baseline.CreateSnapshot();
        }

        var saved = await _wallpaper
            .RemoveRecentMediaAsync(media.MediaId, cancellationToken)
            .ConfigureAwait(true);
        RefreshRecents(saved);
        return saved;
    }

    public async Task<SettingsV2> ClearRecentsAsync(
        SettingsV2 baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var saved = await _wallpaper
            .ClearRecentMediaAsync(cancellationToken)
            .ConfigureAwait(true);
        RefreshRecents(saved);
        return saved;
    }

    public async Task<SettingsV2> ResetWallpaperSettingsAsync(
        CancellationToken cancellationToken)
    {
        var saved = await _wallpaper
            .ResetWallpaperSettingsAsync(cancellationToken)
            .ConfigureAwait(true);
        ClearProtection();
        RefreshRecents(saved);
        return saved;
    }

    public async Task<SettingsV2> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken)
    {
        var restored = await _wallpaper
            .RestoreVersion1BackupAsync(cancellationToken)
            .ConfigureAwait(true);
        ClearProtection();
        RefreshRecents(restored);
        return restored;
    }

    public void RefreshRecents(SettingsV2 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.CreateSnapshot();
        Recents.Clear();
        foreach (var mediaId in snapshot.RecentMediaIds.Take(SettingsV2.MaximumRecentMediaIds))
        {
            var media = snapshot.FindMedia(mediaId);
            if (media is null)
            {
                continue;
            }

            var path = media.SourceIdentifier;
            Recents.Add(
                new RecentMediaItem(
                    path,
                    Path.GetFileName(path),
                    media.LastKnownKind,
                    _previewMedia.IsAvailable(path))
                {
                    MediaId = media.MediaId,
                });
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
        _wallpaper.WorkspaceChanged -= Wallpaper_WorkspaceChanged;
        _preferencesMutationGate.Dispose();
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
        HasVersion1Backup = _wallpaper.HasVersion1Backup;
    }

    private void Editor_DraftChanged(object? sender, EventArgs eventArgs)
    {
        if (_isDisposed || HasProtectedSettings)
        {
            return;
        }

        try
        {
            _wallpaper.ReplaceDraft(
                _editor.ProjectOnto(_wallpaper.Workspace.Draft));
        }
        catch (InvalidOperationException)
        {
            // Initialization/recovery owns the workspace until a valid V2 document is available.
        }
    }

    private void Wallpaper_WorkspaceChanged(
        object? sender,
        WallpaperWorkspaceStateChangedEventArgs eventArgs)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            SynchronizeWorkspace(eventArgs.State);
            return;
        }

        _uiContext.Post(
            static state =>
            {
                var payload = (WorkspaceUpdate)state!;
                if (!payload.Owner._isDisposed)
                {
                    payload.Owner.SynchronizeWorkspace(payload.State);
                }
            },
            new WorkspaceUpdate(this, eventArgs.State));
    }

    private void SynchronizeWorkspace(WallpaperWorkspaceState workspace)
    {
        if (!ReferenceEquals(workspace, _wallpaper.Workspace))
        {
            return;
        }

        ConfigurationState = WallpaperConfigurationState.FromWorkspace(workspace);
        RefreshRecents(workspace.SavedDesired);
    }

    private sealed record WorkspaceUpdate(
        SettingsManagementViewModel Owner,
        WallpaperWorkspaceState State);
}
