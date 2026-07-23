using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackdropForCodex.App.Models;
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
public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    public const double MaximumOverlay = 0.60;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly string[] VideoExtensions = [".mp4", ".webm"];

    private readonly IWallpaperApplicationService _wallpaper;
    private readonly IAppPreferencesStore _preferencesStore;
    private readonly IUserFacingErrorMapper _errorMapper;
    private readonly IAppTextProvider _text;
    private readonly SynchronizationContext? _uiContext;
    private readonly object _initializationLock = new();
    private readonly SemaphoreSlim _preferencesMutationGate = new(1, 1);
    private Task? _initializationTask;
    private CancellationTokenSource? _operationCancellation;
    private WallpaperConfigurationState _configurationState =
        WallpaperConfigurationState.FromPersisted(SettingsV1.CreateDefault());
    private WallpaperOperationProgress _operationProgress =
        WallpaperOperationProgress.Idle;
    private AppPreferencesV1 _preferences = AppPreferencesV1.CreateDefault();
    private string? _selectedMediaPath;
    private MediaKind _selectedMediaKind;
    private WallpaperFit _fit = WallpaperFit.Cover;
    private double _focusX = 0.5;
    private double _focusY = 0.5;
    private double _panelOpacity = 0.78;
    private double _blurPx = 14;
    private double _darkOverlay = 0.30;
    private double _lightOverlay = 0.18;
    private bool _acceptedCdpRisk;
    private bool _isMediaMissing;
    private bool _isPaused;
    private bool _shortcutNeedsRetry;
    private string _operationStage = string.Empty;
    private string _statusTitle = string.Empty;
    private string _statusMessage = string.Empty;
    private UiStatusTone _statusTone;
    private bool _isStatusOpen;
    private bool _isSynchronizingEditor;
    private bool _isDisposed;

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
        CancelCommand =
            new RelayCommand(
                CancelCurrentOperation,
                () => OperationProgress.CanCancel);
        RetryShortcutCommand = new AsyncRelayCommand(RetryShortcutAsync, CanRetryShortcut);
        ClearRecentsCommand = new AsyncRelayCommand(ClearRecentsAsync, () => !IsBusy && Recents.Count > 0);
    }

    public ObservableCollection<RecentMediaItem> Recents { get; } = [];

    public IAsyncRelayCommand TogglePauseCommand { get; }

    public IAsyncRelayCommand DisableCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand RetryShortcutCommand { get; }

    public IAsyncRelayCommand ClearRecentsCommand { get; }

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
                OnPropertyChanged(nameof(ApplyButtonText));
                NotifyCommandStateChanged();
            }
        }
    }

    public SettingsV1 SavedDesired => ConfigurationState.SavedDesired;

    public SettingsV1? ActiveSnapshot => ConfigurationState.ActiveSnapshot;

    public WallpaperOperationProgress OperationProgress
    {
        get => _operationProgress;
        private set
        {
            if (SetProperty(ref _operationProgress, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(CanAdjustFocus));
                NotifyCommandStateChanged();
            }
        }
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
                OnPropertyChanged(nameof(CanAdjustFocus));
                SynchronizeDraftState();
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
                SynchronizeDraftState();
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
                OnPropertyChanged(nameof(IsCoverFit));
                OnPropertyChanged(nameof(CanAdjustFocus));
                SynchronizeDraftState();
            }
        }
    }

    public bool IsCoverFit => Fit == WallpaperFit.Cover;

    public bool CanAdjustFocus => CanEdit && HasSelectedMedia && IsCoverFit;

    public double FocusX
    {
        get => _focusX;
        set
        {
            if (SetProperty(ref _focusX, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(FocusLabel));
                SynchronizeDraftState();
            }
        }
    }

    public double FocusY
    {
        get => _focusY;
        set
        {
            if (SetProperty(ref _focusY, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(FocusLabel));
                SynchronizeDraftState();
            }
        }
    }

    public string FocusLabel => $"{FocusX:P0}, {FocusY:P0}";

    public double PanelOpacity
    {
        get => _panelOpacity;
        set
        {
            if (SetProperty(ref _panelOpacity, value))
            {
                OnPropertyChanged(nameof(PanelOpacityPercent));
                SynchronizeDraftState();
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
                SynchronizeDraftState();
            }
        }
    }

    public string BlurLabel => $"{BlurPx:N0} px";

    public double DarkOverlay
    {
        get => _darkOverlay;
        set
        {
            if (SetProperty(ref _darkOverlay, ClampOverlay(value)))
            {
                OnPropertyChanged(nameof(DarkOverlayPercent));
                SynchronizeDraftState();
            }
        }
    }

    public string DarkOverlayPercent => $"{DarkOverlay:P0}";

    public double LightOverlay
    {
        get => _lightOverlay;
        set
        {
            if (SetProperty(ref _lightOverlay, ClampOverlay(value)))
            {
                OnPropertyChanged(nameof(LightOverlayPercent));
                SynchronizeDraftState();
            }
        }
    }

    public string LightOverlayPercent => $"{LightOverlay:P0}";

    public bool AcceptedCdpRisk
    {
        get => _acceptedCdpRisk;
        private set
        {
            if (SetProperty(ref _acceptedCdpRisk, value))
            {
                SynchronizeDraftState();
            }
        }
    }

    public bool IsBusy => OperationProgress.IsBusy;

    public bool CanEdit => !IsBusy;

    public bool IsActive => ConfigurationState.IsRuntimeActive;

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

    public bool IsSavedButInactive => ConfigurationState.IsSavedButNotActive;

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

    public bool IsDraftDirty => ConfigurationState.HasUnsavedChanges;

    public string ApplyButtonText => IsActive
        ? Text("Action_ApplyChanges", "Apply changes")
        : Text("Action_ApplyAndLaunch", "Apply & launch Codex");

    public string PauseButtonText => IsPaused
        ? Text("Action_ResumeVideo", "Resume video")
        : Text("Action_PauseVideo", "Pause video");

    public void SetFocus(double focusX, double focusY)
    {
        FocusX = focusX;
        FocusY = focusY;
    }

    public void ResetFocus() => SetFocus(0.5, 0.5);

    public void NudgeFocus(double horizontalDelta, double verticalDelta) =>
        SetFocus(FocusX + horizontalDelta, FocusY + verticalDelta);

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

        _isSynchronizingEditor = true;
        try
        {
            SelectedMediaKind = kind;
            SelectedMediaPath = normalizedPath;
            IsMediaMissing = !File.Exists(normalizedPath);
        }
        finally
        {
            _isSynchronizingEditor = false;
        }

        SynchronizeDraftState();
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
        if (AcceptedCdpRisk || IsBusy)
        {
            return;
        }

        BeginOperation(
            Text("Stage_Saving", "Saving settings…"),
            cancellationToken,
            WallpaperOperationStage.Saving);
        try
        {
            var saved = await _wallpaper
                .SaveSettingsAsync(
                    ClampLegacyOverlays(SavedDesired) with { AcceptedCdpRisk = true },
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
            SetPersistedSettings(saved, synchronizeEditor: false);
            AcceptedCdpRisk = true;
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
        if (IsBusy)
        {
            return;
        }

        BeginOperation(
            Text("Stage_Saving", "Saving settings…"),
            cancellationToken,
            WallpaperOperationStage.Saving);
        try
        {
            var saved = await _wallpaper
                .SaveSettingsAsync(
                    ClampLegacyOverlays(SavedDesired) with { AcceptedCdpRisk = false },
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
            SetPersistedSettings(saved, synchronizeEditor: false);
            AcceptedCdpRisk = false;
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

        var request = ConfigurationState.Draft with { AcceptedCdpRisk = true };

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
        if (IsBusy)
        {
            return;
        }

        BeginOperation(
            Text("Stage_Saving", "Saving settings…"),
            cancellationToken,
            WallpaperOperationStage.Saving);
        try
        {
            var saved = await _wallpaper
                .SaveSettingsAsync(
                    ClampLegacyOverlays(SavedDesired).RemoveRecentMediaPath(mediaPath),
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
            SetPersistedSettings(saved, synchronizeEditor: false);
            RefreshRecents(saved);
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task ClearRecentsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var operationStarted = false;
        try
        {
            await InitializeAsync().ConfigureAwait(true);
            if (IsBusy)
            {
                return;
            }

            BeginOperation(
                Text("Stage_Saving", "Saving settings…"),
                CancellationToken.None,
                WallpaperOperationStage.Saving);
            operationStarted = true;
            var saved = await _wallpaper
                .SaveSettingsAsync(
                    ClampLegacyOverlays(SavedDesired).ClearRecentMediaPaths(),
                    _operationCancellation!.Token)
                .ConfigureAwait(true);
            SetPersistedSettings(saved, synchronizeEditor: false);
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
        await UpdatePreferencesAsync(
                current => current with { ThemeMode = themeMode },
                cancellationToken)
            .ConfigureAwait(true);
    }

    public async Task MarkTrayTipShownAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync().ConfigureAwait(true);
        if (Preferences.HasShownTrayTip)
        {
            return;
        }

        await UpdatePreferencesAsync(
                current => current with { HasShownTrayTip = true },
                cancellationToken)
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
                    var saved = await _wallpaper
                        .SaveSettingsAsync(
                            SettingsV1.CreateDefault(),
                            _operationCancellation!.Token)
                        .ConfigureAwait(true);
                    _ = saved;
                },
                failures).ConfigureAwait(true);
            await TryStepAsync(
                () => ResetPreferencesAsync(_operationCancellation!.Token),
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

    public void ShowUnexpectedError(Exception exception) =>
        ShowError(_errorMapper.Map(exception));

    private async Task InitializeCoreAsync()
    {
        var preferenceWarning = false;
        try
        {
            Preferences = await LoadPreferencesAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Preferences = AppPreferencesV1.CreateDefault();
            preferenceWarning = true;
            ShowError(_errorMapper.Map(exception));
        }

        try
        {
            var saved = await _wallpaper.LoadSettingsAsync().ConfigureAwait(true);
            SetPersistedSettings(saved, synchronizeEditor: false);
        }
        catch (Exception exception)
        {
            SetPersistedSettings(
                SettingsV1.CreateDefault(),
                synchronizeEditor: false);
            ShowError(
                _errorMapper.Map(exception, UserFacingOperation.LoadWallpaperSettings));
        }

        ApplySavedSettingsToDraft(SavedDesired);
        RefreshRecents(SavedDesired);
        ConfigurationState = _wallpaper.IsActive
            ? ConfigurationState.WithRuntimeActive(isRuntimeActive: true)
            : ConfigurationState.WithoutActive();
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
            ConfigurationState = ConfigurationState.WithActive(result.Settings);
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
            foregroundFailure = true;
            ConfigurationState = _wallpaper.IsActive
                ? ConfigurationState.WithRuntimeActive(isRuntimeActive: true)
                : ConfigurationState.WithoutActive();
            IsPaused = _wallpaper.IsPaused;

            ShowError(_errorMapper.Map(exception, UserFacingOperation.ApplyWallpaper));
            return false;
        }
        finally
        {
            try
            {
                var reloaded = await ReloadPersistedSettingsAsync().ConfigureAwait(true);
                SetPersistedSettings(reloaded, synchronizeEditor: false);
                RefreshRecents(reloaded);
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

            ConfigurationState = _wallpaper.IsActive
                ? ConfigurationState.WithRuntimeActive(isRuntimeActive: true)
                : ConfigurationState.WithoutActive();
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
            ConfigurationState = _wallpaper.IsActive
                ? ConfigurationState.WithRuntimeActive(isRuntimeActive: true)
                : ConfigurationState.WithoutActive();
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
            ConfigurationState = _wallpaper.IsActive
                ? ConfigurationState.WithRuntimeActive(isRuntimeActive: true)
                : ConfigurationState.WithoutActive();
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
            ConfigurationState = ConfigurationState.WithoutActive();
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
            ConfigurationState = _wallpaper.IsActive
                ? ConfigurationState.WithRuntimeActive(isRuntimeActive: true)
                : ConfigurationState.WithoutActive();
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
                    ConfigurationState =
                        ConfigurationState.WithRuntimeActive(isRuntimeActive: true);
                    IsPaused = false;
                    break;
                case WallpaperRuntimePhase.Paused:
                    ConfigurationState =
                        ConfigurationState.WithRuntimeActive(isRuntimeActive: true);
                    IsPaused = true;
                    break;
                case WallpaperRuntimePhase.Idle:
                    ConfigurationState = ConfigurationState.WithoutActive();
                    IsPaused = false;
                    break;
                case WallpaperRuntimePhase.Faulted:
                    ConfigurationState = _wallpaper.IsActive
                        ? ConfigurationState.WithRuntimeActive(isRuntimeActive: true)
                        : ConfigurationState.WithoutActive();
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

    private void ApplySavedSettingsToDraft(SettingsV1 settings)
    {
        _isSynchronizingEditor = true;
        try
        {
            SelectedMediaKind = settings.MediaKind;
            SelectedMediaPath = settings.MediaPath;
            Fit = settings.Fit;
            FocusX = settings.FocusX;
            FocusY = settings.FocusY;
            PanelOpacity = settings.PanelOpacity;
            BlurPx = settings.BlurPx;
            DarkOverlay = settings.DarkOverlay;
            LightOverlay = settings.LightOverlay;
            AcceptedCdpRisk = settings.AcceptedCdpRisk;
            IsMediaMissing =
                settings.MediaPath is not null &&
                !File.Exists(settings.MediaPath);
        }
        finally
        {
            _isSynchronizingEditor = false;
        }

        SynchronizeDraftState();
    }

    private SettingsV1 BuildDraftSettings(SettingsV1 baseline) =>
        baseline with
        {
            MediaPath = SelectedMediaPath,
            MediaKind = SelectedMediaPath is null ? MediaKind.None : SelectedMediaKind,
            Fit = Fit,
            FocusX = FocusX,
            FocusY = FocusY,
            PanelOpacity = PanelOpacity,
            BlurPx = BlurPx,
            DarkOverlay = DarkOverlay,
            LightOverlay = LightOverlay,
            AcceptedCdpRisk = AcceptedCdpRisk,
        };

    private void SynchronizeDraftState()
    {
        if (_isSynchronizingEditor)
        {
            return;
        }

        ConfigurationState =
            ConfigurationState.WithDraft(BuildDraftSettings(SavedDesired));
    }

    private void SetPersistedSettings(
        SettingsV1 settings,
        bool synchronizeEditor)
    {
        ConfigurationState =
            ConfigurationState.WithPersisted(
                settings,
                synchronizeDraft: synchronizeEditor);
        if (synchronizeEditor)
        {
            ApplySavedSettingsToDraft(settings);
            return;
        }

        ConfigurationState =
            ConfigurationState.WithDraft(BuildDraftSettings(settings));
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

    private static double ClampOverlay(double value) =>
        Math.Clamp(value, 0, MaximumOverlay);

    private static SettingsV1 ClampLegacyOverlays(SettingsV1 settings) =>
        settings with
        {
            DarkOverlay = ClampOverlay(settings.DarkOverlay),
            LightOverlay = ClampOverlay(settings.LightOverlay),
        };

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
            var saved = await _wallpaper
                .LoadSettingsAsync(CancellationToken.None)
                .ConfigureAwait(true);
            SetPersistedSettings(saved, synchronizeEditor: false);
            ApplySavedSettingsToDraft(saved);
            RefreshRecents(saved);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            Preferences = await LoadPreferencesAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        ConfigurationState = _wallpaper.IsActive
            ? ConfigurationState.WithRuntimeActive(isRuntimeActive: true)
            : ConfigurationState.WithoutActive();
        IsPaused = _wallpaper.IsActive && _wallpaper.IsPaused;
        ShortcutNeedsRetry = false;
    }

    private Task<SettingsV1> ReloadPersistedSettingsAsync() =>
        _wallpaper.LoadSettingsAsync(CancellationToken.None);

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _wallpaper.StatusChanged -= Wallpaper_StatusChanged;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
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

    private async Task ResetPreferencesAsync(CancellationToken cancellationToken)
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

    private async Task<AppPreferencesV1> LoadPreferencesAsync(
        CancellationToken cancellationToken)
    {
        await _preferencesMutationGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(true);
        try
        {
            return await _preferencesStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            _ = _preferencesMutationGate.Release();
        }
    }
}
