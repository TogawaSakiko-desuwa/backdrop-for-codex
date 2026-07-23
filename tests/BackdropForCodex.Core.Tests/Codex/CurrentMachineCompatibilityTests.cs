using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Tests.Infrastructure;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class CurrentMachineCompatibilityTests
{
    private const string OptInVariable = "BACKDROP_FOR_CODEX_RUN_MACHINE_TESTS";

    [IntegrationFact(OptInVariable)]
    [Trait("Category", "Integration")]
    public void InstalledStorePackage_MatchesReviewedCompatibilityProfile_WhenOptedIn()
    {
        var package = new InstalledCodexPackageLocator().Locate();
        var result = CodexCompatibilityCatalog.Evaluate(
            package.Descriptor,
            CodexRuntimeDescriptor.Current);

        Assert.True(result.IsSupported, result.Reason);
        Assert.Equal(CodexCompatibilityCatalog.OfficialPackageFamilyName, package.Descriptor.FamilyName);
        Assert.Equal("ChatGPT.exe", Path.GetFileName(package.ExecutablePath));
        Assert.True(File.Exists(package.ExecutablePath));
    }

    [IntegrationFact(OptInVariable)]
    [Trait("Category", "Integration")]
    public async Task RunningCodexProcesses_AreBoundToOfficialPackage_WhenOptedIn()
    {
        var processes = await new WindowsCodexProcessSnapshotSource().GetProcessesAsync();

        Assert.Contains(
            processes,
            process =>
                string.Equals(process.ExecutableName, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    process.PackageFamilyName,
                    CodexCompatibilityCatalog.OfficialPackageFamilyName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    process.PackageFullName,
                    CodexCompatibilityCatalog.SupportedPackageFullName,
                    StringComparison.Ordinal) &&
                process.StartTimeUtc != default &&
                process.SessionId == WindowsCodexProcessSnapshotSource.CurrentSessionId);
    }
}
