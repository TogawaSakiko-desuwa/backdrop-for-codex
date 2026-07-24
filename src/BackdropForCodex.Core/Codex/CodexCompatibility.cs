using System.Collections.Frozen;

namespace BackdropForCodex.Core.Codex;

public enum CodexCompatibilityFailure
{
    None = 0,
    WrongOperatingSystem,
    UnsupportedOperatingSystemVersion,
    UnsupportedRuntimeArchitecture,
    UnofficialPackageIdentity,
    UnsupportedPackageArchitecture,
    UnsupportedPackageVersion,
    UnexpectedApplicationId,
}

public sealed record CodexRuntimeDescriptor(
    bool IsWindows,
    Version OperatingSystemVersion,
    CodexPackageArchitecture Architecture)
{
    public static CodexRuntimeDescriptor Current
    {
        get
        {
            var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => CodexPackageArchitecture.X64,
                System.Runtime.InteropServices.Architecture.X86 => CodexPackageArchitecture.X86,
                System.Runtime.InteropServices.Architecture.Arm64 => CodexPackageArchitecture.Arm64,
                _ => CodexPackageArchitecture.Unknown,
            };

            return new CodexRuntimeDescriptor(
                OperatingSystem.IsWindows(),
                Environment.OSVersion.Version,
                architecture);
        }
    }
}

/// <summary>
/// Exact, reviewed knowledge about a Codex build. Profiles are intentionally immutable and
/// only produced by <see cref="CodexCompatibilityCatalog"/>.
/// </summary>
public sealed class CodexCompatibilityProfile
{
    internal CodexCompatibilityProfile(
        string id,
        string packageName,
        string packageFamilyName,
        string packageFullName,
        Version packageVersion,
        string applicationId,
        IEnumerable<string> executableNames,
        IEnumerable<string> pageTitleMarkers,
        IEnumerable<string> allowedRemotePageHosts)
    {
        Id = id;
        PackageName = packageName;
        PackageFamilyName = packageFamilyName;
        PackageFullName = packageFullName;
        PackageVersion = packageVersion;
        ApplicationId = applicationId;
        ExecutableNames = ToReadOnlySet(executableNames);
        PageTitleMarkers = ToReadOnlySet(pageTitleMarkers);
        AllowedRemotePageHosts = ToReadOnlySet(allowedRemotePageHosts);
    }

    public string Id { get; }

    public string PackageName { get; }

    public string PackageFamilyName { get; }

    public string PackageFullName { get; }

    public Version PackageVersion { get; }

    public string ApplicationId { get; }

    public IReadOnlySet<string> ExecutableNames { get; }

    public IReadOnlySet<string> PageTitleMarkers { get; }

    public IReadOnlySet<string> AllowedRemotePageHosts { get; }

    public string AppUserModelId => $"{PackageFamilyName}!{ApplicationId}";

    public bool IsKnownExecutable(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        return ExecutableNames.Contains(Path.GetFileName(executableName));
    }

