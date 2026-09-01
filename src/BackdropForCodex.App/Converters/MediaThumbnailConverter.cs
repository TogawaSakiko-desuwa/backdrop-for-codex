using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Data;
using System.Windows.Media;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.App.Converters;

/// <summary>
/// Decodes small image previews into a bounded process-memory cache. It never writes thumbnails.
/// </summary>
public sealed class MediaThumbnailConverter : IValueConverter
{
    private const int MaximumCachedThumbnails = 32;
    private const int MaximumFailedRefreshBackoffExponent = 5;
    private readonly ISafeMediaPreviewService _previewMedia;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedThumbnail> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public MediaThumbnailConverter()
        : this(AppWallpaperSources.Preview)
    {
    }

    public MediaThumbnailConverter(ISafeMediaPreviewService previewMedia)
    {
        _previewMedia =
            previewMedia ?? throw new ArgumentNullException(nameof(previewMedia));
    }

    public object? Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;
        if (value is not string path || !IsImage(path))
        {
            return null;
        }

        CachedThumbnail? cachedFallback = null;
        var generation = 0L;
        try
        {
            path = Path.GetFullPath(path);
            // Generation lookup is deliberately memory-only. SafeMediaPreviewService establishes
            // directory monitoring only after its provider has pinned and validated the final file.
            generation = MediaThumbnailInvalidationHub.ObserveSource(path);
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(path, out var cached))
                {
                    cachedFallback = cached;
                    if (cached.Generation == generation ||
                        (cached.FailedGeneration == generation &&
                         Environment.TickCount64 < cached.RetryAfterTick))
                    {
                        // A valid or throttled cache hit is a pure in-memory path: no stat, handle
                        // open, or decoder call can block WPF layout.
                        return cached.Image;
                    }
                }
            }

            using var lease = _previewMedia.Acquire(path);
            // The validated lease remains held here, so a watcher created by the preview service
            // cannot race a path replacement before this generation is sampled. An invalidation
            // that arrives during decoding then leaves the cached generation stale on purpose.
            generation = MediaThumbnailInvalidationHub.ObserveSource(path);
            var thumbnail = lease.LoadBitmap(decodePixelWidth: 112);

            lock (_cacheLock)
            {
                if (_cache.Count >= MaximumCachedThumbnails)
                {
                    _cache.Clear();
                }

                // Store the generation observed before decoding. If the file changed while it was
                // being read, the next conversion sees a mismatch and retries.
                _cache[path] = new CachedThumbnail(generation, thumbnail);
            }

            return thumbnail;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException or
            MediaReferenceValidationException or
            FormatException or
            ExternalException or
            SecurityException)
        {
            if (cachedFallback is null)
            {
                return null;
            }

            var retryDelay = TimeSpan.FromMilliseconds(500);
            lock (_cacheLock)
            {
                if (ReferenceEquals(_cache.GetValueOrDefault(path), cachedFallback))
                {
                    cachedFallback.FailureCount = Math.Min(
                        cachedFallback.FailureCount + 1,
                        MaximumFailedRefreshBackoffExponent + 1);
                    cachedFallback.FailedGeneration = generation;
                    var retryDelayMilliseconds =
                        500L << Math.Min(
                            cachedFallback.FailureCount - 1,
                            MaximumFailedRefreshBackoffExponent);
                    cachedFallback.RetryAfterTick =
                        Environment.TickCount64 + retryDelayMilliseconds;
                    retryDelay = TimeSpan.FromMilliseconds(retryDelayMilliseconds);
                }
            }

            // A finite, hub-deduplicated retry produces another UI invalidation after a transient
            // writer releases the file; repeated layout passes do not synchronously hammer it.
            MediaThumbnailInvalidationHub.ScheduleRetry(path, generation, retryDelay);
            return cachedFallback.Image;
        }
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CachedThumbnail(long generation, ImageSource image)
    {
        public long Generation { get; } = generation;

        public ImageSource Image { get; } = image;

        public long FailedGeneration { get; set; } = -1;

        public long RetryAfterTick { get; set; }

        public int FailureCount { get; set; }
    }
}
