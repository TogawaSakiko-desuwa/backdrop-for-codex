using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Tests.Infrastructure;
using Xunit;

namespace BackdropForCodex.Core.Tests.Codex;

public sealed class CurrentMachineCompatibilityTests
{
    private const string OptInVariable = "BACKDROP_FOR_CODEX_RUN_MACHINE_TESTS";

    [IntegrationFact(OptInVariable)]
    [Trait("Category", "Integration")]
    public void InstalledStorePackage_PassesSecurityIdentityValidation_WhenOptedIn()
    {
        var package = new InstalledCodexPackageLocator().Locate();
        var result = CodexSecurityValidator.Validate(
            package.Descriptor,
            CodexRuntimeDescriptor.Current);

        Assert.True(result.IsVerified, result.Reason);
        Assert.Equal(result.Identity!.PackageFullName, package.PackageFullName);
        Assert.Equal(
            CodexSecurityValidator.OfficialPackageFamilyName,
            package.Descriptor.FamilyName);
        Assert.Equal("ChatGPT.exe", Path.GetFileName(package.ExecutablePath));
        Assert.True(File.Exists(package.ExecutablePath));
    }

    [IntegrationFact(OptInVariable)]
    [Trait("Category", "Integration")]
    public async Task RunningCodexProcesses_AreBoundToOfficialPackage_WhenOptedIn()
    {
        var package = new InstalledCodexPackageLocator().Locate();
        var security = CodexSecurityValidator.Validate(
            package.Descriptor,
            CodexRuntimeDescriptor.Current);
        Assert.True(security.IsVerified, security.Reason);
        var identity = security.Identity!;
        var processes = await new WindowsCodexProcessSnapshotSource().GetProcessesAsync();

        Assert.Contains(
            processes,
            process =>
                string.Equals(process.ExecutableName, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    process.PackageFamilyName,
                    identity.PackageFamilyName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    process.PackageFullName,
                    identity.PackageFullName,
                    StringComparison.Ordinal) &&
                process.StartTimeUtc != default &&
                process.SessionId == WindowsCodexProcessSnapshotSource.CurrentSessionId);
    }
}
