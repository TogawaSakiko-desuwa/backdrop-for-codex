namespace BackdropForCodex.Core.Dynamic;

/// <summary>
/// Immutable format information for one generation-scoped fragmented MP4 stream.
/// </summary>
public sealed record EncodedWallpaperStreamDescriptor
{
    public const long MaximumJavaScriptSafeInteger = 9_007_199_254_740_991;

    public EncodedWallpaperStreamDescriptor(
        long generation,
        string mimeType,
        int width,
        int height,
        double frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        if (generation > MaximumJavaScriptSafeInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "The generation must be exactly representable by the page runtime.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        if (mimeType.Length > 256 ||
            mimeType.Contains('\r', StringComparison.Ordinal) ||
            mimeType.Contains('\n', StringComparison.Ordinal) ||
            !mimeType.StartsWith("video/mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The encoded stream MIME type must describe fragmented MP4 video.",
                nameof(mimeType));
        }

        if (width is <= 0 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height is <= 0 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (!double.IsFinite(frameRate) || frameRate is < 1 or > 240)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        Generation = generation;
        MimeType = mimeType;
        Width = width;
        Height = height;
        FrameRate = frameRate;
    }

    public long Generation { get; }

    public string MimeType { get; }

    public int Width { get; }

    public int Height { get; }

    public double FrameRate { get; }
}

public enum EncodedWallpaperSegmentKind
{
    Initialization = 0,
    Media,
}

/// <summary>
/// One independently owned encoded segment. Payload bytes are copied at construction so an
/// encoder cannot mutate data after transferring it to the asynchronous delivery pipeline.
/// </summary>
public sealed class EncodedWallpaperSegment
{
    public EncodedWallpaperSegment(
        long generation,
        long sequence,
        EncodedWallpaperSegmentKind kind,
        bool isKeyFrame,
        ReadOnlyMemory<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        if (generation > EncodedWallpaperStreamDescriptor.MaximumJavaScriptSafeInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "The generation must be exactly representable by the page runtime.");
        }

        if (sequence > EncodedWallpaperStreamDescriptor.MaximumJavaScriptSafeInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                "The sequence must be exactly representable by the page runtime.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (payload.IsEmpty || payload.Length > EncodedWallpaperStreamBuffer.MaximumBufferedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        if (kind == EncodedWallpaperSegmentKind.Initialization && isKeyFrame)
        {
            throw new ArgumentException(
                "An initialization segment cannot be marked as a key frame.",
                nameof(isKeyFrame));
        }

        Generation = generation;
        Sequence = sequence;
        Kind = kind;
        IsKeyFrame = isKeyFrame;
        Payload = payload.ToArray();
    }

    public long Generation { get; }

    public long Sequence { get; }

    public EncodedWallpaperSegmentKind Kind { get; }

    public bool IsKeyFrame { get; }

