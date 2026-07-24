using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackdropForCodex.App.Models;
using BackdropForCodex.App.Services.Errors;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.App.Services.Preferences;
using BackdropForCodex.App.Services.Wallpaper;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using BackdropForCodex.Core.Shortcuts;

namespace BackdropForCodex.App.ViewModels;

/// <summary>
/// Owns editable, persisted, and active wallpaper state independently so a failed launch can still
/// be represented as "saved, but not active".
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    public const double MaximumOverlay = WallpaperEditorViewModel.MaximumOverlay;

    private readonly IWallpaperApplicationService _wallpaper;
    private readonly IWallpaperApplicationCapabilitySource? _capabilitySource;
    private readonly IUserFacingErrorMapper _errorMapper;
    private readonly IAppTextProvider _text;
    private readonly SynchronizationContext? _uiContext;
    private readonly object _initializationLock = new();
    private Task? _initializationTask;
    private CancellationTokenSource? _operationCancellation;
    private WallpaperOperationProgress _operationProgress =
        WallpaperOperationProgress.Idle;
    private bool _isPaused;
    private bool _shortcutNeedsRetry;
    private string _operationStage = string.Empty;
    private string _statusTitle = string.Empty;
    private string _statusMessage = string.Empty;
    private UiStatusTone _statusTone;
    private bool _isStatusOpen;
    private bool _isDisposed;
    private WallpaperRuntimePhase _runtimePhase = WallpaperRuntimePhase.Idle;

    public MainWindowViewModel(
        IWallpaperApplicationService wallpaper,
        IAppPreferencesStore preferencesStore,
        IUserFacingErrorMapper errorMapper,
        IAppTextProvider text,
        ISafeMediaPreviewService? previewMedia = null)
    {
        _wallpaper = wallpaper ?? throw new ArgumentNullException(nameof(wallpaper));
        _capabilitySource = wallpaper as IWallpaperApplicationCapabilitySource;
        _errorMapper = errorMapper ?? throw new ArgumentNullException(nameof(errorMapper));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        var mediaPreview = previewMedia ?? SafeMediaPreviewService.Shared;
        Editor = new WallpaperEditorViewModel(_text, mediaPreview);
        Editor.PropertyChanged += Editor_PropertyChanged;
        Settings = new SettingsManagementViewModel(
            wallpaper,
            preferencesStore,
            Editor,
            mediaPreview);
        Settings.PropertyChanged += Settings_PropertyChanged;
        Settings.Recents.CollectionChanged += Recents_CollectionChanged;
        _uiContext = SynchronizationContext.Current;
        _wallpaper.StatusChanged += Wallpaper_StatusChanged;
        if (_capabilitySource is not null)
        {
            _capabilitySource.CapabilitiesChanged += Wallpaper_CapabilitiesChanged;
        }

        TogglePauseCommand = new AsyncRelayCommand(TogglePauseAsync, CanTogglePause);
        DisableCommand = new AsyncRelayCommand(DisableAsync, CanDisable);
        CancelCommand =
            new RelayCommand(
                CancelCurrentOperation,
                () => OperationProgress.CanCancel);
        RetryShortcutCommand = new AsyncRelayCommand(RetryShortcutAsync, CanRetryShortcut);
        ClearRecentsCommand =
            new AsyncRelayCommand(
                ClearRecentsAsync,
                () => CanEdit && Recents.Count > 0);
    }

    public ObservableCollection<RecentMediaItem> Recents => Settings.Recents;

    public WallpaperEditorViewModel Editor { get; }

    public SettingsManagementViewModel Settings { get; }

    public IAsyncRelayCommand TogglePauseCommand { get; }

    public IAsyncRelayCommand DisableCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand RetryShortcutCommand { get; }

    public IAsyncRelayCommand ClearRecentsCommand { get; }

    public WallpaperConfigurationState ConfigurationState => Settings.ConfigurationState;

    public SettingsV1 SavedDesired => Settings.SavedDesired;

    public SettingsV1? ActiveSnapshot => Settings.ActiveSnapshot;

    public WallpaperOperationProgress OperationProgress
    {
        get => _operationProgress;
        private set
        {
            if (SetProperty(ref _operationProgress, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(CanOpenSettings));
                OnPropertyChanged(nameof(CanAdjustFocus));
                OnPropertyChanged(nameof(CanRestoreVersion1Backup));
                Editor.SetEditingEnabled(CanEdit);
                NotifyCommandStateChanged();
            }
        }
    }

    public AppPreferencesV1 Preferences => Settings.Preferences;

    public ThemeMode ThemeMode => Settings.ThemeMode;

    public bool HasShownTrayTip => Settings.HasShownTrayTip;

    public string? SelectedMediaPath => Editor.SelectedMediaPath;

    public string SelectedMediaName => Editor.SelectedMediaName;

    public bool HasSelectedMedia => Editor.HasSelectedMedia;

    public MediaKind SelectedMediaKind => Editor.SelectedMediaKind;

    public bool IsVideoSelected => Editor.IsVideoSelected;

    public bool IsMediaMissing => Editor.IsMediaMissing;

    public WallpaperFit Fit
    {
        get => Editor.Fit;
        set => Editor.Fit = value;
    }

    public bool IsCoverFit => Editor.IsCoverFit;

    public bool CanAdjustFocus => Editor.CanAdjustFocus;

    public double FocusX
    {
        get => Editor.FocusX;
        set => Editor.FocusX = value;
    }

    public double FocusY
    {
        get => Editor.FocusY;
        set => Editor.FocusY = value;
    }

    public string FocusLabel => Editor.FocusLabel;

    public double PanelOpacity
    {
        get => Editor.PanelOpacity;
        set => Editor.PanelOpacity = value;
    }

    public string PanelOpacityPercent => Editor.PanelOpacityPercent;

    public double BlurPx
    {
        get => Editor.BlurPx;
        set => Editor.BlurPx = value;
    }

    public string BlurLabel => Editor.BlurLabel;

    public double DarkOverlay
    {
        get => Editor.DarkOverlay;
        set => Editor.DarkOverlay = value;
    }

    public string DarkOverlayPercent => Editor.DarkOverlayPercent;

    public double LightOverlay
    {
        get => Editor.LightOverlay;
        set => Editor.LightOverlay = value;
    }

    public string LightOverlayPercent => Editor.LightOverlayPercent;

    public bool AcceptedCdpRisk => Editor.AcceptedCdpRisk;

    public bool IsBusy => OperationProgress.IsBusy;

    public bool CanEdit => !IsBusy && !HasProtectedSettings;

    public bool CanOpenSettings => !IsBusy;

    public bool HasProtectedSettings => Settings.HasProtectedSettings;

    public bool HasVersion1Backup => Settings.HasVersion1Backup;

    public bool CanRestoreVersion1Backup =>
        !IsBusy &&
        HasProtectedSettings &&
        HasVersion1Backup &&
        Settings.SupportsVersion1BackupRestore;

    public bool IsActive => Settings.IsActive;

    public WallpaperRuntimePhase RuntimePhase
    {
        get => _runtimePhase;
        private set => SetProperty(ref _runtimePhase, value);
    }

    internal CompatibilityCapabilities? CompatibilityCapabilities =>
        (_wallpaper as IWallpaperApplicationCapabilitySource)?.Capabilities;

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
                NotifyCommandStateChanged();
            }
        }
    }

    public bool IsSavedButInactive => Settings.IsSavedButInactive;

    public bool ShortcutNeedsRetry
    {
        get => _shortcutNeedsRetry;
        private set
        {
            if (SetProperty(ref _shortcutNeedsRetry, value))
            {
                NotifyCommandStateChanged();
            }
        }
    }

    public string OperationStage
    {
        get => _operationStage;
        private set => SetProperty(ref _operationStage, value);
    }

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetProperty(ref _statusTitle, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public UiStatusTone StatusTone
    {
        get => _statusTone;
        private set => SetProperty(ref _statusTone, value);
    }

    public bool IsStatusOpen
    {
        get => _isStatusOpen;
        set => SetProperty(ref _isStatusOpen, value);
    }

    public bool IsDraftDirty => Settings.IsDraftDirty;

    public string ApplyButtonText => IsActive
        ? Text("Action_ApplyChanges", "Apply changes")
        : Text("Action_ApplyAndLaunch", "Apply & launch Codex");

    public string PauseButtonText => IsPaused
        ? Text("Action_ResumeVideo", "Resume video")
        : Text("Action_PauseVideo", "Pause video");

    public void SetFocus(double focusX, double focusY) =>
        Editor.SetFocus(focusX, focusY);

    public void ResetFocus() => Editor.ResetFocus();

    public void NudgeFocus(double horizontalDelta, double verticalDelta) =>
        Editor.NudgeFocus(horizontalDelta, verticalDelta);

    public Task InitializeAsync()
    {
        lock (_initializationLock)
        {
            return _initializationTask ??= InitializeCoreAsync();
        }
    }

    public void SelectMedia(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        if (!CanEdit)
        {
            return;
        }

        Editor.SelectMedia(mediaPath);
        if (IsMediaMissing)
        {
            ShowStatus(
                Text("Status_MissingTitle", "Media unavailable"),
                Text(
                    "Status_MissingMessage",
                    "The saved file no longer exists. Choose another file or remove it from recent media."),
                UiStatusTone.Warning);
        }
    }

    public async Task AcceptRiskAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (AcceptedCdpRisk || IsBusy || HasProtectedSettings)
        {
            return;
        }

        BeginOperation(
            Text("Stage_Saving", "Saving settings…"),
            cancellationToken,
            WallpaperOperationStage.Saving);
        try
        {
            var saved = await Settings
                .SaveRiskAcceptanceAsync(
                    SavedDesired,
                    accepted: true,
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
            Settings.SetPersistedSettings(saved, synchronizeEditor: false);
            Editor.SetRiskAccepted(accepted: true);
            ShowStatus(
                Text("Status_RiskAcceptedTitle", "Enhanced launch enabled"),
                Text(
                    "Status_RiskAcceptedMessage",
                    "The local debugging-port acknowledgement was saved."),
                UiStatusTone.Success);
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task RevokeRiskAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (IsBusy || HasProtectedSettings)
        {
            return;
        }

        BeginOperation(
            Text("Stage_Saving", "Saving settings…"),
            cancellationToken,
            WallpaperOperationStage.Saving);
        try
        {
            var saved = await Settings
                .SaveRiskAcceptanceAsync(
                    SavedDesired,
                    accepted: false,
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
            Settings.SetPersistedSettings(saved, synchronizeEditor: false);
            Editor.SetRiskAccepted(accepted: false);
            ShowStatus(
                Text("Status_RiskRevokedTitle", "Enhanced launch disabled"),
                Text(
                    "Status_RiskRevokedMessage",
                    "Future launches will require acknowledgement again."),
                UiStatusTone.Informational);
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (IsBusy || HasProtectedSettings)
        {
            return false;
        }

        if (SelectedMediaPath is null || IsMediaMissing)
        {
            ShowStatus(
                Text("Status_SelectMediaTitle", "Choose a wallpaper"),
                Text("Status_SelectMediaMessage", "Select an available image or muted video first."),
                UiStatusTone.Warning);
            return false;
        }

        if (!AcceptedCdpRisk)
        {
            ShowStatus(
                Text("Status_RiskRequiredTitle", "Review enhanced launch"),
                Text(
                    "Status_RiskRequiredMessage",
                    "Review the local Chromium debugging-port notice before applying."),
                UiStatusTone.Warning);
            return false;
        }

        var request = ConfigurationState.Draft with { AcceptedCdpRisk = true };

        return await RunApplyAsync(request, cancellationToken).ConfigureAwait(true);
    }

    public async Task<AutoLaunchOutcome> AutoLaunchAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (HasProtectedSettings)
        {
            return AutoLaunchOutcome.Failed;
        }

        if (SelectedMediaPath is null || IsMediaMissing)
        {
            ShowStatus(
                Text("Status_AutoLaunchNeedsMediaTitle", "Wallpaper needs attention"),
                Text(
                    "Status_AutoLaunchNeedsMediaMessage",
                    "Choose an available wallpaper before using the enhanced shortcut."),
                UiStatusTone.Warning);
            return AutoLaunchOutcome.NeedsMedia;
        }

        if (!AcceptedCdpRisk)
        {
            ShowStatus(
                Text("Status_RiskRequiredTitle", "Review enhanced launch"),
                Text(
                    "Status_RiskRequiredMessage",
                    "Review the local Chromium debugging-port notice before applying."),
                UiStatusTone.Warning);
            return AutoLaunchOutcome.NeedsRiskAcknowledgement;
        }

        return await ApplyAsync(cancellationToken).ConfigureAwait(true)
            ? AutoLaunchOutcome.Applied
            : AutoLaunchOutcome.Failed;
    }

    public async Task RemoveRecentAsync(
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (IsBusy || HasProtectedSettings)
        {
            return;
        }

        BeginOperation(
            Text("Stage_Saving", "Saving settings…"),
            cancellationToken,
            WallpaperOperationStage.Saving);
        try
        {
            var saved = await Settings
                .RemoveRecentAsync(
                    SavedDesired,
                    mediaPath,
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
            Settings.SetPersistedSettings(saved, synchronizeEditor: false);
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task ClearRecentsAsync()
    {
        if (IsBusy || HasProtectedSettings)
        {
            return;
        }

        var operationStarted = false;
        try
        {
            await InitializeAsync().ConfigureAwait(true);
            if (IsBusy || HasProtectedSettings)
            {
                return;
            }

            BeginOperation(
                Text("Stage_Saving", "Saving settings…"),
                CancellationToken.None,
                WallpaperOperationStage.Saving);
            operationStarted = true;
            var saved = await Settings
                .ClearRecentsAsync(
                    SavedDesired,
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
            Settings.SetPersistedSettings(saved, synchronizeEditor: false);
            ShowStatus(
                Text("Status_RecentsClearedTitle", "Recent media cleared"),
                Text(
                    "Status_RecentsClearedMessage",
                    "No wallpaper files were deleted from disk."),
                UiStatusTone.Success);
        }
        catch (Exception exception)
        {
            ShowError(_errorMapper.Map(exception, UserFacingOperation.SaveWallpaperSettings));
        }
        finally
        {
            if (operationStarted)
            {
                EndOperation();
            }
        }
    }

    public async Task SetThemeModeAsync(
        ThemeMode themeMode,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        await Settings
            .SetThemeModeAsync(themeMode, cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task MarkTrayTipShownAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        await Settings
            .MarkTrayTipShownAsync(cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task ResetEverythingAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        BeginOperation(
            Text("Stage_Resetting", "Resetting Backdrop for Codex…"),
            cancellationToken,
            WallpaperOperationStage.Resetting);
        var failures = new List<Exception>();
        OperationCanceledException? cancellationException = null;
        try
        {
            await TryStepAsync(
                () => _wallpaper.DisableAsync(_operationCancellation!.Token),
                failures).ConfigureAwait(true);
            await TryStepAsync(
                async () =>
                {
                    var saved = await Settings
                        .ResetWallpaperSettingsAsync(_operationCancellation!.Token)
                        .ConfigureAwait(true);
                    _ = saved;
                },
                failures).ConfigureAwait(true);
            await TryStepAsync(
                () => Settings.ResetPreferencesAsync(_operationCancellation!.Token),
                failures).ConfigureAwait(true);

            _operationCancellation!.Token.ThrowIfCancellationRequested();
            try
            {
                _ = _wallpaper.DeleteOwnedShortcut();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

        }
        catch (OperationCanceledException exception)
        {
            cancellationException = exception;
        }
        finally
        {
            await ReconcileAfterResetAsync(failures).ConfigureAwait(true);
            if (cancellationException is not null && failures.Count == 0)
            {
                ShowError(
                    _errorMapper.Map(
                        cancellationException,
                        UserFacingOperation.General));
            }
            else if (failures.Count == 0)
            {
                ShowStatus(
                    Text("Status_ResetCompleteTitle", "Reset complete"),
                    Text(
                        "Status_ResetCompleteMessage",
                        "Settings, recent media, acknowledgement, UI preferences, and the owned shortcut were reset."),
                    UiStatusTone.Success);
            }
            else
            {
                ShowError(
                    _errorMapper.Map(
                        new AggregateException(failures),
                        UserFacingOperation.General));
            }

            EndOperation();
        }
    }

    public async Task RestoreVersion1BackupAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (!CanRestoreVersion1Backup)
        {
            return;
        }

        BeginOperation(
            Text("Stage_Saving", "Saving settings…"),
            cancellationToken,
        WallpaperOperationStage.Saving);
        try
        {
            var restored = await Settings
                .RestoreVersion1BackupAsync(_operationCancellation!.Token)
                .ConfigureAwait(true);
            Settings.SetPersistedSettings(restored, synchronizeEditor: false);
            Settings.ApplySavedSettingsToEditor(restored);
            ShowStatus(
                Text("Status_BackupRestoredTitle", "V1 backup restored"),
                Text(
                    "Status_BackupRestoredMessage",
                    "The preserved V1 backup was migrated into Settings V2. The read-only backup remains available for manual downgrade."),
                UiStatusTone.Success);
        }
        catch (Exception exception)
        {
            ShowError(
                _errorMapper.Map(
                    exception,
                    UserFacingOperation.LoadWallpaperSettings));
        }
        finally
        {
            EndOperation();
        }
    }

    public void ShowUnexpectedError(Exception exception) =>
        ShowError(_errorMapper.Map(exception));

    private async Task InitializeCoreAsync()
    {
        var preferenceWarning = false;
        try
        {
            await Settings
                .LoadPreferencesAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Settings.UseDefaultPreferences();
            preferenceWarning = true;
            ShowError(_errorMapper.Map(exception));
        }

        var wallpaperSettings = await Settings
            .InitializeWallpaperSettingsAsync(CancellationToken.None)
            .ConfigureAwait(true);
        Settings.SetPersistedSettings(
            wallpaperSettings.Settings,
            synchronizeEditor: false);
        if (wallpaperSettings.Error is not null)
        {
            ShowError(
                _errorMapper.Map(
                    wallpaperSettings.Error,
                    UserFacingOperation.LoadWallpaperSettings));
        }

        Settings.ApplySavedSettingsToEditor(SavedDesired);
        Settings.SetRuntimeActivity(_wallpaper.IsActive);
        IsPaused = _wallpaper.IsPaused;
        if (!preferenceWarning && !IsStatusOpen)
        {
            ShowStatus(
                Text("Status_ReadyTitle", "Ready"),
                Text(
                    "Status_ReadyMessage",
                    "Choose local media, tune the glass panel, then apply when ready."),
                UiStatusTone.Informational);
        }
    }

    private async Task<bool> RunApplyAsync(
        SettingsV1 request,
        CancellationToken cancellationToken)
    {
        BeginOperation(Text("Stage_Validating", "Validating media and Codex…"), cancellationToken);
        var foregroundFailure = false;
        ShortcutNeedsRetry = false;
        try
        {
            var result = await _wallpaper
                .ApplyAsync(request, _operationCancellation!.Token)
                .ConfigureAwait(true);
            Settings.SetActive(result.Settings);
            IsPaused = false;
            ShortcutNeedsRetry = !result.ShortcutReady;

            var capabilities = CompatibilityCapabilities;
            var presentationDegraded =
                capabilities is not null &&
                (!capabilities.GlassStyle.IsAvailable ||
                 !capabilities.AdvancedSurfaces.IsAvailable);
            if (result.ShortcutReady && presentationDegraded)
            {
                ShowStatus(
                    Text("Status_AppliedDegradedTitle", "Wallpaper active with reduced effects"),
                    Text(
                        "Status_AppliedDegradedMessage",
                        "Compatibility checks disabled one or more optional visual effects. The global wallpaper remains active; export a diagnostic report to review capability reason codes."),
                    UiStatusTone.Warning);
            }
            else if (result.ShortcutReady)
            {
                ShowStatus(
                    Text("Status_AppliedTitle", "Wallpaper is active"),
                    Text(
                        "Status_AppliedMessage",
                        "Codex is using the saved wallpaper and the enhanced desktop shortcut is ready."),
                    UiStatusTone.Success);
            }
            else
            {
                ShowStatus(
                    Text("Status_AppliedShortcutFailedTitle", "Wallpaper active"),
                    Text(
                        "Status_AppliedShortcutFailedMessage",
                        "The wallpaper is active, but the desktop shortcut could not be updated. You can retry it."),
                    UiStatusTone.Warning);
            }

            return true;
        }
        catch (Exception exception)
        {
            foregroundFailure = true;
            Settings.SetRuntimeActivity(_wallpaper.IsActive);
            IsPaused = _wallpaper.IsPaused;

            ShowError(_errorMapper.Map(exception, UserFacingOperation.ApplyWallpaper));
            return false;
        }
        finally
        {
            try
            {
                var reloaded = await Settings
                    .LoadWallpaperSettingsAsync(CancellationToken.None)
                    .ConfigureAwait(true);
                Settings.SetPersistedSettings(reloaded, synchronizeEditor: false);
            }
            catch (Exception reloadException)
            {
                if (!foregroundFailure)
                {
                    ShowError(
                        _errorMapper.Map(
                            reloadException,
                            UserFacingOperation.LoadWallpaperSettings));
                }
            }

            Settings.SetRuntimeActivity(_wallpaper.IsActive);
            IsPaused = _wallpaper.IsPaused;
            EndOperation();
        }
    }

    private async Task TogglePauseAsync()
    {
        if (!CanTogglePause())
        {
            return;
        }

        BeginOperation(
            Text("Stage_Updating", "Updating playback…"),
            CancellationToken.None,
            WallpaperOperationStage.Updating);
        try
        {
            var pause = !IsPaused;
            await _wallpaper
                .SetPausedAsync(pause, _operationCancellation!.Token)
                .ConfigureAwait(true);
            Settings.SetRuntimeActivity(_wallpaper.IsActive);
            IsPaused = _wallpaper.IsActive && _wallpaper.IsPaused;
            ShowStatus(
                IsPaused
                    ? Text("Status_PausedTitle", "Video paused")
                    : Text("Status_ResumedTitle", "Video resumed"),
                IsPaused
                    ? Text(
                        "Status_PausedMessage",
                        "Codex and the local preview are paused.")
                    : Text(
                        "Status_ResumedMessage",
                        "Codex and the local preview are playing."),
                UiStatusTone.Success);
        }
        catch (Exception exception)
        {
            Settings.SetRuntimeActivity(_wallpaper.IsActive);
            IsPaused = _wallpaper.IsActive && _wallpaper.IsPaused;
            ShowError(_errorMapper.Map(exception, UserFacingOperation.ApplyWallpaper));
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task DisableAsync()
    {
        if (!CanDisable())
        {
            return;
        }

        BeginOperation(
            Text("Stage_Restoring", "Restoring the official Codex background…"),
            CancellationToken.None,
            WallpaperOperationStage.Restoring);
        try
        {
            await _wallpaper.DisableAsync(_operationCancellation!.Token).ConfigureAwait(true);
            Settings.SetRuntimeActivity(isActive: false);
            IsPaused = false;
            ShowStatus(
                Text("Status_RestoredTitle", "Official background restored"),
                Text(
                    "Status_RestoredMessage",
                    "Saved wallpaper settings remain available for the next launch."),
                UiStatusTone.Success);
        }
        catch (Exception exception)
        {
            Settings.SetRuntimeActivity(_wallpaper.IsActive);
            IsPaused = _wallpaper.IsPaused;
            ShowError(_errorMapper.Map(exception, UserFacingOperation.RestoreWallpaper));
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RetryShortcutAsync()
    {
        if (!CanRetryShortcut())
        {
            return;
        }

        try
        {
            _ = _wallpaper.CreateOrUpdateShortcut();
            ShortcutNeedsRetry = false;
            ShowStatus(
                Text("Status_ShortcutReadyTitle", "Shortcut ready"),
                Text(
                    "Status_ShortcutReadyMessage",
                    "The enhanced desktop shortcut was created or updated."),
                UiStatusTone.Success);
        }
        catch (Exception exception)
        {
            ShortcutNeedsRetry = true;
            ShowError(_errorMapper.Map(exception, UserFacingOperation.CreateShortcut));
        }

        await Task.CompletedTask;
    }

    private void Wallpaper_StatusChanged(
        object? sender,
        WallpaperRuntimeStatusChangedEventArgs eventArgs)
    {
        void Update()
        {
            RuntimePhase = eventArgs.Phase;
            var stage = eventArgs.Phase switch
            {
                WallpaperRuntimePhase.Validating =>
                    Text("Stage_Validating", "Validating media and Codex…"),
                WallpaperRuntimePhase.LaunchingCodex =>
                    Text("Stage_Launching", "Launching Codex securely…"),
                WallpaperRuntimePhase.DiscoveringEndpoint =>
                    Text("Stage_Discovering", "Discovering the local Codex endpoint…"),
                WallpaperRuntimePhase.Applying =>
                    Text("Stage_Applying", "Applying wallpaper and glass effects…"),
                WallpaperRuntimePhase.Stopping =>
                    Text("Stage_Restoring", "Restoring the official Codex background…"),
                _ => string.Empty,
            };
            if (!string.IsNullOrEmpty(stage))
            {
                OperationStage = stage;
            }

            AdvanceOperation(eventArgs.Phase);

            switch (eventArgs.Phase)
            {
                case WallpaperRuntimePhase.Active:
                    Settings.SetRuntimeActivity(isActive: true);
                    IsPaused = false;
                    break;
                case WallpaperRuntimePhase.Paused:
                    Settings.SetRuntimeActivity(isActive: true);
                    IsPaused = true;
                    break;
                case WallpaperRuntimePhase.Idle:
                    Settings.SetRuntimeActivity(isActive: false);
                    IsPaused = false;
                    break;
                case WallpaperRuntimePhase.Faulted:
                    Settings.SetRuntimeActivity(_wallpaper.IsActive);
                    IsPaused = _wallpaper.IsActive && _wallpaper.IsPaused;
                    if (!IsBusy ||
                        OperationProgress.Stage == WallpaperOperationStage.Saving)
                    {
                        ShowStatus(
                            Text("Status_RuntimeStoppedTitle", "Wallpaper connection stopped"),
                            Text(
                                "Status_RuntimeStoppedMessage",
                                "The runtime connection ended and the app attempted to restore the official background."),
                            UiStatusTone.Error);
                    }

                    break;
            }
        }

        if (_uiContext is null || ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            Update();
        }
        else
        {
            _uiContext.Post(_ => Update(), null);
        }
    }

    private void Settings_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        OnPropertyChanged(eventArgs.PropertyName);
        if (eventArgs.PropertyName is
            nameof(SettingsManagementViewModel.ConfigurationState))
        {
            OnPropertyChanged(nameof(ApplyButtonText));
            NotifyCommandStateChanged();
        }

        if (eventArgs.PropertyName is nameof(SettingsManagementViewModel.HasProtectedSettings))
        {
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanAdjustFocus));
            Editor.SetEditingEnabled(CanEdit);
            NotifyCommandStateChanged();
        }

        if (eventArgs.PropertyName is
            nameof(SettingsManagementViewModel.HasProtectedSettings) or
            nameof(SettingsManagementViewModel.HasVersion1Backup))
        {
            OnPropertyChanged(nameof(CanRestoreVersion1Backup));
        }
    }

    private void Recents_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs) =>
        ClearRecentsCommand.NotifyCanExecuteChanged();

    private void Editor_PropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        OnPropertyChanged(eventArgs.PropertyName);
    }

    private void BeginOperation(
        string stage,
        CancellationToken cancellationToken,
        WallpaperOperationStage operationStage = WallpaperOperationStage.Validating)
    {
        _operationCancellation?.Dispose();
        _operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        OperationStage = stage;
        OperationProgress = WallpaperOperationProgress.Begin(operationStage);
    }

    private void AdvanceOperation(WallpaperRuntimePhase phase)
    {
        var nextStage = phase switch
        {
            WallpaperRuntimePhase.LaunchingCodex => WallpaperOperationStage.Launching,
            WallpaperRuntimePhase.DiscoveringEndpoint => WallpaperOperationStage.Discovering,
            WallpaperRuntimePhase.Applying => WallpaperOperationStage.Applying,
            WallpaperRuntimePhase.Stopping => WallpaperOperationStage.Restoring,
            _ => WallpaperOperationStage.Idle,
        };
        if (!OperationProgress.IsBusy ||
            nextStage is WallpaperOperationStage.Idle ||
            nextStage <= OperationProgress.Stage)
        {
            return;
        }

        OperationProgress = OperationProgress.AdvanceTo(nextStage);
    }

    private void EndOperation()
    {
        OperationProgress = OperationProgress.Complete();
        OperationStage = string.Empty;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private void CancelCurrentOperation()
    {
        try
        {
            OperationProgress = OperationProgress.RequestCancellation();
            _operationCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation completed while the cancel input was being delivered.
        }
    }

    private bool CanTogglePause() =>
        !IsBusy &&
        IsActive &&
        ActiveSnapshot?.MediaKind == MediaKind.Video;

    private bool CanDisable() => !IsBusy && IsActive;

    private bool CanRetryShortcut() => !IsBusy && ShortcutNeedsRetry;

    private void NotifyCommandStateChanged()
    {
        TogglePauseCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RetryShortcutCommand.NotifyCanExecuteChanged();
        ClearRecentsCommand.NotifyCanExecuteChanged();
    }

    private void ShowError(UserFacingError error) =>
        ShowStatus(
            error.Title,
            string.IsNullOrWhiteSpace(error.Recovery)
                ? error.Message
                : $"{error.Message} {error.Recovery}",
            error.Code == UserFacingErrorCode.OperationCanceled
                ? UiStatusTone.Warning
                : UiStatusTone.Error);

    private void ShowStatus(string title, string message, UiStatusTone tone)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusTone = tone;
        IsStatusOpen = true;
    }

    private string Text(string key, string fallback)
    {
        var localized = _text.GetString(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? fallback
            : localized;
    }

    private void Wallpaper_CapabilitiesChanged(
        object? sender,
        WallpaperInjectionCapabilitiesChangedEventArgs eventArgs)
    {
        void Update()
        {
            OnPropertyChanged(nameof(CompatibilityCapabilities));
            var visualCapabilityDropped =
                (eventArgs.Previous.GlassStyle.IsAvailable &&
                 !eventArgs.Current.GlassStyle.IsAvailable) ||
                (eventArgs.Previous.AdvancedSurfaces.IsAvailable &&
                 !eventArgs.Current.AdvancedSurfaces.IsAvailable);
            if (IsActive && visualCapabilityDropped)
            {
                ShowStatus(
                    Text("Status_AppliedDegradedTitle", "Wallpaper active with reduced effects"),
                    Text(
                        "Status_AppliedDegradedMessage",
                        "Compatibility checks disabled one or more optional visual effects. The global wallpaper remains active; export a diagnostic report to review capability reason codes."),
                    UiStatusTone.Warning);
            }
        }

        if (_uiContext is null ||
            ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            Update();
        }
        else
        {
            _uiContext.Post(_ => Update(), null);
        }
    }

    private static async Task TryStepAsync(
        Func<Task> operation,
        List<Exception> failures)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async Task ReconcileAfterResetAsync(List<Exception> failures)
    {
        try
        {
            var saved = await Settings
                .LoadWallpaperSettingsAsync(CancellationToken.None)
                .ConfigureAwait(true);
            Settings.SetPersistedSettings(saved, synchronizeEditor: false);
            Settings.ApplySavedSettingsToEditor(saved);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await Settings
                .LoadPreferencesAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        Settings.SetRuntimeActivity(_wallpaper.IsActive);
        IsPaused = _wallpaper.IsActive && _wallpaper.IsPaused;
        ShortcutNeedsRetry = false;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _wallpaper.StatusChanged -= Wallpaper_StatusChanged;
        if (_capabilitySource is not null)
        {
            _capabilitySource.CapabilitiesChanged -= Wallpaper_CapabilitiesChanged;
        }
        Editor.PropertyChanged -= Editor_PropertyChanged;
        Settings.PropertyChanged -= Settings_PropertyChanged;
        Settings.Recents.CollectionChanged -= Recents_CollectionChanged;
        Settings.Dispose();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        GC.SuppressFinalize(this);
    }

}
