using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Runtime;
using PuppeteerSharp;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class EncodedWallpaperPageStreamLeaseTests
{
    [Fact]
    public async Task StartWaitsForStartupAndEveryAppendAcknowledgementBeforePublication()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink { BlockFirstAppend = true };
        buffer.TryWrite(Initialization(sequence: 0));

        var starting = EncodedWallpaperPageStreamLease.StartAsync(
            buffer,
            sink,
            Options());
        await Task.Delay(50);
        Assert.Empty(sink.Events);

        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));
        await sink.FirstAppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(starting.IsCompleted);
        Assert.Equal(["prepare", "append:0"], sink.Events);

        sink.AllowFirstAppend.TrySetResult();
        await using var lease = await starting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            ["prepare", "append:0", "append:1"],
            sink.Events);
        Assert.Equal(ActiveWallpaperDeliveryKind.DynamicStream, lease.DeliveryKind);
        Assert.Equal(51, lease.Generation);
    }

    [Fact]
    public async Task BrokenStreamCleansItsGenerationAndFaultsCompletion()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink();
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));
        await using var lease = await EncodedWallpaperPageStreamLease.StartAsync(
            buffer,
            sink,
            Options());
        var failure = new IOException("capture stream disconnected");

        buffer.Complete(failure);

        var thrown = await Assert.ThrowsAsync<IOException>(
            () => lease.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, thrown);
        Assert.Contains("cleanup:51", sink.Events);
    }

    [Fact]
    public async Task StartFailsClosedWhenPagePublishesBeforeTheKeyFrame()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink { PublishInitialization = true };
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            EncodedWallpaperPageStreamLease.StartAsync(buffer, sink, Options()));

        Assert.Contains("cleanup:51", sink.Events);
    }

    [Fact]
    public async Task StartReportsPossibleMutationWhenKeyFrameAcknowledgementIsLost()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink
        {
            AppendFailureSequence = 1,
            AppendFailure = new PuppeteerException("fixture acknowledgement lost"),
        };
        var mutationReports = 0;
        var mutationSignal = new RuntimeMutationSignal(
            generation: 51,
            () => Interlocked.Increment(ref mutationReports));
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            EncodedWallpaperPageStreamLease.StartAsync(
                buffer,
                sink,
                Options(mutationSignal)));

        Assert.Equal(1, mutationReports);
        Assert.Contains("append:1", sink.Events);
        Assert.Contains("cleanup:51", sink.Events);
    }

    [Fact]
    public async Task StartDoesNotReportMutationWhenInitializationAppendFails()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink
        {
            AppendFailureSequence = 0,
            AppendFailure = new PuppeteerException("fixture initialization failure"),
        };
        var mutationReports = 0;
        var mutationSignal = new RuntimeMutationSignal(
            generation: 51,
            () => Interlocked.Increment(ref mutationReports));
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            EncodedWallpaperPageStreamLease.StartAsync(
                buffer,
                sink,
                Options(mutationSignal)));

        Assert.Equal(0, mutationReports);
        Assert.DoesNotContain("append:1", sink.Events);
    }

    [Fact]
    public async Task StartDoesNotReportMutationWhenKeyFrameReceiptSaysUnpublished()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink { SuppressKeyFramePublication = true };
        var mutationReports = 0;
        var mutationSignal = new RuntimeMutationSignal(
            generation: 51,
            () => Interlocked.Increment(ref mutationReports));
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            EncodedWallpaperPageStreamLease.StartAsync(
                buffer,
                sink,
                Options(mutationSignal)));

        Assert.Equal(0, mutationReports);
        Assert.Contains("append:1", sink.Events);
    }

    [Fact]
    public async Task StartReportsMutationBeforeRejectingAnInvalidPublishedReceipt()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink { InvalidReceiptSequence = 1 };
        var mutationReports = 0;
        var mutationSignal = new RuntimeMutationSignal(
            generation: 51,
            () => Interlocked.Increment(ref mutationReports));
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            EncodedWallpaperPageStreamLease.StartAsync(
                buffer,
                sink,
                Options(mutationSignal)));

        Assert.Equal(1, mutationReports);
        Assert.Contains("append:1", sink.Events);
    }

    [Fact]
    public async Task CompletionNormalizesActivePuppeteerTransportFailureForLocalRecovery()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink
        {
            AppendFailureSequence = 2,
            AppendFailure = new PuppeteerException("fixture transport disconnect"),
        };
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));
        await using var lease = await EncodedWallpaperPageStreamLease.StartAsync(
            buffer,
            sink,
            Options());

        buffer.TryWrite(Media(sequence: 2, isKeyFrame: true));

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            lease.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            nameof(PuppeteerException),
            failure.PrimaryDiagnosticExceptionType);
        Assert.Contains("cleanup:51", sink.Events);
    }

    [Fact]
    public async Task CompletionNormalizesAnInvalidActiveReceiptForLocalRecovery()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink { InvalidReceiptSequence = 2 };
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));
        await using var lease = await EncodedWallpaperPageStreamLease.StartAsync(
            buffer,
            sink,
            Options());

        buffer.TryWrite(Media(sequence: 2, isKeyFrame: true));

        await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            lease.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("cleanup:51", sink.Events);
    }

    [Fact]
    public async Task StartFailsWithTypedBackpressureWhenStartupAppendIsNotAcknowledged()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink { BlockSequence = 0 };
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));

        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            EncodedWallpaperPageStreamLease.StartAsync(
                buffer,
                sink,
                Options(),
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None));

        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure,
            failure.ReasonCode);
        Assert.DoesNotContain("append:1", sink.Events);
        Assert.Contains("cleanup:51", sink.Events);
    }

    [Fact]
    public async Task CompletionFaultsWithTypedBackpressureWhenActiveAppendStopsAcknowledging()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink { BlockSequence = 2 };
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));
        await using var lease = await EncodedWallpaperPageStreamLease.StartAsync(
            buffer,
            sink,
            Options(),
            TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        buffer.TryWrite(Media(sequence: 2, isKeyFrame: true));

        var failure = await Assert.ThrowsAsync<DynamicWallpaperUnavailableException>(() =>
            lease.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure,
            failure.ReasonCode);
        Assert.Contains("cleanup:51", sink.Events);
    }

    [Fact]
    public async Task PauseIsAcknowledgedAndResumeCanWaitForANewAcknowledgedKeyFrame()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink();
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));
        await using var lease = await EncodedWallpaperPageStreamLease.StartAsync(
            buffer,
            sink,
            Options());
        var playback = Assert.IsAssignableFrom<IDynamicWallpaperPagePlaybackLease>(lease);

        Assert.Equal(1, playback.LastAcknowledgedKeyFrameSequence);
        await playback.SetPausedAsync(paused: true);
        var nextKeyFrame = playback
            .WaitForKeyFrameAfterAsync(sequence: 1, CancellationToken.None)
            .AsTask();

        buffer.TryWrite(Media(sequence: 2, isKeyFrame: false));
        await playback.WaitForBufferedSegmentsAsync(CancellationToken.None);
        Assert.False(nextKeyFrame.IsCompleted);
        Assert.Contains("append:2", sink.Events);

        buffer.TryWrite(Media(sequence: 3, isKeyFrame: true));
        await nextKeyFrame.WaitAsync(TimeSpan.FromSeconds(5));
        await playback.SetPausedAsync(paused: false);

        Assert.Equal(3, playback.LastAcknowledgedKeyFrameSequence);
        Assert.Contains("paused:51:True", sink.Events);
        Assert.Contains("paused:51:False", sink.Events);
    }

    [Fact]
    public async Task PauseNormalizesTransientPageTransportFailureForLocalRecovery()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink
        {
            PauseFailure = new PuppeteerException(
                "ws://127.0.0.1:9222/devtools/page/private-pause-target"),
            PauseFailures = 1,
        };
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));
        await using var lease = await EncodedWallpaperPageStreamLease.StartAsync(
            buffer,
            sink,
            Options());
        var playback = Assert.IsAssignableFrom<IDynamicWallpaperPagePlaybackLease>(lease);

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            playback.SetPausedAsync(paused: true).AsTask());

        Assert.Equal(
            DynamicWallpaperPageSessionFailureKind.Transient,
            failure.FailureKind);
        Assert.Equal(nameof(PuppeteerException), failure.PrimaryDiagnosticExceptionType);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("private-pause-target", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeRetainsTheSinkAfterCleanupTimeoutAndRetriesBeforeDisposal()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var sink = new RecordingSink { CleanupTimeouts = 1 };
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));
        var lease = await EncodedWallpaperPageStreamLease.StartAsync(
            buffer,
            sink,
            Options(),
            appendAcknowledgementTimeout: TimeSpan.FromSeconds(1),
            cleanupTimeout: TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        var failure = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            DynamicWallpaperPageSessionFailureKind.CleanupNotProven,
            failure.FailureKind);
        await sink.CleanupCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(sink.CleanupCancellationObserved);
        Assert.Equal(0, sink.DisposeCount);

        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, sink.Events.Count(item => item == "cleanup:51"));
        Assert.Equal(1, sink.DisposeCount);
    }

    [Fact]
    public async Task CleanupFailureRemainsObservableAndTheRetainedSinkCanBeRetried()
    {
        await using var buffer = new EncodedWallpaperStreamBuffer(Descriptor());
        var failure = new IOException("page cleanup failed");
        var sink = new RecordingSink
        {
            CleanupFailure = failure,
            CleanupFailures = 1,
        };
        buffer.TryWrite(Initialization(sequence: 0));
        buffer.TryWrite(Media(sequence: 1, isKeyFrame: true));
        var lease = await EncodedWallpaperPageStreamLease.StartAsync(
            buffer,
            sink,
            Options());

        buffer.Complete();

        var thrown = await Assert.ThrowsAsync<DynamicWallpaperPageSessionException>(() =>
            lease.Completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(
            DynamicWallpaperPageSessionFailureKind.CleanupNotProven,
            thrown.FailureKind);
        Assert.Equal(nameof(IOException), thrown.PrimaryDiagnosticExceptionType);
        Assert.Equal(failure.HResult, thrown.PrimaryDiagnosticHResult);
        Assert.Null(thrown.InnerException);
        Assert.Equal(0, sink.DisposeCount);

        await lease.DisposeAsync();

        Assert.Equal(1, sink.DisposeCount);
    }

    private static EncodedWallpaperStreamDescriptor Descriptor() =>
        new(
            generation: 51,
            "video/mp4; codecs=\"avc1.640028\"",
            width: 1920,
            height: 1080,
            frameRate: 30);

    private static DynamicWallpaperInjectionOptions Options() =>
        new(generation: 51);

    private static DynamicWallpaperInjectionOptions Options(
        RuntimeMutationSignal mutationSignal) =>
        new(
            generation: 51,
            WallpaperObjectFit.Cover,
            mediaOpacity: 1,
            glass: null,
            composition: null,
            lockedPresentationContract: null,
            capabilityCeiling: null,
            mutationSignal);

    private static EncodedWallpaperSegment Initialization(long sequence) =>
        new(
            generation: 51,
            sequence,
            EncodedWallpaperSegmentKind.Initialization,
            isKeyFrame: false,
            new byte[] { 1 });

    private static EncodedWallpaperSegment Media(long sequence, bool isKeyFrame) =>
        new(
            generation: 51,
            sequence,
            EncodedWallpaperSegmentKind.Media,
            isKeyFrame,
            new byte[] { 2 });

    private sealed class RecordingSink : IEncodedWallpaperPageSink
    {
        private bool _published;

        public List<string> Events { get; } = [];

        public TaskCompletionSource FirstAppendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowFirstAppend { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool PublishInitialization { get; init; }

        public bool SuppressKeyFramePublication { get; init; }

        public bool BlockFirstAppend { get; init; }

        public long? BlockSequence { get; init; }

        public long? AppendFailureSequence { get; init; }

        public Exception? AppendFailure { get; init; }

        public long? InvalidReceiptSequence { get; init; }

        public bool BlockCleanupUntilCanceled { get; init; }

        public int CleanupTimeouts { get; set; }

        public bool CleanupCancellationObserved { get; private set; }

        public TaskCompletionSource CleanupCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public Exception? CleanupFailure { get; init; }

        public int CleanupFailures { get; set; }

        public Exception? PauseFailure { get; init; }

        public int PauseFailures { get; set; }

        public Task<EncodedWallpaperPrepareReceipt> PrepareAsync(
            DynamicWallpaperInjectionOptions options,
            EncodedWallpaperStreamDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            Events.Add("prepare");
            return Task.FromResult(new EncodedWallpaperPrepareReceipt(
                Prepared: true,
                Reason: "prepared",
                descriptor.Generation));
        }

        public async Task<EncodedWallpaperAppendReceipt> AppendAsync(
            EncodedWallpaperSegment segment,
            CancellationToken cancellationToken = default)
        {
            Events.Add($"append:{segment.Sequence}");
            if (segment.Sequence == 0)
            {
                FirstAppendStarted.TrySetResult();
                if (BlockFirstAppend)
                {
                    await AllowFirstAppend.Task.WaitAsync(cancellationToken);
                }
            }

            if (segment.Sequence == BlockSequence)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (segment.Sequence == AppendFailureSequence)
            {
                throw AppendFailure ?? new InvalidOperationException(
                    "The configured append failure was not supplied.");
            }

            if (segment.IsKeyFrame && !SuppressKeyFramePublication)
            {
                _published = true;
            }

            return new EncodedWallpaperAppendReceipt(
                Appended: true,
                Published: PublishInitialization || _published,
                Reason: segment.IsKeyFrame ? "published" : "appended",
                segment.Sequence == InvalidReceiptSequence
                    ? checked(segment.Sequence + 1)
                    : segment.Sequence);
        }

        public Task<bool> SetPausedAsync(
            long generation,
            bool paused,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"paused:{generation}:{paused}");
            if (PauseFailure is not null && PauseFailures-- > 0)
            {
                throw PauseFailure;
            }

            return Task.FromResult(true);
        }

        public async Task<bool> CleanupAsync(
            long generation,
            CancellationToken cancellationToken = default)
        {
            Events.Add($"cleanup:{generation}");
            if (CleanupFailure is not null && CleanupFailures-- > 0)
            {
                throw CleanupFailure;
            }

            if (BlockCleanupUntilCanceled || CleanupTimeouts-- > 0)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CleanupCancellationObserved = true;
                    CleanupCanceled.TrySetResult();
                    throw;
                }
            }

            return true;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
