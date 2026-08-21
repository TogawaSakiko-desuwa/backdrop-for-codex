using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace BackdropForCodex.Core.Media;

/// <summary>
/// Enumerates existing sessions on the current user's default multimedia render endpoint. Session
/// identifiers and process paths remain in-memory authorization evidence and are redacted from
/// every string representation.
/// </summary>
internal sealed class WindowsCoreAudioSessionSource : IWallpaperEngineAudioSessionSource
{
    private const uint ClassContextAll = 23;
    private static readonly Guid DeviceEnumeratorClassId =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid AudioSessionManager2InterfaceId =
        new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    public ValueTask<IReadOnlyList<IWallpaperEngineAudioSessionHandle>> CaptureAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Core Audio requires Windows.");
        }

        object? enumeratorObject = null;
        IMMDevice? device = null;
        object? managerObject = null;
        IAudioSessionEnumerator? sessionEnumerator = null;
        var handles = new List<IWallpaperEngineAudioSessionHandle>();
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(
                DeviceEnumeratorClassId,
                throwOnError: true) ??
                throw new InvalidOperationException("Core Audio device enumeration is unavailable.");
            enumeratorObject = Activator.CreateInstance(enumeratorType) ??
                throw new InvalidOperationException("Core Audio device enumeration is unavailable.");
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            ThrowIfFailed(enumerator.GetDefaultAudioEndpoint(
                AudioDataFlow.Render,
                AudioRole.Multimedia,
                out device));
            var managerInterfaceId = AudioSessionManager2InterfaceId;
            ThrowIfFailed(device.Activate(
                ref managerInterfaceId,
                ClassContextAll,
                nint.Zero,
                out managerObject));
            var manager = (IAudioSessionManager2)managerObject;
            ThrowIfFailed(manager.GetSessionEnumerator(out sessionEnumerator));
            ThrowIfFailed(sessionEnumerator.GetCount(out var count));
            if (count is < 0 or > 4096)
            {
                throw new InvalidOperationException("Core Audio returned an invalid session count.");
            }

            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IAudioSessionControl? control = null;
                try
                {
                    ThrowIfFailed(sessionEnumerator.GetSession(index, out control));
                    if (control is not IAudioSessionControl2 control2 ||
                        control is not ISimpleAudioVolume volume)
                    {
                        ReleaseComObject(control);
                        control = null;
                        continue;
                    }

                    ThrowIfFailed(control2.GetProcessId(out var processId));
                    if (processId == 0 || processId > int.MaxValue)
                    {
                        ReleaseComObject(control);
                        control = null;
                        continue;
                    }

                    ThrowIfFailed(control.GetGroupingParam(out var groupingParameter));
                    var sessionIdentifier = ReadAllocatedString(control2.GetSessionIdentifier);
                    var sessionInstanceIdentifier = ReadAllocatedString(
                        control2.GetSessionInstanceIdentifier);
                    ThrowIfFailed(volume.GetMute(out var isMuted));

                    string? processPath;
                    DateTimeOffset processStartTimeUtc;
                    try
                    {
                        using var process = Process.GetProcessById(checked((int)processId));
                        processPath = process.MainModule?.FileName;
                        processStartTimeUtc = process.StartTime.ToUniversalTime();
                    }
                    catch (Exception exception) when (
                        exception is ArgumentException or InvalidOperationException or
                            System.ComponentModel.Win32Exception or NotSupportedException or
                            PlatformNotSupportedException or System.Security.SecurityException or
                            IOException or UnauthorizedAccessException)
                    {
                        ReleaseComObject(control);
                        control = null;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(processPath) ||
                        !Path.IsPathFullyQualified(processPath))
                    {
                        ReleaseComObject(control);
                        control = null;
                        continue;
                    }

                    handles.Add(
                        new WindowsCoreAudioSessionHandle(
                            control,
                            volume,
                            checked((int)processId),
                            processStartTimeUtc,
                            processPath,
                            sessionIdentifier,
                            sessionInstanceIdentifier,
                            groupingParameter,
                            isMuted));
                    control = null;
                }
                finally
                {
                    ReleaseComObject(control);
                }
            }

            return ValueTask.FromResult<IReadOnlyList<IWallpaperEngineAudioSessionHandle>>(
                new ReadOnlyCollection<IWallpaperEngineAudioSessionHandle>(handles));
        }
        catch
        {
            foreach (var handle in handles)
            {
                handle.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            throw;
        }
        finally
        {
            ReleaseComObject(sessionEnumerator);
            ReleaseComObject(managerObject);
            ReleaseComObject(device);
            ReleaseComObject(enumeratorObject);
        }
    }

    private static string ReadAllocatedString(AllocatedStringReader reader)
    {
        nint valuePointer = nint.Zero;
        try
        {
            ThrowIfFailed(reader(out valuePointer));
            var value = Marshal.PtrToStringUni(valuePointer);
            if (string.IsNullOrEmpty(value) || value.Length > 4096)
            {
                throw new InvalidOperationException("Core Audio returned an invalid identity.");
            }

            return value;
        }
        finally
        {
            if (valuePointer != nint.Zero)
            {
                Marshal.FreeCoTaskMem(valuePointer);
            }
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }

    private delegate int AllocatedStringReader(out nint valuePointer);

    private enum AudioDataFlow
    {
        Render = 0,
    }

    private enum AudioRole
    {
        Multimedia = 1,
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage(
        "Style",
        "IDE1006:Naming Styles",
        Justification = "The name matches the Windows Core Audio SDK interface.")]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(AudioDataFlow dataFlow, uint stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(
            AudioDataFlow dataFlow,
            AudioRole role,
            out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage(
        "Style",
        "IDE1006:Naming Styles",
        Justification = "The name matches the Windows Core Audio SDK interface.")]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            nint activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(uint storageAccess, out nint properties);

        [PreserveSig]
        int GetId(out nint id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage(
        "Style",
        "IDE1006:Naming Styles",
        Justification = "The name matches the Windows Core Audio SDK interface.")]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(ref Guid sessionGuid, uint streamFlags, out nint control);

        [PreserveSig]
        int GetSimpleAudioVolume(ref Guid sessionGuid, uint streamFlags, out nint volume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

        [PreserveSig]
        int RegisterSessionNotification(nint notification);

        [PreserveSig]
        int UnregisterSessionNotification(nint notification);

        [PreserveSig]
        int RegisterDuckNotification(
            [MarshalAs(UnmanagedType.LPWStr)] string sessionId,
            nint notification);

        [PreserveSig]
        int UnregisterDuckNotification(nint notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage(
        "Style",
        "IDE1006:Naming Styles",
        Justification = "The name matches the Windows Core Audio SDK interface.")]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl sessionControl);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage(
        "Style",
        "IDE1006:Naming Styles",
        Justification = "The name matches the Windows Core Audio SDK interface.")]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName(out nint displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);

        [PreserveSig]
        int GetIconPath(out nint iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingParameter);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingParameter, ref Guid context);

        [PreserveSig]
        int RegisterAudioSessionNotification(nint notification);

        [PreserveSig]
        int UnregisterAudioSessionNotification(nint notification);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage(
        "Style",
        "IDE1006:Naming Styles",
        Justification = "The name matches the Windows Core Audio SDK interface.")]
    private interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName(out nint displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);

        [PreserveSig]
        int GetIconPath(out nint iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid context);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingParameter);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingParameter, ref Guid context);

        [PreserveSig]
        int RegisterAudioSessionNotification(nint notification);

        [PreserveSig]
        int UnregisterAudioSessionNotification(nint notification);

        [PreserveSig]
        int GetSessionIdentifier(out nint valuePointer);

        [PreserveSig]
        int GetSessionInstanceIdentifier(out nint valuePointer);

        [PreserveSig]
        int GetProcessId(out uint processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage(
        "Style",
        "IDE1006:Naming Styles",
        Justification = "The name matches the Windows Core Audio SDK interface.")]
    private interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolume(out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }

    private sealed class WindowsCoreAudioSessionHandle
        : IWallpaperEngineAudioSessionHandle
    {
        private readonly object _controlObject;
        private readonly ISimpleAudioVolume _volume;
        private bool _isMuted;
        private int _disposed;

        internal WindowsCoreAudioSessionHandle(
            object controlObject,
            ISimpleAudioVolume volume,
            int processId,
            DateTimeOffset processStartTimeUtc,
            string processPath,
            string sessionIdentifier,
            string sessionInstanceIdentifier,
            Guid groupingParameter,
            bool isMuted)
        {
            _controlObject = controlObject;
            _volume = volume;
            ProcessId = processId;
            ProcessStartTimeUtc = processStartTimeUtc;
            ProcessPath = Path.GetFullPath(processPath);
            SessionIdentifier = sessionIdentifier;
            SessionInstanceIdentifier = sessionInstanceIdentifier;
            GroupingParameter = groupingParameter;
            _isMuted = isMuted;
        }

        public int ProcessId { get; }

        public DateTimeOffset ProcessStartTimeUtc { get; }

        public string ProcessPath { get; }

        public string SessionIdentifier { get; }

        public string SessionInstanceIdentifier { get; }

        public Guid GroupingParameter { get; }

        public bool IsMuted => _isMuted;

        public void SetMuted(bool muted)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var eventContext = Guid.Empty;
            ThrowIfFailed(_volume.SetMute(muted, ref eventContext));
            _isMuted = muted;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseComObject(_controlObject);
            }

            return ValueTask.CompletedTask;
        }

        public override string ToString() =>
            $"{nameof(WindowsCoreAudioSessionHandle)} {{ Process = <redacted>, " +
            "Session = <redacted> }}";
    }
}
