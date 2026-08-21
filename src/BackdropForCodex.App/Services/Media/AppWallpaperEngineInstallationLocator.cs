using System.IO;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.App.Services.Media;

public interface IWallpaperEngineInstallationPreferenceManager
{
    string? PreferredInstallRootPath { get; }

    ValueTask<string> ValidateSelectionAsync(
        string selectedPath,
        CancellationToken cancellationToken = default);

    void SetPreferredInstallRootPath(string preferredInstallRootPath);

    void ClearPreferredInstallRootPath();
}

/// <summary>
/// UI-facing transaction boundary for selecting or clearing a machine-local Wallpaper Engine
/// installation. The choice is process-local and must never be persisted as a path.
/// </summary>
public interface IWallpaperEngineInstallationSelectionService
{
    bool HasPreferredWallpaperEngineInstallation { get; }

    Task SelectWallpaperEngineInstallationAsync(
        string selectedPath,
        CancellationToken cancellationToken);

    Task ClearWallpaperEngineInstallationAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Application-wide, refreshable Wallpaper Engine locator. A candidate selection is validated by
/// the Core locator before the caller publishes it to process-local consumers; validation alone
/// never changes what runtime, source providers, previews, or the workspace observe. The selected
/// root is deliberately not persisted and must be selected again after an app restart.
/// </summary>
public sealed class AppWallpaperEngineInstallationLocator
    : IWallpaperEngineInstallationLocator,
      IWallpaperEngineInstallationPreferenceManager
{
    private const string ControlExecutableName = "wallpaper64.exe";

    private readonly Func<string?, IWallpaperEngineInstallationLocator> _locatorFactory;
    private SelectionState _state = new(PreferredInstallRootPath: null);

    public AppWallpaperEngineInstallationLocator()
        : this(
            preferredInstallRootPath => preferredInstallRootPath is null
                ? new WallpaperEngineInstallationLocator()
                : new WallpaperEngineInstallationLocator(preferredInstallRootPath))
    {
    }

    public AppWallpaperEngineInstallationLocator(
        Func<string?, IWallpaperEngineInstallationLocator> locatorFactory)
    {
        _locatorFactory = locatorFactory ?? throw new ArgumentNullException(nameof(locatorFactory));
    }

    /// <summary>
    /// Gets the machine-local preferred installation root. The path is never included in this
    /// object's diagnostics and is not part of SettingsV3 or any wallpaper profile.
    /// </summary>
    public string? PreferredInstallRootPath =>
        Volatile.Read(ref _state).PreferredInstallRootPath;

    public ValueTask<WallpaperEngineInstallation> LocateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preferredInstallRootPath =
            Volatile.Read(ref _state).PreferredInstallRootPath;
        return _locatorFactory(preferredInstallRootPath)
            .LocateAsync(cancellationToken);
    }

    /// <summary>
    /// Strictly validates a selected <c>wallpaper64.exe</c> or its installation directory without
    /// publishing the selection. The returned canonical root may only be retained in process and
    /// subsequently passed to <see cref="SetPreferredInstallRootPath"/>.
    /// </summary>
    public async ValueTask<string> ValidateSelectionAsync(
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installRootPath = ResolveSelectedInstallRoot(selectedPath);
        WallpaperEngineInstallation installation;
        try
        {
            installation = await _locatorFactory(installRootPath)
                .LocateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WallpaperEngineUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid,
                exception);
        }

        if (!string.Equals(
                installRootPath,
                installation.InstallRootPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid);
        }

        return installation.InstallRootPath;
    }

    /// <summary>
    /// Publishes a root that has already passed <see cref="ValidateSelectionAsync"/>. This method
    /// intentionally performs no I/O so every consumer observes the process-local switch atomically.
    /// </summary>
    public void SetPreferredInstallRootPath(string preferredInstallRootPath)
    {
        var normalized = NormalizeLocalAbsolutePath(preferredInstallRootPath);
        Volatile.Write(ref _state, new SelectionState(normalized));
    }

    public void ClearPreferredInstallRootPath() =>
        Volatile.Write(ref _state, new SelectionState(PreferredInstallRootPath: null));

    public override string ToString() =>
        $"{nameof(AppWallpaperEngineInstallationLocator)} {{ HasPreferredInstallation = " +
        $"{PreferredInstallRootPath is not null}, Path = <redacted> }}";

    internal static string NormalizeLocalAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid);
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid,
                exception);
        }
    }

    private static string ResolveSelectedInstallRoot(string selectedPath)
    {
        var normalized = NormalizeLocalAbsolutePath(selectedPath);
        if (Directory.Exists(normalized))
        {
            return normalized;
        }

        if (!File.Exists(normalized) ||
            !string.Equals(
                Path.GetFileName(normalized),
                ControlExecutableName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid);
        }

        return Path.GetDirectoryName(normalized) is { Length: > 0 } directory
            ? directory
            : throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid);
    }

    private sealed record SelectionState(string? PreferredInstallRootPath);
}
