using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using BackdropForCodex.Core.Shortcuts;
using BackdropForCodex.App.Services.Media;

namespace BackdropForCodex.App.Services.Wallpaper;

public sealed record WallpaperApplyResult(
    RuntimeActivationResult Activation,
    SettingsV3 SavedDesired,
    bool ShortcutReady,
    Exception? ShortcutError = null)
{
    public RuntimeActivationOutcome Outcome => Activation.Outcome;

    public long Revision => Activation.Revision;

    public SettingsV3? ActiveSnapshot => Activation.ActiveSnapshot;

    public WallpaperRuntimeSurface Surface => Activation.Surface;
}

public interface IWallpaperApplicationService : IAsyncDisposable
{
    event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? StatusChanged;

    event EventHandler<WallpaperWorkspaceStateChangedEventArgs>? WorkspaceChanged;

    WallpaperWorkspaceState Workspace { get; }

    bool IsActive { get; }

    bool IsPaused { get; }

    bool HasVersion1Backup { get; }

    Task<WallpaperWorkspaceState> InitializeAsync(
        CancellationToken cancellationToken = default);

    void ReplaceDraft(SettingsV3 draft);

    WallpaperProfile CreateProfile(string baseName = "New profile");

    WallpaperProfile DuplicateProfile(Guid profileId, string suffix = "Copy");

    WallpaperProfile RenameProfile(Guid profileId, string name);

    void DeleteProfile(Guid profileId, Guid? replacementProfileId = null);

    void SelectProfile(Guid profileId);

    MediaReference SelectLocalMedia(Guid profileId, string path, MediaKind mediaKind);

    void ClearMedia(Guid profileId);

    Task<WallpaperApplyResult> ApplyAsync(
        RuntimeLaunchMode launchMode = RuntimeLaunchMode.ManualApply,
        CancellationToken cancellationToken = default);

    Task<WallpaperApplyResult> ApplyAsync(
        RuntimeLaunchMode launchMode,
        WallpaperSourceResolution? expectedSourceResolution,
        CancellationToken cancellationToken = default);

    void CancelLatestApply();

    Task<SettingsV3> SetRiskAcceptanceAsync(
        bool accepted,
        CancellationToken cancellationToken = default);

    Task<SettingsV3> RemoveRecentMediaAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default);

    Task<SettingsV3> ClearRecentMediaAsync(
        CancellationToken cancellationToken = default);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default);

    Task<RuntimeActivationResult> RestoreOfficialAsync(
        CancellationToken cancellationToken = default);

    Task<SettingsV3> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken = default);

    Task<SettingsV3> ResetWallpaperSettingsAsync(
        CancellationToken cancellationToken = default);

    DesktopShortcutWriteResult CreateOrUpdateShortcut();

    DesktopShortcutDeleteResult DeleteOwnedShortcut();
}

public interface IWallpaperApplicationCapabilitySource
{
    event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>? CapabilitiesChanged;

    CompatibilityCapabilities Capabilities { get; }

    WallpaperCompatibilitySnapshot Compatibility { get; }
}

