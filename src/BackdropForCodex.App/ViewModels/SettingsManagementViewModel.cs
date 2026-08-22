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

public sealed record RecentMediaItem
{
    private readonly MediaReference _reference;

    public RecentMediaItem(
        MediaReference reference,
        string displayName,
        bool exists)
    {
        ArgumentNullException.ThrowIfNull(reference);
        _reference = reference.Snapshot();
        DisplayName = displayName;
        Exists = exists;
    }

    public MediaReference Reference => _reference.Snapshot();

    public Guid MediaId => _reference.MediaId;

    /// <summary>
    /// Local thumbnail compatibility path. External provider identifiers intentionally do not
    /// flow through path-based thumbnail conversion.
    /// </summary>
    public string? Path => _reference.SourceKind == MediaSourceKind.LocalFile
        ? _reference.SourceIdentifier
        : null;

    public string DisplayName { get; }

    public MediaKind Kind => _reference.LastKnownKind;

    public WallpaperContentKind ContentKind =>
        _reference.LastKnownContentKind switch
        {
            WallpaperContentKind.Image or
            WallpaperContentKind.Video or
            WallpaperContentKind.Scene or
            WallpaperContentKind.Web => _reference.LastKnownContentKind,
            _ => Kind switch
            {
                MediaKind.Image => WallpaperContentKind.Image,
                MediaKind.Video => WallpaperContentKind.Video,
                _ => WallpaperContentKind.Unknown,
            },
        };

    public bool Exists { get; }
}

public sealed record WallpaperSettingsInitializationResult(
    SettingsV3 Settings,
    Exception? Error);

