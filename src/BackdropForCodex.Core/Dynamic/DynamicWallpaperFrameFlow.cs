using System.Runtime.ExceptionServices;

namespace BackdropForCodex.Core.Dynamic;

/// <summary>
/// Orders frames across capture-session restarts. WGC session-local sequence numbers reset on
/// resume, while SystemRelativeTime remains monotonic for the wallpaper generation.
/// </summary>
internal sealed class GenerationScopedWallpaperFrameOrder
{
    private readonly long _generation;
    private IWallpaperCapturedFrame? _lastFrame;
    private long _lastTimestampTicks = long.MinValue;

    internal GenerationScopedWallpaperFrameOrder(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        _generation = generation;
    }

    internal void Accept(IWallpaperCapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Generation != _generation)
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.EncodingFailed);
        }

        if (ReferenceEquals(frame, _lastFrame))
        {
            return;
        }

        if (frame.Timestamp.Ticks <= _lastTimestampTicks)
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.EncodingFailed);
        }

        _lastFrame = frame;
        _lastTimestampTicks = frame.Timestamp.Ticks;
    }
}

/// <summary>
/// Maintains an absolute fixed-point media clock. Every encoded sample advances the stream by one
/// fixed duration, so a late wake hands out the missed slots one at a time until the encoded
/// timeline is back on wall time; otherwise the player drains its buffer by the lost time. A
/// backlog beyond the catch-up bound (sleep, debugger, sustained overload) is dropped by
/// reanchoring instead.
/// </summary>
internal sealed class FixedPointWallpaperFramePacer
{
    internal static readonly TimeSpan DefaultMaximumCatchUp = TimeSpan.FromSeconds(1);

    private readonly decimal _frameRate;
    private readonly long _maximumCatchUpSamples;
    private long _anchorTicks;
    private long _nextSampleIndex;
    private bool _started;

    internal FixedPointWallpaperFramePacer(
        double frameRate,
        TimeSpan? maximumCatchUp = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameRate);
        if (!double.IsFinite(frameRate))
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        }

        var resolvedMaximumCatchUp = maximumCatchUp ?? DefaultMaximumCatchUp;
        ArgumentOutOfRangeException.ThrowIfLessThan(
            resolvedMaximumCatchUp,
            TimeSpan.Zero,
            nameof(maximumCatchUp));
        _frameRate = (decimal)frameRate;
        _maximumCatchUpSamples = checked((long)decimal.Ceiling(
            resolvedMaximumCatchUp.Ticks * _frameRate / TimeSpan.TicksPerSecond));
    }

    internal void Start(TimeSpan monotonicNow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(monotonicNow, TimeSpan.Zero);
        if (_started)
        {
            throw new InvalidOperationException("The wallpaper frame pacer is already running.");
        }

        _anchorTicks = monotonicNow.Ticks;
        _nextSampleIndex = 1;
        _started = true;
    }

    internal bool TryTakeDueSample(
        TimeSpan monotonicNow,
        out TimeSpan wait,
        out bool reanchored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(monotonicNow, TimeSpan.Zero);
        if (!_started)
        {
            throw new InvalidOperationException("The wallpaper frame pacer has not started.");
        }

        var nowTicks = (decimal)monotonicNow.Ticks;
        var nextDueTicks = GetDueTicks(_nextSampleIndex);
        if (nowTicks < nextDueTicks)
        {
            wait = TimeSpan.FromTicks(checked(
                (long)decimal.Ceiling(nextDueTicks - nowTicks)));
            reanchored = false;
            return false;
        }

        if (nowTicks >= GetDueTicks(checked(_nextSampleIndex + _maximumCatchUpSamples + 1)))
        {
            Reanchor(monotonicNow);
            wait = TimeSpan.Zero;
            reanchored = true;
            return true;
        }

        _nextSampleIndex = checked(_nextSampleIndex + 1);
        wait = TimeSpan.Zero;
        reanchored = false;
        return true;
    }

    /// <summary>
    /// Reports whether the next slot is already due, i.e. the caller is still catching up.
    /// </summary>
    internal bool IsBehind(TimeSpan monotonicNow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(monotonicNow, TimeSpan.Zero);
        if (!_started)
        {
            throw new InvalidOperationException("The wallpaper frame pacer has not started.");
        }

        return (decimal)monotonicNow.Ticks >= GetDueTicks(_nextSampleIndex);
    }

    internal void ResetForCapturePause()
    {
        _anchorTicks = 0;
        _nextSampleIndex = 0;
        _started = false;
    }

    private decimal GetDueTicks(long sampleIndex) => checked(
        _anchorTicks +
        ((decimal)sampleIndex * TimeSpan.TicksPerSecond / _frameRate));

    private void Reanchor(TimeSpan monotonicNow)
    {
        _anchorTicks = monotonicNow.Ticks;
        _nextSampleIndex = 1;
    }
}

