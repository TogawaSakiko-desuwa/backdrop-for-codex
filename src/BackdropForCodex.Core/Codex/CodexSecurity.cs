using System.Collections.Frozen;
using System.Globalization;

namespace BackdropForCodex.Core.Codex;

/// <summary>
/// The terminal or transient state of one security-validation attempt.
/// </summary>
public enum CodexSecurityStatus
{
    NotEvaluated = 0,
    InProgress,
    Verified,
    Rejected,
}

/// <summary>
/// Identifies the last security boundary reached by a validation attempt.
/// </summary>
public enum CodexSecurityStage
{
    None = 0,
    RuntimeEnvironment,
    PackageIdentity,
    ApplicationIdentity,
    ProcessIdentity,
    LoopbackEndpoint,
    BrowserHandshake,
    TargetValidation,
}

/// <summary>
/// Stable, non-sensitive reason codes for fail-closed security decisions.
/// </summary>
public enum CodexSecurityFailureCode
{
    None = 0,
    WrongOperatingSystem,
    UnsupportedOperatingSystemVersion,
    UnsupportedRuntimeArchitecture,
    UnofficialPackageIdentity,
    UnsupportedPackageArchitecture,
    UnexpectedPackageFullName,
    UnexpectedApplicationId,
    PackageDiscoveryFailed,
    NoVerifiedProcess,
    NonLoopbackEndpoint,
    ProcessIdentityMismatch,
    EndpointUnreachable,
    EndpointDiscoveryTimedOut,
    AmbiguousEndpoint,
    MalformedCdpResponse,
    UnexpectedBrowser,
    BrowserSocketMismatch,
    ValidationCanceled,
    NoCodexTarget,
    TargetSocketMismatch,
    NoVerifiedTarget,
    AmbiguousTarget,
    TargetRevalidationFailed,
}

