using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace BackdropForCodex.Core.Media;

/// <summary>
/// Locates Wallpaper Engine through Steam metadata instead of fixed drive letters or product
/// versions. Every returned executable has passed the configured trust verifier.
/// </summary>
public sealed class WallpaperEngineInstallationLocator : IWallpaperEngineInstallationLocator
{
    private const string WallpaperEngineManifestName = "appmanifest_431960.acf";
    private const string WallpaperEngineExecutableName = "wallpaper64.exe";

    private readonly IReadOnlyList<string> _steamRootCandidates;
    private readonly IWallpaperEngineExecutableTrustVerifier _trustVerifier;
    private readonly string? _preferredInstallRootPath;
    private readonly bool _hasInvalidSteamRootCandidate;

    public WallpaperEngineInstallationLocator()
        : this(
            WindowsSteamRootDiscovery.Discover(),
            new WindowsAuthenticodeTrustVerifier(),
            preferredInstallRootPath: null)
    {
    }

    public WallpaperEngineInstallationLocator(string preferredInstallRootPath)
        : this(
            WindowsSteamRootDiscovery.Discover(),
            new WindowsAuthenticodeTrustVerifier(),
            preferredInstallRootPath)
    {
    }

    public WallpaperEngineInstallationLocator(
        IEnumerable<string> steamRootCandidates,
        IWallpaperEngineExecutableTrustVerifier trustVerifier,
        string? preferredInstallRootPath = null)
        : this(
            steamRootCandidates,
            trustVerifier,
            preferredInstallRootPath,
            hasInvalidSteamRootCandidate: false)
    {
    }

    private WallpaperEngineInstallationLocator(
        IEnumerable<string> steamRootCandidates,
        IWallpaperEngineExecutableTrustVerifier trustVerifier,
        string? preferredInstallRootPath,
        bool hasInvalidSteamRootCandidate)
    {
        ArgumentNullException.ThrowIfNull(steamRootCandidates);
        ArgumentNullException.ThrowIfNull(trustVerifier);

        _steamRootCandidates = new ReadOnlyCollection<string>(
            steamRootCandidates
                .Select(WallpaperEngineLocalPath.NormalizeAbsolutePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());
        _trustVerifier = trustVerifier;
        _preferredInstallRootPath = string.IsNullOrWhiteSpace(preferredInstallRootPath)
            ? null
            : WallpaperEngineLocalPath.NormalizeAbsolutePath(preferredInstallRootPath);
        _hasInvalidSteamRootCandidate = hasInvalidSteamRootCandidate;
    }

    internal WallpaperEngineInstallationLocator(
        IWindowsSteamRegistryValueSource registryValueSource,
        IWallpaperEngineExecutableTrustVerifier trustVerifier)
        : this(
            WindowsSteamRootDiscovery.Discover(registryValueSource),
            trustVerifier,
            preferredInstallRootPath: null)
    {
    }

    private WallpaperEngineInstallationLocator(
        WindowsSteamRootDiscoveryResult discovery,
        IWallpaperEngineExecutableTrustVerifier trustVerifier,
        string? preferredInstallRootPath)
        : this(
            discovery.Candidates,
            trustVerifier,
            preferredInstallRootPath,
            discovery.HasInvalidCandidate)
    {
    }

    public async ValueTask<WallpaperEngineInstallation> LocateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.UnsupportedPlatform);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var steamRoots = new List<string>();
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalidConfiguration = _hasInvalidSteamRootCandidate;
        string? preferredLibraryPath = null;

        foreach (var candidate in _steamRootCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            try
            {
                var steamRoot = WallpaperEngineLocalPath.ValidateExistingDirectory(candidate);
                var discoveredLibraries = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    steamRoot,
                };

                var libraryFoldersPath = Path.Combine(
                    steamRoot,
                    "steamapps",
                    "libraryfolders.vdf");
                if (!File.Exists(libraryFoldersPath))
                {
                    steamRoots.Add(steamRoot);
                    libraries.UnionWith(discoveredLibraries);
                    continue;
                }

                var document = await ValveKeyValuesParser
                    .ParseFileAsync(libraryFoldersPath, steamRoot, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var libraryPath in ReadLibraryPaths(document))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(libraryPath))
                    {
                        // Steam retains entries for temporarily disconnected local drives. They are
                        // unavailable, but do not make an otherwise valid installation ambiguous.
                        continue;
                    }