internal sealed class SustainedWallpaperCadenceMonitor
{
    private readonly TimeSpan _limit;
    private TimeSpan? _behindSince;

    internal SustainedWallpaperCadenceMonitor(TimeSpan limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limit, TimeSpan.Zero);
        _limit = limit;
    }

    internal bool Observe(bool isBehind, TimeSpan monotonicNow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(monotonicNow, TimeSpan.Zero);
        if (!isBehind)
        {
            _behindSince = null;
            return false;
        }

        _behindSince ??= monotonicNow;
        return monotonicNow - _behindSince.Value >= _limit;
    }

    internal void ResetForCapturePause()
    {
        _behindSince = null;
    }
}

/// <summary>
/// Owns at most one captured GPU frame. Reading <see cref="Current"/> does not transfer ownership,
/// allowing the pacer to replay the same surface until a newer frame arrives.
/// </summary>
internal sealed class LatestWallpaperCapturedFrameSlot : IAsyncDisposable
{
    private IWallpaperCapturedFrame? _current;
    private IWallpaperCapturedFrame? _rejectedReplacement;

    internal IWallpaperCapturedFrame? Current => _current;

    internal async ValueTask ReplaceAsync(IWallpaperCapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (ReferenceEquals(frame, _current))
        {
            return;
        }

        var previous = _current;
        if (previous is null)
        {
            _current = frame;
            return;
        }

        if (_rejectedReplacement is not null)
        {
            throw new InvalidOperationException(
                "A rejected captured frame is still awaiting cleanup.");
        }

        try
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception previousFailure)
        {
            try
            {
                await frame.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception replacementFailure)
            {
                _rejectedReplacement = frame;
                throw new AggregateException(previousFailure, replacementFailure);
            }

            ExceptionDispatchInfo.Capture(previousFailure).Throw();
        }

        _current = frame;
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception>? failures = null;
        if (_current is { } current)
        {
            try
            {
                await current.DisposeAsync().ConfigureAwait(false);
                _current = null;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (_rejectedReplacement is { } rejectedReplacement)
        {
            try
            {
                await rejectedReplacement.DisposeAsync().ConfigureAwait(false);
                _rejectedReplacement = null;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is [var failure])
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (failures is { Count: > 1 })
        {
            throw new AggregateException(failures);
        }
    }
}

/// <summary>
/// Tracks non-blocking segment-queue pressure without hiding dropped GOPs.
/// </summary>
internal sealed class DynamicWallpaperBackpressureMonitor
{
    internal static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _limit;
    private TimeSpan? _startedAt;

    internal DynamicWallpaperBackpressureMonitor(TimeSpan? limit = null)
    {
        _limit = limit ?? DefaultLimit;
        if (_limit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }

    internal bool Observe(
        EncodedWallpaperWriteResult result,
        TimeSpan monotonicNow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            monotonicNow,
            TimeSpan.Zero);

        if (result == EncodedWallpaperWriteResult.DroppedUntilKeyFrame)
        {
            _startedAt ??= monotonicNow;
            return monotonicNow - _startedAt.Value >= _limit;
        }

        if (result == EncodedWallpaperWriteResult.RecoveredAfterQueueDrain)
        {
            _startedAt = null;
            return false;
        }

        if (_startedAt.HasValue &&
            result is EncodedWallpaperWriteResult.Accepted or
                EncodedWallpaperWriteResult.RecoveredAtKeyFrame)
        {
            return monotonicNow - _startedAt.Value >= _limit;
        }

        _startedAt = null;
        return false;
    }

    internal void ResetForCapturePause()
    {
        _startedAt = null;
    }
}
