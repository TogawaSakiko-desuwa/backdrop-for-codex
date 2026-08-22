using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BackdropForCodex.Core.Media;

internal sealed record WallpaperEngineWindowProcessIdentity
{
    internal WallpaperEngineWindowProcessIdentity(
        int processId,
        int sessionId,
        DateTimeOffset processStartTimeUtc,
        string processPath,
        bool executableTrusted)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        if (!Path.IsPathFullyQualified(processPath))
        {
            throw new ArgumentException(
                "A window process path must be fully qualified.",
                nameof(processPath));
        }

        ProcessId = processId;
        SessionId = sessionId;
        ProcessStartTimeUtc = processStartTimeUtc;
        ProcessPath = Path.GetFullPath(processPath);
        ExecutableTrusted = executableTrusted;
    }

    internal int ProcessId { get; }

    internal int SessionId { get; }

    internal DateTimeOffset ProcessStartTimeUtc { get; }

    internal string ProcessPath { get; }

    internal bool ExecutableTrusted { get; }

    public override string ToString() =>
        $"{nameof(WallpaperEngineWindowProcessIdentity)} {{ Process = <redacted>, " +
        $"ExecutableTrusted = {ExecutableTrusted} }}";
}

/// <summary>
/// Records the title-level window observation independently from the optional process proof.
/// Enumeration can therefore preserve an exact high-entropy title even when process identity
/// or Authenticode inspection is temporarily unavailable.
/// </summary>
internal sealed record WallpaperEngineWindowSnapshot
{
    internal WallpaperEngineWindowSnapshot(
        nint windowHandle,
        string title,
        bool isVisible,
        bool isTopLevel)
        : this(windowHandle, title, isVisible, isTopLevel, processIdentity: null)
    {
    }

    internal WallpaperEngineWindowSnapshot(
        nint windowHandle,
        string title,
        bool isVisible,
        bool isTopLevel,
        int processId,
        int sessionId,
        DateTimeOffset processStartTimeUtc,
        string processPath,
        bool executableTrusted)
        : this(
            windowHandle,
            title,
            isVisible,
            isTopLevel,
            new WallpaperEngineWindowProcessIdentity(
                processId,
                sessionId,
                processStartTimeUtc,
                processPath,
                executableTrusted))
    {
    }

    private WallpaperEngineWindowSnapshot(
        nint windowHandle,
        string title,
        bool isVisible,
        bool isTopLevel,
        WallpaperEngineWindowProcessIdentity? processIdentity)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        ArgumentNullException.ThrowIfNull(title);
        WindowHandle = windowHandle;
        Title = title;
        IsVisible = isVisible;
        IsTopLevel = isTopLevel;
        ProcessIdentity = processIdentity;
    }

    internal nint WindowHandle { get; }

    internal string Title { get; }

    internal bool IsVisible { get; }

    internal bool IsTopLevel { get; }

    internal WallpaperEngineWindowProcessIdentity? ProcessIdentity { get; }

    public override string ToString() =>
        $"{nameof(WallpaperEngineWindowSnapshot)} {{ Window = <redacted>, " +
        $"Process = <redacted>, IsVisible = {IsVisible}, IsTopLevel = {IsTopLevel}, " +
        $"HasProcessIdentity = {ProcessIdentity is not null}, " +
        $"ExecutableTrusted = {ProcessIdentity?.ExecutableTrusted == true} }}";
}