                    discoveredLibraries.Add(
                        WallpaperEngineLocalPath.ValidateExistingDirectory(libraryPath));
                }

                steamRoots.Add(steamRoot);
                libraries.UnionWith(discoveredLibraries);
            }
            catch (Exception exception) when (IsInvalidConfigurationException(exception))
            {
                invalidConfiguration = true;
            }
        }

        if (_preferredInstallRootPath is not null)
        {
            try
            {
                preferredLibraryPath = InferSteamLibraryFromInstallRoot(
                    _preferredInstallRootPath);
                libraries.Add(preferredLibraryPath);
                if (steamRoots.Count == 0)
                {
                    steamRoots.Add(preferredLibraryPath);
                }
            }
            catch (Exception exception) when (IsInvalidConfigurationException(exception))
            {
                throw new WallpaperEngineUnavailableException(
                    WallpaperEngineAvailabilityReason.PreferredInstallationInvalid,
                    exception);
            }
        }

        if (_preferredInstallRootPath is not null)
        {
            return await LocatePreferredInstallationAsync(
                    preferredLibraryPath!,
                    steamRoots,
                    libraries,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (steamRoots.Count == 0)
        {
            throw new WallpaperEngineUnavailableException(
                invalidConfiguration
                    ? WallpaperEngineAvailabilityReason.InvalidSteamConfiguration
                    : WallpaperEngineAvailabilityReason.SteamNotFound);
        }

        var candidates = new List<InstallationCandidate>();
        var sawManifest = false;
        var sawMissingExecutable = false;
        var sawUntrustedExecutable = false;
        foreach (var library in libraries.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(
                library,
                "steamapps",
                WallpaperEngineManifestName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            sawManifest = true;
            InstallationCandidate? candidate;
            try
            {
                candidate = await ReadInstallationCandidateAsync(
                        manifestPath,
                        library,
                        steamRoots,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                sawMissingExecutable = true;
                continue;
            }
            catch (Exception exception) when (IsInvalidConfigurationException(exception))
            {
                invalidConfiguration = true;
                continue;
            }

            if (!_trustVerifier.IsTrusted(candidate.ControlExecutablePath))
            {
                sawUntrustedExecutable = true;
                continue;
            }

            candidates.Add(candidate);
        }

        candidates = candidates
            .DistinctBy(candidate => candidate.InstallRootPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count > 1)
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.MultipleInstallations);
        }

        if (candidates.Count == 0)
        {
            var reason = invalidConfiguration
                ? WallpaperEngineAvailabilityReason.InvalidSteamConfiguration
                : sawUntrustedExecutable
                    ? WallpaperEngineAvailabilityReason.ExecutableUntrusted
                    : sawMissingExecutable
                        ? WallpaperEngineAvailabilityReason.ExecutableMissing
                        : sawManifest
                            ? WallpaperEngineAvailabilityReason.InvalidSteamConfiguration
                            : WallpaperEngineAvailabilityReason.NotInstalled;
            throw new WallpaperEngineUnavailableException(reason);
        }

        return CreateInstallation(candidates[0], libraries);
    }

    private static WallpaperEngineInstallation CreateInstallation(
        InstallationCandidate candidate,
        IEnumerable<string> libraries) =>
        new(
            candidate.SteamRootPath,
            candidate.InstallRootPath,
            candidate.ControlExecutablePath,
            libraries);

    private async ValueTask<WallpaperEngineInstallation> LocatePreferredInstallationAsync(
        string preferredLibraryPath,
        IReadOnlyList<string> steamRoots,
        IEnumerable<string> libraries,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(
            preferredLibraryPath,
            "steamapps",
            WallpaperEngineManifestName);
        if (!File.Exists(manifestPath))
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid);
        }

        InstallationCandidate candidate;
        try
        {
            candidate = await ReadInstallationCandidateAsync(
                    manifestPath,
                    preferredLibraryPath,
                    steamRoots,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.ExecutableMissing,
                exception);
        }
        catch (Exception exception) when (IsInvalidConfigurationException(exception))
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid,
                exception);
        }

        if (!string.Equals(
                candidate.InstallRootPath,
                _preferredInstallRootPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid);
        }

        if (!_trustVerifier.IsTrusted(candidate.ControlExecutablePath))
        {
            throw new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.ExecutableUntrusted);
        }

        return CreateInstallation(candidate, libraries);
    }

    private static async ValueTask<InstallationCandidate> ReadInstallationCandidateAsync(
        string manifestPath,
        string libraryPath,
        IReadOnlyList<string> steamRoots,
        CancellationToken cancellationToken)
    {
        var steamAppsPath = Path.Combine(libraryPath, "steamapps");
        var manifest = await ValveKeyValuesParser
            .ParseFileAsync(manifestPath, steamAppsPath, cancellationToken)
            .ConfigureAwait(false);
        var appState = manifest.GetRequiredObject("AppState");
        if (!string.Equals(
                appState.GetRequiredString("appid"),
                WallpaperEngineInstallation.SteamApplicationId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Steam application manifest has an unexpected AppID.");
        }

        var installDirectoryName = appState.GetRequiredString("installdir");
        if (!WallpaperEngineLocalPath.IsSafeSinglePathSegment(installDirectoryName))
        {
            throw new InvalidDataException("The Steam installation directory is invalid.");
        }

        var commonPath = WallpaperEngineLocalPath.CombineContained(
            steamAppsPath,
            "common");
        var installRootPath = WallpaperEngineLocalPath.ValidateExistingDirectory(
            WallpaperEngineLocalPath.CombineContained(commonPath, installDirectoryName));
        var executablePath = WallpaperEngineLocalPath.CombineContained(
            installRootPath,
            WallpaperEngineExecutableName);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("The Wallpaper Engine control executable is missing.");
        }

        executablePath = WallpaperEngineLocalPath.ValidateExistingRegularFile(
            executablePath,
            installRootPath);
        var steamRootPath = steamRoots.FirstOrDefault(root =>
            WallpaperEngineLocalPath.IsContainedBy(root, libraryPath)) ?? libraryPath;
        return new InstallationCandidate(steamRootPath, installRootPath, executablePath);
    }

    private static IEnumerable<string> ReadLibraryPaths(ValveKeyValuesObject document)
    {
        var libraryFolders = document.GetRequiredObject("libraryfolders");
        foreach (var pair in libraryFolders.Values)
        {
            if (!uint.TryParse(
                    pair.Key,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                continue;
            }

            var path = pair.Value switch
            {
                ValveKeyValuesString stringValue => stringValue.Value,
                ValveKeyValuesObject objectValue => objectValue.GetRequiredString("path"),
                _ => throw new InvalidDataException("A Steam library entry is invalid."),
            };
            yield return WallpaperEngineLocalPath.NormalizeAbsolutePath(path);
        }
    }

    private static string InferSteamLibraryFromInstallRoot(string installRootPath)
    {
        var validatedInstallRoot = WallpaperEngineLocalPath.ValidateExistingDirectory(
            installRootPath);
        var common = Directory.GetParent(validatedInstallRoot);
        var steamApps = common?.Parent;
        var library = steamApps?.Parent;
        if (common is null || steamApps is null || library is null ||
            !string.Equals(common.Name, "common", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(steamApps.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The selected directory is not a Steam application installation.");
        }

        return WallpaperEngineLocalPath.ValidateExistingDirectory(library.FullName);
    }

    private static bool IsInvalidConfigurationException(Exception exception) =>
        exception is ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException or SecurityException or MediaValidationException;

    private sealed record InstallationCandidate(
        string SteamRootPath,
        string InstallRootPath,
        string ControlExecutablePath);
}

internal static class WindowsSteamRootDiscovery
{
    public static WindowsSteamRootDiscoveryResult Discover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsSteamRootDiscoveryResult([], HasInvalidCandidate: false);
        }

        return Discover(new WindowsSteamRegistryValueSource());
    }

    internal static WindowsSteamRootDiscoveryResult Discover(
        IWindowsSteamRegistryValueSource registryValueSource)
    {
        ArgumentNullException.ThrowIfNull(registryValueSource);

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasInvalidCandidate = ReadCandidate(
            registryValueSource,
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"Software\Valve\Steam",
            "SteamPath",
            candidates);
        hasInvalidCandidate |= ReadCandidate(
            registryValueSource,
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            @"Software\Valve\Steam",
            "InstallPath",
            candidates);
        hasInvalidCandidate |= ReadCandidate(
            registryValueSource,
            RegistryHive.LocalMachine,
            RegistryView.Registry32,
            @"Software\Valve\Steam",
            "InstallPath",
            candidates);
        return new WindowsSteamRootDiscoveryResult(
            new ReadOnlyCollection<string>(candidates.ToArray()),
            hasInvalidCandidate);
    }

    private static bool ReadCandidate(
        IWindowsSteamRegistryValueSource registryValueSource,
        RegistryHive hive,
        RegistryView view,
        string subKeyName,
        string valueName,
        HashSet<string> candidates)
    {
        string? value;
        try
        {
            value = registryValueSource.ReadValue(hive, view, subKeyName, valueName);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or SecurityException or
                UnauthorizedAccessException)
        {
            // Another registry view or the explicit-selection path can still locate Steam.
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            candidates.Add(WallpaperEngineLocalPath.NormalizeAbsolutePath(value));
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }
}

internal sealed record WindowsSteamRootDiscoveryResult(
    IReadOnlyList<string> Candidates,
    bool HasInvalidCandidate);

internal interface IWindowsSteamRegistryValueSource
{
    string? ReadValue(
        RegistryHive hive,
        RegistryView view,
        string subKeyName,
        string valueName);
}

internal sealed class WindowsSteamRegistryValueSource : IWindowsSteamRegistryValueSource
{
    public string? ReadValue(
        RegistryHive hive,
        RegistryView view,
        string subKeyName,
        string valueName)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var key = baseKey.OpenSubKey(subKeyName, writable: false);
        return key?.GetValue(valueName) as string;
    }
}

