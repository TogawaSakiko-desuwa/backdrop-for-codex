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
    private readonly WallpaperProfileCardProjection _profileProjection;
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
    private WallpaperProfileCardItem? _selectedProfileCard;
    private bool _isSynchronizingProfileSelection;
    private long _latestApplySequence;

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
        _profileProjection = new WallpaperProfileCardProjection(_text, mediaPreview);
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
        CreateProfileCommand =
            new RelayCommand(CreateProfile, () => CanEditDraft);
        DuplicateProfileCommand =
            new RelayCommand<WallpaperProfileCardItem>(
                DuplicateProfile,
                _ => CanEditDraft);
        RenameProfileCommand =
            new AsyncRelayCommand<WallpaperProfileCardItem>(
                RenameProfileAsync,
                _ => CanEditDraft);
        DeleteProfileCommand =
            new AsyncRelayCommand<WallpaperProfileCardItem>(
                DeleteProfileAsync,
                item => CanEditDraft && item is not null && ProfileCards.Count > 1);
    }

    public ObservableCollection<RecentMediaItem> Recents => Settings.Recents;

    public WallpaperEditorViewModel Editor { get; }

    public SettingsManagementViewModel Settings { get; }

    public IAsyncRelayCommand TogglePauseCommand { get; }

    public IAsyncRelayCommand DisableCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand RetryShortcutCommand { get; }

    public IAsyncRelayCommand ClearRecentsCommand { get; }

    public IRelayCommand CreateProfileCommand { get; }

    public IRelayCommand<WallpaperProfileCardItem> DuplicateProfileCommand { get; }

    public IAsyncRelayCommand<WallpaperProfileCardItem> RenameProfileCommand { get; }

    public IAsyncRelayCommand<WallpaperProfileCardItem> DeleteProfileCommand { get; }

    public ObservableCollection<WallpaperProfileCardItem> ProfileCards { get; } = [];

    public WallpaperProfileCardItem? SelectedProfileCard
    {
        get => _selectedProfileCard;
        set
        {
            if (!SetProperty(ref _selectedProfileCard, value) ||
                value is null ||
                _isSynchronizingProfileSelection)
            {
                return;
            }

            _wallpaper.SelectProfile(value.ProfileId);
            Editor.ApplySettings(_wallpaper.Workspace.Draft);
        }
    }

    public WallpaperConfigurationState ConfigurationState => Settings.ConfigurationState;

    public SettingsV2 SavedDesired => Settings.SavedDesired;

    public SettingsV2? ActiveSnapshot => Settings.ActiveSnapshot;

    public WallpaperOperationProgress OperationProgress
    {
        get => _operationProgress;
        private set
        {
            if (SetProperty(ref _operationProgress, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(FooterStatusText));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(CanEditDraft));
                OnPropertyChanged(nameof(CanSubmitApply));
                OnPropertyChanged(nameof(CanClearSelectedMedia));
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

    public bool RequiresCdpRisk => HasSelectedMedia && !AcceptedCdpRisk;

    public bool IsBusy => OperationProgress.IsBusy;

    public bool CanEditDraft =>
        !HasProtectedSettings &&
        OperationProgress.Stage != WallpaperOperationStage.Resetting;

    public bool CanSubmitApply =>
        !HasProtectedSettings &&
        OperationProgress.Stage is not
            WallpaperOperationStage.Resetting and not
            WallpaperOperationStage.Restoring;

    public bool CanClearSelectedMedia => CanEditDraft && HasSelectedMedia;

    public bool CanEdit => CanEditDraft;

    public bool CanOpenSettings =>
        OperationProgress.Stage != WallpaperOperationStage.Resetting;

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

    internal WallpaperCompatibilitySnapshot WallpaperCompatibility =>
        _capabilitySource?.Compatibility ?? WallpaperCompatibilitySnapshot.NotEvaluated;

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
        private set
        {
            if (SetProperty(ref _operationStage, value))
            {
                OnPropertyChanged(nameof(FooterStatusText));
            }
        }
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

    public string WorkspaceStatusText
    {
        get
        {
            var workspace = _wallpaper.Workspace;
            if (workspace.RuntimeSurface.Kind == WallpaperRuntimeSurfaceKind.Faulted)
            {
                return Text("Workspace_ActivationFailed", "Activation failed");
            }

            if (workspace.Error is not null)
            {
                return workspace.Error.Stage ==
                        WallpaperWorkspaceErrorStage.Runtime &&
                    IsSavedButInactive
                    ? Text(
                        "Workspace_SavedNotActive",
                        "Saved, not activated")
                    : Text("Workspace_ActivationFailed", "Activation failed");
            }

            if (IsDraftDirty)
            {
                return Text("Workspace_DraftUnsaved", "Draft has unsaved changes");
            }

            if (IsSavedButInactive)
            {
                return Text(
                    "Workspace_SavedNotActive",
                    "Saved, not activated");
            }

            if (workspace.RuntimeSurface.Kind ==
                WallpaperRuntimeSurfaceKind.MediaActive)
            {
                return Text("Workspace_MediaActive", "Media running");
            }

            if (workspace.RuntimeSurface.Kind == WallpaperRuntimeSurfaceKind.Official &&
                workspace.ActiveSnapshot is not null)
            {
                return Text("Workspace_Official", "Official background");
            }

            return workspace.RuntimeSurface.Kind ==
                WallpaperRuntimeSurfaceKind.Disconnected
                ? Text("Workspace_Disconnected", "Codex disconnected")
                : Text("Workspace_Official", "Official background");
        }
    }

    public string FooterStatusText =>
        IsBusy && !string.IsNullOrWhiteSpace(OperationStage)
            ? OperationStage
            : WorkspaceStatusText;

    public string ApplyButtonText => IsActive
        ? Text("Action_ApplyChanges", "Apply changes")
        : Text("Action_ApplyAndLaunch", "Apply & launch Codex");

    public string PauseButtonText => IsPaused
        ? Text("Action_ResumeVideo", "Resume video")
        : Text("Action_PauseVideo", "Pause video");

    internal Func<ProfileRenameRequestedEventArgs, Task<string?>>?
        RenameProfilePromptAsync
    { get; set; }

    internal Func<ProfileDeleteRequestedEventArgs, Task<bool>>?
        DeleteProfilePromptAsync
    { get; set; }

    internal Action? RestoreProfileFocus { get; set; }

    public void SetFocus(double focusX, double focusY) =>
        Editor.SetFocus(focusX, focusY);

    public void ResetFocus() => Editor.ResetFocus();

    public void NudgeFocus(double horizontalDelta, double verticalDelta) =>
        Editor.NudgeFocus(horizontalDelta, verticalDelta);

    public void ClearSelectedMedia()
    {
        if (!CanEditDraft || SelectedProfileCard is null)
        {
            return;
        }

        _wallpaper.ClearMedia(SelectedProfileCard.ProfileId);
        Editor.ApplySettings(_wallpaper.Workspace.Draft);
    }

    private void CreateProfile()
    {
        if (!CanEditDraft)
        {
            return;
        }

        var profile = _wallpaper.CreateProfile(
            Text("Action_NewProfile", "New profile"));
        Editor.ApplySettings(_wallpaper.Workspace.Draft);
        RefreshProfileCards(profile.ProfileId);
        RestoreProfileFocus?.Invoke();
    }

    private void DuplicateProfile(WallpaperProfileCardItem? item)
    {
        if (!CanEditDraft || item is null)
        {
            return;
        }

        var profile = _wallpaper.DuplicateProfile(
            item.ProfileId,
            Text("Profile_CopySuffix", "Copy"));
        Editor.ApplySettings(_wallpaper.Workspace.Draft);
        RefreshProfileCards(profile.ProfileId);
        RestoreProfileFocus?.Invoke();
    }

    private async Task RenameProfileAsync(WallpaperProfileCardItem? item)
    {
        if (!CanEditDraft || item is null)
        {
            return;
        }

        var request = new ProfileRenameRequestedEventArgs(
            item.ProfileId,
            item.Name);
        var newName = RenameProfilePromptAsync is null
            ? null
            : await RenameProfilePromptAsync(request).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        var profile = _wallpaper.RenameProfile(item.ProfileId, newName);
        RefreshProfileCards(profile.ProfileId);
        RestoreProfileFocus?.Invoke();
    }

    private async Task DeleteProfileAsync(WallpaperProfileCardItem? item)
    {
        if (!CanEditDraft || item is null || ProfileCards.Count <= 1)
        {
            return;
        }

        var replacement = ProfileCards.First(card => card.ProfileId != item.ProfileId);
        var request = new ProfileDeleteRequestedEventArgs(
            item.ProfileId,
            item.Name,
            replacement.ProfileId,
            replacement.Name);
        if (DeleteProfilePromptAsync is null ||
            !await DeleteProfilePromptAsync(request).ConfigureAwait(true))
        {
            return;
        }

        _wallpaper.DeleteProfile(item.ProfileId, replacement.ProfileId);
        Editor.ApplySettings(_wallpaper.Workspace.Draft);
        RefreshProfileCards(replacement.ProfileId);
        RestoreProfileFocus?.Invoke();
    }

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
        if (AcceptedCdpRisk || HasProtectedSettings)
        {
            return;
        }

        var preserveCurrentOperation = IsBusy;
        if (!preserveCurrentOperation)
        {
            BeginOperation(
            Text("Stage_Saving", "Saving settings…"),
            cancellationToken,
            WallpaperOperationStage.Saving);
        }

        try
        {
            var saved = await Settings
                .SaveRiskAcceptanceAsync(
                    SavedDesired,
                    accepted: true,
                    preserveCurrentOperation
                        ? cancellationToken
                        : _operationCancellation!.Token)
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
            if (!preserveCurrentOperation)
            {
                EndOperation();
            }
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
        if (!CanSubmitApply)
        {
            return false;
        }

        if (SelectedMediaPath is not null && IsMediaMissing)
        {
            ShowStatus(
                Text("Status_MissingTitle", "Media unavailable"),
                Text(
                    "Status_MissingMessage",
                    "The saved file no longer exists. Choose another file or clear this profile's media."),
                UiStatusTone.Warning);
            return false;
        }

        if (SelectedMediaPath is not null && !AcceptedCdpRisk)
        {
            ShowStatus(
                Text("Status_RiskRequiredTitle", "Review enhanced launch"),
                Text(
                    "Status_RiskRequiredMessage",
                    "Review the local Chromium debugging-port notice before applying."),
                UiStatusTone.Warning);
            return false;
        }

        return await RunApplyAsync(
                RuntimeLaunchMode.ManualApply,
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task<AutoLaunchOutcome> AutoLaunchAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (HasProtectedSettings)
        {
            return AutoLaunchOutcome.Failed;
        }

        if (SelectedMediaPath is not null && IsMediaMissing)
        {
            ShowStatus(
                Text("Status_AutoLaunchNeedsMediaTitle", "Wallpaper needs attention"),
                Text(
                    "Status_AutoLaunchNeedsMediaMessage",
                    "Choose an available wallpaper before using the enhanced shortcut."),
                UiStatusTone.Warning);
            return AutoLaunchOutcome.NeedsMedia;
        }

        if (SelectedMediaPath is not null && !AcceptedCdpRisk)
        {
            ShowStatus(
                Text("Status_RiskRequiredTitle", "Review enhanced launch"),
                Text(
                    "Status_RiskRequiredMessage",
                    "Review the local Chromium debugging-port notice before applying."),
                UiStatusTone.Warning);
            return AutoLaunchOutcome.NeedsRiskAcknowledgement;
        }

        return await RunApplyAsync(
                RuntimeLaunchMode.EnhancedShortcut,
                cancellationToken)
            .ConfigureAwait(true)
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
                async () =>
                {
                    _ = await _wallpaper
                        .RestoreOfficialAsync(_operationCancellation!.Token)
                        .ConfigureAwait(true);
                },
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
        RefreshProfileCards();
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
        RuntimeLaunchMode launchMode,
        CancellationToken cancellationToken)
    {
        var applySequence = Interlocked.Increment(ref _latestApplySequence);
        BeginOperation(Text("Stage_Validating", "Validating media and Codex…"), cancellationToken);
        var operationToken = _operationCancellation!.Token;
        ShortcutNeedsRetry = false;
        try
        {
            var result = await _wallpaper
                .ApplyAsync(launchMode, operationToken)
                .ConfigureAwait(true);
            if (applySequence != Volatile.Read(ref _latestApplySequence))
            {
                return false;
            }

            IsPaused = false;
            ShortcutNeedsRetry = !result.ShortcutReady;

            if (result.Outcome == RuntimeActivationOutcome.Superseded)
            {
                ShowStatus(
                    Text("Status_ApplySupersededTitle", "Apply replaced"),
                    Text(
                        "Status_ApplySupersededMessage",
                        "A newer draft replaced this activation request."),
                    UiStatusTone.Informational);
                return false;
            }

            if (result.Outcome == RuntimeActivationOutcome.Canceled)
            {
                ShowStatus(
                    Text("Status_ApplyCanceledTitle", "Apply canceled"),
                    Text(
                        "Status_ApplyCanceledMessage",
                        "The saved and active states were left as reported by the runtime."),
                    UiStatusTone.Warning);
                return false;
            }

            if (result.Outcome is RuntimeActivationOutcome.Failed or
                RuntimeActivationOutcome.SavedButNotActivated)
            {
                ShowStatus(
                    result.Outcome == RuntimeActivationOutcome.SavedButNotActivated
                        ? Text(
                            "Status_SavedNotActivatedTitle",
                            "Saved, but not activated")
                        : Text("Status_ActivationFailedTitle", "Activation failed"),
                    result.Activation.Error?.Message ??
                    Text(
                        "Status_ActivationFailedMessage",
                        "The runtime could not activate this saved profile."),
                    result.Outcome == RuntimeActivationOutcome.SavedButNotActivated
                        ? UiStatusTone.Warning
                        : UiStatusTone.Error);
                return false;
            }

            if (result.ActiveSnapshot is not null)
            {
                Settings.SetActive(result.ActiveSnapshot);
            }

            if (result.Outcome == RuntimeActivationOutcome.Official)
            {
                ShowStatus(
                    Text("Status_OfficialActiveTitle", "Official background active"),
                    Text(
                        "Status_OfficialActiveMessage",
                        "The empty profile was saved and this app's wallpaper resources were cleared."),
                    UiStatusTone.Success);
                return true;
            }

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
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            if (applySequence == Volatile.Read(ref _latestApplySequence))
            {
                ShowStatus(
                    Text("Status_ApplyCanceledTitle", "Apply canceled"),
                    Text(
                        "Status_ApplyCanceledMessage",
                        "Cancellation requested; required runtime cleanup was completed before returning."),
                    UiStatusTone.Warning);
            }

            return false;
        }
        catch (Exception exception)
        {
            if (applySequence == Volatile.Read(ref _latestApplySequence))
            {
                Settings.SetRuntimeActivity(_wallpaper.IsActive);
                IsPaused = _wallpaper.IsPaused;
                ShowError(_errorMapper.Map(exception, UserFacingOperation.ApplyWallpaper));
            }

            return false;
        }
        finally
        {
            if (applySequence == Volatile.Read(ref _latestApplySequence))
            {
                Settings.SetRuntimeActivity(_wallpaper.IsActive);
                IsPaused = _wallpaper.IsPaused;
                EndOperation();
            }
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
            var result = await _wallpaper
                .RestoreOfficialAsync(_operationCancellation!.Token)
                .ConfigureAwait(true);
            if (result.Surface.Kind != WallpaperRuntimeSurfaceKind.Official)
            {
                Settings.SetRuntimeActivity(_wallpaper.IsActive);
                IsPaused = _wallpaper.IsPaused;
                ShowStatus(
                    Text(
                        "Status_RestoreFailedTitle",
                        "Official background could not be restored"),
                    result.Error?.Message ??
                    Text(
                        "Status_RestoreFailedMessage",
                        "Cleanup could not be confirmed. The runtime state shown below reflects the resources still owned by this app."),
                    UiStatusTone.Error);
                return;
            }

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
        if (eventArgs.Revision is { } revision &&
            revision < _wallpaper.Workspace.LatestRevision)
        {
            return;
        }

        void Update()
        {
            // A newer Apply can be submitted after the producer-side check but
            // before this callback reaches the UI dispatcher.
            if (eventArgs.Revision is { } dispatchedRevision &&
                dispatchedRevision < _wallpaper.Workspace.LatestRevision)
            {
                return;
            }

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
            RefreshProfileCards();
            OnPropertyChanged(nameof(ApplyButtonText));
            OnPropertyChanged(nameof(WorkspaceStatusText));
            OnPropertyChanged(nameof(FooterStatusText));
            NotifyCommandStateChanged();
        }

        if (eventArgs.PropertyName is nameof(SettingsManagementViewModel.HasProtectedSettings))
        {
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanEditDraft));
            OnPropertyChanged(nameof(CanSubmitApply));
            OnPropertyChanged(nameof(CanClearSelectedMedia));
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
        if (eventArgs.PropertyName is
            nameof(WallpaperEditorViewModel.SelectedMediaPath) or
            nameof(WallpaperEditorViewModel.AcceptedCdpRisk))
        {
            OnPropertyChanged(nameof(RequiresCdpRisk));
            OnPropertyChanged(nameof(CanClearSelectedMedia));
        }
    }

    private void RefreshProfileCards(Guid? selectedProfileId = null)
    {
        var draft = _wallpaper.Workspace.Draft;
        var selectedId = selectedProfileId ??
            draft.ResolveProfile(SemanticRegion.Global).ProfileId;
        var cards = _profileProjection.CreateItems(draft);

        _isSynchronizingProfileSelection = true;
        try
        {
            ProfileCards.Clear();
            foreach (var card in cards)
            {
                ProfileCards.Add(card);
            }

            SelectedProfileCard =
                ProfileCards.FirstOrDefault(card => card.ProfileId == selectedId) ??
                ProfileCards.FirstOrDefault();
        }
        finally
        {
            _isSynchronizingProfileSelection = false;
        }

        DeleteProfileCommand.NotifyCanExecuteChanged();
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
            _wallpaper.CancelLatestApply();
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
        ActiveSnapshot is { } active &&
        active.ResolveProfile(SemanticRegion.Global).MediaId is { } mediaId &&
        active.FindMedia(mediaId)?.LastKnownKind == MediaKind.Video;

    private bool CanDisable() =>
        OperationProgress.Stage != WallpaperOperationStage.Resetting &&
        (_wallpaper.Workspace.RuntimeSurface.Kind !=
             WallpaperRuntimeSurfaceKind.Official ||
         OperationProgress.Stage is
             WallpaperOperationStage.Validating or
             WallpaperOperationStage.Launching or
             WallpaperOperationStage.Discovering or
             WallpaperOperationStage.Applying);

    private bool CanRetryShortcut() => !IsBusy && ShortcutNeedsRetry;

    private void NotifyCommandStateChanged()
    {
        TogglePauseCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RetryShortcutCommand.NotifyCanExecuteChanged();
        ClearRecentsCommand.NotifyCanExecuteChanged();
        CreateProfileCommand.NotifyCanExecuteChanged();
        DuplicateProfileCommand.NotifyCanExecuteChanged();
        RenameProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
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
            OnPropertyChanged(nameof(WallpaperCompatibility));
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

public sealed class ProfileRenameRequestedEventArgs(
    Guid profileId,
    string currentName) : EventArgs
{
    public Guid ProfileId { get; } = profileId;

    public string CurrentName { get; } = currentName;

    public string? NewName { get; set; }

    public bool IsCanceled { get; set; } = true;
}

public sealed class ProfileDeleteRequestedEventArgs(
    Guid profileId,
    string profileName,
    Guid replacementProfileId,
    string replacementProfileName) : EventArgs
{
    public Guid ProfileId { get; } = profileId;

    public string ProfileName { get; } = profileName;

    public Guid ReplacementProfileId { get; set; } = replacementProfileId;

    public string ReplacementProfileName { get; } = replacementProfileName;

    public bool IsConfirmed { get; set; }
}
