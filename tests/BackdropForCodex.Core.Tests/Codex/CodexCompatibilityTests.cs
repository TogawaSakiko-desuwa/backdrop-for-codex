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
    public void Evaluate_AcceptsOnlyReviewedPackage()
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

    [Theory]
    [InlineData("26.715.10078.0")]
    [InlineData("26.715.10080.0")]
    [InlineData("27.0.0.0")]
    public void Evaluate_FailsClosedForUnknownVersion(string version)
    {
        var package = CreateOfficialPackage() with { };
        package = new CodexPackageDescriptor(
            package.Name,
            package.FamilyName,
            Version.Parse(version),
            package.Architecture,
            package.ApplicationId);

        var result = CodexCompatibilityCatalog.Evaluate(package, Windows11X64);

        Assert.False(result.IsSupported);
        Assert.Null(result.Profile);
        Assert.Equal(CodexCompatibilityFailure.UnsupportedPackageVersion, result.Failure);
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
        CodexCompatibilityCatalog.OfficialApplicationId);

    internal static CodexCompatibilityProfile GetProfile() =>
        CodexCompatibilityCatalog.Evaluate(CreateOfficialPackage(), Windows11X64).Profile!;
}