/// <summary>
/// Owns the application workspace actor and keeps optional shell integration outside Core.
/// </summary>
public sealed class WallpaperApplicationService :
    IWallpaperApplicationService,
    IWallpaperApplicationCapabilitySource,
    IDynamicWallpaperCapabilitySource
{
    private readonly WallpaperWorkspaceCoordinator _workspace;
    private readonly IWallpaperRuntimeCapabilitySource? _capabilitySource;
    private readonly IDynamicWallpaperCapabilitySource? _dynamicCapabilitySource;
    private int _disposeState;

    public WallpaperApplicationService(
        WallpaperWorkspaceCoordinator workspace,
        IWallpaperRuntimeCapabilitySource? capabilitySource = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _capabilitySource = capabilitySource;
        _dynamicCapabilitySource = capabilitySource as IDynamicWallpaperCapabilitySource;
        _workspace.RuntimeStatusChanged += Workspace_RuntimeStatusChanged;
        _workspace.StateChanged += Workspace_StateChanged;
    }

    public event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<WallpaperWorkspaceStateChangedEventArgs>? WorkspaceChanged;

    public event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>? CapabilitiesChanged
    {
        add
        {
            if (_capabilitySource is not null)
            {
                _capabilitySource.CapabilitiesChanged += value;
            }
        }
        remove
        {
            if (_capabilitySource is not null)
            {
                _capabilitySource.CapabilitiesChanged -= value;
            }
        }
    }

    public WallpaperWorkspaceState Workspace => _workspace.State;

    public bool IsActive => _workspace.IsRuntimeActive;

    public bool IsPaused => _workspace.IsPaused;

    public bool HasVersion1Backup => _workspace.HasVersion1Backup;

    public CompatibilityCapabilities Capabilities =>
        _capabilitySource?.Capabilities ??
        WallpaperCompatibilitySnapshot.NotEvaluated.Capabilities;

    public WallpaperCompatibilitySnapshot Compatibility =>
        _capabilitySource?.Compatibility ??
        WallpaperCompatibilitySnapshot.NotEvaluated;

    public ValueTask<DynamicWallpaperCapability> ProbeDynamicWallpaperAsync(
        CancellationToken cancellationToken = default) =>
        _dynamicCapabilitySource?.ProbeDynamicWallpaperAsync(cancellationToken) ??
        ValueTask.FromResult(DynamicWallpaperCapability.Unavailable(
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineUnavailable));

    public static WallpaperApplicationService CreateDefault(string settingsPath) =>
        CreateDefault(settingsPath, AppWallpaperSources.Registry);

    public static WallpaperApplicationService CreateDefault(
        string settingsPath,
        IWallpaperSourceProviderRegistry sourceRegistry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        ArgumentNullException.ThrowIfNull(sourceRegistry);
        var runtime = WallpaperCoordinator.CreateDefault(
            sourceRegistry,
            AppWallpaperSources.WallpaperEngineLocator);
        var workspace = new WallpaperWorkspaceCoordinator(
            new SettingsRepository(settingsPath),
            runtime,
            sourceRegistry);
        return new WallpaperApplicationService(workspace, runtime);
    }

    public Task<WallpaperWorkspaceState> InitializeAsync(
        CancellationToken cancellationToken = default) =>
        _workspace.InitializeAsync(cancellationToken);

    public void ReplaceDraft(SettingsV3 draft) => _workspace.ReplaceDraft(draft);

    public WallpaperProfile CreateProfile(string baseName = "New profile") =>
        _workspace.CreateProfile(baseName);

    public WallpaperProfile DuplicateProfile(Guid profileId, string suffix = "Copy") =>
        _workspace.DuplicateProfile(profileId, suffix);

    public WallpaperProfile RenameProfile(Guid profileId, string name) =>
        _workspace.RenameProfile(profileId, name);

    public void DeleteProfile(Guid profileId, Guid? replacementProfileId = null) =>
        _workspace.DeleteProfile(profileId, replacementProfileId);

    public void SelectProfile(Guid profileId) => _workspace.SelectProfile(profileId);

    public MediaReference SelectLocalMedia(
        Guid profileId,
        string path,
        MediaKind mediaKind) =>
        _workspace.SelectLocalMedia(profileId, path, mediaKind);

    public void ClearMedia(Guid profileId) => _workspace.ClearMedia(profileId);

    public async Task<WallpaperApplyResult> ApplyAsync(
        RuntimeLaunchMode launchMode = RuntimeLaunchMode.ManualApply,
        CancellationToken cancellationToken = default) =>
        await ApplyAsync(launchMode, expectedSourceResolution: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task<WallpaperApplyResult> ApplyAsync(
        RuntimeLaunchMode launchMode,
        WallpaperSourceResolution? expectedSourceResolution,
        CancellationToken cancellationToken = default)
    {
        var activation = await _workspace
            .ApplyAsync(launchMode, expectedSourceResolution, cancellationToken)
            .ConfigureAwait(false);

        if (!ShouldCreateShortcut(activation.Outcome))
        {
            return new WallpaperApplyResult(
                activation,
                _workspace.State.SavedDesired,
                ShortcutReady: false);
        }

        try
        {
            _ = CreateOrUpdateShortcut();
            return new WallpaperApplyResult(
                activation,
                _workspace.State.SavedDesired,
                ShortcutReady: true);
        }
        catch (Exception exception)
        {
            return new WallpaperApplyResult(
                activation,
                _workspace.State.SavedDesired,
                ShortcutReady: false,
                ShortcutError: exception);
        }
    }

    internal static bool ShouldCreateShortcut(RuntimeActivationOutcome outcome) =>
        outcome == RuntimeActivationOutcome.MediaActive;

    public void CancelLatestApply() => _workspace.CancelLatestApply();

    public Task<SettingsV3> SetRiskAcceptanceAsync(
        bool accepted,
        CancellationToken cancellationToken = default) =>
        _workspace.SetRiskAcceptanceAsync(accepted, cancellationToken);

    public Task<SettingsV3> RemoveRecentMediaAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default) =>
        _workspace.RemoveRecentMediaAsync(mediaId, cancellationToken);

    public Task<SettingsV3> ClearRecentMediaAsync(
        CancellationToken cancellationToken = default) =>
        _workspace.ClearRecentMediaAsync(cancellationToken);

    public Task SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default) =>
        _workspace.SetPausedAsync(paused, cancellationToken);

    public Task<RuntimeActivationResult> RestoreOfficialAsync(
        CancellationToken cancellationToken = default) =>
        _workspace.RestoreOfficialAsync(cancellationToken);

    public Task<SettingsV3> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken = default) =>
        _workspace.RestoreVersion1BackupAsync(cancellationToken);

    public Task<SettingsV3> ResetWallpaperSettingsAsync(
        CancellationToken cancellationToken = default) =>
        _workspace.ResetAsync(cancellationToken);

    public DesktopShortcutWriteResult CreateOrUpdateShortcut()
    {
        EnsureSupportedPlatform();
        return WindowsDesktopShortcutService.CreateOrUpdate();
    }

    public DesktopShortcutDeleteResult DeleteOwnedShortcut()
    {
        EnsureSupportedPlatform();
        return WindowsDesktopShortcutService.DeleteIfOwned();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _workspace.RuntimeStatusChanged -= Workspace_RuntimeStatusChanged;
        _workspace.StateChanged -= Workspace_StateChanged;
        await _workspace.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            throw new PlatformNotSupportedException(
                "Backdrop for Codex requires Windows 11.");
        }
    }

    private void Workspace_RuntimeStatusChanged(
        object? sender,
        WallpaperRuntimeStatusChangedEventArgs eventArgs) =>
        StatusChanged?.Invoke(this, eventArgs);

    private void Workspace_StateChanged(
        object? sender,
        WallpaperWorkspaceStateChangedEventArgs eventArgs) =>
        WorkspaceChanged?.Invoke(this, eventArgs);
}