internal sealed class WindowsAuthenticodeTrustVerifier : IWallpaperEngineExecutableTrustVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public bool IsTrusted(string executablePath)
    {
        ArgumentNullException.ThrowIfNull(executablePath);
        if (!OperatingSystem.IsWindows() || !File.Exists(executablePath))
        {
            return false;
        }

        var fileInfo = new WinTrustFileInfo
        {
            StructureSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>()),
            FilePath = executablePath,
            FileHandle = nint.Zero,
            KnownSubject = nint.Zero,
        };
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);
            var trustData = new WinTrustData
            {
                StructureSize = checked((uint)Marshal.SizeOf<WinTrustData>()),
                PolicyCallbackData = nint.Zero,
                SipClientData = nint.Zero,
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                StateData = nint.Zero,
                UrlReference = nint.Zero,
                ProviderFlags = 0x00000100,
                UiContext = 0,
                SignatureSettings = nint.Zero,
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(nint.Zero, ref action, ref trustData) == 0;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or
                BadImageFormatException)
        {
            return false;
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        nint windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructureSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        public nint FileHandle;

        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructureSize;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint FileInfo;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }
}

internal static class WallpaperEngineLocalPath
{
    public static string NormalizeAbsolutePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Only fully qualified local paths are supported.");
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("The local path is invalid.", exception);
        }
    }

    public static string ValidateExistingDirectory(string path)
    {
        var fullPath = NormalizeAbsolutePath(path);
        WindowsLocalFileIdentity.EnsureInputPathTargetsLocalVolume(fullPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("A required local directory is missing.");
        }

        RejectReparsePoints(fullPath, includeLeaf: true);
        return fullPath;
    }

    public static string ValidateExistingRegularFile(string path, string containmentRoot)
    {
        var fullPath = NormalizeAbsolutePath(path);
        var validatedRoot = ValidateExistingDirectory(containmentRoot);
        if (!IsContainedBy(validatedRoot, fullPath))
        {
            throw new InvalidDataException("A file path escaped its authorized root.");
        }

        RejectReparsePoints(fullPath, includeLeaf: true);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.SequentialScan);
        var resolvedPath = WindowsLocalFileIdentity.ResolveFinalPath(stream.SafeFileHandle);
        _ = WindowsLocalFileIdentity.Read(stream.SafeFileHandle, resolvedPath);
        if (!IsContainedBy(validatedRoot, resolvedPath))
        {
            throw new InvalidDataException("A file target escaped its authorized root.");
        }

        return resolvedPath;
    }

    public static string CombineContained(string rootPath, string relativePath)
    {
        var root = NormalizeAbsolutePath(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException("A project-relative path must not be absolute.");
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("A project-relative path is invalid.", exception);
        }

        if (!IsContainedBy(root, candidate))
        {
            throw new InvalidDataException("A project-relative path escaped its authorized root.");
        }

        return candidate;
    }

    public static bool IsContainedBy(string rootPath, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(NormalizeAbsolutePath(rootPath));
        var candidate = NormalizeAbsolutePath(candidatePath);
        return string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeSinglePathSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        !Path.IsPathFullyQualified(value) &&
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public static FileStream OpenPinnedReadOnlyFile(string path, string containmentRoot)
    {
        var fullPath = NormalizeAbsolutePath(path);
        var validatedRoot = ValidateExistingDirectory(containmentRoot);
        if (!IsContainedBy(validatedRoot, fullPath))
        {
            throw new InvalidDataException("A file path escaped its authorized root.");
        }

        RejectReparsePoints(fullPath, includeLeaf: true);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var resolvedPath = WindowsLocalFileIdentity.ResolveFinalPath(stream.SafeFileHandle);
            _ = WindowsLocalFileIdentity.Read(stream.SafeFileHandle, resolvedPath);
            if (!IsContainedBy(validatedRoot, resolvedPath))
            {
                throw new InvalidDataException("A file target escaped its authorized root.");
            }

            var result = stream;
            stream = null;
            return result;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static void RejectReparsePoints(string path, bool includeLeaf)
    {
        var fullPath = NormalizeAbsolutePath(path);
        var root = Path.GetPathRoot(fullPath) ??
            throw new InvalidDataException("The local path has no volume root.");
        var relative = Path.GetRelativePath(root, fullPath);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var limit = includeLeaf ? segments.Length : Math.Max(0, segments.Length - 1);
        for (var index = 0; index < limit; index++)
        {
            current = Path.Combine(current, segments[index]);
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Reparse-point paths are not supported.");
            }
        }
    }
}

