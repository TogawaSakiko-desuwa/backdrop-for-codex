using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BackdropForCodex.Core.Codex;

[Flags]
public enum ApplicationActivationOptions : uint
{
    None = 0,
    DesignMode = 0x1,
    NoErrorUi = 0x2,
    NoSplashScreen = 0x4,
    PreLaunch = 0x2000000,
}

public sealed record ApplicationActivationResult(uint ProcessId);

/// <summary>
/// Narrow, testable surface over the Windows Application Activation Manager.
/// </summary>
public interface IApplicationActivationManager
{
    ApplicationActivationResult Activate(
        CodexCompatibilityProfile profile,
        string? arguments = null,
        ApplicationActivationOptions options = ApplicationActivationOptions.NoErrorUi);
}

/// <summary>
/// The native call seam. Tests can supply an in-memory implementation without activating an app.
/// </summary>
public interface IApplicationActivationBackend
{
    int ActivateApplication(
        string appUserModelId,
        string arguments,
        ApplicationActivationOptions options,
        out uint processId);
}

[SupportedOSPlatform("windows10.0.22000")]
public sealed class WindowsApplicationActivationManager : IApplicationActivationManager
{
    private readonly IApplicationActivationBackend _backend;

    public WindowsApplicationActivationManager()
        : this(new ComApplicationActivationBackend())
    {
    }

    public WindowsApplicationActivationManager(IApplicationActivationBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    public ApplicationActivationResult Activate(
        CodexCompatibilityProfile profile,
        string? arguments = null,
        ApplicationActivationOptions options = ApplicationActivationOptions.NoErrorUi)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var hresult = _backend.ActivateApplication(
            profile.AppUserModelId,
            arguments ?? string.Empty,
            options,
            out var processId);

        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }

        if (processId == 0)
        {
            throw new InvalidOperationException(
                "Application Activation Manager succeeded without returning a process id.");
        }

        return new ApplicationActivationResult(processId);
    }
}

[SupportedOSPlatform("windows10.0.22000")]
internal sealed class ComApplicationActivationBackend : IApplicationActivationBackend
{
    public int ActivateApplication(
        string appUserModelId,
        string arguments,
        ApplicationActivationOptions options,
        out uint processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            throw new PlatformNotSupportedException("Windows 11 is required for Codex activation.");
        }

        var manager = (IApplicationActivationManagerNative)(object)new ApplicationActivationManagerComObject();
        try
        {
            return manager.ActivateApplication(appUserModelId, arguments, options, out processId);
        }
        finally
        {
            if (Marshal.IsComObject(manager))
            {
                Marshal.FinalReleaseComObject(manager);
            }
        }
    }
}

[ComImport]
[Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManagerNative
{
    [PreserveSig]
    int ActivateApplication(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments,
        ApplicationActivationOptions options,
        out uint processId);

    [PreserveSig]
    int ActivateForFile(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        nint itemArray,
        [MarshalAs(UnmanagedType.LPWStr)] string verb,
        out uint processId);

    [PreserveSig]
    int ActivateForProtocol(
        [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
        nint itemArray,
        out uint processId);
}

[ComImport]
[Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
internal sealed class ApplicationActivationManagerComObject
{
}
