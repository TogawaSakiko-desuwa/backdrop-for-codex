using System.IO;
using System.Security;

namespace BackdropForCodex.App.Services.Media;

/// <summary>
/// Process-scoped, bounded change notification for validated local thumbnail sources. Generation
/// reads are memory-only. A directory watcher can be created only by <see cref="TrackValidatedSource"/>
/// while <see cref="SafeMediaPreviewService"/> still owns the provider's validated file lease.
/// </summary>
internal static class MediaThumbnailInvalidationHub
{
    private const int MaximumTrackedSources = 256;
    private const int MaximumWatchedDirectories = 64;
    private const int MaximumMonitoringGaps = 256;
    private const int MaximumPendingRetries = 512;
    private const int MaximumPendingRetriesPerSource = 4;
    private static readonly object StateLock = new();
    private static readonly Dictionary<string, SourceState> TrackedSources =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, WatcherRegistration> DirectoryWatchers =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MonitoringGapState> MonitoringGaps =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<long>> PendingRetryGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private static Timer? _monitoringRetryTimer;
    private static long _nextGeneration;
    private static long _nextAccessSequence;
    private static int _pendingRetryCount;

    internal static event EventHandler<MediaThumbnailSourceInvalidatedEventArgs>?
        SourceInvalidated;

    /// <summary>
    /// Returns the source generation without touching the file system or creating a watcher.
    /// </summary>
    internal static long ObserveSource(string sourceIdentifier)
    {
        var sourceKey = NormalizePath(sourceIdentifier);
        lock (StateLock)
        {
            return GetOrAddSource(sourceKey).Generation;
        }
    }

