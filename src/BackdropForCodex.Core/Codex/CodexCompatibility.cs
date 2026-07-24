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
    UnexpectedPackageFullName,
    UnsupportedPackageVersion,
    UnexpectedApplicationId,
}

public sealed record SecurityVerdict
{
    public SecurityVerdict(CodexCompatibilityFailure failure, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Failure = failure;
        Reason = reason;
    }

    public CodexCompatibilityFailure Failure { get; }

    public string Reason { get; }

    public bool IsAllowed => Failure == CodexCompatibilityFailure.None;

    internal static SecurityVerdict Allowed() => new(
        CodexCompatibilityFailure.None,
        "The installed package and runtime passed all security identity checks.");

    internal static SecurityVerdict Rejected(
        CodexCompatibilityFailure failure,
        string reason)
    {
        if (failure == CodexCompatibilityFailure.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failure),
                "A rejected security verdict must have a failure code.");
        }

        return new SecurityVerdict(failure, reason);
    }
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
/// Immutable compatibility knowledge selected for an exact build, an explicitly reviewed build
/// band, or the conservative generic fallback. Profiles are produced only by
/// <see cref="CodexCompatibilityCatalog"/>.
/// </summary>
public sealed class CodexCompatibilityProfile
{
    internal CodexCompatibilityProfile(
        string id,
        string packageName,
        string packageFamilyName,
        string packageFullName,
        string? packageRoot,
        Version packageVersion,
        string applicationId,
        IEnumerable<string> executableNames,
        IEnumerable<string> pageTitleMarkers,
        IEnumerable<string> allowedRemotePageHosts,
        CompatibilityProbePackageKind probePackageKind,
        CompatibilityCapabilities capabilities)
    {
        Id = id;
        PackageName = packageName;
        PackageFamilyName = packageFamilyName;
        PackageFullName = packageFullName;
        PackageRoot = packageRoot;
        PackageVersion = packageVersion;
        ApplicationId = applicationId;
        ExecutableNames = ToReadOnlySet(executableNames);
        PageTitleMarkers = ToReadOnlySet(pageTitleMarkers);
        AllowedRemotePageHosts = ToReadOnlySet(allowedRemotePageHosts);
        ProbePackageKind = probePackageKind;
        ProbePackageId = probePackageKind switch
        {
            CompatibilityProbePackageKind.Exact or
            CompatibilityProbePackageKind.ReviewedBand => $"{id}-dom-probes",
            CompatibilityProbePackageKind.Generic => "openai-codex-generic-dom-probes-v1",
            _ => throw new ArgumentOutOfRangeException(nameof(probePackageKind)),
        };
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    public string Id { get; }

    public string PackageName { get; }

    public string PackageFamilyName { get; }

    public string PackageFullName { get; }

    public string? PackageRoot { get; }

    public Version PackageVersion { get; }

    public string ApplicationId { get; }

    public IReadOnlySet<string> ExecutableNames { get; }

    public IReadOnlySet<string> PageTitleMarkers { get; }

    public IReadOnlySet<string> AllowedRemotePageHosts { get; }

    public CompatibilityProbePackageKind ProbePackageKind { get; }

    public string ProbePackageId { get; }

    public CompatibilityCapabilities Capabilities { get; }

    public bool UsesExactProbePackage => ProbePackageKind == CompatibilityProbePackageKind.Exact;

    public bool UsesReviewedBandProbePackage =>
        ProbePackageKind == CompatibilityProbePackageKind.ReviewedBand;

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

public sealed record CodexCompatibilityResult
{
    public CodexCompatibilityResult(
        SecurityVerdict security,
        CompatibilityCapabilities capabilities,
        CodexCompatibilityProfile? profile)
    {
        Security = security ?? throw new ArgumentNullException(nameof(security));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        Profile = profile;
    }

    public SecurityVerdict Security { get; }

    public CompatibilityCapabilities Capabilities { get; }

    public CodexCompatibilityProfile? Profile { get; }

    // Compatibility aliases for 1.x callers.
    public CodexCompatibilityFailure Failure => Security.Failure;

    public string Reason => Security.Reason;

    public bool IsSupported =>
        Security.IsAllowed &&
        Profile is not null &&
        Capabilities.CanInjectGlobalWallpaper;

    public static CodexCompatibilityResult Supported(CodexCompatibilityProfile profile) =>
        new(SecurityVerdict.Allowed(), profile.Capabilities, profile);

    public static CodexCompatibilityResult Rejected(
        CodexCompatibilityFailure failure,
        string reason) => new(
            SecurityVerdict.Rejected(failure, reason),
            CompatibilityCapabilities.SecurityRejected(),
            null);
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

    private sealed record ExactProfileDefinition(
        string Id,
        Version PackageVersion,
        CompatibilityCapabilities Capabilities);

    private sealed record ReviewedBandProfileDefinition(
        string Id,
        Version MinimumVersionInclusive,
        Version MaximumVersionExclusive,
        CompatibilityCapabilities Capabilities)
    {
        public bool Contains(Version version) =>
            version >= MinimumVersionInclusive &&
            version < MaximumVersionExclusive;
    }

    private static readonly CompatibilityCapabilities ExactCapabilities =
        CompatibilityCapabilities.FromProbePackage(
            CompatibilityProbePackageKind.Exact,
            globalBackground: true,
            regionRecognition: false,
            glassStyle: true,
            audio: false,
            advancedSurfaces: true,
            unavailableReasonOverride:
                CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease);

    private static readonly CompatibilityCapabilities GenericCapabilities = new(
        CompatibilityCapability.Available(CompatibilityProbePackageKind.Generic),
        CompatibilityCapability.Disabled(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
        CompatibilityCapability.Disabled(
            CompatibilityCapabilityReasonCode.UnavailableForGenericProbePackage),
        CompatibilityCapability.Disabled(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
        CompatibilityCapability.Disabled(
            CompatibilityCapabilityReasonCode.UnavailableForGenericProbePackage));

    private static readonly CompatibilityCapabilities ReviewedBandCapabilities =
        CompatibilityCapabilities.FromProbePackage(
            CompatibilityProbePackageKind.ReviewedBand,
            globalBackground: true,
            regionRecognition: false,
            glassStyle: true,
            audio: false,
            advancedSurfaces: true,
            unavailableReasonOverride:
                CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease);

    private static readonly ExactProfileDefinition LegacySupportedProfile = new(
        "openai-codex-26.715.10079.0-windows11-x64-v1",
        SupportedPackageVersion,
        ExactCapabilities);

    private static readonly ExactProfileDefinition CurrentSupportedProfile = new(
        "openai-codex-26.721.3404.0-windows11-x64-v1",
        new Version(26, 721, 3404, 0),
        ExactCapabilities);

    private static readonly ExactProfileDefinition LatestReviewedProfile = new(
        "openai-codex-26.721.3996.0-windows11-x64-v1",
        new Version(26, 721, 3996, 0),
        ExactCapabilities);

    private static readonly FrozenDictionary<Version, ExactProfileDefinition>
        SupportedProfilesByVersion = new[]
        {
            LegacySupportedProfile,
            CurrentSupportedProfile,
            LatestReviewedProfile,
        }.ToFrozenDictionary(profile => profile.PackageVersion);

    private static readonly FrozenDictionary<string, ReviewedBandProfileDefinition>
        SupportedReviewedBandsById = new[]
        {
            new ReviewedBandProfileDefinition(
                "openai-codex-26.721-reviewed-band-windows11-x64-v1",
                new Version(26, 721, 3404, 0),
                new Version(26, 722, 0, 0),
                ReviewedBandCapabilities),
        }.ToFrozenDictionary(band => band.Id, StringComparer.Ordinal);

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

        if (!string.Equals(package.ApplicationId, OfficialApplicationId, StringComparison.Ordinal))
        {
            return CodexCompatibilityResult.Rejected(
                CodexCompatibilityFailure.UnexpectedApplicationId,
                "The MSIX application id is not the reviewed Codex application id.");
        }

        var expectedPackageFullName = BuildExpectedPackageFullName(package.Version);
        if (package.PackageFullName is null ||
            !string.Equals(
                package.PackageFullName,
                expectedPackageFullName,
                StringComparison.Ordinal))
        {
            return CodexCompatibilityResult.Rejected(
                CodexCompatibilityFailure.UnexpectedPackageFullName,
                "The installed package full name does not match its verified identity fields.");
        }

        var installedPackageFullName = package.PackageFullName;
        CodexCompatibilityProfile profile;
        if (SupportedProfilesByVersion.TryGetValue(
                package.Version,
                out var exactDefinition))
        {
            profile = CreateProfile(
                exactDefinition.Id,
                package,
                installedPackageFullName,
                CompatibilityProbePackageKind.Exact,
                exactDefinition.Capabilities);
        }
        else
        {
            var reviewedBand = SupportedReviewedBandsById.Values.SingleOrDefault(
                definition => definition.Contains(package.Version));
            profile = reviewedBand is not null
                ? CreateProfile(
                    reviewedBand.Id,
                    package,
                    installedPackageFullName,
                    CompatibilityProbePackageKind.ReviewedBand,
                    reviewedBand.Capabilities)
                : CreateProfile(
                "openai-codex-generic-windows11-x64-v1",
                package,
                installedPackageFullName,
                CompatibilityProbePackageKind.Generic,
                GenericCapabilities);
        }

        return CodexCompatibilityResult.Supported(profile);
    }

    private static CodexCompatibilityProfile CreateProfile(
        string id,
        CodexPackageDescriptor package,
        string installedPackageFullName,
        CompatibilityProbePackageKind probePackageKind,
        CompatibilityCapabilities capabilities) => new(
        id,
        package.Name,
        package.FamilyName,
        installedPackageFullName,
        package.PackageRoot,
        package.Version,
        package.ApplicationId,
        ["ChatGPT.exe"],
        ["Codex"],
        ["chatgpt.com", "codex.openai.com"],
        probePackageKind,
        capabilities);

    private static string BuildExpectedPackageFullName(Version version) =>
        $"{OfficialPackageName}_{version}_x64__2p2nqsd0c76g0";
}
