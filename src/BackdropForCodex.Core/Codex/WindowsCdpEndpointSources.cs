using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace BackdropForCodex.Core.Codex;

public sealed record TcpListenerSnapshot(int ProcessId, IPAddress Address, int Port);

public interface ITcpListenerSnapshotSource
{
    IReadOnlyList<TcpListenerSnapshot> GetListeners();
}

/// <summary>
/// Discovers random CDP ports by intersecting packaged Codex processes with their actual
/// loopback-only listening sockets. It does not trust process arguments or scan unrelated ports.
/// </summary>
public sealed class LoopbackTcpCdpEndpointCandidateSource : ICdpEndpointCandidateSource
{
    private readonly ICodexProcessSnapshotSource _processes;
    private readonly ITcpListenerSnapshotSource _listeners;

    public LoopbackTcpCdpEndpointCandidateSource(
        ICodexProcessSnapshotSource processes,
        ITcpListenerSnapshotSource listeners)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _listeners = listeners ?? throw new ArgumentNullException(nameof(listeners));
    }

    public async ValueTask<IReadOnlyList<CdpEndpointCandidate>> GetCandidatesAsync(
        CodexCompatibilityProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var processes = await _processes.GetProcessesAsync(cancellationToken).ConfigureAwait(false);
        var trustedProcesses = processes
            .Where(process =>
                process.ProcessId > 0 &&
                profile.IsKnownExecutable(process.ExecutableName) &&
                string.Equals(
                    process.PackageFamilyName,
                    profile.PackageFamilyName,
                    StringComparison.Ordinal) &&
                string.Equals(
                    process.PackageFullName,
                    profile.PackageFullName,
                    StringComparison.Ordinal) &&
                process.StartTimeUtc != default &&
                process.SessionId == WindowsCodexProcessSnapshotSource.CurrentSessionId)
            .ToDictionary(process => process.ProcessId);

        return _listeners.GetListeners()
            .Where(listener =>
                trustedProcesses.ContainsKey(listener.ProcessId) &&
                listener.Port is > 0 and <= ushort.MaxValue &&
                listener.Address.Equals(IPAddress.Loopback))
            .Select(listener =>
            {
                var process = trustedProcesses[listener.ProcessId];
                return new CdpEndpointCandidate(
                    listener.ProcessId,
                    Path.GetFileName(process.ExecutableName),
                    process.PackageFamilyName,
                    process.PackageFullName,
                    process.StartTimeUtc,
                    process.SessionId,
                    new Uri($"http://127.0.0.1:{listener.Port}/", UriKind.Absolute));
            })
            .DistinctBy(candidate => (candidate.ProcessId, candidate.BaseUri.Port))
            .OrderBy(candidate => candidate.ProcessId)
            .ThenBy(candidate => candidate.BaseUri.Port)
            .ToArray();
    }
}

public sealed class WindowsCodexProcessSnapshotSource : ICodexProcessSnapshotSource
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    internal static int CurrentSessionId
    {
        get
        {
            using var currentProcess = Process.GetCurrentProcess();
            return currentProcess.SessionId;
        }
    }

    public ValueTask<IReadOnlyList<CodexProcessSnapshot>> GetProcessesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<IReadOnlyList<CodexProcessSnapshot>>([]);
        }

        var snapshots = new List<CodexProcessSnapshot>();
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var executable = TryGetExecutablePath(handle);
                    var family = TryGetPackageFamilyName(handle);
                    var packageFullName = TryGetPackageFullName(handle);
                    if (executable is null ||
                        family is null ||
                        packageFullName is null ||
                        !TryGetLifetimeIdentity(process, out var startTimeUtc, out var sessionId))
                    {
                        continue;
                    }

                    snapshots.Add(new CodexProcessSnapshot(
                        process.Id,
                        Path.GetFileName(executable),
                        family,
                        packageFullName,
                        startTimeUtc,
                        sessionId,
                        CommandLine: null));
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }

        return ValueTask.FromResult<IReadOnlyList<CodexProcessSnapshot>>(snapshots);
    }

    private static string? TryGetExecutablePath(IntPtr processHandle)
    {
        var capacity = 32768;
        var builder = new StringBuilder(capacity);
        return QueryFullProcessImageName(processHandle, 0, builder, ref capacity)
            ? builder.ToString()
            : null;
    }

    private static string? TryGetPackageFamilyName(IntPtr processHandle)
    {
        uint length = 0;
        var result = GetPackageFamilyName(processHandle, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(checked((int)length));
        result = GetPackageFamilyName(processHandle, ref length, builder);
        return result == 0 ? builder.ToString() : null;
    }

    private static string? TryGetPackageFullName(IntPtr processHandle)
    {
        uint length = 0;
        var result = GetPackageFullName(processHandle, ref length, null);
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(checked((int)length));
        result = GetPackageFullName(processHandle, ref length, builder);
        return result == 0 ? builder.ToString() : null;
    }

    private static bool TryGetLifetimeIdentity(
        Process process,
        out DateTimeOffset startTimeUtc,
        out int sessionId)
    {
        try
        {
            startTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            sessionId = process.SessionId;
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            startTimeUtc = default;
            sessionId = -1;
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "QueryFullProcessImageName writes into a caller-owned bounded character buffer.")]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "GetPackageFamilyName uses the documented two-call bounded buffer contract.")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(
        IntPtr processHandle,
        ref uint packageFamilyNameLength,
        StringBuilder? packageFamilyName);

    [SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "GetPackageFullName uses the documented two-call bounded buffer contract.")]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(
        IntPtr processHandle,
        ref uint packageFullNameLength,
        StringBuilder? packageFullName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

public sealed class WindowsTcpListenerSnapshotSource : ITcpListenerSnapshotSource
{
    private const int AddressFamilyInet = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidListener = 3;

    public IReadOnlyList<TcpListenerSnapshot> GetListeners()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var bufferSize = 0;
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            ref bufferSize,
            order: false,
            AddressFamilyInet,
            TcpTableOwnerPidListener,
            0);
        if (result != ErrorInsufficientBuffer || bufferSize <= sizeof(uint))
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                ref bufferSize,
                order: false,
                AddressFamilyInet,
                TcpTableOwnerPidListener,
                0);
            if (result != 0)
            {
                throw new Win32Exception(result, "Unable to enumerate TCP listeners.");
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var rowPointer = IntPtr.Add(buffer, sizeof(uint));
            var listeners = new List<TcpListenerSnapshot>(count);
            for (var index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                rowPointer = IntPtr.Add(rowPointer, rowSize);
                var address = new IPAddress(BitConverter.GetBytes(row.LocalAddress));
                var port = unchecked((ushort)IPAddress.NetworkToHostOrder((short)row.LocalPort));
                listeners.Add(new TcpListenerSnapshot(checked((int)row.OwningProcessId), address, port));
            }

            return listeners;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MibTcpRowOwnerPid
    {
        public readonly uint State;
        public readonly uint LocalAddress;
        public readonly uint LocalPort;
        public readonly uint RemoteAddress;
        public readonly uint RemotePort;
        public readonly uint OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int outBufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int ipVersion,
        int tableClass,
        uint reserved);
}
