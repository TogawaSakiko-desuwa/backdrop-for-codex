using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace BackdropForCodex.Core.Shortcuts;

public enum DesktopShortcutWriteKind
{
    Created,
    Updated,
}

public sealed record DesktopShortcutWriteResult(string ShortcutPath, DesktopShortcutWriteKind Kind);

public enum DesktopShortcutOwnership
{
    Missing,
    OwnedByCurrentApp,
    NotOwnedByCurrentApp,
    Unreadable,
}

public sealed record DesktopShortcutInspectionResult(
    string ShortcutPath,
    DesktopShortcutOwnership Ownership,
    string? TargetPath = null,
    string? Arguments = null)
{
    public bool IsOwnedByCurrentApp => Ownership == DesktopShortcutOwnership.OwnedByCurrentApp;
}

public enum DesktopShortcutDeleteKind
{
    Deleted,
    Missing,
    SkippedNotOwned,
    SkippedUnreadable,
}

public sealed record DesktopShortcutDeleteResult(
    string ShortcutPath,
    DesktopShortcutDeleteKind Kind);

/// <summary>
/// Creates, inspects, and safely removes the enhanced-launch shortcut using the Windows Shell Link
/// COM contract.
/// No command shell, PowerShell, or Windows Script Host process is used.
/// </summary>
[SupportedOSPlatform("windows10.0.22000")]
public static class WindowsDesktopShortcutService
{
    private const int ShellLinkBufferLength = 32768;
    private const int NormalWindow = 1;
    private const uint ShellLinkGetPathRaw = 0x00000004;
    private const int StorageModeRead = 0;
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    public static DesktopShortcutWriteResult CreateOrUpdate()
    {
        return CreateOrUpdate(DesktopShortcutPlan.ForCurrentProcess());
    }

