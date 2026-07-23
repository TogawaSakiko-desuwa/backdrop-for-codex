namespace BackdropForCodex.Core.Shortcuts;

/// <summary>
/// Describes the stable, user-visible enhanced-launch shortcut.
/// </summary>
public sealed class DesktopShortcutPlan
{
    public const string ShortcutFileName = "Codex（动态背景）.lnk";
    public const string LaunchArguments = "--launch";
    public const string ShortcutDescription = "使用动态背景启动 Codex";

    private DesktopShortcutPlan(
        string targetPath,
        string desktopDirectory,
        string workingDirectory)
    {
        TargetPath = targetPath;
        DesktopDirectory = desktopDirectory;
        WorkingDirectory = workingDirectory;
        ShortcutPath = Path.Combine(desktopDirectory, ShortcutFileName);
        Arguments = LaunchArguments;
        Description = ShortcutDescription;
    }

    public string TargetPath { get; }

    public string Arguments { get; }

    public string Description { get; }

    public string DesktopDirectory { get; }

    public string WorkingDirectory { get; }

    public string ShortcutPath { get; }

    /// <summary>
    /// Returns whether resolved shell-link values identify a shortcut owned by this app instance.
    /// The Windows target path is compared case-insensitively; launch arguments must be exact.
    /// </summary>
    public bool MatchesOwnedShortcut(string? targetPath, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(targetPath) ||
            !string.Equals(arguments, LaunchArguments, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return string.Equals(
                TargetPath,
                Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a deterministic shortcut plan without touching the file system.
    /// </summary>
    public static DesktopShortcutPlan Create(string executablePath, string desktopDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopDirectory);

        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The executable path must be absolute.", nameof(executablePath));
        }

        if (!Path.IsPathFullyQualified(desktopDirectory))
        {
            throw new ArgumentException("The desktop directory must be absolute.", nameof(desktopDirectory));
        }

        var normalizedExecutablePath = Path.GetFullPath(executablePath);
        if (!string.Equals(
                Path.GetExtension(normalizedExecutablePath),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The shortcut target must be an executable file.", nameof(executablePath));
        }

        var normalizedDesktopDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(desktopDirectory));
        var workingDirectory = Path.GetDirectoryName(normalizedExecutablePath);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException(
                "The executable path must include a working directory.",
                nameof(executablePath));
        }

        return new DesktopShortcutPlan(
            normalizedExecutablePath,
            normalizedDesktopDirectory,
            workingDirectory);
    }

    /// <summary>
    /// Resolves the running app host and the user's redirected Desktop folder.
    /// Environment values can be supplied explicitly for deterministic tests.
    /// </summary>
    public static DesktopShortcutPlan ForCurrentProcess(
        string? executablePath = null,
        string? desktopDirectory = null)
    {
        executablePath ??= Environment.ProcessPath
            ?? throw new InvalidOperationException("Windows did not expose the current executable path.");
        desktopDirectory ??= Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(desktopDirectory))
        {
            throw new InvalidOperationException("Windows did not expose the user's Desktop directory.");
        }

        return Create(executablePath, desktopDirectory);
    }
}
