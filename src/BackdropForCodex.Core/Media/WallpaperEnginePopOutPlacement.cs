using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace BackdropForCodex.Core.Media;

internal readonly record struct WallpaperEngineWindowRect
{
    internal WallpaperEngineWindowRect(int left, int top, int right, int bottom)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(right, left);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bottom, top);

        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    internal int Left { get; }

    internal int Top { get; }

    internal int Right { get; }

    internal int Bottom { get; }

    internal int Width => checked(Right - Left);

    internal int Height => checked(Bottom - Top);
}

internal sealed record WallpaperEngineNativeWindowState
{
    internal WallpaperEngineNativeWindowState(
        nint windowHandle,
        string title,
        int processId,
        DateTimeOffset processStartTimeUtc,
        string processPath,
        WallpaperEngineWindowRect rect,
        uint style,
        uint extendedStyle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfEqual(processStartTimeUtc, default);

        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        if (!Path.IsPathFullyQualified(processPath))
        {
            throw new ArgumentException(
                "The renderer process path must be fully qualified.",
                nameof(processPath));
        }

        WindowHandle = windowHandle;
        Title = title;
        ProcessId = processId;
        ProcessStartTimeUtc = processStartTimeUtc;
        ProcessPath = Path.GetFullPath(processPath);
        Rect = rect;
        Style = style;
        ExtendedStyle = extendedStyle;
    }

    internal nint WindowHandle { get; init; }

    internal string Title { get; init; }

    internal int ProcessId { get; init; }

    internal DateTimeOffset ProcessStartTimeUtc { get; init; }

    internal string ProcessPath { get; init; }

    internal WallpaperEngineWindowRect Rect { get; init; }

    internal uint Style { get; init; }

    internal uint ExtendedStyle { get; init; }

    public override string ToString() =>
        $"{nameof(WallpaperEngineNativeWindowState)} {{ Window = <redacted>, " +
        "Process = <redacted>, Rect = <redacted>, Styles = <redacted> }";
}

[Flags]
internal enum WallpaperEngineSetWindowPositionFlags : uint
{
    None = 0,
    NoActivate = 0x0010,
    FrameChanged = 0x0020,
    NoOwnerZOrder = 0x0200,
}

internal interface IWallpaperEngineWindowPlacementNativeApi
{
    WallpaperEngineWindowRect GetVirtualScreen();

    nint GetForegroundWindow();

    bool DoesWindowExist(nint windowHandle);

    WallpaperEngineNativeWindowState CaptureWindowState(nint windowHandle);

    WallpaperEngineWindowRect GetClientRect(nint windowHandle);

    void SetWindowPosition(
        nint windowHandle,
        nint insertAfter,
        WallpaperEngineWindowRect rect,
        WallpaperEngineSetWindowPositionFlags flags);

    void SetWindowStyles(nint windowHandle, uint style, uint extendedStyle);
}

internal interface IWallpaperEnginePopOutPlacementLease : IAsyncDisposable
{
    /// <summary>
    /// Faults if the verified window loses its identity or safe edge placement. A deliberate
    /// close or disposal cancels the monitor instead.
    /// </summary>
    Task Completion { get; }

    ValueTask MarkClosedAsync(CancellationToken cancellationToken = default);
}

