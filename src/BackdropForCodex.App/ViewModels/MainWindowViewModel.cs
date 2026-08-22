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
using BackdropForCodex.Core.Dynamic;
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
    private readonly record struct WebWallpaperPrivacyAuthorization(
        bool IsAuthorized,
        WallpaperSourceResolution? ExpectedSourceResolution)
    {
        internal static WebWallpaperPrivacyAuthorization Rejected =>
            new(IsAuthorized: false, ExpectedSourceResolution: null);

        internal static WebWallpaperPrivacyAuthorization Authorized(
            WallpaperSourceResolution? expectedSourceResolution = null) =>
            new(
                IsAuthorized: true,
                ExpectedSourceResolution: expectedSourceResolution);
    }

    private readonly record struct WebWallpaperPrivacyPreflight(
        bool IsAvailable,
        bool RequiresAcknowledgement,
        WallpaperSourceResolution? ExpectedSourceResolution)
    {
        internal static WebWallpaperPrivacyPreflight Unavailable =>
            new(
                IsAvailable: false,
                RequiresAcknowledgement: false,
                ExpectedSourceResolution: null);

        internal static WebWallpaperPrivacyPreflight Available(
            bool requiresAcknowledgement = false,
            WallpaperSourceResolution? expectedSourceResolution = null) =>
            new(
                IsAvailable: true,
                RequiresAcknowledgement: requiresAcknowledgement,
                ExpectedSourceResolution: expectedSourceResolution);
    }

    private readonly IWallpaperApplicationService _wallpaper;
    private readonly IWallpaperApplicationCapabilitySource? _capabilitySource;
    private readonly IUserFacingErrorMapper _errorMapper;
    private readonly IAppTextProvider _text;
    private readonly WallpaperProfileCardProjection _profileProjection;
    private readonly IWallpaperSourceProviderRegistry _sourceRegistry;
    private readonly SynchronizationContext? _uiContext;
    private readonly object _initializationLock = new();
    private readonly object _webPrivacyPromptLock = new();
    private Task? _initializationTask;
    private Task<WebWallpaperPrivacyAuthorization>? _webPrivacyPromptTask;
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
    private bool _canRetryStatusApply;
    private bool _hasStatusDetails;
    private bool _isDisposed;
    private WallpaperRuntimePhase _runtimePhase = WallpaperRuntimePhase.Idle;
    private WallpaperProfileCardItem? _selectedProfileCard;
    private bool _isSynchronizingProfileSelection;
    private bool _sourceDiscoveryStatusActive;
    private long _latestApplySequence;

    public MainWindowViewModel(
        IWallpaperApplicationService wallpaper,
        IAppPreferencesStore preferencesStore,
        IUserFacingErrorMapper errorMapper,
        IAppTextProvider text,
        ISafeMediaPreviewService? previewMedia = null,
        IWallpaperSourceProviderRegistry? sourceRegistry = null,
        IWallpaperEngineInstallationPreferenceManager?
            wallpaperEngineInstallationPreferenceManager = null)
    {
        _wallpaper = wallpaper ?? throw new ArgumentNullException(nameof(wallpaper));
        _capabilitySource = wallpaper as IWallpaperApplicationCapabilitySource;
        _errorMapper = errorMapper ?? throw new ArgumentNullException(nameof(errorMapper));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        var mediaPreview = previewMedia ?? AppWallpaperSources.Preview;
        _sourceRegistry = sourceRegistry ??
            (mediaPreview as SafeMediaPreviewService)?.SourceRegistry ??
            AppWallpaperSources.Registry;
        _profileProjection = new WallpaperProfileCardProjection(_text, mediaPreview);
        Editor = new WallpaperEditorViewModel(_text, mediaPreview);
        Settings = new SettingsManagementViewModel(
            wallpaper,
            preferencesStore,
            Editor,
            mediaPreview,
            _sourceRegistry,
            wallpaperEngineInstallationPreferenceManager);
        Settings.PropertyChanged += Settings_PropertyChanged;
        Settings.Recents.CollectionChanged += Recents_CollectionChanged;
        SourceLibrary = new WallpaperSourceLibraryViewModel(
            _sourceRegistry,
            dynamicCapabilitySource: wallpaper as IDynamicWallpaperCapabilitySource,
            installationSelectionService:
                wallpaperEngineInstallationPreferenceManager is null
                    ? null
                    : Settings);
        SourceLibrary.PropertyChanged += SourceLibrary_PropertyChanged;
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
        RemoveRecentCommand =
            new AsyncRelayCommand<RecentMediaItem>(
                item => item is null
                    ? Task.CompletedTask
                    : RemoveRecentAsync(item.MediaId),
                item => CanEdit && item is not null);
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

    public WallpaperSourceLibraryViewModel SourceLibrary { get; }

    public SettingsManagementViewModel Settings { get; }

    public IAsyncRelayCommand TogglePauseCommand { get; }

    public IAsyncRelayCommand DisableCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand RetryShortcutCommand { get; }

    public IAsyncRelayCommand ClearRecentsCommand { get; }

    public IAsyncRelayCommand<RecentMediaItem> RemoveRecentCommand { get; }

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
            QueueReferenceAvailabilityRefresh(Editor.SelectedMediaReference);
        }
    }

    public WallpaperConfigurationState ConfigurationState => Settings.ConfigurationState;

    public SettingsV3 SavedDesired => Settings.SavedDesired;

    public SettingsV3? ActiveSnapshot => Settings.ActiveSnapshot;

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
                OnPropertyChanged(nameof(CanRestoreVersion1Backup));
                Editor.SetEditingEnabled(CanEdit);
                NotifyCommandStateChanged();
            }
        }
    }

    public AppPreferencesV1 Preferences => Settings.Preferences;

    public ThemeMode ThemeMode => Settings.ThemeMode;

    public bool HasShownTrayTip => Settings.HasShownTrayTip;

    public bool IsBusy => OperationProgress.IsBusy;

    public bool CanEditDraft =>
        !HasProtectedSettings &&
        OperationProgress.Stage != WallpaperOperationStage.Resetting;

    public bool CanSubmitApply =>
        !HasProtectedSettings &&
        (Editor.SelectedMediaReference is not { } selectedMedia ||
         SourceLibrary.CanActivate(selectedMedia)) &&
        OperationProgress.Stage is not
            WallpaperOperationStage.Resetting and not
            WallpaperOperationStage.Restoring;

    public bool CanClearSelectedMedia => CanEditDraft && Editor.HasSelectedMedia;

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

    internal WallpaperRuntimeError? RuntimeError =>
        _wallpaper.Workspace.RuntimeSurface.Error;

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
        set
        {
            if (!SetProperty(ref _isStatusOpen, value) || value)
            {
                return;
            }

            CanRetryStatusApply = false;
            HasStatusDetails = false;
        }
    }

    public bool CanRetryStatusApply
    {
        get => _canRetryStatusApply;
        private set => SetProperty(ref _canRetryStatusApply, value);
    }

    public bool HasStatusDetails
    {
        get => _hasStatusDetails;
        private set => SetProperty(ref _hasStatusDetails, value);
    }

    public bool IsDraftDirty => Settings.IsDraftDirty;

    public string WorkspaceStatusText
    {
        get
        {
            var workspace = _wallpaper.Workspace;
            if (workspace.RuntimeSurface.Kind == WallpaperRuntimeSurfaceKind.Faulted)
            {
                return _text.GetStringOrFallback("Workspace_ActivationFailed", "Activation failed");
            }

            if (workspace.Error is not null)
            {
                return workspace.Error.Stage ==
                        WallpaperWorkspaceErrorStage.Runtime &&
                    IsSavedButInactive
                    ? _text.GetStringOrFallback(
                        "Workspace_SavedNotActive",
                        "Saved, not activated")
                    : _text.GetStringOrFallback("Workspace_ActivationFailed", "Activation failed");
            }

            if (IsDraftDirty)
            {
                return _text.GetStringOrFallback("Workspace_DraftUnsaved", "Draft has unsaved changes");
            }

            if (IsSavedButInactive)
            {
                return _text.GetStringOrFallback(
                    "Workspace_SavedNotActive",
                    "Saved, not activated");
            }

            if (workspace.RuntimeSurface.Kind ==
                WallpaperRuntimeSurfaceKind.MediaActive)
            {
                return _text.GetStringOrFallback("Workspace_MediaActive", "Media running");
            }

            if (workspace.RuntimeSurface.Kind == WallpaperRuntimeSurfaceKind.Official &&
                workspace.ActiveSnapshot is not null)
            {
                return _text.GetStringOrFallback("Workspace_Official", "Official background");
            }

            return workspace.RuntimeSurface.Kind ==
                WallpaperRuntimeSurfaceKind.Disconnected
                ? _text.GetStringOrFallback("Workspace_Disconnected", "Codex disconnected")
                : _text.GetStringOrFallback("Workspace_Official", "Official background");
        }
    }

    public string FooterStatusText =>
        IsBusy && !string.IsNullOrWhiteSpace(OperationStage)
            ? OperationStage
            : WorkspaceStatusText;

    public string ApplyButtonText => IsActive
        ? _text.GetStringOrFallback("Action_ApplyChanges", "Apply changes")
        : _text.GetStringOrFallback("Action_ApplyAndLaunch", "Apply & launch Codex");

    public string PauseButtonText => IsPaused
        ? _text.GetStringOrFallback("Action_ResumeVideo", "Resume wallpaper")
        : _text.GetStringOrFallback("Action_PauseVideo", "Pause wallpaper");

    internal Func<ProfileRenameRequestedEventArgs, Task<string?>>?
        RenameProfilePromptAsync
    { get; set; }

    internal Func<ProfileDeleteRequestedEventArgs, Task<bool>>?
        DeleteProfilePromptAsync
    { get; set; }

    internal Func<CancellationToken, Task<bool>>?
        WebWallpaperPrivacyPromptAsync
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
        QueueReferenceAvailabilityRefresh(reference: null);
    }

    private void CreateProfile()
    {
        if (!CanEditDraft)
        {
            return;
        }

        var profile = _wallpaper.CreateProfile(
            _text.GetStringOrFallback("Action_NewProfile", "New profile"));
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
            _text.GetStringOrFallback("Profile_CopySuffix", "Copy"));
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

    public async Task RefreshWallpaperEngineLibraryAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().WaitAsync(cancellationToken).ConfigureAwait(true);
        await SourceLibrary.RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    public void SelectMedia(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        if (!CanEdit)
        {
            return;
        }

        Editor.SelectMedia(mediaPath);
        QueueReferenceAvailabilityRefresh(reference: null);
        ShowMissingMediaStatusIfNeeded();
    }

    private void ShowMissingMediaStatusIfNeeded()
    {
        if (Editor.IsMediaMissing)
        {
            ShowStatus(
                _text.GetStringOrFallback("Status_MissingTitle", "Media unavailable"),
                _text.GetStringOrFallback(
                    "Status_MissingMessage",
                    "The saved file no longer exists. Choose another file or remove it from recent media."),
                UiStatusTone.Warning);
        }
    }

    public void SelectSource(WallpaperSourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _ = _sourceRegistry.GetRequired(descriptor.SourceKind);
        if (!CanEdit)
        {
            return;
        }

        if (descriptor.DeliveryKind is not (
                WallpaperDeliveryKind.DirectMedia or
                WallpaperDeliveryKind.WallpaperEngineWindow))
        {
            throw new WallpaperContentNotSupportedException(descriptor);
        }

        Editor.SelectSource(descriptor);
        SourceLibrary.UseResolvedDescriptor(descriptor);
        ShowMissingMediaStatusIfNeeded();
    }

    public void SelectSource(MediaReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var snapshot = reference.Snapshot();
        _ = _sourceRegistry.GetRequired(snapshot.SourceKind);
        if (!IsSelectableReference(snapshot))
        {
            throw new ArgumentException(
                "The saved reference does not identify supported direct or dynamic wallpaper content.",
                nameof(reference));
        }

        if (!CanEdit)
        {
            return;
        }

        Editor.SelectMediaReference(snapshot);
        QueueReferenceAvailabilityRefresh(snapshot);
        ShowMissingMediaStatusIfNeeded();
    }

    public async Task AcceptRiskAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (Editor.AcceptedCdpRisk || HasProtectedSettings)
        {
            return;
        }

        var preserveCurrentOperation = IsBusy;
        if (!preserveCurrentOperation)
        {
            BeginOperation(
            _text.GetStringOrFallback("Stage_Saving", "Saving settings…"),
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
                _text.GetStringOrFallback("Status_RiskAcceptedTitle", "Enhanced launch enabled"),
                _text.GetStringOrFallback(
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
            _text.GetStringOrFallback("Stage_Saving", "Saving settings…"),
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
                _text.GetStringOrFallback("Status_RiskRevokedTitle", "Enhanced launch disabled"),
                _text.GetStringOrFallback(
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

        if (Editor.HasSelectedMedia && Editor.IsMediaMissing)
        {
            ShowStatus(
                _text.GetStringOrFallback("Status_MissingTitle", "Media unavailable"),
                _text.GetStringOrFallback(
                    "Status_ProfileMediaMissingMessage",
                    "The saved file no longer exists. Choose another file or clear this profile's media."),
                UiStatusTone.Warning);
            return false;
        }

        var privacyAuthorization =
            await PrepareWebWallpaperPrivacyAuthorizationAsync(cancellationToken)
                .ConfigureAwait(true);
        if (!privacyAuthorization.IsAuthorized)
        {
            return false;
        }

        if (Editor.HasSelectedMedia && !Editor.AcceptedCdpRisk)
        {
            ShowStatus(
                _text.GetStringOrFallback("Status_RiskRequiredTitle", "Review enhanced launch"),
                _text.GetStringOrFallback(
                    "Status_RiskRequiredMessage",
                    "Review the local Chromium debugging-port notice before applying."),
                UiStatusTone.Warning);
            return false;
        }

        return await RunApplyAsync(
                RuntimeLaunchMode.ManualApply,
                privacyAuthorization.ExpectedSourceResolution,
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

        if (Editor.HasSelectedMedia && Editor.IsMediaMissing)
        {
            ShowStatus(
                _text.GetStringOrFallback("Status_AutoLaunchNeedsMediaTitle", "Wallpaper needs attention"),
                _text.GetStringOrFallback(
                    "Status_AutoLaunchNeedsMediaMessage",
                    "Choose an available wallpaper before using the enhanced shortcut."),
                UiStatusTone.Warning);
            return AutoLaunchOutcome.NeedsMedia;
        }

        var privacyAuthorization =
            await PrepareWebWallpaperPrivacyAuthorizationAsync(cancellationToken)
                .ConfigureAwait(true);
        if (!privacyAuthorization.IsAuthorized)
        {
            return AutoLaunchOutcome.NeedsPrivacyAcknowledgement;
        }

        if (Editor.HasSelectedMedia && !Editor.AcceptedCdpRisk)
        {
            ShowStatus(
                _text.GetStringOrFallback("Status_RiskRequiredTitle", "Review enhanced launch"),
                _text.GetStringOrFallback(
                    "Status_RiskRequiredMessage",
                    "Review the local Chromium debugging-port notice before applying."),
                UiStatusTone.Warning);
            return AutoLaunchOutcome.NeedsRiskAcknowledgement;
        }

        return await RunApplyAsync(
                RuntimeLaunchMode.EnhancedShortcut,
                privacyAuthorization.ExpectedSourceResolution,
                cancellationToken)
            .ConfigureAwait(true)
            ? AutoLaunchOutcome.Applied
            : AutoLaunchOutcome.Failed;
    }

    internal async Task<bool> EnsureWebWallpaperPrivacyAcknowledgedAsync(
        CancellationToken cancellationToken = default) =>
        (await PrepareWebWallpaperPrivacyAuthorizationAsync(cancellationToken)
            .ConfigureAwait(true)).IsAuthorized;

    private Task<WebWallpaperPrivacyAuthorization>
        PrepareWebWallpaperPrivacyAuthorizationAsync(
            CancellationToken cancellationToken)
    {
        if (Editor.SelectedMediaReference is null)
        {
            return Task.FromResult(WebWallpaperPrivacyAuthorization.Authorized());
        }

        lock (_webPrivacyPromptLock)
        {
            if (Editor.SelectedMediaReference is null)
            {
                return Task.FromResult(WebWallpaperPrivacyAuthorization.Authorized());
            }

            return _webPrivacyPromptTask ??=
                ConfirmWebWallpaperPrivacyAsync(cancellationToken);
        }
    }

    private async Task<WebWallpaperPrivacyAuthorization>
        ConfirmWebWallpaperPrivacyAsync(
        CancellationToken cancellationToken)
    {
        // Yield once so the shared task is published before a synchronous test or dialog seam completes.
        await Task.Yield();
        try
        {
            var preflight =
                await ResolveWebWallpaperPrivacyRequirementAsync(cancellationToken)
                    .ConfigureAwait(true);
            if (!preflight.IsAvailable)
            {
                ShowStatus(
                    _text.GetStringOrFallback(
                        "Status_MissingTitle",
                        "Media unavailable"),
                    _text.GetStringOrFallback(
                        "Status_ProfileMediaMissingMessage",
                        "The saved file no longer exists. Choose another file or clear this profile's media."),
                    UiStatusTone.Warning);
                return WebWallpaperPrivacyAuthorization.Rejected;
            }

            if (!preflight.RequiresAcknowledgement)
            {
                return WebWallpaperPrivacyAuthorization.Authorized(
                    preflight.ExpectedSourceResolution);
            }

            var prompt = WebWallpaperPrivacyPromptAsync;
            if (prompt is null || !await prompt(cancellationToken).ConfigureAwait(true))
            {
                ShowStatus(
                    _text.GetStringOrFallback(
                        "Status_WebPrivacyRequiredTitle",
                        "Web wallpaper confirmation required"),
                    _text.GetStringOrFallback(
                        "Status_WebPrivacyRequiredMessage",
                        "Confirm the Web wallpaper privacy notice before applying."),
                    UiStatusTone.Warning);
                return WebWallpaperPrivacyAuthorization.Rejected;
            }

            await Settings
                .AcknowledgeWebWallpaperPrivacyNoticeAsync(cancellationToken)
                .ConfigureAwait(true);
            return WebWallpaperPrivacyAuthorization.Authorized(
                preflight.ExpectedSourceResolution);
        }
        finally
        {
            lock (_webPrivacyPromptLock)
            {
                _webPrivacyPromptTask = null;
            }
        }
    }

    private async Task<WebWallpaperPrivacyPreflight>
        ResolveWebWallpaperPrivacyRequirementAsync(
        CancellationToken cancellationToken)
    {
        var selected = Editor.SelectedMediaReference?.Snapshot();
        if (selected is null)
        {
            return WebWallpaperPrivacyPreflight.Available();
        }

        if (selected.SourceKind is not (
                MediaSourceKind.WallpaperEngineLocalProject or
                MediaSourceKind.WallpaperEngineWorkshopProject))
        {
            return WebWallpaperPrivacyPreflight.Available(
                requiresAcknowledgement:
                    !Preferences.HasAcknowledgedWebWallpaperPrivacyNotice &&
                    selected.LastKnownContentKind == WallpaperContentKind.Web);
        }

        try
        {
            var resolution = await _sourceRegistry
                .ResolveRequiredAsync(selected, cancellationToken)
                .ConfigureAwait(true);
            var current = Editor.SelectedMediaReference;
            if (current is null || !IdentifiesSameSource(selected, current))
            {
                return WebWallpaperPrivacyPreflight.Unavailable;
            }

            return WebWallpaperPrivacyPreflight.Available(
                requiresAcknowledgement:
                    !Preferences.HasAcknowledgedWebWallpaperPrivacyNotice &&
                    resolution.Descriptor.ContentKind == WallpaperContentKind.Web,
                resolution);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            WallpaperEngineUnavailableException or
            WallpaperEngineProjectUnavailableException or
            WallpaperSourceCapabilityException or
            KeyNotFoundException)
        {
            return WebWallpaperPrivacyPreflight.Unavailable;
        }
    }

    private static bool IdentifiesSameSource(MediaReference left, MediaReference right) =>
        left.SourceKind == right.SourceKind &&
        string.Equals(
            left.SourceIdentifier,
            right.SourceIdentifier,
            left.SourceKind == MediaSourceKind.WallpaperEngineLocalProject
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

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
            _text.GetStringOrFallback("Stage_Saving", "Saving settings…"),
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

    public async Task RemoveRecentAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (IsBusy || HasProtectedSettings)
        {
            return;
        }

        BeginOperation(
            _text.GetStringOrFallback("Stage_Saving", "Saving settings…"),
            cancellationToken,
            WallpaperOperationStage.Saving);
        try
        {
            var saved = await Settings
                .RemoveRecentAsync(
                    SavedDesired,
                    mediaId,
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
                _text.GetStringOrFallback("Stage_Saving", "Saving settings…"),
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
                _text.GetStringOrFallback("Status_RecentsClearedTitle", "Recent media cleared"),
                _text.GetStringOrFallback(
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
            _text.GetStringOrFallback("Stage_Resetting", "Resetting Backdrop for Codex…"),
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
                    _text.GetStringOrFallback("Status_ResetCompleteTitle", "Reset complete"),
                    _text.GetStringOrFallback(
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
            _text.GetStringOrFallback("Stage_Saving", "Saving settings…"),
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
                _text.GetStringOrFallback("Status_BackupRestoredTitle", "Earlier backup restored"),
                _text.GetStringOrFallback(
                    "Status_BackupRestoredMessage",
                    "Settings from the protected backup are now in use. The original backup remains available."),
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
        await SourceLibrary
            .RefreshActivationAvailabilityAsync(CancellationToken.None)
            .ConfigureAwait(true);
        await SourceLibrary
            .RefreshReferenceAvailabilityAsync(
                Editor.SelectedMediaReference,
                CancellationToken.None)
            .ConfigureAwait(true);
        Settings.SetRuntimeActivity(_wallpaper.IsActive);
        IsPaused = _wallpaper.IsPaused;
        if (!preferenceWarning && !IsStatusOpen)
        {
            ShowStatus(
                _text.GetStringOrFallback("Status_ReadyTitle", "Ready"),
                _text.GetStringOrFallback(
                    "Status_ReadyMessage",
                    "Choose local media, tune the glass panel, then apply when ready."),
                UiStatusTone.Informational);
        }
    }

    private async Task<bool> RunApplyAsync(
        RuntimeLaunchMode launchMode,
        WallpaperSourceResolution? expectedSourceResolution,
        CancellationToken cancellationToken)
    {
        var applySequence = Interlocked.Increment(ref _latestApplySequence);
        BeginOperation(_text.GetStringOrFallback("Stage_Validating", "Validating media and Codex…"), cancellationToken);
        var operationToken = _operationCancellation!.Token;
        ShortcutNeedsRetry = false;
        try
        {
            var result = await _wallpaper
                .ApplyAsync(launchMode, expectedSourceResolution, operationToken)
                .ConfigureAwait(true);
            if (applySequence != Volatile.Read(ref _latestApplySequence))
            {
                return false;
            }

            IsPaused = false;
            ShortcutNeedsRetry =
                result.Outcome == RuntimeActivationOutcome.MediaActive &&
                !result.ShortcutReady;

            if (result.Outcome == RuntimeActivationOutcome.Superseded)
            {
                ShowStatus(
                    _text.GetStringOrFallback("Status_ApplySupersededTitle", "Apply replaced"),
                    _text.GetStringOrFallback(
                        "Status_ApplySupersededMessage",
                        "A newer draft replaced this activation request."),
                    UiStatusTone.Informational);
                return false;
            }

            if (result.Outcome == RuntimeActivationOutcome.Canceled)
            {
                ShowStatus(
                    _text.GetStringOrFallback("Status_ApplyCanceledTitle", "Apply canceled"),
                    _text.GetStringOrFallback(
                        "Status_ApplyCanceledMessage",
                        "The operation stopped. The latest confirmed wallpaper state remains in use."),
                    UiStatusTone.Warning);
                return false;
            }

            if (result.Outcome is RuntimeActivationOutcome.Failed or
                RuntimeActivationOutcome.SavedButNotActivated)
            {
                if (result.Activation.Error is { } runtimeError)
                {
                    ShowError(
                        _errorMapper.Map(
                            runtimeError,
                            UserFacingOperation.ApplyWallpaper),
                        canRetryApply: true,
                        toneOverride:
                            result.Outcome ==
                                RuntimeActivationOutcome.SavedButNotActivated
                                ? UiStatusTone.Warning
                                : null);
                }
                else
                {
                    ShowStatus(
                        result.Outcome ==
                            RuntimeActivationOutcome.SavedButNotActivated
                            ? _text.GetStringOrFallback(
                                "Status_SavedNotActivatedTitle",
                                "Saved, but not activated")
                            : _text.GetStringOrFallback(
                                "Status_ActivationFailedTitle",
                                "Activation failed"),
                        _text.GetStringOrFallback(
                            "Status_ActivationFailedMessage",
                            "The saved profile could not be applied to Codex."),
                        result.Outcome ==
                            RuntimeActivationOutcome.SavedButNotActivated
                            ? UiStatusTone.Warning
                            : UiStatusTone.Error);
                }

                return false;
            }

            if (result.ActiveSnapshot is not null)
            {
                Settings.SetActive(result.ActiveSnapshot);
            }

            if (result.Outcome == RuntimeActivationOutcome.Official)
            {
                ShowStatus(
                    _text.GetStringOrFallback("Status_OfficialActiveTitle", "Official background active"),
                    _text.GetStringOrFallback(
                        "Status_OfficialActiveMessage",
                        "The profile was saved without a wallpaper, and Codex is using its official background."),
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
                    _text.GetStringOrFallback("Status_AppliedDegradedTitle", "Wallpaper active with reduced effects"),
                    _text.GetStringOrFallback(
                        "Status_AppliedDegradedMessage",
                        "Some optional effects are unavailable, but the wallpaper is active. Export a diagnostic report if you need help troubleshooting."),
                    UiStatusTone.Warning);
            }
            else if (result.ShortcutReady)
            {
                ShowStatus(
                    _text.GetStringOrFallback("Status_AppliedTitle", "Wallpaper is active"),
                    _text.GetStringOrFallback(
                        "Status_AppliedMessage",
                        "Codex is using the saved wallpaper and the enhanced desktop shortcut is ready."),
                    UiStatusTone.Success);
            }
            else
            {
                ShowStatus(
                    _text.GetStringOrFallback("Status_AppliedShortcutFailedTitle", "Wallpaper active"),
                    _text.GetStringOrFallback(
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
                    _text.GetStringOrFallback("Status_ApplyCanceledTitle", "Apply canceled"),
                    _text.GetStringOrFallback(
                        "Status_ApplyCanceledAfterCleanupMessage",
                        "The operation stopped without applying a new wallpaper."),
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
                ShowError(
                    _errorMapper.Map(
                        exception,
                        UserFacingOperation.ApplyWallpaper),
                    canRetryApply: true);
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
            _text.GetStringOrFallback("Stage_Updating", "Updating playback…"),
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
                    ? _text.GetStringOrFallback("Status_PausedTitle", "Wallpaper paused")
                    : _text.GetStringOrFallback("Status_ResumedTitle", "Wallpaper resumed"),
                IsPaused
                    ? _text.GetStringOrFallback(
                        "Status_PausedMessage",
                        "The wallpaper is holding its last frame in Codex and the preview.")
                    : _text.GetStringOrFallback(
                        "Status_ResumedMessage",
                        "The wallpaper is playing in Codex and the preview."),
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
            _text.GetStringOrFallback("Stage_Restoring", "Restoring the official Codex background…"),
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
                var mappedError = result.Error is { } runtimeError
                    ? _errorMapper.Map(
                        runtimeError,
                        UserFacingOperation.RestoreWallpaper)
                    : _errorMapper.Map(
                        new InvalidOperationException(
                            "The restore operation returned without the official surface."),
                        UserFacingOperation.RestoreWallpaper);
                ShowError(
                    mappedError with
                    {
                        Title = _text.GetStringOrFallback(
                            "Status_RestoreFailedTitle",
                            "Official background could not be restored"),
                    });
                return;
            }

            Settings.SetRuntimeActivity(isActive: false);
            IsPaused = false;
            ShowStatus(
                _text.GetStringOrFallback("Status_RestoredTitle", "Official background restored"),
                _text.GetStringOrFallback(
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
                _text.GetStringOrFallback("Status_ShortcutReadyTitle", "Shortcut ready"),
                _text.GetStringOrFallback(
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
            if (_isDisposed)
            {
                return;
            }

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
                    _text.GetStringOrFallback("Stage_Validating", "Validating media and Codex…"),
                WallpaperRuntimePhase.LaunchingCodex =>
                    _text.GetStringOrFallback("Stage_Launching", "Launching Codex securely…"),
                WallpaperRuntimePhase.DiscoveringEndpoint =>
                    _text.GetStringOrFallback("Stage_Discovering", "Finding the Codex window…"),
                WallpaperRuntimePhase.Applying =>
                    _text.GetStringOrFallback("Stage_Applying", "Applying wallpaper and glass effects…"),
                WallpaperRuntimePhase.Stopping =>
                    _text.GetStringOrFallback("Stage_Restoring", "Restoring the official Codex background…"),
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
                            _text.GetStringOrFallback("Status_RuntimeStoppedTitle", "Wallpaper connection stopped"),
                            _text.GetStringOrFallback(
                                "Status_RuntimeStoppedMessage",
                                "The wallpaper connection ended. Backdrop tried to restore the official background."),
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
            OnPropertyChanged(nameof(CanSubmitApply));
            OnPropertyChanged(nameof(CanClearSelectedMedia));
            NotifyCommandStateChanged();
        }

        if (eventArgs.PropertyName is nameof(SettingsManagementViewModel.HasProtectedSettings))
        {
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanEditDraft));
            OnPropertyChanged(nameof(CanSubmitApply));
            OnPropertyChanged(nameof(CanClearSelectedMedia));
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
        NotifyCollectionChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ClearRecentsCommand.NotifyCanExecuteChanged();
        RemoveRecentCommand.NotifyCanExecuteChanged();
    }

    private void SourceLibrary_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName is
            nameof(WallpaperSourceLibraryViewModel.DynamicCapability) or
            nameof(WallpaperSourceLibraryViewModel.Items) or
            nameof(WallpaperSourceLibraryViewModel.ResolvedReferenceAvailability))
        {
            OnPropertyChanged(nameof(CanSubmitApply));
            NotifyCommandStateChanged();
        }

        if (eventArgs.PropertyName !=
            nameof(WallpaperSourceLibraryViewModel.HasDiscoveryFailures))
        {
            return;
        }

        void Update()
        {
            var failureTitle = _text.GetStringOrFallback(
                "Status_SourceDiscoveryFailedTitle",
                "Some sources are unavailable");
            var failureMessage = _text.GetStringOrFallback(
                "Status_SourceDiscoveryFailedMessage",
                "Other sources are still available. Open Sources and retry the ones that did not load.");
            if (SourceLibrary.HasDiscoveryFailures)
            {
                var canReplaceCurrentStatus =
                    !IsBusy &&
                    (!IsStatusOpen ||
                     StatusTone is UiStatusTone.Informational or UiStatusTone.Success ||
                     IsCurrentSourceDiscoveryStatus(failureTitle, failureMessage));
                _sourceDiscoveryStatusActive = true;
                if (canReplaceCurrentStatus)
                {
                    ShowStatus(
                        failureTitle,
                        failureMessage,
                        UiStatusTone.Warning);
                }

                return;
            }

            if (!_sourceDiscoveryStatusActive)
            {
                return;
            }

            _sourceDiscoveryStatusActive = false;
            if (IsCurrentSourceDiscoveryStatus(failureTitle, failureMessage))
            {
                ShowStatus(
                    _text.GetStringOrFallback("Status_ReadyTitle", "Ready"),
                    _text.GetStringOrFallback(
                        "Status_ReadyMessage",
                        "Choose local media, tune the glass panel, then apply when ready."),
                    UiStatusTone.Informational);
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

    private bool IsCurrentSourceDiscoveryStatus(string title, string message) =>
        IsStatusOpen &&
        string.Equals(StatusTitle, title, StringComparison.Ordinal) &&
        string.Equals(StatusMessage, message, StringComparison.Ordinal);

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
        IsStatusOpen = false;
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
        active.FindMedia(mediaId) is { } media &&
        (media.LastKnownKind == MediaKind.Video ||
         media.LastKnownContentKind is
             WallpaperContentKind.Scene or WallpaperContentKind.Web);

    private static bool IsSelectableReference(MediaReference reference) =>
        reference.LastKnownKind is MediaKind.Image or MediaKind.Video ||
        ((reference.SourceKind is
             MediaSourceKind.WallpaperEngineLocalProject or
             MediaSourceKind.WallpaperEngineWorkshopProject) &&
         reference.LastKnownKind == MediaKind.None &&
         reference.LastKnownContentKind is
             WallpaperContentKind.Scene or WallpaperContentKind.Web);

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
        RemoveRecentCommand.NotifyCanExecuteChanged();
        CreateProfileCommand.NotifyCanExecuteChanged();
        DuplicateProfileCommand.NotifyCanExecuteChanged();
        RenameProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private void ShowError(
        UserFacingError error,
        bool canRetryApply = false,
        UiStatusTone? toneOverride = null)
    {
        ShowStatus(
            error.Title,
            string.IsNullOrWhiteSpace(error.Recovery)
                ? error.Message
                : $"{error.Message} {error.Recovery}",
            toneOverride ?? (error.Code == UserFacingErrorCode.OperationCanceled
                ? UiStatusTone.Warning
                : UiStatusTone.Error));
        CanRetryStatusApply = canRetryApply && error.CanRetry;
        HasStatusDetails = true;
    }

    private void ShowStatus(string title, string message, UiStatusTone tone)
    {
        CanRetryStatusApply = false;
        HasStatusDetails = false;
        StatusTitle = title;
        StatusMessage = message;
        StatusTone = tone;
        IsStatusOpen = true;
    }

    private void Wallpaper_CapabilitiesChanged(
        object? sender,
        WallpaperInjectionCapabilitiesChangedEventArgs eventArgs)
    {
        _ = RefreshDynamicCapabilityAfterRuntimeChangeAsync();

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
                    _text.GetStringOrFallback("Status_AppliedDegradedTitle", "Wallpaper active with reduced effects"),
                    _text.GetStringOrFallback(
                        "Status_AppliedDegradedMessage",
                        "Some optional effects are unavailable, but the wallpaper is active. Export a diagnostic report if you need help troubleshooting."),
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

    private async Task RefreshDynamicCapabilityAfterRuntimeChangeAsync()
    {
        try
        {
            await SourceLibrary
                .RefreshActivationAvailabilityAsync()
                .ConfigureAwait(true);
        }
        catch (ObjectDisposedException)
        {
            // A queued runtime event can outlive the closing source library.
        }
        catch (OperationCanceledException)
        {
            // A newer refresh owns the current capability snapshot.
        }
    }

    private void QueueReferenceAvailabilityRefresh(MediaReference? reference) =>
        _ = RefreshReferenceAvailabilityAfterSelectionAsync(reference);

    private async Task RefreshReferenceAvailabilityAfterSelectionAsync(
        MediaReference? reference)
    {
        try
        {
            await SourceLibrary
                .RefreshReferenceAvailabilityAsync(reference)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer profile or recent-media selection owns the latest reference check.
        }
        catch (ObjectDisposedException)
        {
            // A queued selection can outlive the closing source library.
        }
        catch (Exception exception)
        {
            ShowUnexpectedError(exception);
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
        Settings.PropertyChanged -= Settings_PropertyChanged;
        Settings.Recents.CollectionChanged -= Recents_CollectionChanged;
        SourceLibrary.PropertyChanged -= SourceLibrary_PropertyChanged;
        SourceLibrary.Dispose();
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
