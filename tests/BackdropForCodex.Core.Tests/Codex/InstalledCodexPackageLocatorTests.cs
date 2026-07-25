using BackdropForCodex.Core.Codex;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class InstalledCodexPackageLocatorTests
{
    [Fact]
    public void Locate_ZeroValidatedCandidatesReportsNotInstalled()
    {
        var locator = CreateLocator([]);

        var exception = Assert.Throws<CodexPackageDiscoveryException>(locator.Locate);

        Assert.Contains("not installed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Locate_OneValidatedCandidateReturnsThatCandidate()
    {
        var expected = CreatePackage(new Version(26, 721, 3996, 0));
        var locator = CreateLocator([expected]);

        var actual = locator.Locate();

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Locate_MultipleValidatedCandidatesFailsClosedRegardlessOfEnumerationOrder(
        bool newestFirst)
    {
        var older = CreatePackage(new Version(26, 721, 3404, 0));
        var newer = CreatePackage(new Version(999, 4, 5, 6));
        InstalledCodexPackage[] candidates = newestFirst
            ? [newer, older]
            : [older, newer];
        var locator = CreateLocator(candidates);

        var exception = Assert.Throws<CodexPackageDiscoveryException>(locator.Locate);

        Assert.Contains("multiple", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static InstalledCodexPackageLocator CreateLocator(
        IReadOnlyList<InstalledCodexPackage> candidates) =>
        new(() => candidates);

    private static InstalledCodexPackage CreatePackage(Version version)
    {
        var descriptor = CodexSecurityValidatorTests.CreateOfficialPackage(version);
        return new InstalledCodexPackage(
            descriptor,
            descriptor.PackageFullName!,
            descriptor.PackageRoot!,
            "app/ChatGPT.exe");
    }
}
