using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEngineAudioIsolationTests
{
    [Fact]
    public void EveryOtherEnumeratedTopLevelWindowCountsEvenWhenItIsNotVisible()
    {
        Assert.True(WindowsWallpaperEngineAudioTopology.IsAnotherOwnedProcessWindow(
            candidateHandle: (nint)43,
            candidateProcessId: 9001,
            expectedProcessId: 9001,
            ownedWindowHandle: (nint)42));
        Assert.False(WindowsWallpaperEngineAudioTopology.IsAnotherOwnedProcessWindow(
            candidateHandle: (nint)42,
            candidateProcessId: 9001,
            expectedProcessId: 9001,
            ownedWindowHandle: (nint)42));
    }

    [Fact]
    public async Task IsolatedProcessSessionIsMutedAndItsPriorStateIsRestored()
    {
        var window = CreateWindow();
        var target = FakeAudioSession.Target(window, "target-1", Guid.NewGuid());
        var source = new BlockingAudioSessionSource([target]);
        var isolation = new WindowsWallpaperEngineAudioIsolation(
            source,
            new FakeAudioTopology(),
            TimeSpan.FromMilliseconds(100));

        var lease = await isolation.AcquireMutedSessionAsync(window, CancellationToken.None);

        Assert.True(target.IsMuted);
        await lease.DisposeAsync();
        Assert.False(target.IsMuted);
        Assert.Equal(1, target.RestoreCount);
        Assert.DoesNotContain(window.ProcessPath, lease.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreExistingWallpaperEngineProcessCannotBecomeThePopOutAudioTarget()
    {
        var window = CreateWindow();
        var target = FakeAudioSession.Target(window, "post-open-session", Guid.NewGuid());
        var source = new SequencedAudioSessionSource([], [target]);
        var preExistingProcess = new WallpaperEngineAudioProcessSnapshot(
            window.ProcessId,
            window.ProcessStartTimeUtc,
            window.ProcessPath);
        var processSource = new FakeAudioProcessSource(
            [preExistingProcess]);
        var isolation = new WindowsWallpaperEngineAudioIsolation(
            source,
            processSource,
            new FakeAudioTopology(),
            TimeSpan.FromMilliseconds(100));
        await using var baseline = await isolation.CaptureBaselineAsync(
            CreateInstallation(),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await isolation.AcquireMutedSessionAsync(
                window,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven,
            exception.Reason);
        Assert.False(baseline.InitialSilenceIsProven);
        Assert.False(target.IsMuted);
        Assert.DoesNotContain(window.ProcessPath, baseline.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            window.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            baseline.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(target.SessionInstanceIdentifier, baseline.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            window.ProcessPath,
            preExistingProcess.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreExistingWallpaperEngineSessionIdentityCannotBeReusedByANewProcess()
    {
        var window = CreateWindow();
        var reusedGrouping = Guid.NewGuid();
        var preExisting = new FakeAudioSession(
            processId: window.ProcessId - 1,
            processStartTimeUtc: window.ProcessStartTimeUtc.AddMinutes(-2),
            processPath: @"C:\WallpaperEngine\wallpaper64.exe",
            sessionIdentifier: "pre-existing-session",
            sessionInstanceIdentifier: "pre-existing-instance",
            reusedGrouping,
            isMuted: false);
        var target = new FakeAudioSession(
            window.ProcessId,
            window.ProcessStartTimeUtc,
            window.ProcessPath,
            preExisting.SessionIdentifier,
            sessionInstanceIdentifier: "new-instance",
            groupingParameter: Guid.NewGuid(),
            isMuted: false);
        var isolation = new WindowsWallpaperEngineAudioIsolation(
            new SequencedAudioSessionSource([preExisting], [target]),
            new FakeAudioProcessSource([]),
            new FakeAudioTopology(),
            TimeSpan.FromMilliseconds(100));
        await using var baseline = await isolation.CaptureBaselineAsync(
            CreateInstallation(),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await isolation.AcquireMutedSessionAsync(
                window,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven,
            exception.Reason);
        Assert.Equal(1, preExisting.DisposeCount);
        Assert.False(target.IsMuted);
        Assert.DoesNotContain(
            preExisting.SessionIdentifier,
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            preExisting.ProcessPath,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultipleInitialSessionsInTheVerifiedProcessFailBeforeAnyMute()
    {
        var window = CreateWindow();
        var first = FakeAudioSession.Target(window, "first", Guid.NewGuid());
        var second = FakeAudioSession.Target(window, "second", Guid.NewGuid());
        var isolation = new WindowsWallpaperEngineAudioIsolation(
            new BlockingAudioSessionSource([first, second]),
            new FakeAudioTopology(),
            TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await isolation.AcquireMutedSessionAsync(
                window,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven,
            exception.Reason);
        Assert.False(first.IsMuted);
        Assert.False(second.IsMuted);
    }

    [Fact]
    public async Task BaselineCaptureFailureDoesNotRetainSensitiveDiagnostics()
    {
        const string sensitivePath = @"C:\Users\Sensitive\wallpaper64.exe";
        const string sensitiveSession = "session-secret-42";
        var isolation = new WindowsWallpaperEngineAudioIsolation(
            new ThrowingAudioSessionSource(
                new InvalidOperationException(
                    $"failed for {sensitivePath}, pid 9123, {sensitiveSession}")),
            new FakeAudioProcessSource([]),
            new FakeAudioTopology(),
            TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await isolation.CaptureBaselineAsync(
                CreateInstallation(),
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven,
            exception.Reason);
        Assert.DoesNotContain(sensitivePath, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sensitiveSession, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("9123", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaselineProcessCaptureFailureStillReleasesEveryAudioHandle()
    {
        var window = CreateWindow();
        var captured = FakeAudioSession.Target(window, "captured-before-failure", Guid.NewGuid());
        var isolation = new WindowsWallpaperEngineAudioIsolation(
            new BlockingAudioSessionSource([captured]),
            new ThrowingAudioProcessSource(new InvalidOperationException("process snapshot failed")),
            new FakeAudioTopology(),
            TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await isolation.CaptureBaselineAsync(
                CreateInstallation(),
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven,
            exception.Reason);
        Assert.Equal(1, captured.DisposeCount);
    }

    [Fact]
    public async Task AnotherDesktopWindowInTheSameProcessFailsClosedBeforeMuting()
    {
        var window = CreateWindow();
        var target = FakeAudioSession.Target(window, "target-2", Guid.NewGuid());
        var isolation = new WindowsWallpaperEngineAudioIsolation(
            new BlockingAudioSessionSource([target]),
            new FakeAudioTopology { HasAnotherWindow = true },
            TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await isolation.AcquireMutedSessionAsync(
                window,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven,
            exception.Reason);
        Assert.False(target.IsMuted);
    }

    [Fact]
    public async Task SessionSharedWithAnotherWallpaperEngineProcessFailsClosed()
    {
        var window = CreateWindow();
        var sharedGrouping = Guid.NewGuid();
        var target = FakeAudioSession.Target(window, "target-3", sharedGrouping);
        var desktop = new FakeAudioSession(
            processId: window.ProcessId + 1,
            processStartTimeUtc: window.ProcessStartTimeUtc,
            processPath: @"C:\WallpaperEngine\wallpaper64.exe",
            sessionIdentifier: "desktop-session",
            sessionInstanceIdentifier: "desktop-instance",
            sharedGrouping,
            isMuted: false);
        var isolation = new WindowsWallpaperEngineAudioIsolation(
            new BlockingAudioSessionSource([target, desktop]),
            new FakeAudioTopology(),
            TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await isolation.AcquireMutedSessionAsync(
                window,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven,
            exception.Reason);
        Assert.False(target.IsMuted);
        Assert.False(desktop.IsMuted);
    }

    [Fact]
    public async Task MonitorRejectsANewSameProcessSessionWithoutMutingOrRestoringEarly()
    {
        var window = CreateWindow();
        var initial = FakeAudioSession.Target(window, "target-4", Guid.NewGuid());
        var newSession = FakeAudioSession.Target(window, "target-5", Guid.NewGuid());
        var sharedDesktop = new FakeAudioSession(
            processId: window.ProcessId + 2,
            processStartTimeUtc: window.ProcessStartTimeUtc,
            processPath: @"C:\WallpaperEngine\webwallpaper64.exe",
            sessionIdentifier: "shared",
            sessionInstanceIdentifier: "desktop-instance-2",
            newSession.GroupingParameter,
            isMuted: false);
        var source = new SequencedAudioSessionSource(
            [initial],
            [FakeAudioSession.Target(window, "target-4", initial.GroupingParameter), newSession],
            [
                FakeAudioSession.Target(window, "target-4", initial.GroupingParameter),
                FakeAudioSession.Target(window, "target-5", newSession.GroupingParameter),
                sharedDesktop,
            ]);
        var isolation = new WindowsWallpaperEngineAudioIsolation(
            source,
            new FakeAudioTopology(),
            TimeSpan.Zero);

        var disposable = await isolation.AcquireMutedSessionAsync(window, CancellationToken.None);
        var lease = Assert.IsType<WallpaperEngineMutedAudioLease>(disposable);
        await source.SecondCaptureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            var reason = await lease.IsolationLost.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(
                WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven,
                reason);
            Assert.True(initial.IsMuted);
            Assert.False(newSession.IsMuted);
        }
        finally
        {
            source.ThirdCaptureRelease.TrySetResult();
            await lease.DisposeAsync();
        }

        Assert.False(initial.IsMuted);
        Assert.False(newSession.IsMuted);
    }

    private static WallpaperEngineVerifiedWindow CreateWindow() => new(
        (nint)42,
        processId: 9001,
        DateTimeOffset.UtcNow.AddMinutes(-1),
        @"C:\WallpaperEngine\wallpaper64.exe",
        WallpaperEngineOwnedWindowName.Create(1));

    private static WallpaperEngineInstallation CreateInstallation() => new(
        @"C:\Steam",
        @"C:\WallpaperEngine",
        @"C:\WallpaperEngine\wallpaper64.exe",
        [@"C:\Steam"]);

    private sealed class FakeAudioTopology : IWallpaperEngineAudioTopology
    {
        internal bool HasAnotherWindow { get; init; }

        public bool HasAnotherTopLevelWindow(int processId, nint ownedWindowHandle) =>
            HasAnotherWindow;
    }

    private sealed class FakeAudioProcessSource(
        IReadOnlyList<WallpaperEngineAudioProcessSnapshot> processes)
        : IWallpaperEngineAudioProcessSource
    {
        public ValueTask<IReadOnlyList<WallpaperEngineAudioProcessSnapshot>> CaptureAsync(
            WallpaperEngineInstallation installation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(processes);
        }
    }

    private sealed class ThrowingAudioProcessSource(Exception exception)
        : IWallpaperEngineAudioProcessSource
    {
        public ValueTask<IReadOnlyList<WallpaperEngineAudioProcessSnapshot>> CaptureAsync(
            WallpaperEngineInstallation installation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<IReadOnlyList<WallpaperEngineAudioProcessSnapshot>>(
                exception);
        }
    }

    private sealed class BlockingAudioSessionSource(
        IReadOnlyList<IWallpaperEngineAudioSessionHandle> initial)
        : IWallpaperEngineAudioSessionSource
    {
        private int _captureCount;

        public async ValueTask<IReadOnlyList<IWallpaperEngineAudioSessionHandle>> CaptureAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _captureCount) == 1)
            {
                return initial;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }

    private sealed class ThrowingAudioSessionSource(Exception exception)
        : IWallpaperEngineAudioSessionSource
    {
        public ValueTask<IReadOnlyList<IWallpaperEngineAudioSessionHandle>> CaptureAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<IReadOnlyList<IWallpaperEngineAudioSessionHandle>>(
                exception);
        }
    }

    private sealed class SequencedAudioSessionSource(
        params IReadOnlyList<IWallpaperEngineAudioSessionHandle>[] captures)
        : IWallpaperEngineAudioSessionSource
    {
        private int _index;

        internal TaskCompletionSource SecondCaptureObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ThirdCaptureRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<IWallpaperEngineAudioSessionHandle>> CaptureAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, captures.Length - 1);
            if (index >= 1)
            {
                SecondCaptureObserved.TrySetResult();
            }

            if (index >= 2)
            {
                await ThirdCaptureRelease.Task.WaitAsync(cancellationToken);
            }

            return captures[index];
        }
    }

    private sealed class FakeAudioSession(
        int processId,
        DateTimeOffset processStartTimeUtc,
        string processPath,
        string sessionIdentifier,
        string sessionInstanceIdentifier,
        Guid groupingParameter,
        bool isMuted) : IWallpaperEngineAudioSessionHandle
    {
        private readonly bool _initiallyMuted = isMuted;

        public int ProcessId { get; } = processId;

        public DateTimeOffset ProcessStartTimeUtc { get; } = processStartTimeUtc;

        public string ProcessPath { get; } = processPath;

        public string SessionIdentifier { get; } = sessionIdentifier;

        public string SessionInstanceIdentifier { get; } = sessionInstanceIdentifier;

        public Guid GroupingParameter { get; } = groupingParameter;

        public bool IsMuted { get; private set; } = isMuted;

        internal int RestoreCount { get; private set; }

        internal int DisposeCount { get; private set; }

        internal static FakeAudioSession Target(
            WallpaperEngineVerifiedWindow window,
            string instanceIdentifier,
            Guid groupingParameter) =>
            new(
                window.ProcessId,
                window.ProcessStartTimeUtc,
                window.ProcessPath,
                $"session-{instanceIdentifier}",
                instanceIdentifier,
                groupingParameter,
                isMuted: false);

        public void SetMuted(bool muted)
        {
            IsMuted = muted;
            if (muted == _initiallyMuted)
            {
                RestoreCount++;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public override string ToString() =>
            $"{nameof(FakeAudioSession)} {{ Identity = <redacted> }}";
    }
}