internal abstract record ValveKeyValuesValue;

internal sealed record ValveKeyValuesString(string Value) : ValveKeyValuesValue;

internal sealed record ValveKeyValuesObject : ValveKeyValuesValue
{
    public ValveKeyValuesObject(IDictionary<string, ValveKeyValuesValue> values)
    {
        Values = new ReadOnlyDictionary<string, ValveKeyValuesValue>(
            new Dictionary<string, ValveKeyValuesValue>(
                values,
                StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyDictionary<string, ValveKeyValuesValue> Values { get; }

    public ValveKeyValuesObject GetRequiredObject(string key) =>
        Values.TryGetValue(key, out var value) && value is ValveKeyValuesObject objectValue
            ? objectValue
            : throw new InvalidDataException("A required Valve KeyValues object is missing.");

    public string GetRequiredString(string key) =>
        Values.TryGetValue(key, out var value) && value is ValveKeyValuesString stringValue
            ? stringValue.Value
            : throw new InvalidDataException("A required Valve KeyValues string is missing.");
}

internal static class ValveKeyValuesParser
{
    public const int MaximumDocumentLength = 1024 * 1024;
    public const int MaximumDepth = 16;
    public const int MaximumEntryCount = 8192;
    public const int MaximumStringLength = 32767;

    public static async ValueTask<ValveKeyValuesObject> ParseFileAsync(
        string path,
        string containmentRoot,
        CancellationToken cancellationToken)
    {
        await using var stream = WallpaperEngineLocalPath.OpenPinnedReadOnlyFile(
            path,
            containmentRoot);
        if (stream.Length > MaximumDocumentLength)
        {
            throw new InvalidDataException("The Valve KeyValues document is too large.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The Valve KeyValues document is not valid UTF-8.", exception);
        }

        return Parse(text);
    }

    private static ValveKeyValuesObject Parse(string text)
    {
        var parser = new Parser(text);
        return parser.ParseDocument();
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _text;
        private int _position;
        private int _entryCount;

        public Parser(string text)
        {
            _text = text.AsSpan();
        }

        public ValveKeyValuesObject ParseDocument()
        {
            if (_text.Length > 0 && _text[0] == '\uFEFF')
            {
                _position++;
            }

            var result = ParseObject(depth: 0, requireClosingBrace: false);
            SkipTrivia();
            if (_position != _text.Length)
            {
                throw new InvalidDataException("The Valve KeyValues document has trailing data.");
            }

            return result;
        }

        private ValveKeyValuesObject ParseObject(int depth, bool requireClosingBrace)
        {
            if (depth > MaximumDepth)
            {
                throw new InvalidDataException("The Valve KeyValues document is too deeply nested.");
            }

            var values = new Dictionary<string, ValveKeyValuesValue>(
                StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                SkipTrivia();
                if (_position == _text.Length)
                {
                    if (requireClosingBrace)
                    {
                        throw new InvalidDataException("The Valve KeyValues object is not closed.");
                    }

                    break;
                }

                if (_text[_position] == '}')
                {
                    if (!requireClosingBrace)
                    {
                        throw new InvalidDataException("The Valve KeyValues document has an extra brace.");
                    }

                    _position++;
                    break;
                }

                var key = ReadString();
                SkipTrivia();
                ValveKeyValuesValue value;
                if (_position < _text.Length && _text[_position] == '{')
                {
                    _position++;
                    value = ParseObject(depth + 1, requireClosingBrace: true);
                }
                else
                {
                    value = new ValveKeyValuesString(ReadString());
                }

                _entryCount++;
                if (_entryCount > MaximumEntryCount)
                {
                    throw new InvalidDataException("The Valve KeyValues document has too many entries.");
                }

                if (!values.TryAdd(key, value))
                {
                    throw new InvalidDataException("The Valve KeyValues document has duplicate keys.");
                }
            }

            return new ValveKeyValuesObject(values);
        }

        private string ReadString()
        {
            SkipTrivia();
            if (_position >= _text.Length || _text[_position] != '"')
            {
                throw new InvalidDataException("A quoted Valve KeyValues string was expected.");
            }

            _position++;
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                var current = _text[_position++];
                if (current == '"')
                {
                    return builder.ToString();
                }

                if (current == '\\')
                {
                    if (_position >= _text.Length)
                    {
                        throw new InvalidDataException("A Valve KeyValues escape is incomplete.");
                    }

                    current = _text[_position++] switch
                    {
                        '\\' => '\\',
                        '"' => '"',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => throw new InvalidDataException(
                            "The Valve KeyValues document contains an invalid escape."),
                    };
                }

                if (char.IsControl(current) && current is not ('\t' or '\r' or '\n'))
                {
                    throw new InvalidDataException(
                        "The Valve KeyValues document contains a control character.");
                }

                builder.Append(current);
                if (builder.Length > MaximumStringLength)
                {
                    throw new InvalidDataException("A Valve KeyValues string is too long.");
                }
            }

            throw new InvalidDataException("A Valve KeyValues string is not closed.");
        }

        private void SkipTrivia()
        {
            while (_position < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_position]))
                {
                    _position++;
                    continue;
                }

                if (_position + 1 < _text.Length &&
                    _text[_position] == '/' &&
                    _text[_position + 1] == '/')
                {
                    _position += 2;
                    while (_position < _text.Length && _text[_position] is not ('\r' or '\n'))
                    {
                        _position++;
                    }

                    continue;
                }

                break;
            }
        }
    }
}
