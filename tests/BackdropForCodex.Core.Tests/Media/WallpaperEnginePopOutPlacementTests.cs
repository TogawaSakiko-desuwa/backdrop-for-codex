using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEnginePopOutPlacementTests
{
    private static readonly WallpaperEngineOwnedWindowName TestWindowName =
        new("BackdropForCodex-placement-test");

    private static readonly WallpaperEngineWindowOptions DefaultOptions =
        new(1920, 1080);

    [Fact]
    public void HealthPollingRejectsIntervalsThatAreNotLowFrequency()
    {
        var native = CreateNative(CreateOriginalState());
        using var scheduler = new ManuallyAdvancedHealthScheduler();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WindowsWallpaperEnginePopOutPlacement(
                native,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(99),
                scheduler));
    }

    [Fact]
    public async Task PlacementHealthFailsWhenThePopOutMovesAwayFromTheOnePixelEdge()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        using var scheduler = new ManuallyAdvancedHealthScheduler();
        var placement = new WindowsWallpaperEnginePopOutPlacement(
            native,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            scheduler);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);
        var lease = await placement.PlaceAsync(verified, DefaultOptions);
        native.State = native.State with { Rect = original.Rect };

        scheduler.Advance();

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await lease.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
            exception.Reason);

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task PlacementHealthFailsWhenThePopOutBecomesTopMost()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        using var scheduler = new ManuallyAdvancedHealthScheduler();
        var placement = new WindowsWallpaperEnginePopOutPlacement(
            native,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            scheduler);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);
        var lease = await placement.PlaceAsync(verified, DefaultOptions);
        native.State = native.State with
        {
            ExtendedStyle = native.State.ExtendedStyle | 0x00000008,
        };

        scheduler.Advance();

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await lease.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
            exception.Reason);

        await lease.DisposeAsync();
    }

    [Fact]
    public async Task PlacementHealthFailsWhenTheVerifiedWindowIdentityChanges()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        using var scheduler = new ManuallyAdvancedHealthScheduler();
        var placement = new WindowsWallpaperEnginePopOutPlacement(
            native,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            scheduler);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);
        var lease = await placement.PlaceAsync(verified, DefaultOptions);
        native.State = native.State with { ProcessId = original.ProcessId + 1 };

        scheduler.Advance();

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await lease.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);

        native.WindowExists = false;
        await lease.MarkClosedAsync();
        await lease.DisposeAsync();
        Assert.Equal(1, native.SetStylesCount);
    }

    [Fact]
    public async Task PlacementHealthFailsTerminallyWhenTheHighEntropyTitleChanges()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        using var scheduler = new ManuallyAdvancedHealthScheduler();
        var placement = new WindowsWallpaperEnginePopOutPlacement(
            native,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            scheduler);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);
        var lease = await placement.PlaceAsync(verified, DefaultOptions);
        native.State = native.State with
        {
            Title = "BackdropForCodex-reused-window",
        };

        scheduler.Advance();

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await lease.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);

        native.WindowExists = false;
        await lease.MarkClosedAsync();
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task DisposingAHealthyPlacementCancelsItsBoundedMonitor()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        using var scheduler = new ManuallyAdvancedHealthScheduler();
        var placement = new WindowsWallpaperEnginePopOutPlacement(
            native,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            scheduler);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);
        var lease = await placement.PlaceAsync(verified, DefaultOptions);

        await lease.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await lease.Completion);
        Assert.Equal(original, native.State);
    }

    [Fact]
    public async Task PlacementHealthFailsIfThePopOutTakesForegroundFocus()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        using var scheduler = new ManuallyAdvancedHealthScheduler();
        var placement = new WindowsWallpaperEnginePopOutPlacement(
            native,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            scheduler);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);
        var lease = await placement.PlaceAsync(verified, DefaultOptions);
        native.ForegroundWindow = original.WindowHandle;

        scheduler.Advance();

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await lease.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
            exception.Reason);

        native.ForegroundWindow = (nint)99;
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task PlacementRemovesNonClientChromeAtTheOnePixelEdgeAndRestoresOriginalState()
    {
        var processStart = new DateTimeOffset(2026, 8, 10, 4, 5, 6, TimeSpan.Zero);
        var original = new WallpaperEngineNativeWindowState(
            (nint)42,
            TestWindowName.Value,
            processId: 9001,
            processStart,
            @"C:\WallpaperEngine\wallpaper64.exe",
            new WallpaperEngineWindowRect(100, 200, 2042, 1336),
            style: 0x10CF0000,
            extendedStyle: 0x00000008);
        var native = new FakePlacementNativeApi(
            original,
            new WallpaperEngineWindowRect(-1920, -1080, 1920, 1080),
            foregroundWindow: (nint)99,
            new WallpaperEngineWindowRect(0, 0, 1920, 1080));
        var placement = new WindowsWallpaperEnginePopOutPlacement(native);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);

        await using (await placement.PlaceAsync(verified, DefaultOptions))
        {
            Assert.Equal(
                new WallpaperEngineWindowRect(-3839, -1080, -1919, 0),
                native.State.Rect);
            Assert.Equal((nint)1, native.PositionCalls.Single().InsertAfter);
            Assert.Equal(0x0230u, (uint)native.PositionCalls.Single().Flags);
            Assert.Equal(0x10000000u, native.State.Style);
            Assert.Equal(0u, native.State.ExtendedStyle);
            Assert.Equal(
                new WallpaperEngineWindowRect(0, 0, 1920, 1080),
                native.GetClientRect(original.WindowHandle));
        }

        Assert.Equal(original, native.State);
    }

    [Fact]
    public async Task RollbackStillRestoresTheRectWhenStyleRestorationFails()
    {
        var processStart = new DateTimeOffset(2026, 8, 10, 4, 5, 6, TimeSpan.Zero);
        var original = new WallpaperEngineNativeWindowState(
            (nint)42,
            TestWindowName.Value,
            processId: 9001,
            processStart,
            @"C:\WallpaperEngine\wallpaper64.exe",
            new WallpaperEngineWindowRect(100, 200, 2020, 1280),
            style: 0x10CF0000,
            extendedStyle: 0x00000008);
        var native = new FakePlacementNativeApi(
            original,
            new WallpaperEngineWindowRect(-1920, -1080, 1920, 1080),
            foregroundWindow: (nint)99);
        var placement = new WindowsWallpaperEnginePopOutPlacement(native);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);
        var lease = await placement.PlaceAsync(verified, DefaultOptions);
        native.SetStylesException = new InvalidOperationException("style restore failed");

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await lease.DisposeAsync());

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
            exception.Reason);
        Assert.Equal(original.Rect, native.State.Rect);
    }

    [Fact]
    public async Task ActivePopOutIsRejectedBeforeAnyWindowMutation()
    {
        var processStart = new DateTimeOffset(2026, 8, 10, 4, 5, 6, TimeSpan.Zero);
        var original = new WallpaperEngineNativeWindowState(
            (nint)42,
            TestWindowName.Value,
            processId: 9001,
            processStart,
            @"C:\WallpaperEngine\wallpaper64.exe",
            new WallpaperEngineWindowRect(100, 200, 2020, 1280),
            style: 0x10CF0000,
            extendedStyle: 0);
        var native = new FakePlacementNativeApi(
            original,
            new WallpaperEngineWindowRect(-1920, -1080, 1920, 1080),
            foregroundWindow: original.WindowHandle);
        var placement = new WindowsWallpaperEnginePopOutPlacement(native);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await placement.PlaceAsync(verified, DefaultOptions));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
            exception.Reason);
        Assert.Empty(native.PositionCalls);
        Assert.Equal(original, native.State);
    }

    [Fact]
    public async Task UnmatchedVerifiedIdentityIsRejectedBeforeAnyWindowMutation()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        var placement = new WindowsWallpaperEnginePopOutPlacement(native);
        var mismatchedVerification = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            processId: original.ProcessId + 1,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await placement.PlaceAsync(
                mismatchedVerification,
                DefaultOptions));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Empty(native.PositionCalls);
        Assert.Equal(0, native.SetStylesCount);
        Assert.Equal(original, native.State);
    }

    [Fact]
    public async Task ReusedWindowHandleIsNeverMutatedByRollback()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        native.AfterSetWindowPosition = current =>
        {
            current.AfterSetWindowPosition = null;
            current.State = current.State with { ProcessId = original.ProcessId + 1 };
        };
        var placement = new WindowsWallpaperEnginePopOutPlacement(native);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await placement.PlaceAsync(verified, DefaultOptions));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Single(native.PositionCalls);
        Assert.Equal(1, native.SetStylesCount);
        Assert.Equal(original.ProcessId + 1, native.State.ProcessId);
    }

    [Fact]
    public async Task ClosedExactWindowCanDisarmRollbackWithoutMovingItBack()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        var placement = new WindowsWallpaperEnginePopOutPlacement(native);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);
        var lease = await placement.PlaceAsync(verified, DefaultOptions);
        native.WindowExists = false;

        await lease.MarkClosedAsync();
        await lease.DisposeAsync();

        Assert.Single(native.PositionCalls);
        Assert.Equal(1, native.SetStylesCount);
    }

    [Fact]
    public void InitialPlacementUsesTheRequestedSizeAtTheSameOnePixelEdge()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        var placement = new WindowsWallpaperEnginePopOutPlacement(native);

        var initial = placement.GetInitialPlacement(
            new WallpaperEngineWindowOptions(1920, 1080));

        Assert.Equal(new WallpaperEngineWindowPlacement(-3839, -1080), initial);
        Assert.Empty(native.PositionCalls);
        Assert.Equal(0, native.SetStylesCount);
    }

    [Fact]
    public async Task ClosedMarkerWaitsForTheExactWindowToDisappear()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        var placement = new WindowsWallpaperEnginePopOutPlacement(
            native,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);
        var lease = await placement.PlaceAsync(verified, DefaultOptions);
        native.WindowExistResponses.Enqueue(true);
        native.WindowExistResponses.Enqueue(true);
        native.WindowExistResponses.Enqueue(false);

        await lease.MarkClosedAsync();
        await lease.DisposeAsync();

        Assert.Equal(3, native.WindowExistCheckCount);
        Assert.Single(native.PositionCalls);
        Assert.Equal(1, native.SetStylesCount);
    }

    [Fact]
    public async Task ClosedMarkerRemainsDisarmedIfTheHandleValueIsLaterReused()
    {
        var original = CreateOriginalState();
        var native = CreateNative(original);
        var placement = new WindowsWallpaperEnginePopOutPlacement(
            native,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero);
        var verified = new WallpaperEngineVerifiedWindow(
            original.WindowHandle,
            original.ProcessId,
            original.ProcessStartTimeUtc,
            original.ProcessPath,
            TestWindowName);
        var lease = await placement.PlaceAsync(verified, DefaultOptions);
        native.WindowExists = false;
        await lease.MarkClosedAsync();
        native.WindowExists = true;

        await lease.MarkClosedAsync();
        await lease.DisposeAsync();

        Assert.Equal(1, native.WindowExistCheckCount);
        Assert.Single(native.PositionCalls);
        Assert.Equal(1, native.SetStylesCount);
    }

    private static WallpaperEngineNativeWindowState CreateOriginalState() =>
        new(
            (nint)42,
            TestWindowName.Value,
            processId: 9001,
            new DateTimeOffset(2026, 8, 10, 4, 5, 6, TimeSpan.Zero),
            @"C:\WallpaperEngine\wallpaper64.exe",
            new WallpaperEngineWindowRect(100, 200, 2020, 1280),
            style: 0x10CF0000,
            extendedStyle: 0x00000008);

    private static FakePlacementNativeApi CreateNative(
        WallpaperEngineNativeWindowState state) =>
        new(
            state,
            new WallpaperEngineWindowRect(-1920, -1080, 1920, 1080),
            foregroundWindow: (nint)99);

    private sealed class FakePlacementNativeApi
        : IWallpaperEngineWindowPlacementNativeApi
    {
        private readonly WallpaperEngineWindowRect _virtualScreen;

        internal FakePlacementNativeApi(
            WallpaperEngineNativeWindowState state,
            WallpaperEngineWindowRect virtualScreen,
            nint foregroundWindow,
            WallpaperEngineWindowRect? clientRect = null)
        {
            State = state;
            _virtualScreen = virtualScreen;
            ForegroundWindow = foregroundWindow;
            ClientRect = clientRect ?? new WallpaperEngineWindowRect(
                0,
                0,
                state.Rect.Width,
                state.Rect.Height);
        }

        internal WallpaperEngineNativeWindowState State { get; set; }

        internal WallpaperEngineWindowRect ClientRect { get; set; }

        internal List<PositionCall> PositionCalls { get; } = [];

        internal Exception? SetStylesException { get; set; }

        internal Action<FakePlacementNativeApi>? AfterSetWindowPosition { get; set; }

        internal int SetStylesCount { get; private set; }

        internal bool WindowExists { get; set; } = true;

        internal Queue<bool> WindowExistResponses { get; } = [];

        internal int WindowExistCheckCount { get; private set; }

        internal nint ForegroundWindow { get; set; }

        public WallpaperEngineWindowRect GetVirtualScreen() => _virtualScreen;

        public nint GetForegroundWindow() => ForegroundWindow;

        public bool DoesWindowExist(nint windowHandle)
        {
            Assert.Equal(State.WindowHandle, windowHandle);
            WindowExistCheckCount++;
            return WindowExistResponses.Count == 0
                ? WindowExists
                : WindowExistResponses.Dequeue();
        }

        public WallpaperEngineNativeWindowState CaptureWindowState(nint windowHandle)
        {
            Assert.True(WindowExists);
            Assert.Equal(State.WindowHandle, windowHandle);
            return State;
        }

        public WallpaperEngineWindowRect GetClientRect(nint windowHandle)
        {
            Assert.True(WindowExists);
            Assert.Equal(State.WindowHandle, windowHandle);
            return ClientRect;
        }

        public void SetWindowPosition(
            nint windowHandle,
            nint insertAfter,
            WallpaperEngineWindowRect rect,
            WallpaperEngineSetWindowPositionFlags flags)
        {
            Assert.Equal(State.WindowHandle, windowHandle);
            PositionCalls.Add(new PositionCall(insertAfter, rect, flags));
            var extendedStyle = insertAfter == (nint)1
                ? State.ExtendedStyle & ~0x00000008u
                : State.ExtendedStyle;
            State = State with
            {
                Rect = rect,
                ExtendedStyle = extendedStyle,
            };
            AfterSetWindowPosition?.Invoke(this);
        }

        public void SetWindowStyles(nint windowHandle, uint style, uint extendedStyle)
        {
            Assert.Equal(State.WindowHandle, windowHandle);
            SetStylesCount++;
            if (SetStylesException is not null)
            {
                throw SetStylesException;
            }

            State = State with
            {
                Style = style,
                ExtendedStyle = extendedStyle,
            };
        }
    }

    private sealed record PositionCall(
        nint InsertAfter,
        WallpaperEngineWindowRect Rect,
        WallpaperEngineSetWindowPositionFlags Flags);

    private sealed class ManuallyAdvancedHealthScheduler
        : IWallpaperEnginePopOutHealthScheduler,
        IDisposable
    {
        private readonly SemaphoreSlim _ticks = new(0);

        internal void Advance() => _ticks.Release();

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            new(_ticks.WaitAsync(cancellationToken));

        public void Dispose() => _ticks.Dispose();
    }
}
