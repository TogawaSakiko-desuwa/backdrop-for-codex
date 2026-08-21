using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class WindowsGraphicsCaptureFactoryTests
{
    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    public void CadenceGateAcceptsOnlyTheBoundedTimestampJitterWindow(double frameRate)
    {
        var targetInterval = TimeSpan.FromTicks(checked((long)Math.Ceiling(
            TimeSpan.TicksPerSecond / frameRate)));
        var minimumInterval = WindowsGraphicsCaptureCadence.GetMinimumAcceptedInterval(
            frameRate);

        Assert.Equal(
            targetInterval - TimeSpan.FromMilliseconds(1),
            minimumInterval);
        Assert.True(WindowsGraphicsCaptureCadence.ShouldAccept(
            previousTimestampTicks: 0,
            currentTimestampTicks: (targetInterval - TimeSpan.FromMilliseconds(0.5)).Ticks,
            minimumInterval));
        Assert.False(WindowsGraphicsCaptureCadence.ShouldAccept(
            previousTimestampTicks: 0,
            currentTimestampTicks: (targetInterval - TimeSpan.FromMilliseconds(1.1)).Ticks,
            minimumInterval));
    }

    [Fact]
    public void CadenceGateDoesNotTurnSixtyHertzFramesIntoExcessFallbackFrames()
    {
        var fallbackInterval = WindowsGraphicsCaptureCadence.GetMinimumAcceptedInterval(15);
        var primaryInterval = WindowsGraphicsCaptureCadence.GetMinimumAcceptedInterval(30);

        Assert.False(WindowsGraphicsCaptureCadence.ShouldAccept(
            previousTimestampTicks: 0,
            currentTimestampTicks: TimeSpan.FromMilliseconds(50).Ticks,
            fallbackInterval));
        Assert.False(WindowsGraphicsCaptureCadence.ShouldAccept(
            previousTimestampTicks: 0,
            currentTimestampTicks: TimeSpan.FromMilliseconds(16.7).Ticks,
            primaryInterval));
        Assert.True(WindowsGraphicsCaptureCadence.ShouldAccept(
            previousTimestampTicks: long.MinValue,
            currentTimestampTicks: 0,
            fallbackInterval));
    }

    [Fact]
    public void EnqueueStateDoesNotCommitCadenceOrSequenceWhenTheBoundedQueueIsFull()
    {
        var state = new WindowsGraphicsCaptureEnqueueState(frameRate: 15);
        var deliveredSequences = new List<long>();

        Assert.True(state.TryCommit(
            TimeSpan.Zero,
            sequence =>
            {
                deliveredSequences.Add(sequence);
                return true;
            }));
        Assert.False(state.TryCommit(
            TimeSpan.FromMilliseconds(70),
            _ => false));
        Assert.True(state.TryCommit(
            TimeSpan.FromMilliseconds(80),
            sequence =>
            {
                deliveredSequences.Add(sequence);
                return true;
            }));

        Assert.Equal([0, 1], deliveredSequences);
    }

    [Theory]
    [InlineData(55)]
    [InlineData(60)]
    [InlineData(144)]
    public void EnqueueStateCarriesCadencePhaseAcrossQuantizedRefreshTimestamps(
        int sourceFramesPerSecond)
    {
        const int targetFramesPerSecond = 15;
        const int durationSeconds = 60;
        var state = new WindowsGraphicsCaptureEnqueueState(
            frameRate: targetFramesPerSecond);
        var deliveredSequences = new List<long>();

        for (var frame = 0;
             frame < sourceFramesPerSecond * durationSeconds;
             frame++)
        {
            state.TryCommit(
                TimeSpan.FromSeconds(frame / (double)sourceFramesPerSecond),
                sequence =>
                {
                    deliveredSequences.Add(sequence);
                    return true;
                });
        }

        Assert.Equal(
            targetFramesPerSecond * durationSeconds,
            deliveredSequences.Count);
        Assert.Equal(
            Enumerable.Range(0, deliveredSequences.Count)
                .Select(static sequence => (long)sequence),
            deliveredSequences);
    }

    [Fact]
    public void EnqueueStateSkipsMissedDeadlinesWithoutBurstingAfterALongGap()
    {
        var state = new WindowsGraphicsCaptureEnqueueState(frameRate: 15);
        var deliveredSequences = new List<long>();

        Assert.True(state.TryCommit(
            TimeSpan.Zero,
            sequence =>
            {
                deliveredSequences.Add(sequence);
                return true;
            }));
        Assert.True(state.TryCommit(
            TimeSpan.FromSeconds(10),
            sequence =>
            {
                deliveredSequences.Add(sequence);
                return true;
            }));
        Assert.False(state.TryCommit(
            TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(1),
            sequence =>
            {
                deliveredSequences.Add(sequence);
                return true;
            }));

        Assert.Equal([0, 1], deliveredSequences);
    }

    [Fact]
    public void CaptureBufferingIsBoundedToFourPixelSurfaces()
    {
        Assert.Equal(4, WindowsGraphicsCaptureFactory.BufferedFrameCapacity);
        Assert.Equal(
            14_745_600,
            WindowsGraphicsCaptureFactory.GetMaximumBufferedPixelBytes(1280, 720));
        Assert.Equal(
            33_177_600,
            WindowsGraphicsCaptureFactory.GetMaximumBufferedPixelBytes(1920, 1080));
        Assert.True(
            TimeSpan.FromSeconds(
                WindowsGraphicsCaptureFactory.BufferedFrameCapacity / 15d) >=
            TimeSpan.FromMilliseconds(235));
    }

    [Theory]
    [InlineData(CaptureIdentityMutation.ProcessId)]
    [InlineData(CaptureIdentityMutation.ProcessStartTime)]
    [InlineData(CaptureIdentityMutation.ProcessPath)]
    [InlineData(CaptureIdentityMutation.ExecutableTrust)]
    [InlineData(CaptureIdentityMutation.WindowTitle)]
    [InlineData(CaptureIdentityMutation.WindowExited)]
    public async Task StartRejectsAReusedWindowHandleWhenVerifiedIdentityDrifts(
        CaptureIdentityMutation mutation)
    {
        var expectedPath = Path.GetFullPath(
            @"C:\WallpaperEngine\bin\wallpaper32.exe");
        var expectedStartTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        var expected = new WallpaperEngineVerifiedWindow(
            windowHandle: new nint(42),
            processId: 1200,
            expectedStartTime,
            expectedPath,
            WallpaperEngineOwnedWindowName.Create(1));
        WallpaperEngineWindowSnapshot[] snapshots = mutation ==
                CaptureIdentityMutation.WindowExited
            ? []
            :
            [
                new WallpaperEngineWindowSnapshot(
                    expected.WindowHandle,
                    mutation == CaptureIdentityMutation.WindowTitle
                        ? WallpaperEngineOwnedWindowName.Create(2).Value
                        : expected.WindowName.Value,
                    isVisible: true,
                    isTopLevel: true,
                    processId: mutation == CaptureIdentityMutation.ProcessId ? 1201 : 1200,
                    WindowsWallpaperEngineWindowSnapshotSource.CurrentSessionId,
                    mutation == CaptureIdentityMutation.ProcessStartTime
                        ? expectedStartTime.AddSeconds(1)
                        : expectedStartTime,
                    mutation == CaptureIdentityMutation.ProcessPath
                        ? Path.GetFullPath(@"C:\WallpaperEngine\bin\other32.exe")
                        : expectedPath,
                    executableTrusted: mutation != CaptureIdentityMutation.ExecutableTrust),
            ];
        var verifier = new WindowsWallpaperEngineOwnedWindowVerifier(
            new FixedWindowSnapshotSource(snapshots),
            TimeSpan.FromMilliseconds(50),
            TimeSpan.Zero);
        var target = new WallpaperEngineWindowCaptureTarget(expected, verifier);
        var request = new WallpaperWindowCaptureRequest(
            generation: 23,
            target,
            width: 1920,
            height: 1080,
            frameRate: 30);

        var exception = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(
            () => new WindowsGraphicsCaptureFactory().StartAsync(request).AsTask());

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable,
            exception.ReasonCode);
        Assert.DoesNotContain("42", target.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1200", target.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(expectedPath, target.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("42", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1200", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(expectedPath, request.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("42", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1200", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            expectedPath,
            exception.InnerException?.Message ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(expectedPath, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeReturnsATypedCapabilityWithoutOpeningACaptureTarget()
    {
        var factory = new WindowsGraphicsCaptureFactory();

        var capability = await factory.ProbeAsync();

        Assert.True(
            capability.IsAvailable ||
            capability.ReasonCode is not DynamicWallpaperCapabilityReasonCode.None);
    }

    [Fact]
    public async Task StartRejectsANonexistentWindowWithoutFallingBackToDesktopCapture()
    {
        var factory = new WindowsGraphicsCaptureFactory();
        var target = new WallpaperEngineWindowCaptureTarget(
            new FixedCaptureAuthority(new nint(-1)));
        var request = new WallpaperWindowCaptureRequest(
            generation: 19,
            target,
            width: 1920,
            height: 1080,
            frameRate: 30);

        var exception = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(
            () => factory.StartAsync(request).AsTask());

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable,
            exception.ReasonCode);
        Assert.DoesNotContain("-1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDisposalSlotRetainsOwnershipUntilAReleaseAttemptSucceeds()
    {
        var resource = new object();
        var slot = new RetryableNativeDisposalSlot<object>(resource);
        var attempts = 0;

        var failure = Assert.Throws<IOException>(() => slot.Dispose(value =>
        {
            Assert.Same(resource, value);
            attempts++;
            throw new IOException("fixture native dispose failed");
        }));

        Assert.Equal("fixture native dispose failed", failure.Message);
        Assert.False(slot.IsEmpty);
        Assert.Same(resource, slot.Value);

        slot.Dispose(value =>
        {
            Assert.Same(resource, value);
            attempts++;
        });
        slot.Dispose(_ => attempts++);

        Assert.True(slot.IsEmpty);
        Assert.Equal(2, attempts);
    }

    public enum CaptureIdentityMutation
    {
        ProcessId,
        ProcessStartTime,
        ProcessPath,
        ExecutableTrust,
        WindowTitle,
        WindowExited,
    }

    private sealed class FixedWindowSnapshotSource(
        params WallpaperEngineWindowSnapshot[] snapshots)
        : IWallpaperEngineWindowSnapshotSource
    {
        public ValueTask<IReadOnlyList<WallpaperEngineWindowSnapshot>> CaptureAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<WallpaperEngineWindowSnapshot>>(
                snapshots);
        }
    }

    private sealed class FixedCaptureAuthority(nint windowHandle)
        : IWallpaperEngineWindowCaptureAuthority
    {
        public ValueTask<nint> RevalidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(windowHandle);
        }
    }
}