internal interface IWallpaperEnginePopOutHealthScheduler
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal interface IWallpaperEnginePopOutPlacement
{
    WallpaperEngineWindowPlacement GetInitialPlacement(
        WallpaperEngineWindowOptions options);

    ValueTask<IWallpaperEnginePopOutPlacementLease> PlaceAsync(
        WallpaperEngineVerifiedWindow verifiedWindow,
        WallpaperEngineWindowOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Moves only an already verified Wallpaper Engine pop-out to a one-pixel virtual-screen edge,
/// then continuously revalidates its identity, focus, geometry, and non-topmost state.
/// </summary>
internal sealed class WindowsWallpaperEnginePopOutPlacement
    : IWallpaperEnginePopOutPlacement
{
    private const uint WindowStyleNonClientMask = 0x00CF0000;
    private const uint ExtendedStyleTopMost = 0x00000008;
    private const uint ExtendedStyleNonClientMask = 0x00000301;
    private static readonly nint WindowBottom = (nint)1;
    private static readonly nint WindowTopMost = (nint)(-1);
    private static readonly nint WindowNotTopMost = (nint)(-2);
    private static readonly TimeSpan DefaultCloseConfirmationTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultClosePollInterval =
        TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan DefaultHealthPollInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumHealthPollInterval =
        TimeSpan.FromMilliseconds(100);
    private readonly IWallpaperEngineWindowPlacementNativeApi _nativeApi;
    private readonly TimeSpan _closeConfirmationTimeout;
    private readonly TimeSpan _closePollInterval;
    private readonly TimeSpan _healthPollInterval;
    private readonly IWallpaperEnginePopOutHealthScheduler _healthScheduler;

    internal WindowsWallpaperEnginePopOutPlacement()
        : this(
            new WindowsWallpaperEngineWindowPlacementNativeApi(),
            DefaultCloseConfirmationTimeout,
            DefaultClosePollInterval,
            DefaultHealthPollInterval,
            new TaskDelayHealthScheduler())
    {
    }

    internal WindowsWallpaperEnginePopOutPlacement(
        IWallpaperEngineWindowPlacementNativeApi nativeApi)
        : this(
            nativeApi,
            DefaultCloseConfirmationTimeout,
            DefaultClosePollInterval,
            DefaultHealthPollInterval,
            new TaskDelayHealthScheduler())
    {
    }

    internal WindowsWallpaperEnginePopOutPlacement(
        IWallpaperEngineWindowPlacementNativeApi nativeApi,
        TimeSpan closeConfirmationTimeout,
        TimeSpan closePollInterval)
        : this(
            nativeApi,
            closeConfirmationTimeout,
            closePollInterval,
            DefaultHealthPollInterval,
            new TaskDelayHealthScheduler())
    {
    }

    internal WindowsWallpaperEnginePopOutPlacement(
        IWallpaperEngineWindowPlacementNativeApi nativeApi,
        TimeSpan closeConfirmationTimeout,
        TimeSpan closePollInterval,
        TimeSpan healthPollInterval,
        IWallpaperEnginePopOutHealthScheduler healthScheduler)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        _healthScheduler = healthScheduler ??
            throw new ArgumentNullException(nameof(healthScheduler));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            closeConfirmationTimeout,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            closeConfirmationTimeout,
            TimeSpan.FromSeconds(10));
        ArgumentOutOfRangeException.ThrowIfLessThan(closePollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            closePollInterval,
            closeConfirmationTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            healthPollInterval,
            MinimumHealthPollInterval);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            healthPollInterval,
            TimeSpan.FromSeconds(5));
        _closeConfirmationTimeout = closeConfirmationTimeout;
        _closePollInterval = closePollInterval;
        _healthPollInterval = healthPollInterval;
    }

    public WallpaperEngineWindowPlacement GetInitialPlacement(
        WallpaperEngineWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            var virtualScreen = _nativeApi.GetVirtualScreen();
            return new WallpaperEngineWindowPlacement(
                checked(virtualScreen.Left - options.Width + 1),
                virtualScreen.Top);
        }
        catch (Exception exception)
        {
            throw PlacementUnavailable(exception);
        }
    }

    public ValueTask<IWallpaperEnginePopOutPlacementLease> PlaceAsync(
        WallpaperEngineVerifiedWindow verifiedWindow,
        WallpaperEngineWindowOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifiedWindow);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        WallpaperEngineNativeWindowState original;
        WallpaperEngineWindowRect target;
        WallpaperEngineWindowRect targetClientRect;
        uint targetStyle;
        uint targetExtendedStyle;
        try
        {
            original = _nativeApi.CaptureWindowState(verifiedWindow.WindowHandle);
            EnsureSameIdentity(verifiedWindow, original);
            if (_nativeApi.GetForegroundWindow() == verifiedWindow.WindowHandle)
            {
                throw new InvalidOperationException(
                    "An active pop-out cannot be moved without interfering with the user.");
            }

            target = CreateTargetRect(
                options.Width,
                options.Height,
                _nativeApi.GetVirtualScreen());
            targetClientRect = CreateClientRect(options.Width, options.Height);
            targetStyle = original.Style & ~WindowStyleNonClientMask;
            targetExtendedStyle = original.ExtendedStyle &
                ~(ExtendedStyleTopMost | ExtendedStyleNonClientMask);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw PlacementUnavailable(exception);
        }

        try
        {
            _nativeApi.SetWindowStyles(
                verifiedWindow.WindowHandle,
                targetStyle,
                targetExtendedStyle);
            _nativeApi.SetWindowPosition(
                verifiedWindow.WindowHandle,
                WindowBottom,
                target,
                WallpaperEngineSetWindowPositionFlags.NoActivate |
                    WallpaperEngineSetWindowPositionFlags.NoOwnerZOrder |
                    WallpaperEngineSetWindowPositionFlags.FrameChanged);
            EnsurePlaced(
                verifiedWindow,
                target,
                targetClientRect,
                targetStyle,
                targetExtendedStyle);
            var lease = new PlacementLease(
                _nativeApi,
                verifiedWindow,
                original,
                target,
                targetClientRect,
                targetStyle,
                targetExtendedStyle,
                _closeConfirmationTimeout,
                _closePollInterval,
                _healthPollInterval,
                _healthScheduler);
            lease.StartHealthMonitor();
            return ValueTask.FromResult<IWallpaperEnginePopOutPlacementLease>(
                lease);
        }
        catch (Exception placementFailure)
        {
            var failure = TryRestoreAfterFailedPlacement(
                _nativeApi,
                verifiedWindow,
                original,
                placementFailure);
            throw PlacementUnavailable(failure);
        }
    }

    private void EnsurePlaced(
        WallpaperEngineVerifiedWindow verifiedWindow,
        WallpaperEngineWindowRect target,
        WallpaperEngineWindowRect targetClientRect,
        uint targetStyle,
        uint targetExtendedStyle)
    {
        var placed = _nativeApi.CaptureWindowState(verifiedWindow.WindowHandle);
        EnsureSameIdentity(verifiedWindow, placed);
        if (_nativeApi.GetForegroundWindow() == verifiedWindow.WindowHandle ||
            placed.Rect != target ||
            _nativeApi.GetClientRect(verifiedWindow.WindowHandle) != targetClientRect ||
            placed.Style != targetStyle ||
            placed.ExtendedStyle != targetExtendedStyle)
        {
            throw new InvalidOperationException(
                "The pop-out placement did not produce the requested capture surface.");
        }
    }

    private static WallpaperEngineWindowRect CreateTargetRect(
        int width,
        int height,
        WallpaperEngineWindowRect virtualScreen)
    {
        var left = checked(virtualScreen.Left - width + 1);
        var top = virtualScreen.Top;
        return new WallpaperEngineWindowRect(
            left,
            top,
            checked(left + width),
            checked(top + height));
    }

    private static WallpaperEngineWindowRect CreateClientRect(int width, int height) =>
        new(0, 0, width, height);

    private static void EnsureSameIdentity(
        WallpaperEngineVerifiedWindow verifiedWindow,
        WallpaperEngineNativeWindowState state)
    {
        if (state.WindowHandle != verifiedWindow.WindowHandle ||
            !string.Equals(
                state.Title,
                verifiedWindow.WindowName.Value,
                StringComparison.Ordinal) ||
            state.ProcessId != verifiedWindow.ProcessId ||
            state.ProcessStartTimeUtc != verifiedWindow.ProcessStartTimeUtc ||
            !string.Equals(
                state.ProcessPath,
                verifiedWindow.ProcessPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WallpaperEnginePlatformUnavailableException(
                WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven);
        }
    }

    private static Exception TryRestoreAfterFailedPlacement(
        IWallpaperEngineWindowPlacementNativeApi nativeApi,
        WallpaperEngineVerifiedWindow verifiedWindow,
        WallpaperEngineNativeWindowState original,
        Exception placementFailure)
    {
        try
        {
            Restore(nativeApi, verifiedWindow, original);
            return placementFailure;
        }
        catch (Exception rollbackFailure)
        {
            return new AggregateException(
                "Pop-out placement and rollback both failed.",
                placementFailure,
                rollbackFailure);
        }
    }

    private static void Restore(
        IWallpaperEngineWindowPlacementNativeApi nativeApi,
        WallpaperEngineVerifiedWindow verifiedWindow,
        WallpaperEngineNativeWindowState original)
    {
        var current = nativeApi.CaptureWindowState(verifiedWindow.WindowHandle);
        EnsureSameIdentity(verifiedWindow, current);
        var failures = new List<Exception>();
        try
        {
            nativeApi.SetWindowStyles(
                verifiedWindow.WindowHandle,
                original.Style,
                original.ExtendedStyle);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            nativeApi.SetWindowPosition(
                verifiedWindow.WindowHandle,
                (original.ExtendedStyle & ExtendedStyleTopMost) != 0
                    ? WindowTopMost
                    : WindowNotTopMost,
                original.Rect,
                WallpaperEngineSetWindowPositionFlags.NoActivate |
                    WallpaperEngineSetWindowPositionFlags.NoOwnerZOrder |
                    WallpaperEngineSetWindowPositionFlags.FrameChanged);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            var restored = nativeApi.CaptureWindowState(verifiedWindow.WindowHandle);
            EnsureSameIdentity(verifiedWindow, restored);
            if (restored.Rect != original.Rect ||
                restored.Style != original.Style ||
                restored.ExtendedStyle != original.ExtendedStyle)
            {
                throw new InvalidOperationException(
                    "The pop-out window state could not be restored.");
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Pop-out rollback encountered multiple failures.",
                failures);
        }
    }

    private static WallpaperEnginePlatformUnavailableException PlacementUnavailable(
        Exception innerException)
    {
        if (innerException is WallpaperEnginePlatformUnavailableException
            {
                Reason: WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            } ownershipFailure)
        {
            return ownershipFailure;
        }

        return ContainsOwnershipFailure(innerException)
            ? new WallpaperEnginePlatformUnavailableException(
                WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven)
            : new WallpaperEnginePlatformUnavailableException(
                WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
                innerException);
    }

    private static bool ContainsOwnershipFailure(Exception exception)
    {
        if (exception is WallpaperEnginePlatformUnavailableException
            {
                Reason: WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            })
        {
            return true;
        }

        if (exception is AggregateException aggregateException &&
            aggregateException.InnerExceptions.Any(ContainsOwnershipFailure))
        {
            return true;
        }

        return exception.InnerException is not null &&
            ContainsOwnershipFailure(exception.InnerException);
    }

    private sealed class PlacementLease(
        IWallpaperEngineWindowPlacementNativeApi nativeApi,
        WallpaperEngineVerifiedWindow verifiedWindow,
        WallpaperEngineNativeWindowState original,
        WallpaperEngineWindowRect target,
        WallpaperEngineWindowRect targetClientRect,
        uint targetStyle,
        uint targetExtendedStyle,
        TimeSpan closeConfirmationTimeout,
        TimeSpan closePollInterval,
        TimeSpan healthPollInterval,
        IWallpaperEnginePopOutHealthScheduler healthScheduler)
        : IWallpaperEnginePopOutPlacementLease
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _healthCancellation = new();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _healthMonitor;
        private bool _disarmed;
        private bool _disposed;

        public Task Completion => _completion.Task;

        internal void StartHealthMonitor() =>
            _healthMonitor = MonitorHealthAsync();

        public async ValueTask MarkClosedAsync(
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_disarmed)
                {
                    return;
                }

                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    while (nativeApi.DoesWindowExist(verifiedWindow.WindowHandle))
                    {
                        if (stopwatch.Elapsed >= closeConfirmationTimeout)
                        {
                            throw new InvalidOperationException(
                                "The exact pop-out window remained present after close.");
                        }

                        if (closePollInterval > TimeSpan.Zero)
                        {
                            await Task.Delay(closePollInterval, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.Yield();
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    _disarmed = true;
                    await StopHealthMonitorAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (WallpaperEnginePlatformUnavailableException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw PlacementUnavailable(exception);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                await StopHealthMonitorAsync().ConfigureAwait(false);
                if (_disarmed)
                {
                    return;
                }

                try
                {
                    Restore(nativeApi, verifiedWindow, original);
                }
                catch (Exception exception)
                {
                    throw PlacementUnavailable(exception);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task StopHealthMonitorAsync()
        {
            _healthCancellation.Cancel();
            var healthMonitor = _healthMonitor;
            if (healthMonitor is not null)
            {
                await healthMonitor.ConfigureAwait(false);
            }
        }

        private async Task MonitorHealthAsync()
        {
            try
            {
                while (true)
                {
                    await healthScheduler
                        .DelayAsync(healthPollInterval, _healthCancellation.Token)
                        .ConfigureAwait(false);
                    var current = nativeApi.CaptureWindowState(
                        verifiedWindow.WindowHandle);
                    EnsureSameIdentity(verifiedWindow, current);
                    var currentTarget = CreateTargetRect(
                        target.Width,
                        target.Height,
                        nativeApi.GetVirtualScreen());
                    if (nativeApi.GetForegroundWindow() == verifiedWindow.WindowHandle ||
                        current.Rect != target ||
                        current.Rect != currentTarget ||
                        nativeApi.GetClientRect(verifiedWindow.WindowHandle) !=
                            targetClientRect ||
                        current.Style != targetStyle ||
                        current.ExtendedStyle != targetExtendedStyle)
                    {
                        throw new InvalidOperationException(
                            "The pop-out left its verified non-topmost edge placement.");
                    }
                }
            }
            catch (OperationCanceledException)
                when (_healthCancellation.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_healthCancellation.Token);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(PlacementUnavailable(exception));
            }
        }
    }

    private sealed class TaskDelayHealthScheduler
        : IWallpaperEnginePopOutHealthScheduler
    {
        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            new(Task.Delay(delay, cancellationToken));
    }
}