    public ReadOnlyMemory<byte> Payload { get; }
}

public enum EncodedWallpaperWriteResult
{
    Accepted = 0,
    DroppedUntilKeyFrame,
    RecoveredAtKeyFrame,
    Closed,
    RecoveredAfterQueueDrain,
}

/// <summary>
/// A bounded producer-consumer queue for encoded wallpaper segments. The reader is held behind a
/// startup barrier until both an initialization segment and a media key frame are buffered.
/// Non-blocking writes reject new media during overload without mutating accepted data; encoder
/// batches can instead wait asynchronously for capacity and preserve every segment in the batch.
/// </summary>
public sealed class EncodedWallpaperStreamBuffer :
    IEncodedWallpaperSegmentSink,
    IAsyncDisposable
{
    public const int MaximumBufferedSegments = 2;
    public const int MaximumBufferedBytes = 8 * 1024 * 1024;

    private readonly object _sync = new();
    private readonly Queue<EncodedWallpaperSegment> _pending = new();
    private readonly TaskCompletionSource _startup = CreateSignal();
    private readonly SemaphoreSlim _batchWriteGate = new(1, 1);
    private TaskCompletionSource _changed = CreateSignal();
    private TaskCompletionSource _spaceAvailable = CreateSignal();
    private long _lastObservedSequence = -1;
    private long _lastDequeuedSequence = -1;
    private long? _startupKeyFrameSequence;
    private int _pendingBytes;
    private bool _initializationAccepted;
    private bool _firstKeyFrameAccepted;
    private bool _startupLocked = true;
    private bool _awaitingKeyFrame;
    private bool _backpressureObserved;
    private bool _consumerCaughtUpSinceBackpressure;
    private bool _completed;
    private bool _disposed;
    private Exception? _completionError;

    public EncodedWallpaperStreamBuffer(EncodedWallpaperStreamDescriptor descriptor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public EncodedWallpaperStreamDescriptor Descriptor { get; }

    public int PendingSegmentCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    public int PendingByteCount
    {
        get
        {
            lock (_sync)
            {
                return _pendingBytes;
            }
        }
    }

    internal long LastDequeuedSequence
    {
        get
        {
            lock (_sync)
            {
                return _lastDequeuedSequence;
            }
        }
    }

    public EncodedWallpaperWriteResult TryWrite(EncodedWallpaperSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                return EncodedWallpaperWriteResult.Closed;
            }

            ValidateNextSegment(segment);
            _lastObservedSequence = segment.Sequence;

            if (segment.Kind == EncodedWallpaperSegmentKind.Initialization)
            {
                _initializationAccepted = true;
                Enqueue(segment);
                return EncodedWallpaperWriteResult.Accepted;
            }

            if (!_firstKeyFrameAccepted && !segment.IsKeyFrame)
            {
                _awaitingKeyFrame = true;
                return RecordDrop();
            }

            if (_awaitingKeyFrame && !segment.IsKeyFrame)
            {
                return RecordDrop();
            }

            if (CanFit(segment))
            {
                var recovered = _awaitingKeyFrame && segment.IsKeyFrame;
                AcceptMedia(segment);
                return CompleteAcceptedWrite(
                    recovered
                        ? EncodedWallpaperWriteResult.RecoveredAtKeyFrame
                        : EncodedWallpaperWriteResult.Accepted);
            }

            if (_startupLocked && _startupKeyFrameSequence.HasValue)
            {
                _awaitingKeyFrame = true;
                return RecordDrop();
            }

            _awaitingKeyFrame = true;
            return RecordDrop();
        }
    }