    internal bool IsKnownTitle(string title) =>
        PageTitleMarkers.Any(marker => title.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static FrozenSet<string> ToReadOnlySet(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record CodexCompatibilityResult(
    CodexCompatibilityFailure Failure,
    string Reason,
    CodexCompatibilityProfile? Profile)
{
    public bool IsSupported => Failure == CodexCompatibilityFailure.None && Profile is not null;

    public static CodexCompatibilityResult Supported(CodexCompatibilityProfile profile) =>
        new(CodexCompatibilityFailure.None, "The installed Codex package is explicitly supported.", profile);

    public static CodexCompatibilityResult Rejected(
        CodexCompatibilityFailure failure,
        string reason) => new(failure, reason, null);
}

/// <summary>
/// Fail-closed compatibility catalog for the official Windows 11 x64 package.
/// </summary>
public static class CodexCompatibilityCatalog
{
    public const string OfficialPackageName = "OpenAI.Codex";
    public const string OfficialPackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";

    /// <summary>
    /// Legacy 1.x alias for the first reviewed package full name. New code must use the profile
    /// returned by <see cref="Evaluate"/> so it remains correct when more than one version is reviewed.
    /// </summary>
    public const string SupportedPackageFullName =
        "OpenAI.Codex_26.715.10079.0_x64__2p2nqsd0c76g0";
    public const string OfficialApplicationId = "App";

    public static readonly Version MinimumWindowsVersion = new(10, 0, 22000, 0);

    /// <summary>
    /// Legacy 1.x alias for the first reviewed package version. New code must use the profile
    /// returned by <see cref="Evaluate"/> so it remains correct when more than one version is reviewed.
    /// </summary>
    public static readonly Version SupportedPackageVersion = new(26, 715, 10079, 0);

    private static readonly CodexCompatibilityProfile LegacySupportedProfile = new(
        "openai-codex-26.715.10079.0-windows11-x64-v1",
        OfficialPackageName,
        OfficialPackageFamilyName,
        SupportedPackageFullName,
        SupportedPackageVersion,
        OfficialApplicationId,
        ["ChatGPT.exe"],
        ["Codex"],
        ["chatgpt.com", "codex.openai.com"]);

    private static readonly CodexCompatibilityProfile CurrentSupportedProfile = new(
        "openai-codex-26.721.3404.0-windows11-x64-v1",
        OfficialPackageName,
        OfficialPackageFamilyName,
        "OpenAI.Codex_26.721.3404.0_x64__2p2nqsd0c76g0",
        new Version(26, 721, 3404, 0),
        OfficialApplicationId,
        ["ChatGPT.exe"],
        ["Codex"],
        ["chatgpt.com", "codex.openai.com"]);

    private static readonly FrozenDictionary<Version, CodexCompatibilityProfile>
        SupportedProfilesByVersion = new[]
        {
            LegacySupportedProfile,
            CurrentSupportedProfile,
        }.ToFrozenDictionary(profile => profile.PackageVersion);

    public static CodexCompatibilityResult Evaluate(
        CodexPackageDescriptor package,
        CodexRuntimeDescriptor runtime)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(runtime);

        if (!runtime.IsWindows)
        {
            return CodexCompatibilityResult.Rejected(
                CodexCompatibilityFailure.WrongOperatingSystem,
                "Backdrop for Codex is supported only on Windows.");
        }

        if (runtime.OperatingSystemVersion < MinimumWindowsVersion)
        {
            return CodexCompatibilityResult.Rejected(
                CodexCompatibilityFailure.UnsupportedOperatingSystemVersion,
                $"Windows 11 build {MinimumWindowsVersion} or newer is required.");
        }

        if (runtime.Architecture != CodexPackageArchitecture.X64)
        {
            return CodexCompatibilityResult.Rejected(
                CodexCompatibilityFailure.UnsupportedRuntimeArchitecture,
                "Only the Windows x64 runtime is supported.");
        }

        if (!string.Equals(package.Name, OfficialPackageName, StringComparison.Ordinal) ||
            !string.Equals(package.FamilyName, OfficialPackageFamilyName, StringComparison.Ordinal))
        {
            return CodexCompatibilityResult.Rejected(
                CodexCompatibilityFailure.UnofficialPackageIdentity,
                "The package identity is not the reviewed official OpenAI Codex identity.");
        }

        if (package.Architecture != CodexPackageArchitecture.X64)
        {
            return CodexCompatibilityResult.Rejected(
                CodexCompatibilityFailure.UnsupportedPackageArchitecture,
                "Only the x64 Codex MSIX package is supported.");
        }

        if (!SupportedProfilesByVersion.TryGetValue(package.Version, out var profile))
        {
            return CodexCompatibilityResult.Rejected(
                CodexCompatibilityFailure.UnsupportedPackageVersion,
                $"Codex {package.Version} has no reviewed compatibility profile.");
        }

        if (!string.Equals(package.ApplicationId, OfficialApplicationId, StringComparison.Ordinal))
        {
            return CodexCompatibilityResult.Rejected(
                CodexCompatibilityFailure.UnexpectedApplicationId,
                "The MSIX application id is not the reviewed Codex application id.");
        }

        return CodexCompatibilityResult.Supported(profile);
    }
}
