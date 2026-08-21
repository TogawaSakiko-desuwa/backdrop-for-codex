using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BackdropForCodex.Core.Media;

internal interface IWallpaperEngineAudioSessionHandle : IAsyncDisposable
{
    int ProcessId { get; }

    DateTimeOffset ProcessStartTimeUtc { get; }

    string ProcessPath { get; }

    string SessionIdentifier { get; }

    string SessionInstanceIdentifier { get; }

    Guid GroupingParameter { get; }

    bool IsMuted { get; }

    void SetMuted(bool muted);
}

internal interface IWallpaperEngineAudioSessionSource
{
    ValueTask<IReadOnlyList<IWallpaperEngineAudioSessionHandle>> CaptureAsync(
        CancellationToken cancellationToken);
}

internal sealed record WallpaperEngineAudioProcessSnapshot
{
    internal WallpaperEngineAudioProcessSnapshot(
        int processId,
        DateTimeOffset processStartTimeUtc,
        string processPath)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        if (!Path.IsPathFullyQualified(processPath))
        {
            throw new ArgumentException(
                "The audio process path must be fully qualified.",
                nameof(processPath));
        }

        ProcessId = processId;
        ProcessStartTimeUtc = processStartTimeUtc.ToUniversalTime();
        ProcessPath = Path.GetFullPath(processPath);
    }

    internal int ProcessId { get; }

    internal DateTimeOffset ProcessStartTimeUtc { get; }

    internal string ProcessPath { get; }

    public override string ToString() =>
        $"{nameof(WallpaperEngineAudioProcessSnapshot)} {{ Identity = <redacted> }}";
}

internal interface IWallpaperEngineAudioProcessSource
{
    ValueTask<IReadOnlyList<WallpaperEngineAudioProcessSnapshot>> CaptureAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken);
}

internal interface IWallpaperEngineAudioTopology
{
    bool HasAnotherTopLevelWindow(int processId, nint ownedWindowHandle);
}

/// <summary>
/// Mutes only sessions proven to belong to the verified pop-out process. A process shared with
/// another top-level window, or a Core Audio grouping shared with another Wallpaper Engine
/// process, is rejected before any mute state is changed.
/// </summary>
internal sealed class WindowsWallpaperEngineAudioIsolation : IWallpaperEngineAudioIsolation
{
    private static readonly TimeSpan DefaultMonitorInterval = TimeSpan.FromMilliseconds(250);
    private readonly IWallpaperEngineAudioSessionSource _sessionSource;
    private readonly IWallpaperEngineAudioProcessSource _processSource;
    private readonly IWallpaperEngineAudioTopology _audioTopology;
    private readonly TimeSpan _monitorInterval;

    internal WindowsWallpaperEngineAudioIsolation()
        : this(
            new WindowsCoreAudioSessionSource(),
            new WindowsWallpaperEngineAudioProcessSource(),
            new WindowsWallpaperEngineAudioTopology(),
            DefaultMonitorInterval)
    {
    }

    internal WindowsWallpaperEngineAudioIsolation(
        IWallpaperEngineAudioSessionSource sessionSource,
        IWallpaperEngineAudioTopology audioTopology,
        TimeSpan monitorInterval)
        : this(
            sessionSource,
            new WindowsWallpaperEngineAudioProcessSource(),
            audioTopology,
            monitorInterval)
    {
    }

    internal WindowsWallpaperEngineAudioIsolation(
        IWallpaperEngineAudioSessionSource sessionSource,
        IWallpaperEngineAudioProcessSource processSource,
        IWallpaperEngineAudioTopology audioTopology,
        TimeSpan monitorInterval)
    {
        _sessionSource = sessionSource ?? throw new ArgumentNullException(nameof(sessionSource));
        _processSource = processSource ?? throw new ArgumentNullException(nameof(processSource));
        _audioTopology = audioTopology ?? throw new ArgumentNullException(nameof(audioTopology));
        if (monitorInterval < TimeSpan.Zero || monitorInterval > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(monitorInterval));
        }