    /// <summary>
    /// Delivers one encoder output batch in sequence without treating the number of MP4 boxes
    /// produced by Media Foundation as queue overload. When the bounded queue fills, this method
    /// waits for the consumer to make room instead of discarding an already encoded GOP. Caller
    /// cancellation closes the generation because the encoder timeline may already have advanced;
    /// a canceled publish can therefore never be resumed as though the batch were still complete.
    /// </summary>
    public async ValueTask<EncodedWallpaperWriteResult> WriteBatchAsync(
        IReadOnlyList<EncodedWallpaperSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
        {
            throw new ArgumentException(
                "An encoded wallpaper batch must contain at least one segment.",
                nameof(segments));
        }

        var batch = segments.ToArray();
        if (batch.Any(segment => segment is null))
        {
            throw new ArgumentException(
                "An encoded wallpaper batch cannot contain a null segment.",
                nameof(segments));
        }

        var batchWriteGateAcquired = false;
        try
        {
            await _batchWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            batchWriteGateAcquired = true;
            DynamicWallpaperUnavailableException? startupCapacityFailure = null;
            TaskCompletionSource? startupFailureChanged = null;
            TaskCompletionSource? startupFailureSpaceAvailable = null;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_completed)
                {
                    return EncodedWallpaperWriteResult.Closed;
                }

                ValidateBatchBeforeWrite(batch);
                if (StartupKeyFrameCannotFit(batch))
                {
                    startupCapacityFailure = new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.EncodingFailed);
                    CompleteLocked(
                        startupCapacityFailure,
                        out startupFailureChanged,
                        out startupFailureSpaceAvailable);
                }
            }

            if (startupCapacityFailure is not null)
            {
                startupFailureChanged!.TrySetResult();
                startupFailureSpaceAvailable!.TrySetResult();
                throw startupCapacityFailure;
            }

            var result = EncodedWallpaperWriteResult.Accepted;
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var segment in batch)
            {
                while (true)
                {
                    Task spaceAvailable;
                    lock (_sync)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        if (_completed)
                        {
                            return EncodedWallpaperWriteResult.Closed;
                        }

                        ValidateNextSegment(segment);
                        if (segment.Kind == EncodedWallpaperSegmentKind.Media &&
                            ((!_firstKeyFrameAccepted && !segment.IsKeyFrame) ||
                             (_awaitingKeyFrame && !segment.IsKeyFrame)))
                        {
                            _lastObservedSequence = segment.Sequence;
                            result = AggregateBatchResult(result, RecordDrop());
                            break;
                        }

                        if (CanFit(segment))
                        {
                            _lastObservedSequence = segment.Sequence;
                            EncodedWallpaperWriteResult acceptedResult;
                            if (segment.Kind == EncodedWallpaperSegmentKind.Initialization)
                            {
                                _initializationAccepted = true;
                                Enqueue(segment);
                                acceptedResult = EncodedWallpaperWriteResult.Accepted;
                            }
                            else
                            {
                                var recovered = _awaitingKeyFrame && segment.IsKeyFrame;
                                AcceptMedia(segment);
                                acceptedResult = recovered
                                    ? EncodedWallpaperWriteResult.RecoveredAtKeyFrame
                                    : EncodedWallpaperWriteResult.Accepted;
                            }

                            result = AggregateBatchResult(result, acceptedResult);
                            break;
                        }

                        spaceAvailable = _spaceAvailable.Task;
                    }

                    await spaceAvailable.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            if (result == EncodedWallpaperWriteResult.DroppedUntilKeyFrame)
            {
                return result;
            }

            lock (_sync)
            {
                return CompleteAcceptedWrite(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Complete(new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.EncodingFailed));
            throw;
        }
        finally
        {
            if (batchWriteGateAcquired)
            {
                _batchWriteGate.Release();
            }
        }
    }

    public ValueTask WaitForStartupAsync(CancellationToken cancellationToken = default) =>
        new(_startup.Task.WaitAsync(cancellationToken));

    public async ValueTask<EncodedWallpaperSegment?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await WaitForStartupAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_pending.Count > 0)
                {
                    var segment = _pending.Dequeue();
                    _pendingBytes -= segment.Payload.Length;
                    _lastDequeuedSequence = segment.Sequence;
                    if (segment.Sequence == _startupKeyFrameSequence)
                    {
                        _startupLocked = false;
                    }

                    if (_pending.Count == 0 && _backpressureObserved)
                    {
                        _consumerCaughtUpSinceBackpressure = true;
                    }

                    var spaceAvailable = PulseSpaceAvailable();
                    spaceAvailable.TrySetResult();

                    return segment;
                }

                if (_completed)
                {
                    if (_completionError is not null)
                    {
                        throw _completionError;
                    }

                    return null;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Complete(Exception? failure = null)
    {
        TaskCompletionSource changed;
        TaskCompletionSource spaceAvailable;
        lock (_sync)
        {
            if (_disposed || _completed)
            {
                return;
            }

            CompleteLocked(failure, out changed, out spaceAvailable);
        }

        changed.TrySetResult();
        spaceAvailable.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource changed;
        TaskCompletionSource spaceAvailable;
        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _completed = true;
            _pending.Clear();
            _pendingBytes = 0;
            _startup.TrySetCanceled();
            changed = PulseChanged();
            spaceAvailable = PulseSpaceAvailable();
        }

        changed.TrySetResult();
        spaceAvailable.TrySetResult();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void ValidateNextSegment(EncodedWallpaperSegment segment)
    {
        ValidateSegment(
            segment,
            _lastObservedSequence,
            _initializationAccepted,
            _pending.Count == 0);
    }

    private void ValidateBatchBeforeWrite(IReadOnlyList<EncodedWallpaperSegment> segments)
    {
        var lastObservedSequence = _lastObservedSequence;
        var initializationAccepted = _initializationAccepted;
        var pendingIsEmpty = _pending.Count == 0;
        foreach (var segment in segments)
        {
            ValidateSegment(
                segment,
                lastObservedSequence,
                initializationAccepted,
                pendingIsEmpty);
            lastObservedSequence = segment.Sequence;
            if (segment.Kind == EncodedWallpaperSegmentKind.Initialization)
            {
                initializationAccepted = true;
                pendingIsEmpty = false;
            }
        }
    }

    private void ValidateSegment(
        EncodedWallpaperSegment segment,
        long lastObservedSequence,
        bool initializationAccepted,
        bool pendingIsEmpty)
    {
        if (segment.Generation != Descriptor.Generation)
        {
            throw new ArgumentException(
                "The encoded segment belongs to a different wallpaper generation.",
                nameof(segment));
        }

        if (segment.Sequence <= lastObservedSequence)
        {
            throw new ArgumentException(
                "Encoded segment sequence numbers must increase monotonically.",
                nameof(segment));
        }

        if (segment.Kind == EncodedWallpaperSegmentKind.Initialization)
        {
            if (initializationAccepted || segment.Sequence != 0 || !pendingIsEmpty)
            {
                throw new ArgumentException(
                    "Exactly one sequence-zero initialization segment is allowed.",
                    nameof(segment));
            }

            return;
        }

        if (!initializationAccepted)
        {
            throw new ArgumentException(
                "The initialization segment must be accepted before media segments.",
                nameof(segment));
        }
    }

    private bool StartupKeyFrameCannotFit(IReadOnlyList<EncodedWallpaperSegment> segments)
    {
        if (_firstKeyFrameAccepted)
        {
            return false;
        }

        var initializationAccepted = _initializationAccepted;
        var requiredBytes = _pendingBytes;
        foreach (var segment in segments)
        {
            if (segment.Kind == EncodedWallpaperSegmentKind.Initialization)
            {
                initializationAccepted = true;
                requiredBytes = checked(requiredBytes + segment.Payload.Length);
                continue;
            }

            if (initializationAccepted && segment.IsKeyFrame)
            {
                return requiredBytes > MaximumBufferedBytes - segment.Payload.Length;
            }
        }

        return false;
    }

    private void CompleteLocked(
        Exception? failure,
        out TaskCompletionSource changed,
        out TaskCompletionSource spaceAvailable)
    {
        _completed = true;
        _completionError = failure;
        if (failure is not null)
        {
            _pending.Clear();
            _pendingBytes = 0;
        }

        if (!_firstKeyFrameAccepted)
        {
            _startup.TrySetException(failure ?? new EndOfStreamException(
                "The encoded wallpaper stream ended before its startup key frame."));
        }

        changed = PulseChanged();
        spaceAvailable = PulseSpaceAvailable();
    }

    private bool CanFit(EncodedWallpaperSegment segment) =>
        _pending.Count < MaximumBufferedSegments &&
        _pendingBytes <= MaximumBufferedBytes - segment.Payload.Length;

    private void AcceptMedia(EncodedWallpaperSegment segment)
    {
        Enqueue(segment);
        if (!segment.IsKeyFrame)
        {
            return;
        }

        _awaitingKeyFrame = false;
        if (_firstKeyFrameAccepted)
        {
            return;
        }

        _firstKeyFrameAccepted = true;
        _startupKeyFrameSequence = segment.Sequence;
        _startup.TrySetResult();
    }

    private void Enqueue(EncodedWallpaperSegment segment)
    {
        if (!CanFit(segment))
        {
            throw new InvalidOperationException("The encoded wallpaper buffer is full.");
        }

        _pending.Enqueue(segment);
        _pendingBytes += segment.Payload.Length;
        var changed = PulseChanged();
        changed.TrySetResult();
    }

    private EncodedWallpaperWriteResult RecordDrop()
    {
        _backpressureObserved = true;
        return EncodedWallpaperWriteResult.DroppedUntilKeyFrame;
    }

    private EncodedWallpaperWriteResult CompleteAcceptedWrite(
        EncodedWallpaperWriteResult result)
    {
        if (!_backpressureObserved || !_consumerCaughtUpSinceBackpressure)
        {
            return result;
        }

        _backpressureObserved = false;
        _consumerCaughtUpSinceBackpressure = false;
        return EncodedWallpaperWriteResult.RecoveredAfterQueueDrain;
    }

    private static EncodedWallpaperWriteResult AggregateBatchResult(
        EncodedWallpaperWriteResult aggregate,
        EncodedWallpaperWriteResult next) =>
        next == EncodedWallpaperWriteResult.DroppedUntilKeyFrame ||
        aggregate != EncodedWallpaperWriteResult.DroppedUntilKeyFrame &&
        next != EncodedWallpaperWriteResult.Accepted
            ? next
            : aggregate;

    private TaskCompletionSource PulseChanged()
    {
        var changed = _changed;
        _changed = CreateSignal();
        return changed;
    }

    private TaskCompletionSource PulseSpaceAvailable()
    {
        var spaceAvailable = _spaceAvailable;
        _spaceAvailable = CreateSignal();
        return spaceAvailable;
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