/// <summary>
/// Projects the canonical V3 workspace into settings-management UI state.
/// Persistence and activation remain serialized by <see cref="IWallpaperApplicationService"/>.
/// </summary>
public sealed class SettingsManagementViewModel
    : ObservableObject,
      IDisposable,
      IWallpaperEngineInstallationSelectionService
{
    private readonly IWallpaperApplicationService _wallpaper;
    private readonly IAppPreferencesStore _preferencesStore;
    private readonly WallpaperEditorViewModel _editor;
    private readonly ISafeMediaPreviewService _previewMedia;
    private readonly IWallpaperSourceProviderRegistry _sourceRegistry;
    private readonly WallpaperEngineInstallationPreferenceCoordinator?
        _wallpaperEngineInstallationPreferences;
    private readonly SynchronizationContext? _uiContext;
    private readonly SemaphoreSlim _preferencesMutationGate = new(1, 1);
    private WallpaperConfigurationState _configurationState =
        WallpaperConfigurationState.FromPersisted(SettingsV3.CreateDefault());
    private AppPreferencesV1 _preferences = AppPreferencesV1.CreateDefault();
    private bool _hasProtectedSettings;
    private bool _hasVersion1Backup;
    private bool _isDisposed;

    public SettingsManagementViewModel(
        IWallpaperApplicationService wallpaper,
        IAppPreferencesStore preferencesStore,
        WallpaperEditorViewModel editor,
        ISafeMediaPreviewService? previewMedia = null,
        IWallpaperSourceProviderRegistry? sourceRegistry = null,
        IWallpaperEngineInstallationPreferenceManager?
            wallpaperEngineInstallationPreferenceManager = null)
    {
        _wallpaper = wallpaper ?? throw new ArgumentNullException(nameof(wallpaper));
        _preferencesStore =
            preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _previewMedia = previewMedia ?? AppWallpaperSources.Preview;
        _sourceRegistry = sourceRegistry ??
            (_previewMedia as SafeMediaPreviewService)?.SourceRegistry ??
            AppWallpaperSources.Registry;
        _wallpaperEngineInstallationPreferences =
            wallpaperEngineInstallationPreferenceManager is null
                ? null
                : new WallpaperEngineInstallationPreferenceCoordinator(
                    wallpaperEngineInstallationPreferenceManager);
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

    public SettingsV3 SavedDesired => ConfigurationState.SavedDesired;

    public SettingsV3? ActiveSnapshot => ConfigurationState.ActiveSnapshot;

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
                OnPropertyChanged(nameof(HasAcknowledgedWebWallpaperPrivacyNotice));
                OnPropertyChanged(nameof(HasPreferredWallpaperEngineInstallation));
            }
        }
    }

    public ThemeMode ThemeMode => Preferences.ThemeMode;

    public bool HasShownTrayTip => Preferences.HasShownTrayTip;

    public bool HasAcknowledgedWebWallpaperPrivacyNotice =>
        Preferences.HasAcknowledgedWebWallpaperPrivacyNotice;

    public bool HasPreferredWallpaperEngineInstallation =>
        _wallpaperEngineInstallationPreferences?
            .HasPreferredWallpaperEngineInstallation == true;

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

    public void SetPersistedSettings(SettingsV3 settings, bool synchronizeEditor)
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

    public void ApplySavedSettingsToEditor(SettingsV3 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _editor.ApplySettings(settings);
    }

    public void SetActive(SettingsV3 settings)
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
            var loaded = await _preferencesStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(true);
            _wallpaperEngineInstallationPreferences?.ApplyLoadedPreferences(loaded);
            Preferences = loaded;
            OnPropertyChanged(nameof(HasPreferredWallpaperEngineInstallation));
        }
        finally
        {
            _ = _preferencesMutationGate.Release();
        }
    }

    public void UseDefaultPreferences()
    {
        var preferences = AppPreferencesV1.CreateDefault();
        _wallpaperEngineInstallationPreferences?.ApplyLoadedPreferences(preferences);
        Preferences = preferences;
        OnPropertyChanged(nameof(HasPreferredWallpaperEngineInstallation));
    }

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

    public Task AcknowledgeWebWallpaperPrivacyNoticeAsync(
        CancellationToken cancellationToken) =>
        Preferences.HasAcknowledgedWebWallpaperPrivacyNotice
            ? Task.CompletedTask
            : UpdatePreferencesAsync(
                current => current with
                {
                    HasAcknowledgedWebWallpaperPrivacyNotice = true,
                },
                cancellationToken);

    public async Task SelectWallpaperEngineInstallationAsync(
        string selectedPath,
        CancellationToken cancellationToken)
    {
        if (_wallpaperEngineInstallationPreferences is null)
        {
            throw new InvalidOperationException(
                "Wallpaper Engine installation selection is not configured for this host.");
        }

        await _preferencesMutationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        try
        {
            Preferences = await _wallpaperEngineInstallationPreferences
                .SelectAsync(Preferences, selectedPath, cancellationToken)
                .ConfigureAwait(true);
            OnPropertyChanged(nameof(HasPreferredWallpaperEngineInstallation));
        }
        finally
        {
            _ = _preferencesMutationGate.Release();
        }
    }

    public async Task ClearWallpaperEngineInstallationAsync(
        CancellationToken cancellationToken)
    {
        if (_wallpaperEngineInstallationPreferences is null)
        {
            throw new InvalidOperationException(
                "Wallpaper Engine installation selection is not configured for this host.");
        }

        await _preferencesMutationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        try
        {
            Preferences = await _wallpaperEngineInstallationPreferences
                .ClearAsync(Preferences, cancellationToken)
                .ConfigureAwait(true);
            OnPropertyChanged(nameof(HasPreferredWallpaperEngineInstallation));
        }
        finally
        {
            _ = _preferencesMutationGate.Release();
        }
    }

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
            var preferences = AppPreferencesV1.CreateDefault();
            _wallpaperEngineInstallationPreferences?.ApplyLoadedPreferences(preferences);
            Preferences = preferences;
            OnPropertyChanged(nameof(HasPreferredWallpaperEngineInstallation));
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
            var settings = SettingsV3.CreateDefault();
            RefreshRecents(settings);
            return new WallpaperSettingsInitializationResult(settings, exception);
        }
    }

    public async Task<SettingsV3> LoadWallpaperSettingsAsync(
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

    public async Task<SettingsV3> SaveRiskAcceptanceAsync(
        SettingsV3 baseline,
        bool accepted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        return await _wallpaper
            .SetRiskAcceptanceAsync(accepted, cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<SettingsV3> RemoveRecentAsync(
        SettingsV3 baseline,
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

    public async Task<SettingsV3> RemoveRecentAsync(
        SettingsV3 baseline,
        Guid mediaId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (baseline.FindMedia(mediaId) is null)
        {
            return baseline.CreateSnapshot();
        }

        var saved = await _wallpaper
            .RemoveRecentMediaAsync(mediaId, cancellationToken)
            .ConfigureAwait(true);
        RefreshRecents(saved);
        return saved;
    }

    public async Task<SettingsV3> ClearRecentsAsync(
        SettingsV3 baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var saved = await _wallpaper
            .ClearRecentMediaAsync(cancellationToken)
            .ConfigureAwait(true);
        RefreshRecents(saved);
        return saved;
    }

    public async Task<SettingsV3> ResetWallpaperSettingsAsync(
        CancellationToken cancellationToken)
    {
        var saved = await _wallpaper
            .ResetWallpaperSettingsAsync(cancellationToken)
            .ConfigureAwait(true);
        ClearProtection();
        RefreshRecents(saved);
        return saved;
    }

    public async Task<SettingsV3> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken)
    {
        var restored = await _wallpaper
            .RestoreVersion1BackupAsync(cancellationToken)
            .ConfigureAwait(true);
        ClearProtection();
        RefreshRecents(restored);
        return restored;
    }

    public void RefreshRecents(SettingsV3 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.CreateSnapshot();
        Recents.Clear();
        foreach (var mediaId in snapshot.RecentMediaIds.Take(SettingsV3.MaximumRecentMediaIds))
        {
            var media = snapshot.FindMedia(mediaId);
            if (media is null)
            {
                continue;
            }

            // V3 keeps Scene/Web identities durable even when their project is temporarily
            // unavailable. Unknown and application content never enters an activation path.
            if (media.LastKnownKind == MediaKind.None &&
                !IsSupportedDynamicReference(media))
            {
                continue;
            }

            if (!_sourceRegistry.TryGet(media.SourceKind, out _))
            {
                continue;
            }

            Recents.Add(
                new RecentMediaItem(
                    media,
                    GetRecentDisplayName(media),
                    media.LastKnownKind is not (MediaKind.Image or MediaKind.Video) ||
                    _previewMedia.IsAvailable(media)));
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
                FutureSettingsVersionException;
        HasVersion1Backup = exception switch
        {
            SettingsRecoveryRequiredException recovery =>
                recovery.HasVersion1Backup,
            FutureSettingsVersionException future =>
                future.HasVersion1Backup,
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

    private static string GetRecentDisplayName(MediaReference media)
    {
        if (!string.IsNullOrWhiteSpace(media.LastKnownDisplayName))
        {
            return media.LastKnownDisplayName;
        }

        if (media.SourceKind is MediaSourceKind.LocalFile or
            MediaSourceKind.WallpaperEngineLocalProject)
        {
            var fileName = Path.GetFileName(media.SourceIdentifier);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return media.SourceKind == MediaSourceKind.WallpaperEngineWorkshopProject
            ? $"Workshop {media.SourceIdentifier}"
            : media.SourceIdentifier;
    }

    private static bool IsSupportedDynamicReference(MediaReference media) =>
        (media.SourceKind is
            MediaSourceKind.WallpaperEngineLocalProject or
            MediaSourceKind.WallpaperEngineWorkshopProject) &&
        media.LastKnownContentKind is
            WallpaperContentKind.Scene or WallpaperContentKind.Web;
}