        _monitorInterval = monitorInterval;
    }

    public ValueTask<IWallpaperEngineAudioBaseline> CaptureBaselineAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken) =>
        CaptureBaselineCoreAsync(installation, cancellationToken);

    private async ValueTask<IWallpaperEngineAudioBaseline> CaptureBaselineCoreAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<IWallpaperEngineAudioSessionHandle> sessions = [];
        WallpaperEngineAudioBaseline? baseline = null;
        try
        {
            sessions = await _sessionSource
                .CaptureAsync(cancellationToken)
                .ConfigureAwait(false);
            var processes = await _processSource
                .CaptureAsync(installation, cancellationToken)
                .ConfigureAwait(false);
            baseline = WallpaperEngineAudioBaseline.Create(
                installation,
                processes,
                sessions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Unavailable();
        }
        finally
        {
            var releaseFailed = await DisposeCapturedSessionsAsync(sessions)
                .ConfigureAwait(false);
            if (releaseFailed)
            {
                if (baseline is not null)
                {
                    await baseline.DisposeAsync().ConfigureAwait(false);
                }

                baseline = null;
            }
        }

        return baseline ?? throw Unavailable();
    }

    public async ValueTask<IWallpaperEngineMutedAudioLease> AcquireMutedSessionAsync(
        WallpaperEngineVerifiedWindow window,
        IWallpaperEngineAudioBaseline baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(baseline);
        cancellationToken.ThrowIfCancellationRequested();
        if (baseline is not WallpaperEngineAudioBaseline trustedBaseline ||
            trustedBaseline.ContainsProcess(window))
        {
            throw Unavailable();
        }

        IReadOnlyList<IWallpaperEngineAudioSessionHandle> captured;
        try
        {
            captured = await _sessionSource
                .CaptureAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Unavailable();
        }

        var selected = new List<IWallpaperEngineAudioSessionHandle>();
        try
        {
            selected.AddRange(SelectAuthorizedTargetSessions(window, captured));
            if (selected.Count != 1 ||
                trustedBaseline.ContainsSession(selected[0]) ||
                _audioTopology.HasAnotherTopLevelWindow(
                    window.ProcessId,
                    window.WindowHandle) ||
                SharesSessionWithAnotherWallpaperEngineProcess(window, selected, captured))
            {
                throw Unavailable();
            }

            foreach (var session in selected)
            {
                session.RememberOriginalMuteState();
                session.SetMuted(true);
            }

            foreach (var session in captured.Except(
                selected,
                WallpaperEngineAudioSessionReferenceComparer.Instance))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            return new WallpaperEngineMutedAudioLease(
                window,
                _sessionSource,
                _audioTopology,
                selected,
                _monitorInterval);
        }
        catch (OperationCanceledException)
        {
            await RestoreAndDisposeAsync(selected).ConfigureAwait(false);
            await DisposeExceptAsync(captured, selected).ConfigureAwait(false);
            throw;
        }
        catch (WallpaperEnginePlatformUnavailableException)
        {
            await RestoreAndDisposeAsync(selected).ConfigureAwait(false);
            await DisposeExceptAsync(captured, selected).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await RestoreAndDisposeAsync(selected).ConfigureAwait(false);
            await DisposeExceptAsync(captured, selected).ConfigureAwait(false);
            throw Unavailable();
        }
    }

    internal async ValueTask<IWallpaperEngineMutedAudioLease> AcquireMutedSessionAsync(
        WallpaperEngineVerifiedWindow window,
        CancellationToken cancellationToken)
    {
        await using var baseline = WallpaperEngineAudioBaseline.CreateEmpty();
        return await AcquireMutedSessionAsync(window, baseline, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static IReadOnlyList<IWallpaperEngineAudioSessionHandle>
        SelectAuthorizedTargetSessions(
            WallpaperEngineVerifiedWindow window,
            IReadOnlyList<IWallpaperEngineAudioSessionHandle> sessions) =>
        sessions
            .Where(session =>
                session.ProcessId == window.ProcessId &&
                PathsEqual(session.ProcessPath, window.ProcessPath) &&
                (session.ProcessStartTimeUtc - window.ProcessStartTimeUtc).Duration() <=
                    TimeSpan.FromSeconds(1))
            .ToArray();

    internal static bool SharesSessionWithAnotherWallpaperEngineProcess(
        WallpaperEngineVerifiedWindow window,
        IReadOnlyList<IWallpaperEngineAudioSessionHandle> targetSessions,
        IReadOnlyList<IWallpaperEngineAudioSessionHandle> allSessions)
    {
        var installDirectory = Path.GetDirectoryName(window.ProcessPath);
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return true;
        }

        foreach (var other in allSessions)
        {
            if (other.ProcessId == window.ProcessId ||
                !IsContainedBy(installDirectory, other.ProcessPath))
            {
                continue;
            }

            if (targetSessions.Any(target =>
                target.GroupingParameter == other.GroupingParameter ||
                string.Equals(
                    target.SessionIdentifier,
                    other.SessionIdentifier,
                    StringComparison.Ordinal) ||
                string.Equals(
                    target.SessionInstanceIdentifier,
                    other.SessionInstanceIdentifier,
                    StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsContainedBy(string root, string candidate)
    {
        try
        {
            return WallpaperEngineLocalPath.IsContainedBy(root, candidate);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static async ValueTask RestoreAndDisposeAsync(
        IEnumerable<IWallpaperEngineAudioSessionHandle> sessions)
    {
        foreach (var session in sessions)
        {
            try
            {
                session.SetMuted(session.OriginalMuteState());
            }
            catch (Exception)
            {
                // The audio gate remains unavailable; no raw Core Audio error is exposed.
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The gate is already unavailable; continue releasing every captured session.
            }
        }
    }

    private static async ValueTask DisposeExceptAsync(
        IEnumerable<IWallpaperEngineAudioSessionHandle> sessions,
        IEnumerable<IWallpaperEngineAudioSessionHandle> except)
    {
        var excluded = except.ToHashSet(
            WallpaperEngineAudioSessionReferenceComparer.Instance);
        foreach (var session in sessions)
        {
            if (!excluded.Contains(session))
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // No captured non-target session is retained as authorization evidence.
                }
            }
        }
    }

    private static async ValueTask<bool> DisposeCapturedSessionsAsync(
        IEnumerable<IWallpaperEngineAudioSessionHandle> sessions)
    {
        var failed = false;
        foreach (var session in sessions)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                failed = true;
            }
        }

        return failed;
    }

    private static WallpaperEnginePlatformUnavailableException Unavailable() =>
        new(WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven);
}

internal sealed class WallpaperEngineAudioBaseline : IWallpaperEngineAudioBaseline
{
    private const string ProcessFingerprintDomain = "process";
    private const string SessionIdentifierFingerprintDomain = "session-id";
    private const string SessionInstanceFingerprintDomain = "session-instance";
    private const string GroupingFingerprintDomain = "grouping";
    private readonly object _sync = new();
    private byte[]? _key;
    private readonly HashSet<string> _processFingerprints;
    private readonly HashSet<string> _sessionIdentifierFingerprints;
    private readonly HashSet<string> _sessionInstanceFingerprints;
    private readonly HashSet<string> _groupingFingerprints;

    private WallpaperEngineAudioBaseline(
        byte[] key,
        HashSet<string> processFingerprints,
        HashSet<string> sessionIdentifierFingerprints,
        HashSet<string> sessionInstanceFingerprints,
        HashSet<string> groupingFingerprints)
    {
        _key = key;
        _processFingerprints = processFingerprints;
        _sessionIdentifierFingerprints = sessionIdentifierFingerprints;
        _sessionInstanceFingerprints = sessionInstanceFingerprints;
        _groupingFingerprints = groupingFingerprints;
    }

    public bool InitialSilenceIsProven => false;

    internal static WallpaperEngineAudioBaseline Create(
        WallpaperEngineInstallation installation,
        IReadOnlyList<WallpaperEngineAudioProcessSnapshot> processes,
        IReadOnlyList<IWallpaperEngineAudioSessionHandle> sessions)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(sessions);
        var key = RandomNumberGenerator.GetBytes(32);
        var baseline = new WallpaperEngineAudioBaseline(
            key,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
        try
        {
            foreach (var process in processes.Where(
                process => IsWallpaperEnginePath(
                    installation.InstallRootPath,
                    process.ProcessPath)))
            {
                baseline._processFingerprints.Add(
                    baseline.FingerprintProcess(
                        process.ProcessId,
                        process.ProcessPath));
            }

            foreach (var session in sessions.Where(
                session => IsWallpaperEnginePath(
                    installation.InstallRootPath,
                    session.ProcessPath)))
            {
                baseline._processFingerprints.Add(
                    baseline.FingerprintProcess(
                        session.ProcessId,
                        session.ProcessPath));
                baseline._sessionIdentifierFingerprints.Add(
                    baseline.Fingerprint(
                        SessionIdentifierFingerprintDomain,
                        session.SessionIdentifier));
                baseline._sessionInstanceFingerprints.Add(
                    baseline.Fingerprint(
                        SessionInstanceFingerprintDomain,
                        session.SessionInstanceIdentifier));
                baseline._groupingFingerprints.Add(
                    baseline.Fingerprint(
                        GroupingFingerprintDomain,
                        session.GroupingParameter.ToString("D")));
            }

            return baseline;
        }
        catch
        {
            baseline.DisposeCore();
            throw;
        }
    }

    internal static WallpaperEngineAudioBaseline CreateEmpty() =>
        new(
            RandomNumberGenerator.GetBytes(32),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

    internal bool ContainsProcess(WallpaperEngineVerifiedWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_sync)
        {
            ThrowIfDisposed();
            return _processFingerprints.Contains(
                FingerprintProcess(
                    window.ProcessId,
                    window.ProcessPath));
        }
    }

    internal bool ContainsSession(IWallpaperEngineAudioSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            ThrowIfDisposed();
            return _sessionIdentifierFingerprints.Contains(
                    Fingerprint(
                        SessionIdentifierFingerprintDomain,
                        session.SessionIdentifier)) ||
                _sessionInstanceFingerprints.Contains(
                    Fingerprint(
                        SessionInstanceFingerprintDomain,
                        session.SessionInstanceIdentifier)) ||
                _groupingFingerprints.Contains(
                    Fingerprint(
                        GroupingFingerprintDomain,
                        session.GroupingParameter.ToString("D")));
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        lock (_sync)
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
            _processFingerprints.Clear();
            _sessionIdentifierFingerprints.Clear();
            _sessionInstanceFingerprints.Clear();
            _groupingFingerprints.Clear();
        }
    }

    public override string ToString() =>
        $"{nameof(WallpaperEngineAudioBaseline)} {{ Identities = <redacted> }}";

    private string FingerprintProcess(
        int processId,
        string processPath) =>
        Fingerprint(
            ProcessFingerprintDomain,
            processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            NormalizePath(processPath));

    private string Fingerprint(string domain, params string[] values)
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(WallpaperEngineAudioBaseline));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(domain);
            writer.Write(values.Length);
            foreach (var value in values)
            {
                writer.Write(value ?? string.Empty);
            }
        }

        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(stream.GetBuffer(), 0, checked((int)stream.Length)));
    }

    private static bool IsWallpaperEnginePath(string installRootPath, string candidatePath)
    {
        try
        {
            return WallpaperEngineLocalPath.IsContainedBy(installRootPath, candidatePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_key is null, this);
}

internal sealed class WindowsWallpaperEngineAudioProcessSource
    : IWallpaperEngineAudioProcessSource
{
    private const uint QueryLimitedInformation = 0x1000;
    private const int MaximumPathCharacters = 32768;

    public ValueTask<IReadOnlyList<WallpaperEngineAudioProcessSnapshot>> CaptureAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var snapshots = new List<WallpaperEngineAudioProcessSnapshot>();
        using var current = Process.GetCurrentProcess();
        var currentSessionId = current.SessionId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int processId;
                int sessionId;
                try
                {
                    processId = process.Id;
                    sessionId = process.SessionId;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (sessionId != currentSessionId)
                {
                    continue;
                }

                var processPath = TryGetProcessPath(processId);
                if (processPath is null)
                {
                    if (HasExited(process))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        "A current-session process identity could not be proven.");
                }

                if (!WallpaperEngineLocalPath.IsContainedBy(
                    installation.InstallRootPath,
                    processPath))
                {
                    continue;
                }

                DateTimeOffset startTimeUtc;
                try
                {
                    startTimeUtc = process.StartTime.ToUniversalTime();
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                snapshots.Add(new WallpaperEngineAudioProcessSnapshot(
                    processId,
                    startTimeUtc,
                    processPath));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<WallpaperEngineAudioProcessSnapshot>>(
            new ReadOnlyCollection<WallpaperEngineAudioProcessSnapshot>(snapshots));
    }

    private static string? TryGetProcessPath(int processId)
    {
        using var processHandle = OpenProcess(
            QueryLimitedInformation,
            inheritHandle: false,
            checked((uint)processId));
        if (processHandle.IsInvalid)
        {
            return null;
        }

        var path = new char[MaximumPathCharacters];
        var pathLength = checked((uint)path.Length);
        return QueryFullProcessImageName(processHandle, 0, path, ref pathLength)
            ? new string(path, 0, checked((int)pathLength))
            : null;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle processHandle,
        uint flags,
        [Out] char[] executablePath,
        ref uint executablePathLength);
}

internal static class WallpaperEngineAudioSessionHandleExtensions
{
    private static readonly ConditionalWeakTable<
        IWallpaperEngineAudioSessionHandle,
        OriginalMuteStateHolder> OriginalStates = new();

    internal static void RememberOriginalMuteState(
        this IWallpaperEngineAudioSessionHandle session)
    {
        _ = OriginalStates.GetValue(
            session,
            key => new OriginalMuteStateHolder(key.IsMuted));
    }

    internal static bool OriginalMuteState(this IWallpaperEngineAudioSessionHandle session) =>
        OriginalStates.TryGetValue(session, out var holder) ? holder.Value : session.IsMuted;

    private sealed record OriginalMuteStateHolder(bool Value);
}

internal sealed class WallpaperEngineMutedAudioLease : IWallpaperEngineMutedAudioLease
{
    private readonly object _sync = new();
    private readonly WallpaperEngineVerifiedWindow _window;
    private readonly IWallpaperEngineAudioSessionSource _sessionSource;
    private readonly IWallpaperEngineAudioTopology _audioTopology;
    private readonly TimeSpan _monitorInterval;
    private readonly CancellationTokenSource _monitorCancellation = new();
    private readonly Dictionary<string, IWallpaperEngineAudioSessionHandle> _ownedSessions;
    private readonly TaskCompletionSource<WallpaperEnginePlatformUnavailableReason>
        _isolationLost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _monitorTask;
    private int _disposed;
    private int _isolationFailed;

    internal WallpaperEngineMutedAudioLease(
        WallpaperEngineVerifiedWindow window,
        IWallpaperEngineAudioSessionSource sessionSource,
        IWallpaperEngineAudioTopology audioTopology,
        IEnumerable<IWallpaperEngineAudioSessionHandle> initialSessions,
        TimeSpan monitorInterval)
    {
        _window = window;
        _sessionSource = sessionSource;
        _audioTopology = audioTopology;
        _monitorInterval = monitorInterval;
        _ownedSessions = new Dictionary<string, IWallpaperEngineAudioSessionHandle>(
            StringComparer.Ordinal);
        foreach (var session in initialSessions)
        {
            session.RememberOriginalMuteState();
            if (!_ownedSessions.TryAdd(session.SessionInstanceIdentifier, session))
            {
                throw new WallpaperEnginePlatformUnavailableException(
                    WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven);
            }
        }

        _monitorTask = MonitorAsync();
    }

    public Task<WallpaperEnginePlatformUnavailableReason> IsolationLost =>
        _isolationLost.Task;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _monitorCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await _monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when caller ends the lease.
        }

        await RestoreOwnedSessionsAsync().ConfigureAwait(false);
        _monitorCancellation.Dispose();
    }

    public override string ToString() =>
        $"{nameof(WallpaperEngineMutedAudioLease)} {{ Window = <redacted>, " +
        "Sessions = <redacted> }}";

    private async Task MonitorAsync()
    {
        while (!_monitorCancellation.IsCancellationRequested)
        {
            if (_monitorInterval > TimeSpan.Zero)
            {
                await Task.Delay(_monitorInterval, _monitorCancellation.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            IReadOnlyList<IWallpaperEngineAudioSessionHandle> captured;
            try
            {
                captured = await _sessionSource
                    .CaptureAsync(_monitorCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                await FailClosedAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                var targets = WindowsWallpaperEngineAudioIsolation
                    .SelectAuthorizedTargetSessions(_window, captured);
                var authorizedSession = GetAuthorizedSession();
                if (targets.Count != 1 ||
                    !IdentifiesAuthorizedSession(targets[0], authorizedSession) ||
                    _audioTopology.HasAnotherTopLevelWindow(
                        _window.ProcessId,
                        _window.WindowHandle) ||
                    WindowsWallpaperEngineAudioIsolation
                        .SharesSessionWithAnotherWallpaperEngineProcess(
                            _window,
                            targets,
                            captured))
                {
                    await FailClosedAsync().ConfigureAwait(false);
                    await DisposeCapturedExceptOwnedAsync(captured).ConfigureAwait(false);
                    return;
                }

                var observedAuthorizedSession = targets[0];
                if (!observedAuthorizedSession.IsMuted)
                {
                    try
                    {
                        observedAuthorizedSession.SetMuted(true);
                    }
                    catch (Exception)
                    {
                        // Isolation is lost below; retain the originally owned mute handle.
                    }

                    await FailClosedAsync().ConfigureAwait(false);
                    await DisposeCapturedExceptOwnedAsync(captured).ConfigureAwait(false);
                    return;
                }

                foreach (var session in captured)
                {
                    if (!ReferenceEquals(session, authorizedSession))
                    {
                        await session.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await FailClosedAsync().ConfigureAwait(false);
                await DisposeCapturedExceptOwnedAsync(captured).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                await FailClosedAsync().ConfigureAwait(false);
                await DisposeCapturedExceptOwnedAsync(captured).ConfigureAwait(false);
                return;
            }
        }
    }

    private ValueTask FailClosedAsync()
    {
        if (Interlocked.Exchange(ref _isolationFailed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _isolationLost.TrySetResult(
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven);
        return ValueTask.CompletedTask;
    }

    private async ValueTask RestoreOwnedSessionsAsync()
    {
        IWallpaperEngineAudioSessionHandle[] sessions;
        lock (_sync)
        {
            sessions = _ownedSessions.Values.ToArray();
            _ownedSessions.Clear();
        }

        foreach (var session in sessions)
        {
            try
            {
                session.SetMuted(session.OriginalMuteState());
            }
            catch (Exception)
            {
                // Continue restoring and releasing every session.
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cleanup remains best-effort after the gate has already failed closed.
            }
        }
    }

    private IWallpaperEngineAudioSessionHandle GetAuthorizedSession()
    {
        lock (_sync)
        {
            if (_ownedSessions.Count != 1)
            {
                throw new WallpaperEnginePlatformUnavailableException(
                    WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven);
            }

            return _ownedSessions.Values.Single();
        }
    }

    private static bool IdentifiesAuthorizedSession(
        IWallpaperEngineAudioSessionHandle observed,
        IWallpaperEngineAudioSessionHandle authorized) =>
        observed.ProcessId == authorized.ProcessId &&
        observed.ProcessStartTimeUtc == authorized.ProcessStartTimeUtc &&
        WindowsWallpaperEngineAudioIsolation.PathsEqual(
            observed.ProcessPath,
            authorized.ProcessPath) &&
        observed.GroupingParameter == authorized.GroupingParameter &&
        string.Equals(
            observed.SessionIdentifier,
            authorized.SessionIdentifier,
            StringComparison.Ordinal) &&
        string.Equals(
            observed.SessionInstanceIdentifier,
            authorized.SessionInstanceIdentifier,
            StringComparison.Ordinal);

    private async ValueTask DisposeCapturedExceptOwnedAsync(
        IEnumerable<IWallpaperEngineAudioSessionHandle> sessions)
    {
        IWallpaperEngineAudioSessionHandle? authorizedSession;
        lock (_sync)
        {
            authorizedSession = _ownedSessions.Count == 1
                ? _ownedSessions.Values.Single()
                : null;
        }

        foreach (var session in sessions)
        {
            if (ReferenceEquals(session, authorizedSession))
            {
                continue;
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Captured handles carry no authorization after this iteration.
            }
        }
    }

}

internal sealed class WallpaperEngineAudioSessionReferenceComparer
    : IEqualityComparer<IWallpaperEngineAudioSessionHandle>
{
    internal static WallpaperEngineAudioSessionReferenceComparer Instance { get; } = new();

    public bool Equals(
        IWallpaperEngineAudioSessionHandle? x,
        IWallpaperEngineAudioSessionHandle? y) =>
        ReferenceEquals(x, y);

    public int GetHashCode(IWallpaperEngineAudioSessionHandle obj) =>
        RuntimeHelpers.GetHashCode(obj);
}

internal sealed class WindowsWallpaperEngineAudioTopology : IWallpaperEngineAudioTopology
{
    public bool HasAnotherTopLevelWindow(int processId, nint ownedWindowHandle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var foundAnother = false;
        var callback = new EnumWindowsCallback((windowHandle, parameter) =>
        {
            _ = parameter;
            var threadId = GetWindowThreadProcessId(windowHandle, out var ownerProcessId);
            if (threadId != 0 && IsAnotherOwnedProcessWindow(
                    windowHandle,
                    ownerProcessId,
                    processId,
                    ownedWindowHandle))
            {
                foundAnother = true;
                return false;
            }

            return true;
        });
        var completed = EnumWindows(callback, nint.Zero);
        return foundAnother || !completed;
    }

    internal static bool IsAnotherOwnedProcessWindow(
        nint candidateHandle,
        uint candidateProcessId,
        int expectedProcessId,
        nint ownedWindowHandle) =>
        candidateProcessId == checked((uint)expectedProcessId) &&
        candidateHandle != ownedWindowHandle;

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);
}
