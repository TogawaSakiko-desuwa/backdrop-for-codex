using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BackdropForCodex.Core.Media;

internal sealed class WindowsWallpaperEngineWindowPlacementNativeApi
    : IWallpaperEngineWindowPlacementNativeApi
{
    private const int MaximumWindowTitleLength =
        WallpaperEngineOwnedWindowName.MaximumLength;
    private const int WindowStyleIndex = -16;
    private const int ExtendedWindowStyleIndex = -20;
    private const int VirtualScreenLeftMetric = 76;
    private const int VirtualScreenTopMetric = 77;
    private const int VirtualScreenWidthMetric = 78;
    private const int VirtualScreenHeightMetric = 79;

    public WallpaperEngineWindowRect GetVirtualScreen()
    {
        EnsureWindows();
        var left = GetSystemMetrics(VirtualScreenLeftMetric);
        var top = GetSystemMetrics(VirtualScreenTopMetric);
        var width = GetSystemMetrics(VirtualScreenWidthMetric);
        var height = GetSystemMetrics(VirtualScreenHeightMetric);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return new WallpaperEngineWindowRect(
            left,
            top,
            checked(left + width),
            checked(top + height));
    }

    public nint GetForegroundWindow()
    {
        EnsureWindows();
        return GetForegroundWindowNative();
    }

    public bool DoesWindowExist(nint windowHandle)
    {
        EnsureWindows();
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        return IsWindow(windowHandle);
    }

    public WallpaperEngineNativeWindowState CaptureWindowState(nint windowHandle)
    {
        EnsureWindows();
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        if (!IsWindow(windowHandle))
        {
            throw new Win32Exception(1400, "The verified pop-out window no longer exists.");
        }

        if (!GetWindowRect(windowHandle, out var rect))
        {
            throw LastWin32Exception("Unable to read the verified pop-out bounds.");
        }

        var threadId = GetWindowThreadProcessId(windowHandle, out var rawProcessId);
        if (threadId == 0 || rawProcessId == 0 || rawProcessId > int.MaxValue)
        {
            throw LastWin32Exception("Unable to read the verified pop-out process identity.");
        }

        var processId = checked((int)rawProcessId);
        using var process = Process.GetProcessById(processId);
        var processPath = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(processPath) ||
            !Path.IsPathFullyQualified(processPath))
        {
            throw new InvalidOperationException(
                "The verified pop-out process path is unavailable.");
        }

        var processDirectory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(processDirectory))
        {
            throw new InvalidOperationException(
                "The verified pop-out process directory is unavailable.");
        }

        var validatedPath = WallpaperEngineLocalPath.ValidateExistingRegularFile(
            processPath,
            processDirectory);
        return new WallpaperEngineNativeWindowState(
            windowHandle,
            GetExactWindowTitle(windowHandle),
            processId,
            process.StartTime.ToUniversalTime(),
            validatedPath,
            new WallpaperEngineWindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
            GetWindowStyle(windowHandle, WindowStyleIndex),
            GetWindowStyle(windowHandle, ExtendedWindowStyleIndex));
    }

    public WallpaperEngineWindowRect GetClientRect(nint windowHandle)
    {
        EnsureWindows();
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        if (!GetClientRectNative(windowHandle, out var rect))
        {
            throw LastWin32Exception("Unable to read the verified pop-out client bounds.");
        }

        return new WallpaperEngineWindowRect(
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom);
    }

    public void SetWindowPosition(
        nint windowHandle,
        nint insertAfter,
        WallpaperEngineWindowRect rect,
        WallpaperEngineSetWindowPositionFlags flags)
    {
        EnsureWindows();
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        if (!SetWindowPos(
                windowHandle,
                insertAfter,
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height,
                (uint)flags))
        {
            throw LastWin32Exception("Unable to position the verified pop-out window.");
        }
    }

    public void SetWindowStyles(nint windowHandle, uint style, uint extendedStyle)
    {
        EnsureWindows();
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        var failures = new List<Exception>();
        TrySetWindowStyle(windowHandle, WindowStyleIndex, style, failures);
        TrySetWindowStyle(windowHandle, ExtendedWindowStyleIndex, extendedStyle, failures);
        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Multiple pop-out style values could not be applied.",
                failures);
        }
    }

    private static uint GetWindowStyle(nint windowHandle, int index)
    {
        Marshal.SetLastPInvokeError(0);
        var value = GetWindowLongPtr(windowHandle, index);
        var error = Marshal.GetLastPInvokeError();
        if (value == 0 && error != 0)
        {
            throw new Win32Exception(error, "Unable to read the verified pop-out style.");
        }

        return unchecked((uint)value.ToInt64());
    }

    private static string GetExactWindowTitle(nint windowHandle)
    {
        var titleLength = GetWindowTextLength(windowHandle);
        if (titleLength is <= 0 or > MaximumWindowTitleLength)
        {
            throw new InvalidOperationException(
                "The verified pop-out title is unavailable or outside its reviewed bound.");
        }

        var buffer = new char[titleLength + 1];
        var copiedCharacterCount = GetWindowText(
            windowHandle,
            buffer,
            buffer.Length);
        if (copiedCharacterCount != titleLength)
        {
            throw LastWin32Exception("Unable to read the exact verified pop-out title.");
        }

        return new string(buffer, 0, copiedCharacterCount);
    }

    private static void TrySetWindowStyle(
        nint windowHandle,
        int index,
        uint value,
        List<Exception> failures)
    {
        try
        {
            Marshal.SetLastPInvokeError(0);
            var previous = SetWindowLongPtr(
                windowHandle,
                index,
                unchecked((nint)(long)value));
            var error = Marshal.GetLastPInvokeError();
            if (previous == 0 && error != 0)
            {
                throw new Win32Exception(
                    error,
                    "Unable to apply the verified pop-out style.");
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Wallpaper Engine pop-out placement requires Windows.");
        }
    }

    private static Win32Exception LastWin32Exception(string message)
    {
        var error = Marshal.GetLastPInvokeError();
        return error == 0
            ? new Win32Exception(message)
            : new Win32Exception(error, message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetForegroundWindowNative();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(
        nint windowHandle,
        [Out] char[] buffer,
        int maximumCharacterCount);

    [DllImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRectNative(
        nint windowHandle,
        out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(
        nint windowHandle,
        int index,
        nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
