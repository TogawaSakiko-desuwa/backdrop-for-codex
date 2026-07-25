using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class CodexSecurityValidatorTests
{
    internal static readonly Version ReferencePackageVersion = new(26, 715, 10079, 0);

    private static readonly CodexRuntimeDescriptor Windows11X64 = new(
        true,
        new Version(10, 0, 26100, 0),
        CodexPackageArchitecture.X64);

    [Fact]
    public void Validate_AcceptsOfficialPackageIdentity()
    {
        var result = CodexSecurityValidator.Validate(
            CreateOfficialPackage(),
            Windows11X64);

        Assert.True(result.IsVerified, result.Reason);
        Assert.Equal(CodexSecurityStatus.Verified, result.Status);
        Assert.Equal(CodexSecurityStage.ApplicationIdentity, result.Stage);
        Assert.Equal(CodexSecurityFailureCode.None, result.FailureCode);
        var identity = Assert.IsType<VerifiedCodexIdentity>(result.Identity);
        Assert.Equal(ReferencePackageVersion, identity.PackageVersion);
        Assert.Equal(
            "OpenAI.Codex_2p2nqsd0c76g0!App",
            identity.AppUserModelId);
        Assert.True(identity.IsKnownExecutable(@"C:\Program Files\WindowsApps\ChatGPT.exe"));
    }

    [Theory]
    [InlineData("26.715.10079.0")]
    [InlineData("26.721.3404.0")]
    [InlineData("26.721.3996.0")]
    [InlineData("26.722.0.0")]
    [InlineData("27.4.5.6")]
    [InlineData("999.0.0.0")]
    public void Validate_AcceptsAnyOfficialVersionWithSelfConsistentFullName(string value)
    {
        var version = Version.Parse(value);

        var result = CodexSecurityValidator.Validate(
            CreateOfficialPackage(version),
            Windows11X64);

        Assert.True(result.IsVerified, result.Reason);
        var identity = Assert.IsType<VerifiedCodexIdentity>(result.Identity);
        Assert.Equal(version, identity.PackageVersion);
        Assert.Equal(ExpectedPackageFullName(version), identity.PackageFullName);
        Assert.Equal(ExpectedPackageRoot(version), identity.PackageRoot);
    }

    [Fact]
    public void Validate_PreservesObservedPackageIdentityForLaterProcessVerification()
    {
        var version = new Version(27, 4, 5, 6);
        var packageFullName = ExpectedPackageFullName(version);
        var packageRoot = Path.Combine(@"D:\WindowsApps", packageFullName);
        var package = new CodexPackageDescriptor(
            CodexSecurityValidator.OfficialPackageName,
            CodexSecurityValidator.OfficialPackageFamilyName,
            version,
            CodexPackageArchitecture.X64,
            CodexSecurityValidator.OfficialApplicationId,
            packageFullName,
            packageRoot);

        var result = CodexSecurityValidator.Validate(package, Windows11X64);

        Assert.True(result.IsVerified, result.Reason);
        Assert.Equal(packageFullName, result.Identity!.PackageFullName);
        Assert.Equal(packageRoot, result.Identity.PackageRoot);
    }

    [Fact]
    public void Validate_RejectsPackageFullNameThatDoesNotMatchObservedVersion()
    {
        var package = new CodexPackageDescriptor(
            CodexSecurityValidator.OfficialPackageName,
            CodexSecurityValidator.OfficialPackageFamilyName,
            new Version(27, 4, 5, 6),
            CodexPackageArchitecture.X64,
            CodexSecurityValidator.OfficialApplicationId,
            ExpectedPackageFullName(new Version(27, 4, 5, 7)));

        var result = CodexSecurityValidator.Validate(package, Windows11X64);

        Assert.False(result.IsVerified);
        Assert.Equal(CodexSecurityStatus.Rejected, result.Status);
        Assert.Equal(CodexSecurityStage.PackageIdentity, result.Stage);
        Assert.Equal(
            CodexSecurityFailureCode.UnexpectedPackageFullName,
            result.FailureCode);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void Validate_RejectsMissingObservedPackageFullName()
    {
        var package = new CodexPackageDescriptor(
            CodexSecurityValidator.OfficialPackageName,
            CodexSecurityValidator.OfficialPackageFamilyName,
            ReferencePackageVersion,
            CodexPackageArchitecture.X64,
            CodexSecurityValidator.OfficialApplicationId);

        var result = CodexSecurityValidator.Validate(package, Windows11X64);

        Assert.Equal(
            CodexSecurityFailureCode.UnexpectedPackageFullName,
            result.FailureCode);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void Validate_RejectsLookalikePackageIdentity()
    {
        var package = new CodexPackageDescriptor(
            CodexSecurityValidator.OfficialPackageName,
            "OpenAI.Codex_attacker",
            ReferencePackageVersion,
            CodexPackageArchitecture.X64,
            CodexSecurityValidator.OfficialApplicationId,
            ExpectedPackageFullName(ReferencePackageVersion));

        var result = CodexSecurityValidator.Validate(package, Windows11X64);

        Assert.Equal(
            CodexSecurityFailureCode.UnofficialPackageIdentity,
            result.FailureCode);
        Assert.Equal(CodexSecurityStage.PackageIdentity, result.Stage);
    }

    [Fact]
    public void Validate_RejectsUnsupportedPackageArchitecture()
    {
        var package = new CodexPackageDescriptor(
            CodexSecurityValidator.OfficialPackageName,
            CodexSecurityValidator.OfficialPackageFamilyName,
            ReferencePackageVersion,
            CodexPackageArchitecture.Arm64,
            CodexSecurityValidator.OfficialApplicationId,
            ExpectedPackageFullName(ReferencePackageVersion));

        var result = CodexSecurityValidator.Validate(package, Windows11X64);

        Assert.Equal(
            CodexSecurityFailureCode.UnsupportedPackageArchitecture,
            result.FailureCode);
        Assert.Equal(CodexSecurityStage.PackageIdentity, result.Stage);
    }

    [Fact]
    public void Validate_RejectsUnexpectedApplicationId()
    {
        var package = new CodexPackageDescriptor(
            CodexSecurityValidator.OfficialPackageName,
            CodexSecurityValidator.OfficialPackageFamilyName,
            ReferencePackageVersion,
            CodexPackageArchitecture.X64,
            "Attacker",
            ExpectedPackageFullName(ReferencePackageVersion));

        var result = CodexSecurityValidator.Validate(package, Windows11X64);

        Assert.Equal(
            CodexSecurityFailureCode.UnexpectedApplicationId,
            result.FailureCode);
        Assert.Equal(CodexSecurityStage.ApplicationIdentity, result.Stage);
    }

    [Fact]
    public void Validate_AcceptsTheMinimumSupportedWindowsBuild()
    {
        var runtime = Windows11X64 with
        {
            OperatingSystemVersion = CodexSecurityValidator.MinimumWindowsVersion,
        };

        var result = CodexSecurityValidator.Validate(CreateOfficialPackage(), runtime);

        Assert.True(result.IsVerified, result.Reason);
    }

    [Fact]
    public void Validate_RejectsCaseChangedOfficialPackageName()
    {
        var official = CreateOfficialPackage();
        var package = new CodexPackageDescriptor(
            CodexSecurityValidator.OfficialPackageName.ToLowerInvariant(),
            official.FamilyName,
            official.Version,
            official.Architecture,
            official.ApplicationId,
            official.PackageFullName,
            official.PackageRoot);

        var result = CodexSecurityValidator.Validate(package, Windows11X64);

        Assert.Equal(
            CodexSecurityFailureCode.UnofficialPackageIdentity,
            result.FailureCode);
    }

    [Theory]
    [InlineData(
        false,
        "10.0.26100.0",
        CodexPackageArchitecture.X64,
        CodexSecurityFailureCode.WrongOperatingSystem)]
    [InlineData(
        true,
        "10.0.21999.0",
        CodexPackageArchitecture.X64,
        CodexSecurityFailureCode.UnsupportedOperatingSystemVersion)]
    [InlineData(
        true,
        "10.0.26100.0",
        CodexPackageArchitecture.Arm64,
        CodexSecurityFailureCode.UnsupportedRuntimeArchitecture)]
    public void Validate_RejectsUnsupportedRuntime(
        bool isWindows,
        string osVersion,
        CodexPackageArchitecture architecture,
        CodexSecurityFailureCode expected)
    {
        var runtime = new CodexRuntimeDescriptor(
            isWindows,
            Version.Parse(osVersion),
            architecture);

        var result = CodexSecurityValidator.Validate(CreateOfficialPackage(), runtime);

        Assert.Equal(CodexSecurityStatus.Rejected, result.Status);
        Assert.Equal(CodexSecurityStage.RuntimeEnvironment, result.Stage);
        Assert.Equal(expected, result.FailureCode);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void ResultFactories_RepresentLaterSecurityStagesWithoutPresentationState()
    {
        var identity = GetIdentity();

        var inProgress = CodexSecurityResult.InProgress(
            CodexSecurityStage.LoopbackEndpoint,
            "Checking the loopback endpoint.",
            identity);
        var verified = CodexSecurityResult.Verified(
            identity,
            CodexSecurityStage.TargetValidation,
            "The unique Codex target passed revalidation.");
        var rejected = CodexSecurityResult.Rejected(
            CodexSecurityStage.TargetValidation,
            CodexSecurityFailureCode.AmbiguousTarget,
            "More than one Codex target remained.",
            identity);

        Assert.Equal(CodexSecurityStatus.InProgress, inProgress.Status);
        Assert.Same(identity, inProgress.Identity);
        Assert.True(verified.IsVerified);
        Assert.Same(identity, verified.Identity);
        Assert.Equal(CodexSecurityStatus.Rejected, rejected.Status);
        Assert.Same(identity, rejected.Identity);
    }

    [Fact]
    public void RejectedResult_RequiresAStableFailureCode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CodexSecurityResult.Rejected(
                CodexSecurityStage.PackageIdentity,
                CodexSecurityFailureCode.None,
                "A rejected result must not omit its failure code."));
    }

    internal static CodexPackageDescriptor CreateOfficialPackage() =>
        CreateOfficialPackage(ReferencePackageVersion);

    internal static CodexPackageDescriptor CreateOfficialPackage(Version version) => new(
        CodexSecurityValidator.OfficialPackageName,
        CodexSecurityValidator.OfficialPackageFamilyName,
        version,
        CodexPackageArchitecture.X64,
        CodexSecurityValidator.OfficialApplicationId,
        ExpectedPackageFullName(version),
        ExpectedPackageRoot(version));

    internal static VerifiedCodexIdentity GetIdentity() =>
        GetIdentity(ReferencePackageVersion);

    internal static VerifiedCodexIdentity GetIdentity(Version version)
    {
        var result = CodexSecurityValidator.Validate(
            CreateOfficialPackage(version),
            Windows11X64);
        return result.Identity ??
            throw new InvalidOperationException(result.Reason);
    }

    internal static string ExpectedPackageFullName(Version version) =>
        CodexSecurityValidator.BuildExpectedPackageFullName(version);

    internal static string ExpectedPackageRoot(Version version) =>
        Path.Combine(
            @"C:\Program Files\WindowsApps",
            ExpectedPackageFullName(version));
}