    /// <summary>
    /// Associates a durable source identifier with the final path validated by an active media
    /// lease, then establishes bounded directory monitoring for that final local path.
    /// </summary>
    internal static long TrackValidatedSource(
        string sourceIdentifier,
        string validatedPath)
    {
        var sourceKey = NormalizePath(sourceIdentifier);
        validatedPath = NormalizePath(validatedPath);
        if (validatedPath.StartsWith(@"\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(validatedPath))
        {
            throw new InvalidOperationException(
                "A validated thumbnail monitor requires a fully qualified local path.");
        }

        var directory = Path.GetDirectoryName(validatedPath);
        var needsWatcher = false;
        lock (StateLock)
        {
            var state = GetOrAddSource(sourceKey);
            if (!string.Equals(
                    state.MonitoredPath,
                    validatedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                state.MonitoredPath = validatedPath;
                state.Generation = ++_nextGeneration;
            }

            if (!string.IsNullOrEmpty(directory))
            {
                if (DirectoryWatchers.TryGetValue(directory, out var registration))
                {
                    registration.LastAccessSequence = ++_nextAccessSequence;
                }
                else
                {
                    needsWatcher =
                        !MonitoringGaps.TryGetValue(directory, out var gap) ||
                        Environment.TickCount64 >= gap.RetryAfterTick;
                    if (gap is not null)
                    {
                        gap.LastAccessSequence = ++_nextAccessSequence;
                    }
                }
            }
        }

        if (needsWatcher)
        {
            TryTrackDirectory(directory!);
        }

        lock (StateLock)
        {
            return TrackedSources.TryGetValue(sourceKey, out var state)
                ? state.Generation
                : ++_nextGeneration;
        }
    }

    internal static void InvalidateSource(string sourceIdentifier) =>
        InvalidateSourceKey(NormalizePath(sourceIdentifier), addIfMissing: true);

    internal static void ScheduleRetry(
        string sourceIdentifier,
        long failedGeneration,
        TimeSpan delay)
    {
        var sourceKey = NormalizePath(sourceIdentifier);
        delay = BoundRetryDelay(delay);
        lock (StateLock)
        {
            if (!TrackedSources.TryGetValue(sourceKey, out var state) ||
                state.Generation != failedGeneration)
            {
                return;
            }

            if (!PendingRetryGenerations.TryGetValue(sourceKey, out var generations))
            {
                if (_pendingRetryCount >= MaximumPendingRetries)
                {
                    return;
                }

                generations = [];
                PendingRetryGenerations.Add(sourceKey, generations);
            }

            if (generations.Count >= MaximumPendingRetriesPerSource ||
                _pendingRetryCount >= MaximumPendingRetries ||
                !generations.Add(failedGeneration))
            {
                return;
            }

            _pendingRetryCount++;
        }

        _ = RetrySourceAfterDelayAsync(sourceKey, failedGeneration, delay);
    }

    private static async Task RetrySourceAfterDelayAsync(
        string sourceKey,
        long failedGeneration,
        TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        var shouldRetry = false;
        lock (StateLock)
        {
            if (PendingRetryGenerations.TryGetValue(sourceKey, out var generations) &&
                generations.Remove(failedGeneration))
            {
                _pendingRetryCount--;
                if (generations.Count == 0)
                {
                    PendingRetryGenerations.Remove(sourceKey);
                }
            }

            shouldRetry =
                TrackedSources.TryGetValue(sourceKey, out var state) &&
                state.Generation == failedGeneration;
        }

        if (shouldRetry)
        {
            InvalidateSourceKey(sourceKey, addIfMissing: false);
        }
    }

    private static void TryTrackDirectory(string directory)
    {
        FileSystemWatcher? watcher = null;
        FileSystemWatcher? evictedWatcher = null;
        string? evictedDirectory = null;
        WatcherRegistration? candidateRegistration = null;
        var scheduleMonitoringRetry = false;
        var invalidateDirectory = false;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size |
                    NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            watcher.Changed += Watcher_Changed;
            watcher.Created += Watcher_Changed;
            watcher.Deleted += Watcher_Changed;
            watcher.Renamed += Watcher_Renamed;
            watcher.Error += Watcher_Error;

            lock (StateLock)
            {
                if (DirectoryWatchers.ContainsKey(directory))
                {
                    return;
                }

                MonitoringGapState? recoveredGap = null;
                invalidateDirectory = MonitoringGaps.Remove(directory, out recoveredGap);
                if (DirectoryWatchers.Count >= MaximumWatchedDirectories)
                {
                    var oldest = DirectoryWatchers.MinBy(
                        entry => entry.Value.LastAccessSequence);
                    if (!string.IsNullOrEmpty(oldest.Key) &&
                        DirectoryWatchers.Remove(oldest.Key, out var registration))
                    {
                        evictedDirectory = oldest.Key;
                        evictedWatcher = registration.Watcher;
                        RecordMonitoringFailure(
                            oldest.Key,
                            Environment.TickCount64,
                            registration.FailureCount);
                        scheduleMonitoringRetry = true;
                    }
                }

                candidateRegistration = new WatcherRegistration(
                    watcher,
                    ++_nextAccessSequence,
                    recoveredGap?.FailureCount ?? 0);
                DirectoryWatchers.Add(
                    directory,
                    candidateRegistration);
            }

            // Register the initializing watcher before enabling notifications. An immediate Error
            // can then remove this exact registration instead of being mistaken for a stale event.
            watcher.EnableRaisingEvents = true;
            lock (StateLock)
            {
                if (DirectoryWatchers.TryGetValue(directory, out var registration) &&
                    ReferenceEquals(registration, candidateRegistration))
                {
                    watcher = null;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            NotSupportedException or
            PlatformNotSupportedException or
            SecurityException)
        {
            lock (StateLock)
            {
                if (candidateRegistration is not null &&
                    DirectoryWatchers.TryGetValue(directory, out var current) &&
                    ReferenceEquals(current, candidateRegistration))
                {
                    DirectoryWatchers.Remove(directory);
                }

                // Watcher_Error may already have removed an initializing registration and
                // recorded the failure. A constructor failure has no registration, however, so
                // an expired gap must be advanced instead of being left permanently due.
                if (!DirectoryWatchers.ContainsKey(directory) &&
                    (candidateRegistration is null ||
                        !MonitoringGaps.ContainsKey(directory)))
                {
                    var previousFailureCount = candidateRegistration?.FailureCount ??
                        (MonitoringGaps.TryGetValue(directory, out var existingGap)
                            ? existingGap.FailureCount
                            : 0);
                    RecordMonitoringFailure(
                        directory,
                        Environment.TickCount64,
                        previousFailureCount);
                }

                scheduleMonitoringRetry = MonitoringGaps.ContainsKey(directory);
            }

            // A failed watcher is a monitoring gap, not a clean cache state. Advance generations
            // now and again after the bounded backoff so every refresh re-enters the safe lease.
            invalidateDirectory = true;
        }
        finally
        {
            watcher?.Dispose();
        }

        evictedWatcher?.Dispose();
        if (evictedDirectory is not null)
        {
            InvalidateTrackedDirectory(evictedDirectory);
        }

        if (invalidateDirectory)
        {
            InvalidateTrackedDirectory(directory);
        }

        if (scheduleMonitoringRetry)
        {
            ScheduleMonitoringRetry();
        }
    }

    private static void Watcher_Changed(object sender, FileSystemEventArgs eventArgs)
    {
        _ = sender;
        InvalidateTrackedPath(eventArgs.FullPath);
    }

    private static void Watcher_Renamed(object sender, RenamedEventArgs eventArgs)
    {
        _ = sender;
        InvalidateTrackedPath(eventArgs.OldFullPath);
        InvalidateTrackedPath(eventArgs.FullPath);
    }

    private static void Watcher_Error(object sender, ErrorEventArgs eventArgs)
    {
        _ = eventArgs;
        if (sender is not FileSystemWatcher watcher)
        {
            return;
        }

        var directory = watcher.Path;
        lock (StateLock)
        {
            if (!DirectoryWatchers.TryGetValue(directory, out var registration) ||
                !ReferenceEquals(registration.Watcher, watcher))
            {
                return;
            }

            DirectoryWatchers.Remove(directory);
            RecordMonitoringFailure(
                directory,
                Environment.TickCount64,
                registration.FailureCount);
        }

        watcher.Dispose();
        InvalidateTrackedDirectory(directory);
        ScheduleMonitoringRetry();
    }

    private static void ScheduleMonitoringRetry()
    {
        lock (StateLock)
        {
            ScheduleMonitoringRetryLocked();
        }
    }

    private static void ScheduleMonitoringRetryLocked()
    {
        var now = Environment.TickCount64;
        var nextRetryTick = MonitoringGaps.Values
            .Where(gap => !gap.RetrySignalIssued)
            .Select(gap => gap.RetryAfterTick)
            .DefaultIfEmpty(long.MaxValue)
            .Min();
        if (nextRetryTick == long.MaxValue)
        {
            _monitoringRetryTimer?.Change(
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            return;
        }

        _monitoringRetryTimer ??= new Timer(
            static _ => SignalDueMonitoringRetries(),
            state: null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _monitoringRetryTimer.Change(
            TimeSpan.FromMilliseconds(Math.Max(1L, nextRetryTick - now)),
            Timeout.InfiniteTimeSpan);
    }

    private static void SignalDueMonitoringRetries()
    {
        List<string> dueDirectories;
        lock (StateLock)
        {
            var now = Environment.TickCount64;
            dueDirectories = MonitoringGaps
                .Where(
                    entry => !entry.Value.RetrySignalIssued &&
                        now >= entry.Value.RetryAfterTick)
                .Select(entry => entry.Key)
                .ToList();
            foreach (var directory in dueDirectories)
            {
                if (MonitoringGaps.TryGetValue(directory, out var gap))
                {
                    gap.RetrySignalIssued = true;
                }
            }

            ScheduleMonitoringRetryLocked();
        }

        foreach (var directory in dueDirectories)
        {
            // Do not recreate a watcher on this worker. Advancing the generation makes the next
            // converter/profile refresh acquire a validated lease before monitoring is retried.
            InvalidateTrackedDirectory(directory);
        }
    }

    private static void InvalidateTrackedDirectory(string directory)
    {
        List<string> affectedSourceKeys;
        lock (StateLock)
        {
            affectedSourceKeys = TrackedSources
                .Where(
                    entry => entry.Value.MonitoredPath is { } monitoredPath &&
                        string.Equals(
                            Path.GetDirectoryName(monitoredPath),
                            directory,
                            StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Key)
                .ToList();
        }

        foreach (var sourceKey in affectedSourceKeys)
        {
            InvalidateSourceKey(sourceKey, addIfMissing: false);
        }
    }

    private static void InvalidateTrackedPath(string monitoredPath)
    {
        monitoredPath = NormalizePath(monitoredPath);
        List<string> affectedSourceKeys;
        lock (StateLock)
        {
            affectedSourceKeys = TrackedSources
                .Where(
                    entry => string.Equals(
                        entry.Value.MonitoredPath,
                        monitoredPath,
                        StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Key)
                .ToList();
        }

        foreach (var sourceKey in affectedSourceKeys)
        {
            InvalidateSourceKey(sourceKey, addIfMissing: false);
        }
    }

    private static void InvalidateSourceKey(string sourceKey, bool addIfMissing)
    {
        EventHandler<MediaThumbnailSourceInvalidatedEventArgs>? handlers;
        lock (StateLock)
        {
            if (TrackedSources.TryGetValue(sourceKey, out var state))
            {
                state.Generation = ++_nextGeneration;
                state.LastAccessSequence = ++_nextAccessSequence;
            }
            else if (addIfMissing)
            {
                TrimTrackedSourcesIfNeeded();
                TrackedSources.Add(
                    sourceKey,
                    new SourceState(++_nextGeneration, ++_nextAccessSequence));
            }
            else
            {
                return;
            }

            handlers = SourceInvalidated;
        }

        handlers?.Invoke(
            sender: null,
            new MediaThumbnailSourceInvalidatedEventArgs(sourceKey));
    }

    private static SourceState GetOrAddSource(string sourceKey)
    {
        var accessSequence = ++_nextAccessSequence;
        if (!TrackedSources.TryGetValue(sourceKey, out var state))
        {
            TrimTrackedSourcesIfNeeded();
            state = new SourceState(++_nextGeneration, accessSequence);
            TrackedSources.Add(sourceKey, state);
        }
        else
        {
            state.LastAccessSequence = accessSequence;
        }

        return state;
    }

    private static void TrimTrackedSourcesIfNeeded()
    {
        if (TrackedSources.Count < MaximumTrackedSources)
        {
            return;
        }

        var oldest = TrackedSources.MinBy(entry => entry.Value.LastAccessSequence);
        if (string.IsNullOrEmpty(oldest.Key))
        {
            return;
        }

        TrackedSources.Remove(oldest.Key);
    }

    private static void RecordMonitoringFailure(
        string directory,
        long now,
        int previousFailureCount)
    {
        if (!MonitoringGaps.TryGetValue(directory, out var gap))
        {
            TrimMonitoringGapsIfNeeded();
            gap = new MonitoringGapState();
            MonitoringGaps.Add(directory, gap);
        }

        gap.LastAccessSequence = ++_nextAccessSequence;
        gap.RetrySignalIssued = false;
        gap.FailureCount = Math.Max(gap.FailureCount, previousFailureCount);
        gap.FailureCount = Math.Min(gap.FailureCount + 1, 6);
        var retryDelayMilliseconds = Math.Min(
            1_000L << (gap.FailureCount - 1),
            60_000L);
        gap.RetryAfterTick = now + retryDelayMilliseconds;
    }

    private static void TrimMonitoringGapsIfNeeded()
    {
        if (MonitoringGaps.Count < MaximumMonitoringGaps)
        {
            return;
        }

        var oldest = MonitoringGaps.MinBy(entry => entry.Value.LastAccessSequence);
        if (!string.IsNullOrEmpty(oldest.Key))
        {
            MonitoringGaps.Remove(oldest.Key);
        }
    }

    private static TimeSpan BoundRetryDelay(TimeSpan delay) =>
        TimeSpan.FromMilliseconds(
            Math.Clamp(
                delay.TotalMilliseconds,
                1d,
                TimeSpan.FromMinutes(1).TotalMilliseconds));

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private sealed class SourceState(long generation, long lastAccessSequence)
    {
        public long Generation { get; set; } = generation;

        public long LastAccessSequence { get; set; } = lastAccessSequence;

        public string? MonitoredPath { get; set; }
    }

    private sealed class WatcherRegistration(
        FileSystemWatcher watcher,
        long lastAccessSequence,
        int failureCount)
    {
        public FileSystemWatcher Watcher { get; } = watcher;

        public long LastAccessSequence { get; set; } = lastAccessSequence;

        public int FailureCount { get; } = failureCount;
    }

    private sealed class MonitoringGapState
    {
        public int FailureCount { get; set; }

        public long RetryAfterTick { get; set; }

        public long LastAccessSequence { get; set; }

        public bool RetrySignalIssued { get; set; }
    }
}

internal sealed class MediaThumbnailSourceInvalidatedEventArgs(string path) : EventArgs
{
    public string Path { get; } = path;
}
