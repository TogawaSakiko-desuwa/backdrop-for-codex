using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Runtime;
using PuppeteerSharp;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace BackdropForCodex.Core.Dynamic;

public sealed record EncodedWallpaperPrepareReceipt(
    bool Prepared,
    string Reason,
    long Generation);

public sealed record EncodedWallpaperAppendReceipt(
    bool Appended,
    bool Published,
    string Reason,
    long Sequence);

/// <summary>
/// Page boundary for a single encoded wallpaper generation. Each append completes only after the
/// page SourceBuffer acknowledges its update.
/// </summary>
public interface IEncodedWallpaperPageSink : IAsyncDisposable
{
    Task<EncodedWallpaperPrepareReceipt> PrepareAsync(
        DynamicWallpaperInjectionOptions options,
        EncodedWallpaperStreamDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task<EncodedWallpaperAppendReceipt> AppendAsync(
        EncodedWallpaperSegment segment,
        CancellationToken cancellationToken = default);

    Task<bool> SetPausedAsync(
        long generation,
        bool paused,
        CancellationToken cancellationToken = default);

    Task<bool> CleanupAsync(
        long generation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes the owned MediaSource expressions against an already verified Codex page. The sink
/// borrows the page and never closes its browser or CDP connection.
/// </summary>
public sealed class PuppeteerEncodedWallpaperPageSink : IEncodedWallpaperPageSink
{
    private readonly IPage _page;
    private readonly CompatibilityCapabilities? _capabilities;

    public PuppeteerEncodedWallpaperPageSink(
        IPage page,
        CompatibilityCapabilities? capabilities = null)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        _capabilities = capabilities;
    }

    public Task<EncodedWallpaperPrepareReceipt> PrepareAsync(
        DynamicWallpaperInjectionOptions options,
        EncodedWallpaperStreamDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var expression = _capabilities is null
            ? InjectionScriptBuilder.BuildPrepareEncodedStream(options, descriptor)
            : InjectionScriptBuilder.BuildPrepareEncodedStream(
                options,
                descriptor,
                _capabilities);
        return _page
            .EvaluateExpressionAsync<EncodedWallpaperPrepareReceipt>(expression)
            .WaitAsync(cancellationToken);
    }

    public Task<EncodedWallpaperAppendReceipt> AppendAsync(
        EncodedWallpaperSegment segment,
        CancellationToken cancellationToken = default) =>
        _page
            .EvaluateExpressionAsync<EncodedWallpaperAppendReceipt>(
                InjectionScriptBuilder.BuildAppendEncodedStream(segment))
            .WaitAsync(cancellationToken);

    public Task<bool> SetPausedAsync(
        long generation,
        bool paused,
        CancellationToken cancellationToken = default) =>
        _page
            .EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildSetEncodedStreamPaused(generation, paused))
            .WaitAsync(cancellationToken);

    public Task<bool> CleanupAsync(
        long generation,
        CancellationToken cancellationToken = default) =>
        _page
            .EvaluateExpressionAsync<bool>(
                InjectionScriptBuilder.BuildCleanupEncodedStream(generation))
            .WaitAsync(cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Pumps a bounded encoded stream into one page generation. Startup returns only after the page
/// acknowledges both initialization and the first key frame and publishes the owned DOM graph.
/// </summary>
public sealed class EncodedWallpaperPageStreamLease :
    IDynamicWallpaperPagePlaybackLease,
    IActiveWallpaperHealthSource
{
    private static readonly TimeSpan DefaultAppendAcknowledgementTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(2);

    private readonly EncodedWallpaperStreamBuffer _buffer;
    private readonly IEncodedWallpaperPageSink _sink;
    private readonly TimeSpan _appendAcknowledgementTimeout;
    private readonly TimeSpan _cleanupTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _pageGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private readonly object _keyFrameLock = new();
    private TaskCompletionSource<long> _nextKeyFrameAcknowledged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<long> _nextSegmentAcknowledged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _completion = Task.CompletedTask;
    private long _lastAcknowledgedKeyFrameSequence;
    private long _lastAcknowledgedSegmentSequence;
    private bool _domCleanupConfirmed;
    private bool _bufferDisposed;
    private bool _sinkDisposed;
    private int _disposeRequested;
    private int _disposed;

    private EncodedWallpaperPageStreamLease(
        EncodedWallpaperStreamBuffer buffer,
        IEncodedWallpaperPageSink sink,
        TimeSpan appendAcknowledgementTimeout,
        TimeSpan cleanupTimeout,
        long initialKeyFrameSequence)
    {
        _buffer = buffer;
        _sink = sink;
        _appendAcknowledgementTimeout = appendAcknowledgementTimeout;
        _cleanupTimeout = cleanupTimeout;
        _lastAcknowledgedKeyFrameSequence = initialKeyFrameSequence;
        _lastAcknowledgedSegmentSequence = initialKeyFrameSequence;
        Generation = buffer.Descriptor.Generation;
    }

    public long Generation { get; }

    public ActiveWallpaperDeliveryKind DeliveryKind =>
        ActiveWallpaperDeliveryKind.DynamicStream;

    public Task Completion => _completion;

    public long LastAcknowledgedKeyFrameSequence
    {
        get
        {
            lock (_keyFrameLock)
            {
                return _lastAcknowledgedKeyFrameSequence;
            }
        }
    }

    public static async Task<EncodedWallpaperPageStreamLease> StartAsync(
        EncodedWallpaperStreamBuffer buffer,
        IEncodedWallpaperPageSink sink,
        DynamicWallpaperInjectionOptions options,
        CancellationToken cancellationToken = default) =>
        await StartAsync(
            buffer,
            sink,
            options,
            DefaultAppendAcknowledgementTimeout,
            DefaultCleanupTimeout,
            cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<EncodedWallpaperPageStreamLease> StartAsync(
        EncodedWallpaperStreamBuffer buffer,
        IEncodedWallpaperPageSink sink,
        DynamicWallpaperInjectionOptions options,
        TimeSpan appendAcknowledgementTimeout,
        CancellationToken cancellationToken) =>
        await StartAsync(
                buffer,
                sink,
                options,
                appendAcknowledgementTimeout,
                DefaultCleanupTimeout,
                cancellationToken)
            .ConfigureAwait(false);

    internal static async Task<EncodedWallpaperPageStreamLease> StartAsync(
        EncodedWallpaperStreamBuffer buffer,
        IEncodedWallpaperPageSink sink,
        DynamicWallpaperInjectionOptions options,
        TimeSpan appendAcknowledgementTimeout,
        TimeSpan cleanupTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        if (appendAcknowledgementTimeout <= TimeSpan.Zero ||
            appendAcknowledgementTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(appendAcknowledgementTimeout));
        }

        if (cleanupTimeout <= TimeSpan.Zero || cleanupTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
        }

        if (buffer.Descriptor.Generation != options.Generation)
        {
            throw new ArgumentException(
                "The encoded stream and visual options must share one generation.",
                nameof(options));
        }

        var lease = new EncodedWallpaperPageStreamLease(
            buffer,
            sink,
            appendAcknowledgementTimeout,
            cleanupTimeout,
            initialKeyFrameSequence: 0);
        try
        {
            await buffer.WaitForStartupAsync(cancellationToken).ConfigureAwait(false);
            var prepared = await sink
                .PrepareAsync(options, buffer.Descriptor, cancellationToken)
                .ConfigureAwait(false);
            if (!prepared.Prepared || prepared.Generation != buffer.Descriptor.Generation)
            {
                throw new InvalidOperationException(
                    $"The page rejected encoded stream preparation: {prepared.Reason}.");
            }

            var initialization = await ReadRequiredAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (initialization.Kind != EncodedWallpaperSegmentKind.Initialization)
            {
                throw new InvalidOperationException(
                    "The encoded wallpaper startup did not begin with initialization bytes.");
            }

            var initializationReceipt = await AppendWithDeadlineAsync(
                    sink,
                    initialization,
                    appendAcknowledgementTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            ValidateReceipt(
                initializationReceipt,
                initialization.Sequence,
                expectedPublished: false);

            var keyFrame = await ReadRequiredAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (keyFrame.Kind != EncodedWallpaperSegmentKind.Media || !keyFrame.IsKeyFrame)
            {
                throw new InvalidOperationException(
                    "The encoded wallpaper startup did not contain a media key frame.");
            }

            EncodedWallpaperAppendReceipt keyFrameReceipt;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                keyFrameReceipt = await AppendWithDeadlineAsync(
                        sink,
                        keyFrame,
                        appendAcknowledgementTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                options.MutationSignal?.ReportPossibleMutation();
                throw;
            }

            if (keyFrameReceipt?.Published == true)
            {
                options.MutationSignal?.ReportPossibleMutation();
            }

            ValidateReceipt(
                keyFrameReceipt,
                keyFrame.Sequence,
                expectedPublished: true);

            lease.InitializeAcknowledgedStartup(keyFrame.Sequence);
            lease._completion = lease.PumpAsync();
            return lease;
        }
        catch (Exception primaryFailure)
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new RetainedEncodedWallpaperPageStreamStartException(
                    primaryFailure,
                    cleanupFailure,
                    lease);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref _disposeRequested, 1);
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (!_stop.IsCancellationRequested)
            {
                await _stop.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                await _completion.ConfigureAwait(false);
            }
            catch
            {
                // Runtime failures remain available through Completion. Cleanup below is an
                // independently retryable ownership boundary.
            }

            await CleanupRetainedResourcesAsync().ConfigureAwait(false);
            CancelPlaybackWaiters();
            Volatile.Write(ref _disposed, 1);
            GC.SuppressFinalize(this);
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    public async ValueTask SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposalRequested();
        await _pageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposalRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_appendAcknowledgementTimeout);
            bool acknowledged;
            try
            {
                acknowledged = await _sink
                    .SetPausedAsync(Generation, paused, deadline.Token)
                    .WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DynamicWallpaperPageSessionException(
                    "The verified Codex page did not acknowledge the playback state change.");
            }
            catch (Exception exception) when (
                exception is PuppeteerException or JsonException or InvalidOperationException)
            {
                throw new DynamicWallpaperPageSessionException(
                    "The verified Codex page could not apply the playback state change.",
                    exception);
            }

            if (!acknowledged)
            {
                throw new DynamicWallpaperPageSessionException(
                    "The verified Codex page rejected the playback state change.");
            }
        }
        finally
        {
            _pageGate.Release();
        }
    }

    public async ValueTask WaitForKeyFrameAfterAsync(
        long sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        while (true)
        {
            Task<long> next;
            lock (_keyFrameLock)
            {
                if (_lastAcknowledgedKeyFrameSequence > sequence)
                {
                    return;
                }

                ThrowIfDisposalRequested();
                next = _nextKeyFrameAcknowledged.Task;
            }

            await next.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask WaitForBufferedSegmentsAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task<long> next;
            lock (_keyFrameLock)
            {
                if (_buffer.PendingSegmentCount == 0 &&
                    _lastAcknowledgedSegmentSequence >= _buffer.LastDequeuedSequence)
                {
                    return;
                }

                ThrowIfDisposalRequested();
                next = _nextSegmentAcknowledged.Task;
            }

            await next.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _buffer.ReadAsync(_stop.Token).ConfigureAwait(false) is { } segment)
            {
                await _pageGate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    var receipt = await AppendWithDeadlineAsync(
                            _sink,
                            segment,
                            _appendAcknowledgementTimeout,
                            _stop.Token)
                        .ConfigureAwait(false);
                    ValidateReceipt(receipt, segment.Sequence, expectedPublished: true);
                    RecordAcknowledgedSegment(segment.Sequence);
                    if (segment.IsKeyFrame)
                    {
                        RecordAcknowledgedKeyFrame(segment.Sequence);
                    }
                }
                finally
                {
                    _pageGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch
        {
            CancelPlaybackWaiters();
            throw;
        }
        finally
        {
            if (!_stop.IsCancellationRequested)
            {
                await CleanupRetainedResourcesAsync().ConfigureAwait(false);
            }
        }
    }

    private void RecordAcknowledgedKeyFrame(long sequence)
    {
        TaskCompletionSource<long>? acknowledged = null;
        lock (_keyFrameLock)
        {
            if (sequence <= _lastAcknowledgedKeyFrameSequence)
            {
                return;
            }

            _lastAcknowledgedKeyFrameSequence = sequence;
            acknowledged = _nextKeyFrameAcknowledged;
            _nextKeyFrameAcknowledged =
                new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        acknowledged.TrySetResult(sequence);
    }

    private void InitializeAcknowledgedStartup(long sequence)
    {
        lock (_keyFrameLock)
        {
            _lastAcknowledgedKeyFrameSequence = sequence;
            _lastAcknowledgedSegmentSequence = sequence;
        }
    }

    private void ThrowIfDisposalRequested() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0 ||
            Volatile.Read(ref _disposed) != 0,
            this);

    private void RecordAcknowledgedSegment(long sequence)
    {
        TaskCompletionSource<long>? acknowledged = null;
        lock (_keyFrameLock)
        {
            if (sequence <= _lastAcknowledgedSegmentSequence)
            {
                return;
            }

            _lastAcknowledgedSegmentSequence = sequence;
            acknowledged = _nextSegmentAcknowledged;
            _nextSegmentAcknowledged =
                new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        acknowledged.TrySetResult(sequence);
    }

    private void CancelPlaybackWaiters()
    {
        TaskCompletionSource<long> waiters;
        TaskCompletionSource<long> segmentWaiters;
        lock (_keyFrameLock)
        {
            waiters = _nextKeyFrameAcknowledged;
            _nextKeyFrameAcknowledged =
                new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            segmentWaiters = _nextSegmentAcknowledged;
            _nextSegmentAcknowledged =
                new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        waiters.TrySetCanceled();
        segmentWaiters.TrySetCanceled();
    }

    private async Task CleanupRetainedResourcesAsync()
    {
        await _cleanupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_domCleanupConfirmed)
            {
                await CleanupWithDeadlineAsync(_sink, Generation, _cleanupTimeout)
                    .ConfigureAwait(false);
                _domCleanupConfirmed = true;
            }

            if (!_bufferDisposed)
            {
                await _buffer.DisposeAsync().ConfigureAwait(false);
                _bufferDisposed = true;
            }

            if (!_sinkDisposed)
            {
                await _sink.DisposeAsync().ConfigureAwait(false);
                _sinkDisposed = true;
            }
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    private static async Task<EncodedWallpaperSegment> ReadRequiredAsync(
        EncodedWallpaperStreamBuffer buffer,
        CancellationToken cancellationToken) =>
        await buffer.ReadAsync(cancellationToken).ConfigureAwait(false) ??
        throw new EndOfStreamException(
            "The encoded wallpaper stream ended during page startup.");

    private static async Task<EncodedWallpaperAppendReceipt> AppendWithDeadlineAsync(
        IEncodedWallpaperPageSink sink,
        EncodedWallpaperSegment segment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await sink
                .AppendAsync(segment, deadline.Token)
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
        }
        catch (Exception exception) when (
            exception is PuppeteerException or JsonException or InvalidOperationException)
        {
            throw new DynamicWallpaperPageSessionException(
                "The verified Codex page could not accept an encoded stream segment.",
                exception);
        }
    }

    private static void ValidateReceipt(
        EncodedWallpaperAppendReceipt? receipt,
        long expectedSequence,
        bool expectedPublished)
    {
        if (receipt is null ||
            !receipt.Appended || receipt.Sequence != expectedSequence ||
            receipt.Published != expectedPublished)
        {
            throw new DynamicWallpaperPageSessionException(
                "The verified Codex page returned an invalid encoded stream acknowledgement.");
        }
    }

    private static async Task CleanupWithDeadlineAsync(
        IEncodedWallpaperPageSink sink,
        long generation,
        TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var cleanup = sink.CleanupAsync(generation, deadline.Token);
        try
        {
            var confirmed = await cleanup.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!confirmed)
            {
                throw new DynamicWallpaperPageSessionException(
                    "The verified Codex page did not confirm encoded stream cleanup.",
                    DynamicWallpaperPageSessionFailureKind.CleanupNotProven);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _ = ObserveLateCleanupAsync(cleanup);
            throw new DynamicWallpaperPageSessionException(
                "The verified Codex page did not confirm encoded stream cleanup before the deadline.",
                DynamicWallpaperPageSessionFailureKind.CleanupNotProven);
        }
        catch (DynamicWallpaperPageSessionException exception) when (
            exception.FailureKind == DynamicWallpaperPageSessionFailureKind.CleanupNotProven)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DynamicWallpaperPageSessionException(
                "The verified Codex page could not prove encoded stream cleanup.",
                DynamicWallpaperPageSessionFailureKind.CleanupNotProven,
                exception);
        }
    }

    private static async Task ObserveLateCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch
        {
            // Cleanup already exceeded its best-effort deadline. Observe any late failure so the
            // borrowed CDP disconnect and outer capture/window cleanup can continue immediately.
        }
    }
}

internal sealed class RetainedEncodedWallpaperPageStreamStartException : Exception
{
    internal RetainedEncodedWallpaperPageStreamStartException(
        Exception primaryFailure,
        Exception cleanupFailure,
        EncodedWallpaperPageStreamLease cleanupOwner)
        : base(
            "The encoded wallpaper page stream failed to start and its page cleanup " +
            "must be retried before the verified connection can be released.",
            primaryFailure)
    {
        PrimaryFailure = primaryFailure ?? throw new ArgumentNullException(nameof(primaryFailure));
        CleanupFailure = cleanupFailure ?? throw new ArgumentNullException(nameof(cleanupFailure));
        CleanupOwner = cleanupOwner ?? throw new ArgumentNullException(nameof(cleanupOwner));
    }

    internal Exception PrimaryFailure { get; }

    internal Exception CleanupFailure { get; }

    internal EncodedWallpaperPageStreamLease CleanupOwner { get; }
}
