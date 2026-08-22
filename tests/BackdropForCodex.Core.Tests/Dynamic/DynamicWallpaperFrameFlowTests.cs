using BackdropForCodex.Core.Dynamic;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class DynamicWallpaperFrameFlowTests
{
    [Fact]
    public void FrameOrderAllowsSessionSequenceResetWhenGenerationTimeAdvances()
    {
        var order = new GenerationScopedWallpaperFrameOrder(generation: 67);

        order.Accept(new StubFrame(67, sequence: 19, TimeSpan.FromSeconds(10)));
        order.Accept(new StubFrame(67, sequence: 0, TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public void FrameOrderRejectsStaleTimeOrAnotherGeneration()
    {
        var order = new GenerationScopedWallpaperFrameOrder(generation: 71);
        order.Accept(new StubFrame(71, sequence: 1, TimeSpan.FromSeconds(10)));

        Assert.Throws<DynamicWallpaperUnavailableException>(() =>
            order.Accept(new StubFrame(71, sequence: 2, TimeSpan.FromSeconds(10))));
        Assert.Throws<DynamicWallpaperUnavailableException>(() =>
            order.Accept(new StubFrame(72, sequence: 0, TimeSpan.FromSeconds(11))));
    }

    [Fact]
    public void FrameOrderAllowsOnlyTheSameFrameInstanceToReplayAtOneTimestamp()
    {
        var order = new GenerationScopedWallpaperFrameOrder(generation: 73);
        var latest = new StubFrame(73, sequence: 7, TimeSpan.FromSeconds(10));

        order.Accept(latest);
        order.Accept(latest);

        Assert.Throws<DynamicWallpaperUnavailableException>(() =>
            order.Accept(new StubFrame(73, sequence: 7, TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public void FixedPointPacerProducesExactlyFifteenAbsoluteSlotsWithoutRoundingDrift()
    {
        const int durationSeconds = 60;
        var pacer = new FixedPointWallpaperFramePacer(frameRate: 15);
        var now = TimeSpan.Zero;
        pacer.Start(now);

        for (var sample = 0; sample < 15 * durationSeconds; sample++)
        {
            Assert.False(pacer.TryTakeDueSample(
                now,
                out var wait,
                out var reanchored));
            Assert.True(wait > TimeSpan.Zero);
            Assert.False(reanchored);
            now += wait;
            Assert.True(pacer.TryTakeDueSample(now, out wait, out reanchored));
            Assert.Equal(TimeSpan.Zero, wait);
            Assert.False(reanchored);
        }

        Assert.Equal(TimeSpan.FromSeconds(durationSeconds), now);
    }

    [Fact]
    public void FixedPointPacerNeverBurstsAndReanchorsAfterMissedSlots()
    {
        var pacer = new FixedPointWallpaperFramePacer(frameRate: 15);
        pacer.Start(TimeSpan.Zero);

        Assert.True(pacer.TryTakeDueSample(
            TimeSpan.FromTicks(666_667),
            out var wait,
            out var reanchored));
        Assert.Equal(TimeSpan.Zero, wait);
        Assert.False(reanchored);
        Assert.False(pacer.TryTakeDueSample(
            TimeSpan.FromTicks(666_667),
            out wait,
            out reanchored));
        Assert.True(wait > TimeSpan.Zero);
        Assert.False(reanchored);

        var missed = new FixedPointWallpaperFramePacer(frameRate: 15);
        missed.Start(TimeSpan.Zero);
        Assert.True(missed.TryTakeDueSample(
            TimeSpan.FromTicks(2_000_001),
            out wait,
            out reanchored));

        Assert.Equal(TimeSpan.Zero, wait);
        Assert.True(reanchored);
        Assert.False(missed.TryTakeDueSample(
            TimeSpan.FromTicks(2_000_001),
            out wait,
            out reanchored));
        Assert.Equal(TimeSpan.FromTicks(666_667), wait);
        Assert.False(reanchored);
    }

    [Fact]
    public void FixedPointPacerPauseResetReanchorsWithoutChargingTheGap()
    {
        var pacer = new FixedPointWallpaperFramePacer(frameRate: 15);
        pacer.Start(TimeSpan.Zero);
        Assert.True(pacer.TryTakeDueSample(
            TimeSpan.FromTicks(666_667),
            out _,
            out _));

        pacer.ResetForCapturePause();
        pacer.Start(TimeSpan.FromSeconds(100));

        Assert.False(pacer.TryTakeDueSample(
            TimeSpan.FromSeconds(100),
            out var wait,
            out var reanchored));
        Assert.Equal(TimeSpan.FromTicks(666_667), wait);
        Assert.False(reanchored);
    }

    [Fact]
    public void SustainedCadenceMonitorFailsOnlyAfterFiveContinuousSecondsBehind()
    {
        var monitor = new SustainedWallpaperCadenceMonitor(TimeSpan.FromSeconds(5));

        Assert.False(monitor.Observe(isBehind: true, TimeSpan.FromSeconds(1)));
        Assert.False(monitor.Observe(isBehind: true, TimeSpan.FromSeconds(5.999)));
        Assert.True(monitor.Observe(isBehind: true, TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void SustainedCadenceMonitorClearsTransientLatenessAndPauseHistory()
    {
        var monitor = new SustainedWallpaperCadenceMonitor(TimeSpan.FromSeconds(5));
        Assert.False(monitor.Observe(isBehind: true, TimeSpan.FromSeconds(1)));

        Assert.False(monitor.Observe(isBehind: false, TimeSpan.FromSeconds(4)));
        Assert.False(monitor.Observe(isBehind: true, TimeSpan.FromSeconds(8)));
        monitor.ResetForCapturePause();
        Assert.False(monitor.Observe(isBehind: true, TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public async Task LatestFrameSlotDisposesSupersededAndFinalFramesButRetainsReplayOwnership()
    {
        var first = new StubFrame(79, sequence: 0, TimeSpan.FromSeconds(1));
        var second = new StubFrame(79, sequence: 1, TimeSpan.FromSeconds(2));
        await using (var slot = new LatestWallpaperCapturedFrameSlot())
        {
            await slot.ReplaceAsync(first);
            Assert.Same(first, slot.Current);
            Assert.Same(first, slot.Current);
            Assert.Equal(0, first.DisposeCount);

            await slot.ReplaceAsync(second);

            Assert.Equal(1, first.DisposeCount);
            Assert.Same(second, slot.Current);
            Assert.Equal(0, second.DisposeCount);
        }

        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task LatestFrameSlotRetainsPreviousOwnershipWhenReplacementCleanupFails()
    {
        var previous = new StubFrame(
            83,
            sequence: 0,
            TimeSpan.FromSeconds(1),
            disposeFailures: 1);
        var rejectedReplacement = new StubFrame(
            83,
            sequence: 1,
            TimeSpan.FromSeconds(2));
        var slot = new LatestWallpaperCapturedFrameSlot();
        await slot.ReplaceAsync(previous);

        await Assert.ThrowsAsync<IOException>(() =>
            slot.ReplaceAsync(rejectedReplacement).AsTask());

        Assert.Same(previous, slot.Current);
        Assert.Equal(1, previous.DisposeCount);
        Assert.Equal(1, rejectedReplacement.DisposeCount);
        await slot.DisposeAsync();
        Assert.Null(slot.Current);
        Assert.Equal(2, previous.DisposeCount);
    }

    [Fact]
    public async Task LatestFrameSlotDisposeRetainsTheFrameUntilCleanupRetrySucceeds()
    {
        var frame = new StubFrame(
            89,
            sequence: 0,
            TimeSpan.FromSeconds(1),
            disposeFailures: 1);
        var slot = new LatestWallpaperCapturedFrameSlot();
        await slot.ReplaceAsync(frame);

        await Assert.ThrowsAsync<IOException>(() => slot.DisposeAsync().AsTask());

        Assert.Same(frame, slot.Current);
        await slot.DisposeAsync();
        Assert.Null(slot.Current);
        Assert.Equal(2, frame.DisposeCount);
    }

    [Fact]
    public void BackpressureEpochSurvivesAlternatingDropsAndKeyFrameRestarts()
    {
        var monitor = new DynamicWallpaperBackpressureMonitor();

        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            TimeSpan.FromSeconds(1)));
        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.RecoveredAtKeyFrame,
            TimeSpan.FromSeconds(3)));
        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            TimeSpan.FromSeconds(5.9)));
        Assert.True(monitor.Observe(
            EncodedWallpaperWriteResult.RecoveredAtKeyFrame,
            TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void AcceptedWritesDoNotMaskInsufficientThroughputWithoutConsumerCatchUp()
    {
        var monitor = new DynamicWallpaperBackpressureMonitor();

        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            TimeSpan.FromSeconds(1)));
        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.RecoveredAtKeyFrame,
            TimeSpan.FromSeconds(2)));
        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.Accepted,
            TimeSpan.FromSeconds(4.9)));
        Assert.True(monitor.Observe(
            EncodedWallpaperWriteResult.Accepted,
            TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void ConsumerQueueDrainEndsTheCurrentBackpressureEpoch()
    {
        var monitor = new DynamicWallpaperBackpressureMonitor();

        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            TimeSpan.FromSeconds(1)));
        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.RecoveredAfterQueueDrain,
            TimeSpan.FromSeconds(3)));
        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            TimeSpan.FromSeconds(4)));
        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.Accepted,
            TimeSpan.FromSeconds(8.9)));
        Assert.True(monitor.Observe(
            EncodedWallpaperWriteResult.Accepted,
            TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void PauseResetDoesNotChargeThePausedWallClockToBackpressure()
    {
        var monitor = new DynamicWallpaperBackpressureMonitor();
        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            TimeSpan.FromSeconds(1)));

        monitor.ResetForCapturePause();

        Assert.False(monitor.Observe(
            EncodedWallpaperWriteResult.Accepted,
            TimeSpan.FromSeconds(100)));
    }

    private sealed class StubFrame : IWallpaperCapturedFrame
    {
        private int _disposeFailures;

        internal StubFrame(
            long generation,
            long sequence,
            TimeSpan timestamp,
            int disposeFailures = 0)
        {
            Generation = generation;
            Sequence = sequence;
            Timestamp = timestamp;
            _disposeFailures = disposeFailures;
        }

        public long Generation { get; }

        public long Sequence { get; }

        public TimeSpan Timestamp { get; }

        public int Width => 1280;

        public int Height => 720;

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (_disposeFailures-- > 0)
            {
                throw new IOException("fixture frame cleanup failed");
            }

            return ValueTask.CompletedTask;
        }
    }
}
