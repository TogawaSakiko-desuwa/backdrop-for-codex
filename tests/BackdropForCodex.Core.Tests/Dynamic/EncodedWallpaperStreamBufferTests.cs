using BackdropForCodex.Core.Dynamic;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class EncodedWallpaperStreamBufferTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public async Task MediaFoundationBatchIsDeliveredInOrderWhenItExceedsQueueSlots(
        int mediaSegmentCount)
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var batch = new[] { Initialization(sequence: 0, size: 16) }
            .Concat(Enumerable.Range(1, mediaSegmentCount).Select(sequence =>
                Media(
                    sequence,
                    isKeyFrame: sequence == 1,
                    size: 16)))
            .ToArray();

        var writing = buffer.WriteBatchAsync(batch).AsTask();
        var delivered = new List<EncodedWallpaperSegment>();
        for (var index = 0; index < batch.Length; index++)
        {
            delivered.Add((await buffer.ReadAsync())!);
        }

        var result = await writing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(EncodedWallpaperWriteResult.Accepted, result);
        Assert.Equal(batch.Select(segment => segment.Sequence),
            delivered.Select(segment => segment.Sequence));
    }

    [Fact]
    public async Task BatchReportsRecoveryOnlyAfterARealDropAndConsumerDrain()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        buffer.TryWrite(Initialization(sequence: 0, size: 1));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true, size: 1));
        Assert.Equal(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            buffer.TryWrite(Media(sequence: 2, isKeyFrame: false, size: 1)));
        _ = await buffer.ReadAsync();
        _ = await buffer.ReadAsync();

        var result = await buffer.WriteBatchAsync(
            [Media(sequence: 3, isKeyFrame: true, size: 1)]);

        Assert.Equal(EncodedWallpaperWriteResult.RecoveredAfterQueueDrain, result);
    }

    [Fact]
    public async Task MixedDropBatchDefersRecoveryUntilItCanBeObservedByTheMonitor()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        buffer.TryWrite(Initialization(sequence: 0, size: 1));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true, size: 1));
        Assert.Equal(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            buffer.TryWrite(Media(sequence: 2, isKeyFrame: false, size: 1)));
        _ = await buffer.ReadAsync();
        _ = await buffer.ReadAsync();

        var mixedResult = await buffer.WriteBatchAsync(
            [
                Media(sequence: 3, isKeyFrame: false, size: 1),
                Media(sequence: 4, isKeyFrame: true, size: 1),
            ]);

        Assert.Equal(EncodedWallpaperWriteResult.DroppedUntilKeyFrame, mixedResult);
        Assert.Equal(4, (await buffer.ReadAsync())!.Sequence);
        Assert.Equal(
            EncodedWallpaperWriteResult.RecoveredAfterQueueDrain,
            buffer.TryWrite(Media(sequence: 5, isKeyFrame: false, size: 1)));
    }

    [Fact]
    public async Task StartupBatchLargerThanByteBoundFailsClosedBeforePublishingPrefix()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var read = buffer.ReadAsync().AsTask();
        var batch = new[]
        {
            Initialization(
                sequence: 0,
                size: EncodedWallpaperStreamBuffer.MaximumBufferedBytes - 1),
            Media(sequence: 1, isKeyFrame: true, size: 2),
        };

        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            buffer
                .WriteBatchAsync(batch)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.EncodingFailed,
            failure.ReasonCode);
        Assert.Equal(0, buffer.PendingSegmentCount);
        Assert.Equal(0, buffer.PendingByteCount);
        Assert.Same(
            failure,
            await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
                read.WaitAsync(TimeSpan.FromSeconds(1))));
        Assert.Equal(
            EncodedWallpaperWriteResult.Closed,
            buffer.TryWrite(Media(sequence: 2, isKeyFrame: true, size: 1)));
    }

    [Fact]
    public async Task FirstKeyThatCannotJoinAcceptedInitializationFailsWithoutWaiting()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        Assert.Equal(
            EncodedWallpaperWriteResult.Accepted,
            buffer.TryWrite(Initialization(
                sequence: 0,
                size: EncodedWallpaperStreamBuffer.MaximumBufferedBytes - 1)));
        var read = buffer.ReadAsync().AsTask();

        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            buffer
                .WriteBatchAsync(
                    [Media(sequence: 1, isKeyFrame: true, size: 2)])
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.EncodingFailed,
            failure.ReasonCode);
        Assert.Equal(0, buffer.PendingSegmentCount);
        Assert.Equal(0, buffer.PendingByteCount);
        Assert.Same(
            failure,
            await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
                read.WaitAsync(TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public void SegmentLargerThanByteBoundIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Initialization(
            sequence: 0,
            size: EncodedWallpaperStreamBuffer.MaximumBufferedBytes + 1));
    }

    [Fact]
    public async Task CancellationAfterBatchProgressFailsClosedInsteadOfLeavingAPartialTimeline()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        using var cancellation = new CancellationTokenSource();
        var writing = buffer.WriteBatchAsync(
            [
                Initialization(sequence: 0, size: 1),
                Media(sequence: 1, isKeyFrame: true, size: 1),
                Media(sequence: 2, isKeyFrame: false, size: 1),
            ],
            cancellation.Token).AsTask();
        Assert.Equal(2, buffer.PendingSegmentCount);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            writing.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, buffer.PendingSegmentCount);
        Assert.Equal(0, buffer.PendingByteCount);
        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            buffer.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.EncodingFailed,
            failure.ReasonCode);
        Assert.Equal(
            EncodedWallpaperWriteResult.Closed,
            buffer.TryWrite(Media(sequence: 3, isKeyFrame: true, size: 1)));
    }

    [Fact]
    public async Task CancellationBeforeFirstAcceptanceAlsoFailsClosedForTheAdvancedEncoderTimeline()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            buffer.WriteBatchAsync(
                [
                    Initialization(sequence: 0, size: 1),
                    Media(sequence: 1, isKeyFrame: true, size: 1),
                ],
                cancellation.Token).AsTask());

        Assert.Equal(0, buffer.PendingSegmentCount);
        Assert.Equal(
            EncodedWallpaperWriteResult.Closed,
            buffer.TryWrite(Initialization(sequence: 0, size: 1)));
        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            buffer.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.EncodingFailed,
            failure.ReasonCode);
    }

    [Fact]
    public async Task CancellationWhileWaitingForBatchGateClosesTheActiveBatch()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var activeWrite = buffer.WriteBatchAsync(
            [
                Initialization(sequence: 0, size: 1),
                Media(sequence: 1, isKeyFrame: true, size: 1),
                Media(sequence: 2, isKeyFrame: false, size: 1),
            ]).AsTask();
        Assert.Equal(2, buffer.PendingSegmentCount);
        using var cancellation = new CancellationTokenSource();
        var waitingWrite = buffer.WriteBatchAsync(
            [Media(sequence: 3, isKeyFrame: true, size: 1)],
            cancellation.Token).AsTask();
        Assert.False(waitingWrite.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            waitingWrite.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            EncodedWallpaperWriteResult.Closed,
            await activeWrite.WaitAsync(TimeSpan.FromSeconds(1)));
        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            buffer.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.EncodingFailed,
            failure.ReasonCode);
    }

    [Fact]
    public async Task CompleteWakesABatchWaitingForQueueSpace()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var writing = buffer.WriteBatchAsync(
            [
                Initialization(sequence: 0, size: 1),
                Media(sequence: 1, isKeyFrame: true, size: 1),
                Media(sequence: 2, isKeyFrame: false, size: 1),
            ]).AsTask();
        Assert.Equal(2, buffer.PendingSegmentCount);
        var failure = new IOException("page stream failed");

        buffer.Complete(failure);

        Assert.Equal(
            EncodedWallpaperWriteResult.Closed,
            await writing.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Same(
            failure,
            await Assert.ThrowsAsync<IOException>(() =>
                buffer.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1))));
    }

    [Fact]
    public async Task DisposeWakesABatchWaitingForQueueSpace()
    {
        var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var writing = buffer.WriteBatchAsync(
            [
                Initialization(sequence: 0, size: 1),
                Media(sequence: 1, isKeyFrame: true, size: 1),
                Media(sequence: 2, isKeyFrame: false, size: 1),
            ]).AsTask();
        Assert.Equal(2, buffer.PendingSegmentCount);

        await buffer.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            writing.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ReaderDoesNotObserveInitializationBeforeAKeyFrameIsBuffered()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());

        Assert.Equal(
            EncodedWallpaperWriteResult.Accepted,
            buffer.TryWrite(Initialization(sequence: 0, size: 16)));
        var read = buffer.ReadAsync().AsTask();

        await Task.Delay(50);
        Assert.False(read.IsCompleted);

        Assert.Equal(
            EncodedWallpaperWriteResult.Accepted,
            buffer.TryWrite(Media(sequence: 1, isKeyFrame: true, size: 16)));
        Assert.Equal(
            EncodedWallpaperSegmentKind.Initialization,
            (await read.WaitAsync(TimeSpan.FromSeconds(5)))!.Kind);
    }

    [Fact]
    public async Task BackpressureNeverExceedsTwoSegmentsOrEightMebibytes()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        buffer.TryWrite(Initialization(sequence: 0, size: 1));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true, size: 1));

        var result = buffer.TryWrite(Media(sequence: 2, isKeyFrame: false, size: 1));

        Assert.Equal(EncodedWallpaperWriteResult.DroppedUntilKeyFrame, result);
        Assert.Equal(2, buffer.PendingSegmentCount);
        Assert.InRange(buffer.PendingByteCount, 0, EncodedWallpaperStreamBuffer.MaximumBufferedBytes);

        Assert.Equal(EncodedWallpaperSegmentKind.Initialization, (await buffer.ReadAsync())!.Kind);
        Assert.True((await buffer.ReadAsync())!.IsKeyFrame);
        Assert.Equal(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            buffer.TryWrite(Media(sequence: 3, isKeyFrame: false, size: 1)));
        Assert.Equal(
            EncodedWallpaperWriteResult.RecoveredAfterQueueDrain,
            buffer.TryWrite(Media(sequence: 4, isKeyFrame: true, size: 32)));
        Assert.Equal(1, buffer.PendingSegmentCount);
    }

    [Fact]
    public async Task FullQueuePreservesAcceptedGopUntilTheConsumerCatchesUp()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        buffer.TryWrite(Initialization(sequence: 0, size: 1));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true, size: 1));
        _ = await buffer.ReadAsync();
        _ = await buffer.ReadAsync();
        buffer.TryWrite(Media(sequence: 2, isKeyFrame: true, size: 1));
        buffer.TryWrite(Media(sequence: 3, isKeyFrame: false, size: 1));

        Assert.Equal(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            buffer.TryWrite(Media(sequence: 4, isKeyFrame: true, size: 1)));
        Assert.Equal(2, buffer.PendingSegmentCount);
        Assert.Equal(2, (await buffer.ReadAsync())!.Sequence);
        Assert.Equal(3, (await buffer.ReadAsync())!.Sequence);
        Assert.Equal(
            EncodedWallpaperWriteResult.RecoveredAfterQueueDrain,
            buffer.TryWrite(Media(sequence: 5, isKeyFrame: true, size: 1)));
    }

    [Fact]
    public async Task OnlyConsumerCatchUpProducesAnExplicitBackpressureRecovery()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        buffer.TryWrite(Initialization(sequence: 0, size: 1));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true, size: 1));
        Assert.Equal(
            EncodedWallpaperWriteResult.DroppedUntilKeyFrame,
            buffer.TryWrite(Media(sequence: 2, isKeyFrame: false, size: 1)));

        Assert.Equal(EncodedWallpaperSegmentKind.Initialization, (await buffer.ReadAsync())!.Kind);
        Assert.Equal(
            EncodedWallpaperWriteResult.RecoveredAtKeyFrame,
            buffer.TryWrite(Media(sequence: 3, isKeyFrame: true, size: 1)));

        Assert.True((await buffer.ReadAsync())!.IsKeyFrame);
        Assert.True((await buffer.ReadAsync())!.IsKeyFrame);
        Assert.Equal(
            EncodedWallpaperWriteResult.RecoveredAfterQueueDrain,
            buffer.TryWrite(Media(sequence: 4, isKeyFrame: false, size: 1)));
    }

    [Fact]
    public async Task GenerationMismatchAndOutOfOrderSegmentsFailClosed()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor(generation: 41));

        Assert.Throws<ArgumentException>(() =>
            buffer.TryWrite(Initialization(sequence: 0, size: 16, generation: 42)));
        Assert.Equal(
            EncodedWallpaperWriteResult.Accepted,
            buffer.TryWrite(Initialization(sequence: 0, size: 16, generation: 41)));
        Assert.Throws<ArgumentException>(() =>
            buffer.TryWrite(Media(sequence: 0, isKeyFrame: true, size: 16, generation: 41)));
    }

    [Fact]
    public async Task FaultBeforeStartupUnblocksReadersWithTheOriginalFailure()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var failure = new IOException("encoder disconnected");
        var read = buffer.ReadAsync().AsTask();

        buffer.Complete(failure);

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, thrown);
    }

    private static EncodedWallpaperStreamDescriptor Descriptor(long generation = 41) =>
        new(
            generation,
            "video/mp4; codecs=\"avc1.640028\"",
            width: 1920,
            height: 1080,
            frameRate: 30);

    private static EncodedWallpaperSegment Initialization(
        long sequence,
        int size,
        long generation = 41) =>
        new(
            generation,
            sequence,
            EncodedWallpaperSegmentKind.Initialization,
            isKeyFrame: false,
            new byte[size]);

    private static EncodedWallpaperSegment Media(
        long sequence,
        bool isKeyFrame,
        int size,
        long generation = 41) =>
        new(
            generation,
            sequence,
            EncodedWallpaperSegmentKind.Media,
            isKeyFrame,
            new byte[size]);
}