/// <summary>
/// A security-only result. Presentation contracts and capabilities are deliberately evaluated
/// after this result and never participate in its decision.
/// </summary>
public sealed record CodexSecurityResult
{
    private CodexSecurityResult(
        CodexSecurityStatus status,
        CodexSecurityStage stage,
        CodexSecurityFailureCode failureCode,
        string reason,
        VerifiedCodexIdentity? identity)
    {
        if (status == CodexSecurityStatus.NotEvaluated)
        {
            if (stage != CodexSecurityStage.None ||
                failureCode != CodexSecurityFailureCode.None ||
                identity is not null)
            {
                throw new ArgumentException(
                    "A not-evaluated result cannot contain validation state.");
            }
        }
        else if (stage == CodexSecurityStage.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stage),
                "An evaluated security result must identify its validation stage.");
        }

        if (status == CodexSecurityStatus.Rejected)
        {
            if (failureCode == CodexSecurityFailureCode.None)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(failureCode),
                    "A rejected security result must include a failure code.");
            }
        }
        else if (failureCode != CodexSecurityFailureCode.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureCode),
                "Only a rejected security result may include a failure code.");
        }

        if (status == CodexSecurityStatus.Verified && identity is null)
        {
            throw new ArgumentNullException(
                nameof(identity),
                "A verified security result must include the verified Codex identity.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = status;
        Stage = stage;
        FailureCode = failureCode;
        Reason = reason;
        Identity = identity;
    }

    public CodexSecurityStatus Status { get; }

    public CodexSecurityStage Stage { get; }

    public CodexSecurityFailureCode FailureCode { get; }

    /// <summary>
    /// A local user-facing explanation. Diagnostics must export the typed fields above instead of
    /// this free-form text.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// The package identity that passed package validation, including on later-stage failures.
    /// </summary>
    public VerifiedCodexIdentity? Identity { get; }

    public bool IsVerified =>
        Status == CodexSecurityStatus.Verified &&
        FailureCode == CodexSecurityFailureCode.None &&
        Identity is not null;

    public static CodexSecurityResult NotEvaluated() => new(
        CodexSecurityStatus.NotEvaluated,
        CodexSecurityStage.None,
        CodexSecurityFailureCode.None,
        "Codex security validation has not started.",
        identity: null);

    public static CodexSecurityResult InProgress(
        CodexSecurityStage stage,
        string reason,
        VerifiedCodexIdentity? identity = null) => new(
        CodexSecurityStatus.InProgress,
        stage,
        CodexSecurityFailureCode.None,
        reason,
        identity);

    public static CodexSecurityResult Verified(
        VerifiedCodexIdentity identity,
        CodexSecurityStage stage = CodexSecurityStage.PackageIdentity,
        string reason = "The official Codex identity passed the requested security checks.") => new(
        CodexSecurityStatus.Verified,
        stage,
        CodexSecurityFailureCode.None,
        reason,
        identity ?? throw new ArgumentNullException(nameof(identity)));

    public static CodexSecurityResult Rejected(
        CodexSecurityStage stage,
        CodexSecurityFailureCode failureCode,
        string reason,
        VerifiedCodexIdentity? identity = null) => new(
        CodexSecurityStatus.Rejected,
        stage,
        failureCode,
        reason,
        identity);
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
/// The immutable official package identity used by activation, process ownership checks and CDP
/// target validation. It contains no presentation profile, probe package or capability state.
/// </summary>
public sealed class VerifiedCodexIdentity
{
    internal VerifiedCodexIdentity(
        string packageName,
        string packageFamilyName,
        string packageFullName,
        string? packageRoot,
        Version packageVersion,
        string applicationId,
        IEnumerable<string> executableNames,
        IEnumerable<string> pageTitleMarkers,
        IEnumerable<string> allowedRemotePageHosts)
    {
        PackageName = RequireValue(packageName, nameof(packageName));
        PackageFamilyName = RequireValue(packageFamilyName, nameof(packageFamilyName));
        PackageFullName = RequireValue(packageFullName, nameof(packageFullName));
        PackageRoot = packageRoot;
        PackageVersion = packageVersion ?? throw new ArgumentNullException(nameof(packageVersion));
        ApplicationId = RequireValue(applicationId, nameof(applicationId));
        ExecutableNames = ToReadOnlySet(executableNames);
        PageTitleMarkers = ToReadOnlySet(pageTitleMarkers);
        AllowedRemotePageHosts = ToReadOnlySet(allowedRemotePageHosts);
    }

    public string PackageName { get; }

    public string PackageFamilyName { get; }

    public string PackageFullName { get; }

    public string? PackageRoot { get; }

    /// <summary>
    /// The observed package version. It is retained for package-full-name consistency checks and
    /// diagnostics only; it must not select or rank presentation behavior.
    /// </summary>
    public Version PackageVersion { get; }

    public string ApplicationId { get; }

    public IReadOnlySet<string> ExecutableNames { get; }

    public IReadOnlySet<string> PageTitleMarkers { get; }

    public IReadOnlySet<string> AllowedRemotePageHosts { get; }

    public string AppUserModelId => string.Create(
        CultureInfo.InvariantCulture,
        $"{PackageFamilyName}!{ApplicationId}");

    public bool IsKnownExecutable(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        return ExecutableNames.Contains(Path.GetFileName(executableName));
    }

    internal bool IsKnownTitle(string title) =>
        PageTitleMarkers.Any(marker =>
            title.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static FrozenSet<string> ToReadOnlySet(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string RequireValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

/// <summary>
/// Fail-closed validator for the official Windows 11 x64 Store/MSIX identity.
/// </summary>
public static class CodexSecurityValidator
{
    public const string OfficialPackageName = "OpenAI.Codex";
    public const string OfficialPackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";
    public const string OfficialApplicationId = "App";

    public static readonly Version MinimumWindowsVersion = new(10, 0, 22000, 0);

    public static CodexSecurityResult Validate(
        CodexPackageDescriptor package,
        CodexRuntimeDescriptor runtime)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(runtime);

        if (!runtime.IsWindows)
        {
            return RejectRuntime(
                CodexSecurityFailureCode.WrongOperatingSystem,
                "Backdrop for Codex is supported only on Windows.");
        }

        if (runtime.OperatingSystemVersion < MinimumWindowsVersion)
        {
            return RejectRuntime(
                CodexSecurityFailureCode.UnsupportedOperatingSystemVersion,
                $"Windows 11 build {MinimumWindowsVersion} or newer is required.");
        }

        if (runtime.Architecture != CodexPackageArchitecture.X64)
        {
            return RejectRuntime(
                CodexSecurityFailureCode.UnsupportedRuntimeArchitecture,
                "Only the Windows x64 runtime is supported.");
        }

        if (!string.Equals(package.Name, OfficialPackageName, StringComparison.Ordinal) ||
            !string.Equals(
                package.FamilyName,
                OfficialPackageFamilyName,
                StringComparison.Ordinal))
        {
            return CodexSecurityResult.Rejected(
                CodexSecurityStage.PackageIdentity,
                CodexSecurityFailureCode.UnofficialPackageIdentity,
                "The package identity is not the reviewed official OpenAI Codex identity.");
        }

        if (package.Architecture != CodexPackageArchitecture.X64)
        {
            return CodexSecurityResult.Rejected(
                CodexSecurityStage.PackageIdentity,
                CodexSecurityFailureCode.UnsupportedPackageArchitecture,
                "Only the x64 Codex MSIX package is supported.");
        }

        var expectedPackageFullName = BuildExpectedPackageFullName(package.Version);
        if (package.PackageFullName is null ||
            !string.Equals(
                package.PackageFullName,
                expectedPackageFullName,
                StringComparison.Ordinal))
        {
            return CodexSecurityResult.Rejected(
                CodexSecurityStage.PackageIdentity,
                CodexSecurityFailureCode.UnexpectedPackageFullName,
                "The installed package full name does not match its verified identity fields.");
        }

        if (!string.Equals(
                package.ApplicationId,
                OfficialApplicationId,
                StringComparison.Ordinal))
        {
            return CodexSecurityResult.Rejected(
                CodexSecurityStage.ApplicationIdentity,
                CodexSecurityFailureCode.UnexpectedApplicationId,
                "The MSIX application id is not the reviewed Codex application id.");
        }

        var identity = new VerifiedCodexIdentity(
            package.Name,
            package.FamilyName,
            package.PackageFullName,
            package.PackageRoot,
            package.Version,
            package.ApplicationId,
            ["ChatGPT.exe"],
            ["Codex"],
            ["chatgpt.com", "codex.openai.com"]);

        return CodexSecurityResult.Verified(
            identity,
            CodexSecurityStage.ApplicationIdentity,
            "The installed package and runtime passed all security identity checks.");
    }

    internal static string BuildExpectedPackageFullName(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return $"{OfficialPackageName}_{version}_x64__2p2nqsd0c76g0";
    }

    private static CodexSecurityResult RejectRuntime(
        CodexSecurityFailureCode failureCode,
        string reason) => CodexSecurityResult.Rejected(
        CodexSecurityStage.RuntimeEnvironment,
        failureCode,
        reason);
}