    public static DesktopShortcutWriteResult CreateOrUpdate(DesktopShortcutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureSupportedPlatform();

        if (!File.Exists(plan.TargetPath))
        {
            throw new FileNotFoundException(
                "The current Backdrop for Codex executable could not be found.",
                plan.TargetPath);
        }

        if (!Directory.Exists(plan.DesktopDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The user's Desktop directory could not be found: {plan.DesktopDirectory}");
        }

        var writeKind = File.Exists(plan.ShortcutPath)
            ? DesktopShortcutWriteKind.Updated
            : DesktopShortcutWriteKind.Created;
        var temporaryPath = Path.Combine(
            plan.DesktopDirectory,
            $".{Path.GetFileNameWithoutExtension(DesktopShortcutPlan.ShortcutFileName)}.{Guid.NewGuid():N}.lnk");

        try
        {
            WriteShellLink(plan, temporaryPath);
            File.Move(temporaryPath, plan.ShortcutPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new DesktopShortcutWriteResult(plan.ShortcutPath, writeKind);
    }

    /// <summary>
    /// Inspects the stable shortcut path without changing it. Ownership requires both the expected
    /// executable target and the exact enhanced-launch argument.
    /// </summary>
    public static DesktopShortcutInspectionResult InspectOwnership()
    {
        return InspectOwnership(DesktopShortcutPlan.ForCurrentProcess());
    }

    /// <summary>
    /// Inspects the stable shortcut path without changing it. An unreadable or foreign shortcut is
    /// never treated as owned.
    /// </summary>
    public static DesktopShortcutInspectionResult InspectOwnership(DesktopShortcutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        EnsureSupportedPlatform();

        if (!File.Exists(plan.ShortcutPath))
        {
            return new DesktopShortcutInspectionResult(
                plan.ShortcutPath,
                DesktopShortcutOwnership.Missing);
        }

        try
        {
            var (targetPath, arguments) = ReadShellLink(plan.ShortcutPath);
            var ownership = plan.MatchesOwnedShortcut(targetPath, arguments)
                ? DesktopShortcutOwnership.OwnedByCurrentApp
                : DesktopShortcutOwnership.NotOwnedByCurrentApp;

            return new DesktopShortcutInspectionResult(
                plan.ShortcutPath,
                ownership,
                targetPath,
                arguments);
        }
        catch (Exception exception) when (
            exception is COMException or
            IOException or
            UnauthorizedAccessException)
        {
            return new DesktopShortcutInspectionResult(
                plan.ShortcutPath,
                DesktopShortcutOwnership.Unreadable);
        }
    }

    /// <summary>
    /// Deletes the stable shortcut only when it targets the current app and contains exactly
    /// <c>--launch</c>. Missing, foreign, and unreadable shortcuts remain untouched.
    /// </summary>
    public static DesktopShortcutDeleteResult DeleteIfOwned()
    {
        return DeleteIfOwned(DesktopShortcutPlan.ForCurrentProcess());
    }

    /// <summary>
    /// Deletes the planned shortcut only after verifying its target and launch argument.
    /// </summary>
    public static DesktopShortcutDeleteResult DeleteIfOwned(DesktopShortcutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var inspection = InspectOwnership(plan);

        switch (inspection.Ownership)
        {
            case DesktopShortcutOwnership.Missing:
                return new DesktopShortcutDeleteResult(
                    plan.ShortcutPath,
                    DesktopShortcutDeleteKind.Missing);
            case DesktopShortcutOwnership.NotOwnedByCurrentApp:
                return new DesktopShortcutDeleteResult(
                    plan.ShortcutPath,
                    DesktopShortcutDeleteKind.SkippedNotOwned);
            case DesktopShortcutOwnership.Unreadable:
                return new DesktopShortcutDeleteResult(
                    plan.ShortcutPath,
                    DesktopShortcutDeleteKind.SkippedUnreadable);
            case DesktopShortcutOwnership.OwnedByCurrentApp:
                File.Delete(plan.ShortcutPath);
                return new DesktopShortcutDeleteResult(
                    plan.ShortcutPath,
                    DesktopShortcutDeleteKind.Deleted);
            default:
                throw new InvalidOperationException("The shortcut ownership result is not supported.");
        }
    }

    private static void WriteShellLink(DesktopShortcutPlan plan, string outputPath)
    {
        object? shellLinkObject = null;
        try
        {
            var shellLinkType = Type.GetTypeFromCLSID(ShellLinkClassId, throwOnError: true)
                ?? throw new InvalidOperationException("Windows Shell Link COM is unavailable.");
            shellLinkObject = Activator.CreateInstance(shellLinkType)
                ?? throw new InvalidOperationException("Windows Shell Link COM could not be created.");

            var shellLink = (IShellLinkW)shellLinkObject;
            ThrowIfFailed(shellLink.SetPath(plan.TargetPath));
            ThrowIfFailed(shellLink.SetArguments(plan.Arguments));
            ThrowIfFailed(shellLink.SetWorkingDirectory(plan.WorkingDirectory));
            ThrowIfFailed(shellLink.SetDescription(plan.Description));
            ThrowIfFailed(shellLink.SetShowCmd(NormalWindow));
            ThrowIfFailed(shellLink.SetIconLocation(plan.TargetPath, 0));

            ((IPersistFile)shellLinkObject).Save(outputPath, true);
        }
        finally
        {
            if (shellLinkObject is not null && Marshal.IsComObject(shellLinkObject))
            {
                _ = Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }
    }

    private static (string TargetPath, string Arguments) ReadShellLink(string shortcutPath)
    {
        object? shellLinkObject = null;
        try
        {
            var shellLinkType = Type.GetTypeFromCLSID(ShellLinkClassId, throwOnError: true)
                ?? throw new InvalidOperationException("Windows Shell Link COM is unavailable.");
            shellLinkObject = Activator.CreateInstance(shellLinkType)
                ?? throw new InvalidOperationException("Windows Shell Link COM could not be created.");

            ((IPersistFile)shellLinkObject).Load(shortcutPath, StorageModeRead);
            var shellLink = (IShellLinkW)shellLinkObject;
            var targetPath = new StringBuilder(ShellLinkBufferLength);
            var arguments = new StringBuilder(ShellLinkBufferLength);
            ThrowIfFailed(shellLink.GetPath(
                targetPath,
                targetPath.Capacity,
                IntPtr.Zero,
                ShellLinkGetPathRaw));
            ThrowIfFailed(shellLink.GetArguments(arguments, arguments.Capacity));
            return (targetPath.ToString(), arguments.ToString());
        }
        finally
        {
            if (shellLinkObject is not null && Marshal.IsComObject(shellLinkObject))
            {
                _ = Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            throw new PlatformNotSupportedException("The enhanced shortcut requires Windows 11.");
        }
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage(
        "Style",
        "IDE1006:Naming Styles",
        Justification = "The name matches the public Windows SDK COM interface.")]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int characterCount,
            IntPtr findData,
            uint flags);

        [PreserveSig]
        int GetIDList(out IntPtr itemIdList);

        [PreserveSig]
        int SetIDList(IntPtr itemIdList);

        [PreserveSig]
        int GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name,
            int characterCount);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        [PreserveSig]
        int GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int characterCount);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        [PreserveSig]
        int GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
            int characterCount);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        [PreserveSig]
        int GetHotkey(out short hotkey);

        [PreserveSig]
        int SetHotkey(short hotkey);

        [PreserveSig]
        int GetShowCmd(out int showCommand);

        [PreserveSig]
        int SetShowCmd(int showCommand);

        [PreserveSig]
        int GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int characterCount,
            out int iconIndex);

        [PreserveSig]
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        [PreserveSig]
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        [PreserveSig]
        int Resolve(IntPtr windowHandle, uint flags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
