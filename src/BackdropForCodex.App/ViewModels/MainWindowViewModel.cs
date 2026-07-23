using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackdropForCodex.App.Services.Errors;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Preferences;
using BackdropForCodex.App.Services.Wallpaper;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using BackdropForCodex.Core.Shortcuts;

namespace BackdropForCodex.App.ViewModels;

public enum UiStatusTone
{
    Informational = 0,
    Success,
    Warning,
    Error,
}

public enum AutoLaunchOutcome
{
    Applied = 0,
    NeedsMedia,
    NeedsRiskAcknowledgement,
    Failed,
}

public sealed record RecentMediaItem(
    string Path,
    string DisplayName,
    MediaKind Kind,
    bool Exists);

/// <summary>
/// Owns editable, persisted, and active wallpaper state independently so a failed launch can still
/// be represented as "saved, but not active".
/// </summary>
public sealed class MainWindowViewModel : ObservableObject
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly string[] VideoExtensions = [".mp4", ".webm"];

    private readonly IWallpaperApplicationService _wallpaper;
    private readonly IAppPreferencesStore _preferencesStore;
    private readonly IUserFacingErrorMapper _errorMapper;
    private readonly IAppTextProvider _text;
    private readonly SynchronizationContext? _uiContext;
    private readonly object _initializationLock = new();
    private Task? _initializationTask;
    private CancellationTokenSource? _operationCancellation;
    private SettingsV1 _savedDesired = SettingsV1.CreateDefault();
    private SettingsV1? _activeSnapshot;
    private AppPreferencesV1 _preferences = AppPreferencesV1.CreateDefault();
    private string? _selectedMediaPath;
    private MediaKind _selectedMediaKind;
    private WallpaperFit _fit = WallpaperFit.Cover;
    private double _panelOpacity = 0.78;
    private double _blurPx = 14;
    private bool _acceptedCdpRisk;
    private bool _isMediaMissing;
    private bool _isBusy;
    private bool _isActive;
    private bool _isPaused;
    private bool _isSavedButInactive;
    private bool _shortcutNeedsRetry;
    private string _operationStage = string.Empty;
    private string _statusTitle = string.Empty;
    private string _statusMessage = string.Empty;
    private UiStatusTone _statusTone;
    private bool _isStatusOpen;
    private bool _hasForegroundFailure;

    public MainWindowViewModel(
        IWallpaperApplicationService wallpaper,
        IAppPreferencesStore preferencesStore,
        IUserFacingErrorMapper errorMapper,
        IAppTextProvider text)
    {
        _wallpaper = wallpaper ?? throw new ArgumentNullException(nameof(wallpaper));
        _preferencesStore =
            preferencesStore ?? throw new ArgumentNullException(nameof(preferencesStore));
        _errorMapper = errorMapper ?? throw new ArgumentNullException(nameof(errorMapper));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _uiContext = SynchronizationContext.Current;
        _wallpaper.StatusChanged += Wallpaper_StatusChanged;

        TogglePauseCommand = new AsyncRelayCommand(TogglePauseAsync, CanTogglePause);
        DisableCommand = new AsyncRelayCommand(DisableAsync, CanDisable);
        CancelCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy);
        RetryShortcutCommand = new AsyncRelayCommand(RetryShortcutAsync, CanRetryShortcut);
        ClearRecentsCommand = new AsyncRelayCommand(ClearRecentsAsync, () => !IsBusy && Recents.Count > 0);
    }

    public ObservableCollection<RecentMediaItem> Recents { get; } = [];

    public IAsyncRelayCommand TogglePauseCommand { get; }

    public IAsyncRelayCommand DisableCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand RetryShortcutCommand { get; }

    public IAsyncRelayCommand ClearRecentsCommand { get; }

    public SettingsV1 SavedDesired
    {
        get => _savedDesired;
        private set => SetProperty(ref _savedDesired, value);
    }

    public SettingsV1? ActiveSnapshot
    {
        get => _activeSnapshot;
        private set => SetProperty(ref _activeSnapshot, value);
    }

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

    public string? SelectedMediaPath
    {
        get => _selectedMediaPath;
        private set
        {
            if (SetProperty(ref _selectedMediaPath, value))
            {
                OnPropertyChanged(nameof(SelectedMediaName));
                OnPropertyChanged(nameof(HasSelectedMedia));
                OnPropertyChanged(nameof(IsVideoSelected));
                OnPropertyChanged(nameof(IsDraftDirty));
            }
        }
    }

    public string SelectedMediaName => SelectedMediaPath is null
        ? Text("Media_None", "No media selected")
        : Path.GetFileName(SelectedMediaPath);

    public bool HasSelectedMedia => SelectedMediaPath is not null;

    public MediaKind SelectedMediaKind
    {
        get => _selectedMediaKind;
        private set
        {
            if (SetProperty(ref _selectedMediaKind, value))
            {
                OnPropertyChanged(nameof(IsVideoSelected));
                OnPropertyChanged(nameof(IsDraftDirty));
            }
        }
    }

    public bool IsVideoSelected => SelectedMediaKind == MediaKind.Video;

    public bool IsMediaMissing
    {
        get => _isMediaMissing;
        private set => SetProperty(ref _isMediaMissing, value);
    }

    public WallpaperFit Fit
    {
        get => _fit;
        set
        {
            if (SetProperty(ref _fit, value))
            {
                OnPropertyChanged(nameof(IsDraftDirty));
            }
        }
    }

    public double PanelOpacity
    {
        get => _panelOpacity;
        set
        {
            if (SetProperty(ref _panelOpacity, value))
            {
                OnPropertyChanged(nameof(PanelOpacityPercent));
                OnPropertyChanged(nameof(IsDraftDirty));
            }
        }
    }

    public string PanelOpacityPercent => $"{PanelOpacity:P0}";

    public double BlurPx
    {
        get => _blurPx;
        set
        {
            if (SetProperty(ref _blurPx, value))
            {
                OnPropertyChanged(nameof(BlurLabel));
                OnPropertyChanged(nameof(IsDraftDirty));
            }
        }
    }

    public string BlurLabel => $"{BlurPx:N0} px";

    public bool AcceptedCdpRisk
    {
        get => _acceptedCdpRisk;
        private set
        {
            if (SetProperty(ref _acceptedCdpRisk, value))
            {
                OnPropertyChanged(nameof(IsDraftDirty));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                NotifyCommandStateChanged();
            }
        }
    }

    public bool CanEdit => !IsBusy;

    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(ApplyButtonText));
                NotifyCommandStateChanged();
            }
        }
    }

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

    public bool IsSavedButInactive
    {
        get => _isSavedButInactive;
        private set => SetProperty(ref _isSavedButInactive, value);
    }

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

    public bool IsDraftDirty =>
        !string.Equals(
            SelectedMediaPath,
            SavedDesired.MediaPath,
            StringComparison.OrdinalIgnoreCase) ||
        Fit != SavedDesired.Fit ||
        Math.Abs(PanelOpacity - SavedDesired.PanelOpacity) > 0.0001 ||
        Math.Abs(BlurPx - SavedDesired.BlurPx) > 0.0001 ||
        AcceptedCdpRisk != SavedDesired.AcceptedCdpRisk;

    public string ApplyButtonText => IsActive
        ? Text("Action_ApplyChanges", "Apply changes")
        : Text("Action_ApplyAndLaunch", "Apply & launch Codex");

    public string PauseButtonText => IsPaused
        ? Text("Action_ResumeVideo", "Resume video")
        : Text("Action_PauseVideo", "Pause video");

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
        var normalizedPath = Path.GetFullPath(mediaPath);
        var kind = InferMediaKind(normalizedPath);
        if (kind == MediaKind.None)
        {
            throw new MediaValidationException("The selected extension is not supported.");
        }

        SelectedMediaPath = normalizedPath;
        SelectedMediaKind = kind;
        IsMediaMissing = !File.Exists(normalizedPath);
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
        if (AcceptedCdpRisk)
        {
            return;
        }

        var saved = await _wallpaper
            .SaveSettingsAsync(
                SavedDesired with { AcceptedCdpRisk = true },
                cancellationToken)
            .ConfigureAwait(true);
        SavedDesired = saved;
        AcceptedCdpRisk = true;
        OnPropertyChanged(nameof(IsDraftDirty));
        ShowStatus(
            Text("Status_RiskAcceptedTitle", "Enhanced launch enabled"),
            Text(
                "Status_RiskAcceptedMessage",
                "The local debugging-port acknowledgement was saved."),
            UiStatusTone.Success);
    }

    public async Task RevokeRiskAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        var saved = await _wallpaper
            .SaveSettingsAsync(
                SavedDesired with { AcceptedCdpRisk = false },
                cancellationToken)
            .ConfigureAwait(true);
        SavedDesired = saved;
        AcceptedCdpRisk = false;
        OnPropertyChanged(nameof(IsDraftDirty));
        ShowStatus(
            Text("Status_RiskRevokedTitle", "Enhanced launch disabled"),
            Text(
                "Status_RiskRevokedMessage",
                "Future launches will require acknowledgement again."),
            UiStatusTone.Informational);
    }

    public async Task<bool> ApplyAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (IsBusy)
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

        var request = SavedDesired with
        {
            MediaPath = SelectedMediaPath,
            MediaKind = SelectedMediaKind,
            Fit = Fit,
            PanelOpacity = PanelOpacity,
            BlurPx = BlurPx,
            AcceptedCdpRisk = true,
        };

        return await RunApplyAsync(request, cancellationToken).ConfigureAwait(true);
    }

    public async Task<AutoLaunchOutcome> AutoLaunchAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
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
        var saved = await _wallpaper
            .SaveSettingsAsync(
                SavedDesired.RemoveRecentMediaPath(mediaPath),
                cancellationToken)
            .ConfigureAwait(true);
        SavedDesired = saved;
        RefreshRecents(saved);
    }

    public async Task ClearRecentsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            await InitializeAsync().ConfigureAwait(true);
            var saved = await _wallpaper
                .SaveSettingsAsync(SavedDesired.ClearRecentMediaPaths())
                .ConfigureAwait(true);
            SavedDesired = saved;
            RefreshRecents(saved);
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
    }

    public async Task SetThemeModeAsync(
        ThemeMode themeMode,
        CancellationToken cancellationToken = default)
    {
        var next = Preferences with { ThemeMode = themeMode };
        await _preferencesStore.SaveAsync(next, cancellationToken).ConfigureAwait(true);
        Preferences = next;
    }

    public async Task MarkTrayTipShownAsync(CancellationToken cancellationToken = default)
    {
        if (Preferences.HasShownTrayTip)
        {
            return;
        }

        var next = Preferences with { HasShownTrayTip = true };
        await _preferencesStore.SaveAsync(next, cancellationToken).ConfigureAwait(true);
        Preferences = next;
    }

    public async Task ResetEverythingAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        BeginOperation(Text("Stage_Resetting", "Resetting Backdrop for Codex…"), cancellationToken);
        var failures = new List<Exception>();
        try
        {
            await TryStepAsync(
                () => _wallpaper.DisableAsync(_operationCancellation!.Token),
                failures).ConfigureAwait(true);
            await TryStepAsync(
                async () =>
                {
                    SavedDesired = await _wallpaper
                        .SaveSettingsAsync(
                            SettingsV1.CreateDefault(),
                            _operationCancellation!.Token)
                        .ConfigureAwait(true);
                },
                failures).ConfigureAwait(true);
            await TryStepAsync(
                () => _preferencesStore.ResetAsync(_operationCancellation!.Token),
                failures).ConfigureAwait(true);

            try
            {
                _ = _wallpaper.DeleteOwnedShortcut();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            Preferences = AppPreferencesV1.CreateDefault();
            ApplySavedSettingsToDraft(SavedDesired);
            ActiveSnapshot = null;
            IsActive = false;
            IsPaused = false;
            IsSavedButInactive = false;
            ShortcutNeedsRetry = false;
            RefreshRecents(SavedDesired);

            if (failures.Count == 0)
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
            Preferences = await _preferencesStore.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Preferences = AppPreferencesV1.CreateDefault();
            preferenceWarning = true;
            ShowError(_errorMapper.Map(exception));
        }

        try
        {
            SavedDesired = await _wallpaper.LoadSettingsAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            SavedDesired = SettingsV1.CreateDefault();
            ShowError(
                _errorMapper.Map(exception, UserFacingOperation.LoadWallpaperSettings));
        }

        ApplySavedSettingsToDraft(SavedDesired);
        RefreshRecents(SavedDesired);
        IsActive = _wallpaper.IsActive;
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
        var applied = false;
        _hasForegroundFailure = false;
        ShortcutNeedsRetry = false;
        IsSavedButInactive = false;
        try
        {
            var result = await _wallpaper
                .ApplyAsync(request, _operationCancellation!.Token)
                .ConfigureAwait(true);
            applied = true;
            ActiveSnapshot = result.Settings;
            IsActive = true;
            IsPaused = false;
            ShortcutNeedsRetry = !result.ShortcutReady;

            if (result.ShortcutReady)
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
            _hasForegroundFailure = true;
            IsActive = _wallpaper.IsActive;
            IsPaused = _wallpaper.IsPaused;
            if (!IsActive)
            {
                ActiveSnapshot = null;
            }

            ShowError(_errorMapper.Map(exception, UserFacingOperation.ApplyWallpaper));
            return false;
        }
        finally
        {
            try
            {
                var reloaded = await ReloadPersistedSettingsAsync().ConfigureAwait(true);
                SavedDesired = reloaded;
                RefreshRecents(reloaded);
                IsSavedButInactive =
                    !applied &&
                    !_wallpaper.IsActive &&
                    MatchesAppliedFields(reloaded, request);
                if (IsSavedButInactive)
                {
                    ShowStatus(
                        Text("Status_SavedInactiveTitle", "Saved, not active"),
                        Text(
                            "Status_SavedInactiveMessage",
                            "Your wallpaper settings were saved, but Codex did not activate them. Resolve the message above and retry."),
                        UiStatusTone.Warning);
                }
            }
            catch (Exception reloadException)
            {
                if (!_hasForegroundFailure)
                {
                    ShowError(
                        _errorMapper.Map(
                            reloadException,
                            UserFacingOperation.LoadWallpaperSettings));
                }
            }

            IsActive = _wallpaper.IsActive;
            IsPaused = _wallpaper.IsPaused;
            EndOperation();
            OnPropertyChanged(nameof(IsDraftDirty));
        }
    }

    private async Task TogglePauseAsync()
    {
        if (!CanTogglePause())
        {
            return;
        }

        try
        {
            var pause = !IsPaused;
            await _wallpaper.SetPausedAsync(pause).ConfigureAwait(true);
            IsPaused = pause;
            ShowStatus(
                pause
                    ? Text("Status_PausedTitle", "Video paused")
                    : Text("Status_ResumedTitle", "Video resumed"),
                pause
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
            _hasForegroundFailure = true;
            ShowError(_errorMapper.Map(exception, UserFacingOperation.ApplyWallpaper));
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
            CancellationToken.None);
        _hasForegroundFailure = false;
        try
        {
            await _wallpaper.DisableAsync(_operationCancellation!.Token).ConfigureAwait(true);
            ActiveSnapshot = null;
            IsActive = false;
            IsPaused = false;
            IsSavedButInactive = SavedDesired.MediaPath is not null;
            ShowStatus(
                Text("Status_RestoredTitle", "Official background restored"),
                Text(
                    "Status_RestoredMessage",
                    "Saved wallpaper settings remain available for the next launch."),
                UiStatusTone.Success);
        }
        catch (Exception exception)
        {
            _hasForegroundFailure = true;
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
            if (eventArgs.Phase == WallpaperRuntimePhase.Faulted &&
                _hasForegroundFailure)
            {
                return;
            }

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

            switch (eventArgs.Phase)
            {
                case WallpaperRuntimePhase.Active:
                    IsActive = true;
                    IsPaused = false;
                    break;
                case WallpaperRuntimePhase.Paused:
                    IsActive = true;
                    IsPaused = true;
                    break;
                case WallpaperRuntimePhase.Idle:
                    IsActive = false;
                    IsPaused = false;
                    break;
                case WallpaperRuntimePhase.Faulted:
                    ActiveSnapshot = null;
                    IsActive = false;
                    IsPaused = false;
                    ShowStatus(
                        Text("Status_RuntimeStoppedTitle", "Wallpaper connection stopped"),
                        Text(
                            "Status_RuntimeStoppedMessage",
                            "The runtime connection ended and the app attempted to restore the official background."),
                        UiStatusTone.Error);
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

    private void ApplySavedSettingsToDraft(SettingsV1 settings)
    {
        SelectedMediaPath = settings.MediaPath;
        SelectedMediaKind = settings.MediaKind;
        Fit = settings.Fit;
        PanelOpacity = settings.PanelOpacity;
        BlurPx = settings.BlurPx;
        AcceptedCdpRisk = settings.AcceptedCdpRisk;
        IsMediaMissing = settings.MediaPath is not null && !File.Exists(settings.MediaPath);
        OnPropertyChanged(nameof(IsDraftDirty));
    }

    private void RefreshRecents(SettingsV1 settings)
    {
        Recents.Clear();
        foreach (var path in settings.RecentMediaPaths.Take(SettingsV1.MaximumRecentMediaPaths))
        {
            Recents.Add(
                new RecentMediaItem(
                    path,
                    Path.GetFileName(path),
                    InferMediaKind(path),
                    File.Exists(path)));
        }

        ClearRecentsCommand.NotifyCanExecuteChanged();
    }

    private void BeginOperation(string stage, CancellationToken cancellationToken)
    {
        _operationCancellation?.Dispose();
        _operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        OperationStage = stage;
        IsBusy = true;
    }

    private void EndOperation()
    {
        IsBusy = false;
        OperationStage = string.Empty;
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private void CancelCurrentOperation()
    {
        try
        {
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
        (ActiveSnapshot?.MediaKind == MediaKind.Video || IsVideoSelected);

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

    private static bool MatchesAppliedFields(SettingsV1 left, SettingsV1 right) =>
        string.Equals(left.MediaPath, right.MediaPath, StringComparison.OrdinalIgnoreCase) &&
        left.Fit == right.Fit &&
        Math.Abs(left.PanelOpacity - right.PanelOpacity) < 0.0001 &&
        Math.Abs(left.BlurPx - right.BlurPx) < 0.0001 &&
        left.AcceptedCdpRisk == right.AcceptedCdpRisk;

    private static MediaKind InferMediaKind(string path)
    {
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return MediaKind.Image;
        }

        return VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? MediaKind.Video
            : MediaKind.None;
    }

    private static async Task TryStepAsync(
        Func<Task> operation,
        List<Exception> failures)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private Task<SettingsV1> ReloadPersistedSettingsAsync() =>
        _wallpaper.LoadSettingsAsync(CancellationToken.None);
}
