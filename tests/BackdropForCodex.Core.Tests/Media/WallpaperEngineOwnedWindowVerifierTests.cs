using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEngineOwnedWindowVerifierTests
{
    [Fact]
    public void VerifiedWindowRejectsAMissingOwnedWindowName()
    {
        Assert.Throws<ArgumentNullException>(() => new WallpaperEngineVerifiedWindow(
            (nint)1,
            processId: 1,
            DateTimeOffset.UtcNow,
            @"C:\WallpaperEngine\wallpaper64.exe",
            windowName: default));
    }

    [Fact]
    public void VerifiedWindowRejectsAMissingProcessStartTime()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WallpaperEngineVerifiedWindow(
                (nint)1,
                processId: 1,
                processStartTimeUtc: default,
                @"C:\WallpaperEngine\wallpaper64.exe",
                WallpaperEngineOwnedWindowName.Create(1)));

        Assert.Equal("processStartTimeUtc", exception.ParamName);
    }

    [Fact]
    public async Task WaitRejectsAMissingOwnedWindowNameBeforeCapturingSnapshots()
    {
        var source = new SequenceWindowSource([]);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.WaitForOwnedWindowAsync(
                CreateInstallation(),
                windowName: default,
                new WallpaperEngineWindowBaseline([]),
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task AbsenceProofRejectsAMissingOwnedWindowNameBeforeCapturingSnapshots()
    {
        var source = new SequenceWindowSource([]);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.ConfirmOwnedWindowAbsentAsync(
                CreateInstallation(),
                windowName: default,
                new WallpaperEngineWindowBaseline([]),
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(0, source.CaptureCount);
    }

    [Fact]
    public async Task VerifierAcceptsOnlyTheUniqueNewTrustedWindowInTheCurrentSession()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(1);
        var currentSession = WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId;
        var source = new SequenceWindowSource(
            [CreateSnapshot(11, "existing", installation.ControlExecutablePath, currentSession)],
            [
                CreateSnapshot(11, "existing", installation.ControlExecutablePath, currentSession),
                CreateSnapshot(12, name.Value, installation.ControlExecutablePath, currentSession),
            ]);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero);

        var baseline = await verifier.CaptureBaselineAsync(installation, CancellationToken.None);
        var result = await verifier.WaitForOwnedWindowAsync(
            installation,
            name,
            baseline,
            CancellationToken.None);

        Assert.Equal((nint)12, result.WindowHandle);
        Assert.Equal(8001, result.ProcessId);
        Assert.Equal(name, result.WindowName);
        Assert.DoesNotContain(name.Value, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(installation.ControlExecutablePath, result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifierRejectsLookalikesWithoutSelectingAnArbitraryWindow()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(2);
        var session = WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId;
        var source = new SequenceWindowSource(
            [],
            [
                CreateSnapshot(21, name.Value, @"C:\Foreign\wallpaper64.exe", session),
                CreateSnapshot(22, name.Value, installation.ControlExecutablePath, session, trusted: false),
                CreateSnapshot(23, name.Value, installation.ControlExecutablePath, session + 1),
                CreateSnapshot(24, name.Value, installation.ControlExecutablePath, session, visible: false),
            ]);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.Zero);

        var baseline = await verifier.CaptureBaselineAsync(installation, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.WaitForOwnedWindowAsync(
                installation,
                name,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
    }

    [Fact]
    public async Task VerifierFailsClosedWhenMoreThanOneWindowPassesEveryProof()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(3);
        var session = WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId;
        var source = new SequenceWindowSource(
            [],
            [
                CreateSnapshot(31, name.Value, installation.ControlExecutablePath, session),
                CreateSnapshot(32, name.Value, installation.ControlExecutablePath, session),
            ]);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);
        var baseline = await verifier.CaptureBaselineAsync(installation, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.WaitForOwnedWindowAsync(
                installation,
                name,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(2, source.CaptureCount);
    }

    [Fact]
    public async Task VerifierDoesNotAuthorizeAnExactTitleWithoutProcessProof()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(15);
        var clock = new ManuallyAdvancedTimeProvider(
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var rawWindow = new WallpaperEngineWindowSnapshot(
            (nint)151,
            name.Value,
            isVisible: true,
            isTopLevel: true);
        var source = new SequenceWindowSource([], [rawWindow])
        {
            CaptureObserved = captureCount =>
            {
                if (captureCount > 1)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(5), TimeSpan.Zero);
                }
            },
        };
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero,
            clock);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.WaitForOwnedWindowAsync(
                installation,
                name,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(3, source.CaptureCount);
    }

    [Fact]
    public async Task RawBaselineObservationCannotLaterBecomeANewAuthorizedWindow()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(16);
        var clock = new ManuallyAdvancedTimeProvider(
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var rawWindow = new WallpaperEngineWindowSnapshot(
            (nint)161,
            name.Value,
            isVisible: true,
            isTopLevel: true);
        var identifiedWindow = CreateSnapshot(
            161,
            name.Value,
            installation.ControlExecutablePath,
            WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId,
            processStartTimeUtc: clock.GetUtcNow().AddMinutes(-1));
        var source = new SequenceWindowSource([rawWindow], [identifiedWindow])
        {
            CaptureObserved = captureCount =>
            {
                if (captureCount > 1)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(5), TimeSpan.Zero);
                }
            },
        };
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero,
            clock);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.WaitForOwnedWindowAsync(
                installation,
                name,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(3, source.CaptureCount);
    }

    [Fact]
    public async Task RevalidationRejectsAWindowWhoseHighEntropyTitleChanged()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(7);
        var replacementName = WallpaperEngineOwnedWindowName.Create(8);
        var session = WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId;
        var processStartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var source = new SequenceWindowSource(
            [],
            [CreateSnapshot(
                71,
                name.Value,
                installation.ControlExecutablePath,
                session,
                processStartTimeUtc: processStartTimeUtc)],
            [CreateSnapshot(
                71,
                replacementName.Value,
                installation.ControlExecutablePath,
                session,
                processStartTimeUtc: processStartTimeUtc)]);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);
        var verified = await verifier.WaitForOwnedWindowAsync(
            installation,
            name,
            baseline,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.RevalidateOwnedWindowAsync(
                verified,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
    }

    [Fact]
    public async Task RevalidationRejectsAnExactTitleWithoutProcessProof()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(17);
        var processStartTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        var source = new SequenceWindowSource(
            [],
            [CreateSnapshot(
                171,
                name.Value,
                installation.ControlExecutablePath,
                WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId,
                processStartTimeUtc: processStartTimeUtc)],
            [new WallpaperEngineWindowSnapshot(
                (nint)171,
                name.Value,
                isVisible: true,
                isTopLevel: true)]);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);
        var verified = await verifier.WaitForOwnedWindowAsync(
            installation,
            name,
            baseline,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.RevalidateOwnedWindowAsync(
                verified,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
    }

    [Fact]
    public async Task AbsenceProofRejectsALateOwnedWindowAfterAnInitiallyEmptySnapshot()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(4);
        var session = WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId;
        var source = new SequenceWindowSource(
            [],
            [],
            [CreateSnapshot(41, name.Value, installation.ControlExecutablePath, session)]);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.ConfirmOwnedWindowAbsentAsync(
                installation,
                name,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(3, source.CaptureCount);
    }

    [Fact]
    public async Task AbsenceProofDoesNotTreatAHiddenExactTitleWindowAsAbsent()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(13);
        var clock = new ManuallyAdvancedTimeProvider(
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var hiddenWindow = CreateSnapshot(
            131,
            name.Value,
            installation.ControlExecutablePath,
            WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId,
            visible: false);
        var source = new SequenceWindowSource([], [hiddenWindow])
        {
            CaptureObserved = captureCount =>
            {
                if (captureCount > 1)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(5), TimeSpan.Zero);
                }
            },
        };
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero,
            clock);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.ConfirmOwnedWindowAbsentAsync(
                installation,
                name,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(3, source.CaptureCount);
    }

    [Fact]
    public async Task AbsenceProofDoesNotTreatAnUnreadableExactTitleWindowAsAbsent()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(14);
        var clock = new ManuallyAdvancedTimeProvider(
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var unreadableWindow = new WallpaperEngineWindowSnapshot(
            (nint)141,
            name.Value,
            isVisible: true,
            isTopLevel: true);
        var source = new SequenceWindowSource([], [unreadableWindow])
        {
            CaptureObserved = captureCount =>
            {
                if (captureCount > 1)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(5), TimeSpan.Zero);
                }
            },
        };
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero,
            clock);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.ConfirmOwnedWindowAbsentAsync(
                installation,
                name,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(3, source.CaptureCount);
    }

    [Fact]
    public async Task AbsenceProofFailsImmediatelyWhenMultipleOwnedCandidatesAppear()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(9);
        var session = WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId;
        var source = new SequenceWindowSource(
            [],
            [
                CreateSnapshot(91, name.Value, installation.ControlExecutablePath, session),
                CreateSnapshot(92, name.Value, installation.ControlExecutablePath, session),
            ],
            []);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.ConfirmOwnedWindowAbsentAsync(
                installation,
                name,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(2, source.CaptureCount);
    }

    [Fact]
    public async Task AbsenceProofFailsWhenTheClosingOwnedIdentityIsReplaced()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(10);
        var session = WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId;
        var source = new SequenceWindowSource(
            [],
            [CreateSnapshot(101, name.Value, installation.ControlExecutablePath, session)],
            [CreateSnapshot(102, name.Value, installation.ControlExecutablePath, session)],
            []);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.Zero);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.ConfirmOwnedWindowAbsentAsync(
                installation,
                name,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(3, source.CaptureCount);
    }

    [Fact]
    public async Task AbsenceProofAllowsTheClosingOwnedWindowToRetireWithinTheBoundedInterval()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(6);
        var session = WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId;
        var closing = CreateSnapshot(
            61,
            name.Value,
            installation.ControlExecutablePath,
            session);
        var source = new SequenceWindowSource([], [closing], [closing], [], []);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(1));
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        await verifier.ConfirmOwnedWindowAbsentAsync(
            installation,
            name,
            baseline,
            CancellationToken.None);

        Assert.True(source.CaptureCount > 4);
    }

    [Fact]
    public async Task AbsenceProofRequiresAQuietWindowForTheEntireBoundedInterval()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(5);
        var source = new SequenceWindowSource([], []);
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.Zero);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        await verifier.ConfirmOwnedWindowAbsentAsync(
            installation,
            name,
            baseline,
            CancellationToken.None);

        Assert.True(source.CaptureCount > 2);
    }

    [Fact]
    public async Task WaitTimeoutUsesMonotonicElapsedTimeWhenWallClockMovesBackward()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(11);
        var clock = new ManuallyAdvancedTimeProvider(
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var source = new SequenceWindowSource([], [])
        {
            CaptureObserved = captureCount =>
            {
                if (captureCount > 1)
                {
                    clock.Advance(
                        TimeSpan.FromMilliseconds(5),
                        wallClockChange: TimeSpan.FromDays(-1));
                }
            },
        };
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero,
            clock);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            async () => await verifier.WaitForOwnedWindowAsync(
                installation,
                name,
                baseline,
                CancellationToken.None));

        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.Equal(3, source.CaptureCount);
    }

    [Fact]
    public async Task AbsenceTimeoutUsesMonotonicElapsedTimeWhenWallClockMovesForward()
    {
        var installation = CreateInstallation();
        var name = WallpaperEngineOwnedWindowName.Create(12);
        var clock = new ManuallyAdvancedTimeProvider(
            new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var source = new SequenceWindowSource([], [])
        {
            CaptureObserved = captureCount =>
            {
                if (captureCount > 1)
                {
                    clock.Advance(
                        TimeSpan.FromMilliseconds(5),
                        wallClockChange: TimeSpan.FromDays(1));
                }
            },
        };
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            source,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.Zero,
            clock);
        var baseline = await verifier.CaptureBaselineAsync(
            installation,
            CancellationToken.None);

        await verifier.ConfirmOwnedWindowAbsentAsync(
            installation,
            name,
            baseline,
            CancellationToken.None);

        Assert.Equal(3, source.CaptureCount);
    }

    private static WallpaperEngineInstallation CreateInstallation() => new(
        @"C:\Steam",
        @"C:\Steam\steamapps\common\wallpaper_engine",
        @"C:\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe",
        [@"C:\Steam"]);

    private static WallpaperEngineWindowSnapshot CreateSnapshot(
        nint handle,
        string title,
        string processPath,
        int sessionId,
        bool trusted = true,
        bool visible = true,
        DateTimeOffset? processStartTimeUtc = null) =>
        new(
            handle,
            title,
            visible,
            isTopLevel: true,
            processId: 8001,
            sessionId,
            processStartTimeUtc ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            processPath,
            trusted);

    private sealed class SequenceWindowSource(
        params IReadOnlyList<WallpaperEngineWindowSnapshot>[] captures)
        : IWallpaperEngineWindowSnapshotSource
    {
        private int _index;

        internal int CaptureCount { get; private set; }

        internal Action<int>? CaptureObserved { get; init; }

        public ValueTask<IReadOnlyList<WallpaperEngineWindowSnapshot>> CaptureAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            CaptureObserved?.Invoke(CaptureCount);
            var index = Math.Min(_index++, captures.Length - 1);
            return ValueTask.FromResult(captures[index]);
        }
    }

    private sealed class ManuallyAdvancedTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = utcNow;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan elapsed, TimeSpan wallClockChange)
        {
            _timestamp = checked(_timestamp + elapsed.Ticks);
            _utcNow += wallClockChange;
        }
    }
}
