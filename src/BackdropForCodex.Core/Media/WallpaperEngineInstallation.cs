using System.Collections.ObjectModel;

namespace BackdropForCodex.Core.Media;

public enum WallpaperEngineAvailabilityReason
{
    UnsupportedPlatform = 0,
    SteamNotFound,
    InvalidSteamConfiguration,
    NotInstalled,
    ExecutableMissing,
    ExecutableUntrusted,
    MultipleInstallations,
    PreferredInstallationInvalid,
}

/// <summary>
/// A validated Wallpaper Engine installation and the local Steam libraries that may contain its
/// already-installed Workshop projects. Instances are produced by an installation locator; paths
/// are intentionally redacted from diagnostics.
/// </summary>
public sealed record WallpaperEngineInstallation
{
    public const uint SteamApplicationId = 431960;

    public WallpaperEngineInstallation(
        string steamRootPath,
        string installRootPath,
        string controlExecutablePath,
        IEnumerable<string> steamLibraryPaths)
    {
        SteamRootPath = NormalizeAbsolutePath(steamRootPath, nameof(steamRootPath));
        InstallRootPath = NormalizeAbsolutePath(installRootPath, nameof(installRootPath));
        ControlExecutablePath = NormalizeAbsolutePath(
            controlExecutablePath,
            nameof(controlExecutablePath));
        ArgumentNullException.ThrowIfNull(steamLibraryPaths);

        var libraries = steamLibraryPaths
            .Select(path => NormalizeAbsolutePath(path, nameof(steamLibraryPaths)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (libraries.Length == 0)
        {
            throw new ArgumentException(
                "At least one local Steam library is required.",
                nameof(steamLibraryPaths));
        }

        SteamLibraryPaths = new ReadOnlyCollection<string>(libraries);
    }

    public string SteamRootPath { get; }

    public string InstallRootPath { get; }

    public string ControlExecutablePath { get; }

    public IReadOnlyList<string> SteamLibraryPaths { get; }

    public override string ToString() =>
        $"{nameof(WallpaperEngineInstallation)} {{ SteamApplicationId = {SteamApplicationId}, " +
        $"SteamLibraryCount = {SteamLibraryPaths.Count}, Paths = <redacted> }}";

    private static string NormalizeAbsolutePath(string path, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(path, parameterName);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The path must be fully qualified.", parameterName);
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The path is invalid.", parameterName, exception);
        }
    }
}

public interface IWallpaperEngineInstallationLocator
{
    ValueTask<WallpaperEngineInstallation> LocateAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Allows tests and alternate hosts to provide the trust decision while the default implementation
/// uses Windows Authenticode verification. A successful result authorizes only the exact path
/// supplied for that call.
/// </summary>
public interface IWallpaperEngineExecutableTrustVerifier
{
    bool IsTrusted(string executablePath);
}

public sealed class WallpaperEngineUnavailableException : InvalidOperationException
{
    public WallpaperEngineUnavailableException(WallpaperEngineAvailabilityReason reason)
        : base(CreateMessage(reason))
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        Reason = reason;
    }

    public WallpaperEngineUnavailableException(
        WallpaperEngineAvailabilityReason reason,
        Exception innerException)
        : base(CreateMessage(reason))
    {
        ArgumentNullException.ThrowIfNull(innerException);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        Reason = reason;
        DiagnosticExceptionType = innerException.GetType().Name;
        DiagnosticHResult = innerException.HResult;
    }

    public WallpaperEngineAvailabilityReason Reason { get; }

    /// <summary>
    /// The path-free CLR type name of the discovery failure. The original exception, message and
    /// stack are deliberately not retained because Steam metadata errors often contain user paths.
    /// </summary>
    public string? DiagnosticExceptionType { get; }

    /// <summary>
    /// The path-free HRESULT associated with the discovery failure.
    /// </summary>
    public int? DiagnosticHResult { get; }

    private static string CreateMessage(WallpaperEngineAvailabilityReason reason) => reason switch
    {
        WallpaperEngineAvailabilityReason.UnsupportedPlatform =>
            "Wallpaper Engine integration requires Windows.",
        WallpaperEngineAvailabilityReason.SteamNotFound =>
            "A local Steam installation could not be found.",
        WallpaperEngineAvailabilityReason.InvalidSteamConfiguration =>
            "The local Steam library configuration could not be validated.",
        WallpaperEngineAvailabilityReason.NotInstalled =>
            "Wallpaper Engine is not installed in a validated local Steam library.",
        WallpaperEngineAvailabilityReason.ExecutableMissing =>
            "The Wallpaper Engine control executable is missing.",
        WallpaperEngineAvailabilityReason.ExecutableUntrusted =>
            "The Wallpaper Engine control executable did not pass Authenticode verification.",
        WallpaperEngineAvailabilityReason.MultipleInstallations =>
            "Multiple Wallpaper Engine installations require an explicit selection.",
        WallpaperEngineAvailabilityReason.PreferredInstallationInvalid =>
            "The selected Wallpaper Engine installation could not be validated.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}
