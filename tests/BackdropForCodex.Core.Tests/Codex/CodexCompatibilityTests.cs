using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class CodexCompatibilityTests
{
    private static readonly CodexRuntimeDescriptor Windows11X64 = new(
        true,
        new Version(10, 0, 26100, 0),
        CodexPackageArchitecture.X64);

    [Fact]
    public void Evaluate_AcceptsReviewedPackageWithExactProbePackage()
    {
        var result = CodexCompatibilityCatalog.Evaluate(CreateOfficialPackage(), Windows11X64);

        Assert.True(result.IsSupported);
        Assert.NotNull(result.Profile);
        Assert.Equal(new Version(26, 715, 10079, 0), result.Profile.PackageVersion);
        Assert.Equal("openai-codex-26.715.10079.0-windows11-x64-v1", result.Profile.Id);
        Assert.Equal(
            "OpenAI.Codex_2p2nqsd0c76g0!App",
            result.Profile.AppUserModelId);
    }

    [Fact]
    public void Evaluate_AcceptsCurrentReviewedPackage()
    {
        var package = CreateOfficialPackage(new Version(26, 721, 3404, 0));

        var result = CodexCompatibilityCatalog.Evaluate(package, Windows11X64);

        Assert.True(result.IsSupported);
        Assert.True(result.Security.IsAllowed);
        Assert.NotNull(result.Profile);
        Assert.True(result.Profile.UsesExactProbePackage);
        Assert.Equal(result.Profile.Capabilities, result.Capabilities);
        Assert.Equal(new Version(26, 721, 3404, 0), result.Profile.PackageVersion);
        Assert.Equal(
            "OpenAI.Codex_26.721.3404.0_x64__2p2nqsd0c76g0",
            result.Profile.PackageFullName);
        Assert.Equal(
            "openai-codex-26.721.3404.0-windows11-x64-v1",
            result.Profile.Id);
    }

    [Fact]
    public void Evaluate_AcceptsInstalled3996PackageWithExactProbePackage()
    {
        var version = new Version(26, 721, 3996, 0);
        var package = CreateOfficialPackage(version);

        var result = CodexCompatibilityCatalog.Evaluate(package, Windows11X64);

        Assert.True(result.IsSupported);
        Assert.True(result.Security.IsAllowed);
        Assert.NotNull(result.Profile);
        Assert.True(result.Profile.UsesExactProbePackage);
        Assert.False(result.Profile.UsesReviewedBandProbePackage);
        Assert.Equal(version, result.Profile.PackageVersion);
        Assert.Equal(ExpectedPackageFullName(version), result.Profile.PackageFullName);
        Assert.Equal(ExpectedPackageRoot(version), result.Profile.PackageRoot);
        Assert.Equal(
            "openai-codex-26.721.3996.0-windows11-x64-v1",
            result.Profile.Id);
        Assert.True(result.Capabilities.Glass.IsAvailable);
        Assert.True(result.Capabilities.Advanced.IsAvailable);
    }

    [Theory]
    [InlineData("26.721.3404.1")]
    [InlineData("26.721.3405.0")]
    [InlineData("26.721.3996.1")]
    [InlineData("26.721.65535.0")]
    public void Evaluate_AcceptsReviewed721BandWithoutPerPatchDegradation(string version)
    {
        var parsedVersion = Version.Parse(version);
        var package = CreateOfficialPackage(parsedVersion);

        var result = CodexCompatibilityCatalog.Evaluate(package, Windows11X64);

        Assert.True(result.IsSupported);
        Assert.True(result.Security.IsAllowed);
        Assert.NotNull(result.Profile);
        Assert.False(result.Profile.UsesExactProbePackage);
        Assert.True(result.Profile.UsesReviewedBandProbePackage);
        Assert.Equal(
            CompatibilityProbePackageKind.ReviewedBand,
            result.Profile.ProbePackageKind);
        Assert.Equal(parsedVersion, result.Profile.PackageVersion);
        Assert.Equal(ExpectedPackageFullName(parsedVersion), result.Profile.PackageFullName);
        Assert.Equal(ExpectedPackageRoot(parsedVersion), result.Profile.PackageRoot);
        Assert.Equal(
            "openai-codex-26.721-reviewed-band-windows11-x64-v1",
            result.Profile.Id);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.AvailableFromReviewedBandProbePackage,
            result.Capabilities.Global.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.AvailableFromReviewedBandProbePackage,
            result.Capabilities.Glass.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.AvailableFromReviewedBandProbePackage,
            result.Capabilities.Advanced.ReasonCode);
        Assert.False(result.Capabilities.Regions.IsAvailable);
        Assert.False(result.Capabilities.Audio.IsAvailable);
    }

    [Theory]
    [InlineData("26.715.10078.0")]
    [InlineData("26.715.10080.0")]
    [InlineData("26.721.3403.0")]
    [InlineData("26.722.0.0")]
    [InlineData("26.720.99999.0")]
    [InlineData("27.0.0.0")]
    public void Evaluate_AcceptsUnknownOfficialVersionWithGenericProbePackage(string version)
    {
        var package = CreateOfficialPackage() with { };
        package = new CodexPackageDescriptor(
            package.Name,
            package.FamilyName,
            Version.Parse(version),
            package.Architecture,
            package.ApplicationId,
            ExpectedPackageFullName(Version.Parse(version)));

        var result = CodexCompatibilityCatalog.Evaluate(package, Windows11X64);

        Assert.True(result.IsSupported);
        Assert.True(result.Security.IsAllowed);
        Assert.NotNull(result.Profile);
        Assert.False(result.Profile.UsesExactProbePackage);
        Assert.False(result.Profile.UsesReviewedBandProbePackage);
        Assert.Equal(
            CompatibilityProbePackageKind.Generic,
            result.Profile.ProbePackageKind);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.AvailableFromGenericProbePackage,
            result.Capabilities.Global.ReasonCode);
        Assert.False(result.Capabilities.Regions.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            result.Capabilities.Regions.ReasonCode);
        Assert.False(result.Capabilities.Glass.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.UnavailableForGenericProbePackage,
            result.Capabilities.Glass.ReasonCode);
        Assert.False(result.Capabilities.Audio.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            result.Capabilities.Audio.ReasonCode);
        Assert.False(result.Capabilities.Advanced.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.UnavailableForGenericProbePackage,
            result.Capabilities.Advanced.ReasonCode);
        Assert.Equal(
            $"OpenAI.Codex_{Version.Parse(version)}_x64__2p2nqsd0c76g0",
            result.Profile.PackageFullName);
    }

    [Fact]
    public void Evaluate_PreservesDynamicInstalledPackageFullNameForProcessVerification()
    {
        var version = new Version(27, 4, 5, 6);
        var packageFullName = "OpenAI.Codex_27.4.5.6_x64__2p2nqsd0c76g0";
        var packageRoot = Path.Combine(@"D:\WindowsApps", packageFullName);
        var package = new CodexPackageDescriptor(
            CodexCompatibilityCatalog.OfficialPackageName,
            CodexCompatibilityCatalog.OfficialPackageFamilyName,
            version,
            CodexPackageArchitecture.X64,
            CodexCompatibilityCatalog.OfficialApplicationId,
            packageFullName,
            packageRoot);

        var result = CodexCompatibilityCatalog.Evaluate(package, Windows11X64);

        Assert.True(result.IsSupported);
        Assert.Equal(packageFullName, result.Profile!.PackageFullName);
        Assert.Equal(packageRoot, result.Profile.PackageRoot);
    }

    [Fact]
    public void Evaluate_RejectsPackageFullNameThatDoesNotMatchVerifiedIdentity()
    {
        var package = new CodexPackageDescriptor(
            CodexCompatibilityCatalog.OfficialPackageName,
            CodexCompatibilityCatalog.OfficialPackageFamilyName,
            new Version(27, 4, 5, 6),
            CodexPackageArchitecture.X64,
            CodexCompatibilityCatalog.OfficialApplicationId,
            "OpenAI.Codex_27.4.5.7_x64__2p2nqsd0c76g0");

        var result = CodexCompatibilityCatalog.Evaluate(package, Windows11X64);

        Assert.False(result.IsSupported);
        Assert.False(result.Security.IsAllowed);
        Assert.Equal(CodexCompatibilityFailure.UnexpectedPackageFullName, result.Failure);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.SecurityRejected,
            result.Capabilities.Global.ReasonCode);
    }

    [Fact]
    public void Evaluate_Rejects3996PackageWithOlderReviewedFullName()
    {
        var version = new Version(26, 721, 3996, 0);
        var package = new CodexPackageDescriptor(
            CodexCompatibilityCatalog.OfficialPackageName,
            CodexCompatibilityCatalog.OfficialPackageFamilyName,
            version,
            CodexPackageArchitecture.X64,
            CodexCompatibilityCatalog.OfficialApplicationId,
            ExpectedPackageFullName(new Version(26, 721, 3404, 0)));

        var result = CodexCompatibilityCatalog.Evaluate(package, Windows11X64);

        Assert.False(result.IsSupported);
        Assert.Equal(CodexCompatibilityFailure.UnexpectedPackageFullName, result.Failure);
        Assert.Null(result.Profile);
        Assert.All(
            GetCapabilities(result.Capabilities),
            capability => Assert.Equal(
                CompatibilityCapabilityReasonCode.SecurityRejected,
                capability.ReasonCode));
    }

    [Fact]
    public void Evaluate_RejectsMissingObservedPackageFullName()
    {
        var package = new CodexPackageDescriptor(
            CodexCompatibilityCatalog.OfficialPackageName,
            CodexCompatibilityCatalog.OfficialPackageFamilyName,
            CodexCompatibilityCatalog.SupportedPackageVersion,
            CodexPackageArchitecture.X64,
            CodexCompatibilityCatalog.OfficialApplicationId);

        var result = CodexCompatibilityCatalog.Evaluate(package, Windows11X64);

        Assert.False(result.IsSupported);
        Assert.Equal(CodexCompatibilityFailure.UnexpectedPackageFullName, result.Failure);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.SecurityRejected,
            result.Capabilities.Global.ReasonCode);
    }

    [Fact]
    public void Evaluate_ExactProbePackageMarksReleaseFeaturesAsNotImplemented()
    {
        var result = CodexCompatibilityCatalog.Evaluate(CreateOfficialPackage(), Windows11X64);

        Assert.False(result.Capabilities.Regions.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            result.Capabilities.Regions.ReasonCode);
        Assert.False(result.Capabilities.Audio.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            result.Capabilities.Audio.ReasonCode);
    }

    [Fact]
    public void Capabilities_DowngradeIndependentlyAndNeverReenableWithinGeneration()
    {
        var initial = CodexCompatibilityCatalog
            .Evaluate(CreateOfficialPackage(), Windows11X64)
            .Capabilities;
        var failedGlassProbe = new CompatibilityCapabilities(
            initial.Global,
            initial.Regions,
            new CompatibilityCapability(
                false,
                CompatibilityCapabilityReasonCode.StructuralProbeFailed),
            initial.Audio,
            initial.Advanced);

        var degraded = initial.DowngradeWith(failedGlassProbe);
        var attemptedRecovery = degraded.DowngradeWith(initial);

        Assert.True(degraded.Global.IsAvailable);
        Assert.False(degraded.Glass.IsAvailable);
        Assert.True(degraded.Advanced.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            degraded.Glass.ReasonCode);
        Assert.False(attemptedRecovery.Glass.IsAvailable);
    }

    [Fact]
    public void Evaluate_RejectsLookalikeFamily()
    {
        var package = new CodexPackageDescriptor(
            CodexCompatibilityCatalog.OfficialPackageName,
            "OpenAI.Codex_attacker",
            CodexCompatibilityCatalog.SupportedPackageVersion,
            CodexPackageArchitecture.X64,
            CodexCompatibilityCatalog.OfficialApplicationId);

        var result = CodexCompatibilityCatalog.Evaluate(package, Windows11X64);

        Assert.Equal(CodexCompatibilityFailure.UnofficialPackageIdentity, result.Failure);
    }

    [Theory]
    [InlineData(false, "10.0.26100.0", CodexPackageArchitecture.X64,
        CodexCompatibilityFailure.WrongOperatingSystem)]
    [InlineData(true, "10.0.21999.0", CodexPackageArchitecture.X64,
        CodexCompatibilityFailure.UnsupportedOperatingSystemVersion)]
    [InlineData(true, "10.0.26100.0", CodexPackageArchitecture.Arm64,
        CodexCompatibilityFailure.UnsupportedRuntimeArchitecture)]
    public void Evaluate_RejectsUnsupportedRuntime(
        bool isWindows,
        string osVersion,
        CodexPackageArchitecture architecture,
        CodexCompatibilityFailure expected)
    {
        var runtime = new CodexRuntimeDescriptor(
            isWindows,
            Version.Parse(osVersion),
            architecture);

        var result = CodexCompatibilityCatalog.Evaluate(CreateOfficialPackage(), runtime);

        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Profile);
    }

    internal static CodexPackageDescriptor CreateOfficialPackage() => new(
        CodexCompatibilityCatalog.OfficialPackageName,
        CodexCompatibilityCatalog.OfficialPackageFamilyName,
        CodexCompatibilityCatalog.SupportedPackageVersion,
        CodexPackageArchitecture.X64,
        CodexCompatibilityCatalog.OfficialApplicationId,
        ExpectedPackageFullName(CodexCompatibilityCatalog.SupportedPackageVersion),
        ExpectedPackageRoot(CodexCompatibilityCatalog.SupportedPackageVersion));

    internal static CodexPackageDescriptor CreateOfficialPackage(Version version) => new(
        CodexCompatibilityCatalog.OfficialPackageName,
        CodexCompatibilityCatalog.OfficialPackageFamilyName,
        version,
        CodexPackageArchitecture.X64,
        CodexCompatibilityCatalog.OfficialApplicationId,
        ExpectedPackageFullName(version),
        ExpectedPackageRoot(version));

    internal static CodexCompatibilityProfile GetProfile() =>
        GetProfile(CodexCompatibilityCatalog.SupportedPackageVersion);

    internal static CodexCompatibilityProfile GetProfile(Version version) =>
        CodexCompatibilityCatalog.Evaluate(
            CreateOfficialPackage(version),
            Windows11X64).Profile!;

    private static string ExpectedPackageFullName(Version version) =>
        $"{CodexCompatibilityCatalog.OfficialPackageName}_{version}_x64__2p2nqsd0c76g0";

    private static string ExpectedPackageRoot(Version version) =>
        Path.Combine(
            @"C:\Program Files\WindowsApps",
            ExpectedPackageFullName(version));

    private static IEnumerable<CompatibilityCapability> GetCapabilities(
        CompatibilityCapabilities capabilities)
    {
        yield return capabilities.Global;
        yield return capabilities.Regions;
        yield return capabilities.Glass;
        yield return capabilities.Audio;
        yield return capabilities.Advanced;
    }
}
