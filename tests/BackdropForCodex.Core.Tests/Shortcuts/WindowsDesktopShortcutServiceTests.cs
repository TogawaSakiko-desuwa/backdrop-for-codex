using BackdropForCodex.Core.Shortcuts;
using Xunit;

namespace BackdropForCodex.Core.Tests.Shortcuts;

public sealed class WindowsDesktopShortcutServiceTests
{
    [Fact]
    public void CreateOrUpdate_WritesAndThenReplacesShellLink()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "BackdropForCodex.ShortcutTests",
            Guid.NewGuid().ToString("N"));
        var desktopDirectory = Path.Combine(testRoot, "Desktop");
        var executablePath = Path.Combine(testRoot, "BackdropForCodex.exe");
        Directory.CreateDirectory(desktopDirectory);
        File.WriteAllBytes(executablePath, [0x4d, 0x5a]);

        try
        {
            var plan = DesktopShortcutPlan.Create(executablePath, desktopDirectory);

            var created = WindowsDesktopShortcutService.CreateOrUpdate(plan);
            var updated = WindowsDesktopShortcutService.CreateOrUpdate(plan);

            Assert.Equal(DesktopShortcutWriteKind.Created, created.Kind);
            Assert.Equal(DesktopShortcutWriteKind.Updated, updated.Kind);
            Assert.Equal(plan.ShortcutPath, created.ShortcutPath);
            Assert.True(File.Exists(plan.ShortcutPath));
            Assert.True(new FileInfo(plan.ShortcutPath).Length > 0);
            Assert.Empty(Directory.EnumerateFiles(desktopDirectory, ".*.lnk"));

            var inspection = WindowsDesktopShortcutService.InspectOwnership(plan);
            Assert.Equal(DesktopShortcutOwnership.OwnedByCurrentApp, inspection.Ownership);
            Assert.Equal(plan.TargetPath, inspection.TargetPath, ignoreCase: true);
            Assert.Equal(DesktopShortcutPlan.LaunchArguments, inspection.Arguments);

            var deleted = WindowsDesktopShortcutService.DeleteIfOwned(plan);
            Assert.Equal(DesktopShortcutDeleteKind.Deleted, deleted.Kind);
            Assert.False(File.Exists(plan.ShortcutPath));

            var missing = WindowsDesktopShortcutService.DeleteIfOwned(plan);
            Assert.Equal(DesktopShortcutDeleteKind.Missing, missing.Kind);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void DeleteIfOwned_PreservesShortcutThatTargetsAnotherExecutable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "BackdropForCodex.ShortcutOwnershipTests",
            Guid.NewGuid().ToString("N"));
        var desktopDirectory = Path.Combine(testRoot, "Desktop");
        var executablePath = Path.Combine(testRoot, "BackdropForCodex.exe");
        var foreignExecutablePath = Path.Combine(testRoot, "AnotherApp.exe");
        Directory.CreateDirectory(desktopDirectory);
        File.WriteAllBytes(executablePath, [0x4d, 0x5a]);
        File.WriteAllBytes(foreignExecutablePath, [0x4d, 0x5a]);

        try
        {
            var ownedPlan = DesktopShortcutPlan.Create(executablePath, desktopDirectory);
            var foreignPlan = DesktopShortcutPlan.Create(foreignExecutablePath, desktopDirectory);
            _ = WindowsDesktopShortcutService.CreateOrUpdate(foreignPlan);

            var inspection = WindowsDesktopShortcutService.InspectOwnership(ownedPlan);
            var deletion = WindowsDesktopShortcutService.DeleteIfOwned(ownedPlan);

            Assert.Equal(DesktopShortcutOwnership.NotOwnedByCurrentApp, inspection.Ownership);
            Assert.Equal(foreignPlan.TargetPath, inspection.TargetPath, ignoreCase: true);
            Assert.Equal(DesktopShortcutDeleteKind.SkippedNotOwned, deletion.Kind);
            Assert.True(File.Exists(ownedPlan.ShortcutPath));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}
