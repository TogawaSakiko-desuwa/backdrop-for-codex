using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace BackdropForCodex.Core.Shortcuts;

public enum DesktopShortcutWriteKind
{
    Created,
    Updated,
}

public sealed record DesktopShortcutWriteResult(string ShortcutPath, DesktopShortcutWriteKind Kind);

/// <summary>
/// Creates the enhanced-launch shortcut using the Windows Shell Link COM contract.
/// No command shell, PowerShell, or Windows Script Host process is used.
/// </summary>
[SupportedOSPlatform("windows10.0.22000")]
public static class WindowsDesktopShortcutService
{
    private const int NormalWindow = 1;
    private static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    public static DesktopShortcutWriteResult CreateOrUpdate()
    {
        return CreateOrUpdate(DesktopShortcutPlan.ForCurrentProcess());
    }

    public static DesktopShortcutWriteResult CreateOrUpdate(DesktopShortcutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            throw new PlatformNotSupportedException("The enhanced shortcut requires Windows 11.");
        }

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
        int GetPath(IntPtr file, int characterCount, IntPtr findData, uint flags);

        [PreserveSig]
        int GetIDList(out IntPtr itemIdList);

        [PreserveSig]
        int SetIDList(IntPtr itemIdList);

        [PreserveSig]
        int GetDescription(IntPtr name, int characterCount);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        [PreserveSig]
        int GetWorkingDirectory(IntPtr directory, int characterCount);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        [PreserveSig]
        int GetArguments(IntPtr arguments, int characterCount);

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
        int GetIconLocation(IntPtr iconPath, int characterCount, out int iconIndex);

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
