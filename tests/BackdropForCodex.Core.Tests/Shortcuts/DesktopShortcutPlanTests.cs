using BackdropForCodex.Core.Shortcuts;
using Xunit;

namespace BackdropForCodex.Core.Tests.Shortcuts;

public sealed class DesktopShortcutPlanTests
{
    [Fact]
    public void Create_UsesStableNameAndEnhancedLaunchArgument()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "shortcut-plan"));
        var executablePath = Path.Combine(root, "Published Folder", "BackdropForCodex.exe");
        var desktopDirectory = Path.Combine(root, "Redirected Desktop");

        var plan = DesktopShortcutPlan.Create(executablePath, desktopDirectory);

        Assert.Equal(Path.GetFullPath(executablePath), plan.TargetPath);
        Assert.Equal("--launch", plan.Arguments);
        Assert.Equal(
            Path.Combine(desktopDirectory, "Codex（动态背景）.lnk"),
            plan.ShortcutPath);
        Assert.Equal(Path.GetDirectoryName(executablePath), plan.WorkingDirectory);
        Assert.Equal("使用动态背景启动 Codex", plan.Description);
    }

    [Fact]
    public void ForCurrentProcess_AcceptsExplicitValuesForDeterministicTests()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "current-shortcut-plan"));
        var executablePath = Path.Combine(root, "BackdropForCodex.EXE");
        var desktopDirectory = Path.Combine(root, "Desktop") + Path.DirectorySeparatorChar;

        var plan = DesktopShortcutPlan.ForCurrentProcess(executablePath, desktopDirectory);

        Assert.Equal(executablePath, plan.TargetPath);
        Assert.Equal(Path.TrimEndingDirectorySeparator(desktopDirectory), plan.DesktopDirectory);
    }

    [Fact]
    public void MatchesOwnedShortcut_RequiresExpectedTargetAndExactLaunchArguments()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "owned-shortcut-plan"));
        var executablePath = Path.Combine(root, "BackdropForCodex.exe");
        var plan = DesktopShortcutPlan.Create(
            executablePath,
            Path.Combine(root, "Desktop"));

        Assert.True(plan.MatchesOwnedShortcut(
            executablePath.ToUpperInvariant(),
            DesktopShortcutPlan.LaunchArguments));
        Assert.False(plan.MatchesOwnedShortcut(
            Path.Combine(root, "AnotherApp.exe"),
            DesktopShortcutPlan.LaunchArguments));
        Assert.False(plan.MatchesOwnedShortcut(executablePath, "--launch "));
        Assert.False(plan.MatchesOwnedShortcut(executablePath, "--LAUNCH"));
        Assert.False(plan.MatchesOwnedShortcut(executablePath, "--launch --extra"));
        Assert.False(plan.MatchesOwnedShortcut(null, DesktopShortcutPlan.LaunchArguments));
    }

    [Theory]
    [InlineData("BackdropForCodex.exe")]
    [InlineData("publish/BackdropForCodex.exe")]
    public void Create_RejectsRelativeExecutablePaths(string executablePath)
    {
        var desktopDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Desktop"));

        Assert.Throws<ArgumentException>(
            () => DesktopShortcutPlan.Create(executablePath, desktopDirectory));
    }

    [Fact]
    public void Create_RejectsNonExecutableTargets()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "invalid-shortcut-plan"));

        Assert.Throws<ArgumentException>(
            () => DesktopShortcutPlan.Create(
                Path.Combine(root, "BackdropForCodex.dll"),
                Path.Combine(root, "Desktop")));
    }
}