internal interface IWallpaperEngineWindowSnapshotSource
{
    ValueTask<IReadOnlyList<WallpaperEngineWindowSnapshot>> CaptureAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Proves one pop-out by combining an exact high-entropy title, a pre-open handle difference,
/// current-session process identity, install-root containment, start time, and Authenticode.
/// No single title, PID, or path is sufficient on its own.
/// </summary>
internal sealed class WindowsWallpaperEngineOwnedWindowVerifier
    : IWallpaperEngineOwnedWindowVerifier
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);
    private readonly IWallpaperEngineWindowSnapshotSource _snapshotSource;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _timeProvider;

    internal WindowsWallpaperEngineOwnedWindowVerifier()
        : this(
            new WindowsWallpaperEngineWindowSnapshotSource(
                new WindowsAuthenticodeTrustVerifier()),
            DefaultTimeout,
            DefaultPollInterval)
    {
    }

    internal WindowsWallpaperEngineOwnedWindowVerifier(
        IWallpaperEngineWindowSnapshotSource snapshotSource,
        TimeSpan timeout,
        TimeSpan pollInterval)
        : this(snapshotSource, timeout, pollInterval, TimeProvider.System)
    {
    }

    internal WindowsWallpaperEngineOwnedWindowVerifier(
        IWallpaperEngineWindowSnapshotSource snapshotSource,
        TimeSpan timeout,
        TimeSpan pollInterval,
        TimeProvider timeProvider)
    {
        _snapshotSource = snapshotSource ??
            throw new ArgumentNullException(nameof(snapshotSource));
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (pollInterval < TimeSpan.Zero || pollInterval > timeout)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }

        _timeout = timeout;
        _pollInterval = pollInterval;
    }

    public async ValueTask<WallpaperEngineWindowBaseline> CaptureBaselineAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        IReadOnlyList<WallpaperEngineWindowSnapshot> snapshots;
        try
        {
            snapshots = await _snapshotSource
                .CaptureAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw OwnershipUnavailable();
        }

        return new WallpaperEngineWindowBaseline(
            snapshots.Select(snapshot => snapshot.WindowHandle));
    }

    public async ValueTask<WallpaperEngineVerifiedWindow> WaitForOwnedWindowAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineOwnedWindowName windowName,
        WallpaperEngineWindowBaseline baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(baseline);
        EnsureOwnedWindowNameIsPresent(windowName);
        cancellationToken.ThrowIfCancellationRequested();
        var baselineHandles = baseline.WindowHandles.ToHashSet();
        var startedAt = _timeProvider.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WallpaperEngineWindowSnapshot> snapshots;
            try
            {
                snapshots = await _snapshotSource
                    .CaptureAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                throw OwnershipUnavailable();
            }
            var candidates = snapshots
                .Where(snapshot => IsAuthorizedCandidate(
                    snapshot,
                    installation,
                    windowName,
                    baselineHandles,
                    _timeProvider.GetUtcNow()))
                .ToArray();
            if (candidates.Length == 1)
            {
                var candidate = candidates[0];
                var processIdentity = candidate.ProcessIdentity ??
                    throw OwnershipUnavailable();
                return new WallpaperEngineVerifiedWindow(
                    candidate.WindowHandle,
                    processIdentity.ProcessId,
                    processIdentity.ProcessStartTimeUtc,
                    processIdentity.ProcessPath,
                    windowName);
            }

            if (candidates.Length > 1 || HasTimedOut(startedAt))
            {
                throw OwnershipUnavailable();
            }

            if (_pollInterval > TimeSpan.Zero)
            {
                await Task.Delay(
                    _pollInterval,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    public async ValueTask RevalidateOwnedWindowAsync(
        WallpaperEngineVerifiedWindow expectedWindow,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedWindow);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WallpaperEngineWindowSnapshot> snapshots;
        try
        {
            snapshots = await _snapshotSource
                .CaptureAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw OwnershipUnavailable();
        }

        var candidates = snapshots
            .Where(snapshot => snapshot.WindowHandle == expectedWindow.WindowHandle)
            .ToArray();
        if (candidates.Length != 1 ||
            !IdentifiesExpectedWindow(candidates[0], expectedWindow))
        {
            throw OwnershipUnavailable();
        }
    }

    public async ValueTask ConfirmOwnedWindowAbsentAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineOwnedWindowName windowName,
        WallpaperEngineWindowBaseline baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(baseline);
        EnsureOwnedWindowNameIsPresent(windowName);
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = _timeProvider.GetTimestamp();
        var absenceObserved = false;
        nint? closingWindowHandle = null;
        WallpaperEngineWindowProcessIdentity? closingProcessIdentity = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WallpaperEngineWindowSnapshot> snapshots;
            try
            {
                snapshots = await _snapshotSource
                    .CaptureAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                throw OwnershipUnavailable();
            }

            // Absence has a deliberately weaker observation threshold than ownership. Once the
            // exact high-entropy title is visible to EnumWindows, hidden state, missing process
            // metadata, or a failed trust read must not turn "present" into "absent". Wait and
            // revalidation still require the complete authorization proof below.
            //
            // Do not exclude baseline handles here: a destroyed baseline HWND can be reused.
            var ownedWindowCandidates = snapshots
                .Where(snapshot => string.Equals(
                    snapshot.Title,
                    windowName.Value,
                    StringComparison.Ordinal))
                .ToArray();
            if (ownedWindowCandidates.Length > 1)
            {
                throw OwnershipUnavailable();
            }

            var ownedWindowPresent = ownedWindowCandidates.Length == 1;
            if (ownedWindowPresent)
            {
                var currentWindow = ownedWindowCandidates[0];
                if (closingWindowHandle is { } expectedHandle &&
                    currentWindow.WindowHandle != expectedHandle)
                {
                    throw OwnershipUnavailable();
                }

                closingWindowHandle ??= currentWindow.WindowHandle;
                if (currentWindow.ProcessIdentity is { } currentProcessIdentity)
                {
                    if (closingProcessIdentity is not null &&
                        !IdentifiesSameProcess(
                            currentProcessIdentity,
                            closingProcessIdentity))
                    {
                        throw OwnershipUnavailable();
                    }

                    closingProcessIdentity ??= currentProcessIdentity;
                }
            }

            if (ownedWindowPresent && absenceObserved)
            {
                throw OwnershipUnavailable();
            }

            if (!ownedWindowPresent)
            {
                absenceObserved = true;
            }

            if (HasTimedOut(startedAt))
            {
                if (absenceObserved)
                {
                    return;
                }

                throw OwnershipUnavailable();
            }

            if (_pollInterval > TimeSpan.Zero)
            {
                await Task.Delay(
                    _pollInterval,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    private static WallpaperEnginePlatformUnavailableException OwnershipUnavailable() =>
        new(WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven);

    private static void EnsureOwnedWindowNameIsPresent(
        WallpaperEngineOwnedWindowName windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName.Value))
        {
            throw OwnershipUnavailable();
        }
    }

    private bool HasTimedOut(long startedAt) =>
        _timeProvider.GetElapsedTime(startedAt) >= _timeout;

    private static bool IdentifiesExpectedWindow(
        WallpaperEngineWindowSnapshot snapshot,
        WallpaperEngineVerifiedWindow expectedWindow) =>
        snapshot.ProcessIdentity is { } processIdentity &&
        snapshot.IsVisible &&
        snapshot.IsTopLevel &&
        processIdentity.SessionId == WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId &&
        processIdentity.ExecutableTrusted &&
        string.Equals(
            snapshot.Title,
            expectedWindow.WindowName.Value,
            StringComparison.Ordinal) &&
        processIdentity.ProcessId == expectedWindow.ProcessId &&
        processIdentity.ProcessStartTimeUtc == expectedWindow.ProcessStartTimeUtc &&
        string.Equals(
            processIdentity.ProcessPath,
            expectedWindow.ProcessPath,
            StringComparison.OrdinalIgnoreCase);

    private static bool IdentifiesSameProcess(
        WallpaperEngineWindowProcessIdentity current,
        WallpaperEngineWindowProcessIdentity expected) =>
        current.ProcessId == expected.ProcessId &&
        current.SessionId == expected.SessionId &&
        current.ProcessStartTimeUtc == expected.ProcessStartTimeUtc &&
        string.Equals(
            current.ProcessPath,
            expected.ProcessPath,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthorizedCandidate(
        WallpaperEngineWindowSnapshot snapshot,
        WallpaperEngineInstallation installation,
        WallpaperEngineOwnedWindowName windowName,
        HashSet<nint> baselineHandles,
        DateTimeOffset utcNow)
    {
        var processIdentity = snapshot.ProcessIdentity;
        if (baselineHandles.Contains(snapshot.WindowHandle) ||
            !string.Equals(snapshot.Title, windowName.Value, StringComparison.Ordinal) ||
            !snapshot.IsVisible ||
            !snapshot.IsTopLevel ||
            processIdentity is null ||
            processIdentity.SessionId != WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId ||
            processIdentity.ProcessStartTimeUtc == default ||
            processIdentity.ProcessStartTimeUtc > utcNow.AddSeconds(2) ||
            !processIdentity.ExecutableTrusted)
        {
            return false;
        }

        try
        {
            return WallpaperEngineLocalPath.IsContainedBy(
                installation.InstallRootPath,
                processIdentity.ProcessPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

internal sealed class WindowsWallpaperEngineWindowSnapshotSource
    : IWallpaperEngineWindowSnapshotSource
{
    private const uint GetAncestorRoot = 2;
    private const int MaximumWindowTitleLength =
        WallpaperEngineOwnedWindowName.MaximumLength;
    private readonly IWallpaperEngineExecutableTrustVerifier _trustVerifier;

    internal WindowsWallpaperEngineWindowSnapshotSource(
        IWallpaperEngineExecutableTrustVerifier trustVerifier)
    {
        _trustVerifier = trustVerifier ??
            throw new ArgumentNullException(nameof(trustVerifier));
    }

    internal static int CurrentSessionId
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return process.SessionId;
        }
    }

    public ValueTask<IReadOnlyList<WallpaperEngineWindowSnapshot>> CaptureAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<IReadOnlyList<WallpaperEngineWindowSnapshot>>([]);
        }

        var rawWindows = new List<RawWindow>();
        var callback = new EnumWindowsCallback((windowHandle, parameter) =>
        {
            _ = parameter;
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            var titleLength = GetWindowTextLength(windowHandle);
            if (titleLength is <= 0 or > MaximumWindowTitleLength)
            {
                return true;
            }

            var titleBuffer = new char[titleLength + 1];
            var copiedCharacterCount = GetWindowText(
                windowHandle,
                titleBuffer,
                titleBuffer.Length);
            if (copiedCharacterCount <= 0)
            {
                return true;
            }

            var titleText = new string(titleBuffer, 0, copiedCharacterCount);
            if (!titleText.StartsWith("BackdropForCodex-", StringComparison.Ordinal))
            {
                return true;
            }

            var threadId = GetWindowThreadProcessId(windowHandle, out var processId);
            int? capturedProcessId = threadId != 0 &&
                processId is > 0 && processId <= (uint)int.MaxValue
                    ? checked((int)processId)
                    : null;

            rawWindows.Add(
                new RawWindow(
                    windowHandle,
                    titleText,
                    IsWindowVisible(windowHandle),
                    GetAncestor(windowHandle, GetAncestorRoot) == windowHandle,
                    capturedProcessId));
            return true;
        });
        if (!EnumWindows(callback, nint.Zero))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to enumerate top-level windows.");
        }

        var snapshots = new List<WallpaperEngineWindowSnapshot>(rawWindows.Count);
        foreach (var rawWindow in rawWindows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new WallpaperEngineWindowSnapshot(
                rawWindow.WindowHandle,
                rawWindow.Title,
                rawWindow.IsVisible,
                rawWindow.IsTopLevel);
            if (rawWindow.ProcessId is not { } processId)
            {
                snapshots.Add(snapshot);
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                var processPath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(processPath) ||
                    !Path.IsPathFullyQualified(processPath))
                {
                    snapshots.Add(snapshot);
                    continue;
                }

                var processDirectory = Path.GetDirectoryName(processPath);
                if (string.IsNullOrWhiteSpace(processDirectory) ||
                    !string.Equals(
                        WallpaperEngineLocalPath.ValidateExistingRegularFile(
                            processPath,
                            processDirectory),
                        Path.GetFullPath(processPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    snapshots.Add(snapshot);
                    continue;
                }

                snapshot = new WallpaperEngineWindowSnapshot(
                    rawWindow.WindowHandle,
                    rawWindow.Title,
                    rawWindow.IsVisible,
                    rawWindow.IsTopLevel,
                    process.Id,
                    process.SessionId,
                    process.StartTime.ToUniversalTime(),
                    processPath,
                    _trustVerifier.IsTrusted(processPath));
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or
                    InvalidOperationException or
                    Win32Exception or NotSupportedException or
                    PlatformNotSupportedException or System.Security.SecurityException or
                    IOException or UnauthorizedAccessException)
            {
                // Keep the already observed handle/title. It cannot become an authorized
                // candidate without process proof, but it remains evidence against absence.
            }

            snapshots.Add(snapshot);
        }

        return ValueTask.FromResult<IReadOnlyList<WallpaperEngineWindowSnapshot>>(
            new ReadOnlyCollection<WallpaperEngineWindowSnapshot>(snapshots));
    }

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(
        nint windowHandle,
        [Out] char[] text,
        int maximumCharacterCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint windowHandle, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    private sealed record RawWindow(
        nint WindowHandle,
        string Title,
        bool IsVisible,
        bool IsTopLevel,
        int? ProcessId);
}
