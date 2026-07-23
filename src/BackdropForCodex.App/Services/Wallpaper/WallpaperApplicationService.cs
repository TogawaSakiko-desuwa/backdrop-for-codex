using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using BackdropForCodex.Core.Shortcuts;

namespace BackdropForCodex.App.Services.Wallpaper;

public sealed record WallpaperApplyResult(
    SettingsV1 Settings,
    bool ShortcutReady,
    Exception? ShortcutError = null);

public interface IWallpaperApplicationService : IAsyncDisposable
{
    event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? StatusChanged;

    bool IsActive { get; }

    bool IsPaused { get; }

    Task<SettingsV1> LoadSettingsAsync(CancellationToken cancellationToken = default);

    Task<SettingsV1> SaveSettingsAsync(
        SettingsV1 settings,
        CancellationToken cancellationToken = default);

    Task<WallpaperApplyResult> ApplyAsync(
        SettingsV1 settings,
        CancellationToken cancellationToken = default);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default);

    Task DisableAsync(CancellationToken cancellationToken = default);

    DesktopShortcutWriteResult CreateOrUpdateShortcut();

    DesktopShortcutDeleteResult DeleteOwnedShortcut();
}

/// <summary>
/// Keeps shell integration outside the view model while preserving the coordinator's lifecycle.
/// </summary>
public sealed class WallpaperApplicationService : IWallpaperApplicationService
{
    private readonly WallpaperCoordinator _coordinator;
    private int _disposeState;

    public WallpaperApplicationService(WallpaperCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _coordinator.StatusChanged += Coordinator_StatusChanged;
    }

    public event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? StatusChanged;

    public bool IsActive => _coordinator.IsActive;

    public bool IsPaused => _coordinator.IsPaused;

    public Task<SettingsV1> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        _coordinator.LoadSettingsAsync(cancellationToken);

    public Task<SettingsV1> SaveSettingsAsync(
        SettingsV1 settings,
        CancellationToken cancellationToken = default) =>
        _coordinator.SaveSettingsAsync(settings, cancellationToken);

    public async Task<WallpaperApplyResult> ApplyAsync(
        SettingsV1 settings,
        CancellationToken cancellationToken = default)
    {
        var persisted = await _coordinator
            .StartOrUpdateAsync(settings, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            _ = CreateOrUpdateShortcut();
            return new WallpaperApplyResult(persisted, ShortcutReady: true);
        }
        catch (Exception exception)
        {
            // The shortcut is optional. An active wallpaper remains a successful runtime result.
            return new WallpaperApplyResult(
                persisted,
                ShortcutReady: false,
                ShortcutError: exception);
        }
    }

    public Task SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default) =>
        _coordinator.SetPausedAsync(paused, cancellationToken);

    public Task DisableAsync(CancellationToken cancellationToken = default) =>
        _coordinator.DisableAsync(cancellationToken);

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

        _coordinator.StatusChanged -= Coordinator_StatusChanged;
        await _coordinator.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            throw new PlatformNotSupportedException("Backdrop for Codex requires Windows 11.");
        }
    }

    private void Coordinator_StatusChanged(
        object? sender,
        WallpaperRuntimeStatusChangedEventArgs eventArgs) =>
        StatusChanged?.Invoke(this, eventArgs);
}
