using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.App.Services.Media;

/// <summary>
/// Renders one still frame from a validated local video for rail thumbnails. Frames are
/// produced on the UI thread through WPF's own media pipeline, one video at a time, and kept
/// in a small in-memory cache. Nothing is written to disk.
/// </summary>
internal static class LocalVideoThumbnailCache
{
    private const int MaximumCachedItems = 32;
    private const int ThumbnailWidth = 192;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan FrameSettleDelay = TimeSpan.FromMilliseconds(320);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, BitmapSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool SupportsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<BitmapSource?> LoadAsync(
        string path,
        ISafeMediaPreviewService previewMedia)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(previewMedia);
        var fullPath = Path.GetFullPath(path);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(fullPath, out var cached))
            {
                return cached;
            }
        }

        await Gate.WaitAsync().ConfigureAwait(true);
        try
        {
            lock (CacheGate)
            {
                if (Cache.TryGetValue(fullPath, out var cached))
                {
                    return cached;
                }
            }

            BitmapSource? frame = null;
            try
            {
                frame = await RenderFrameAsync(fullPath, previewMedia)
                    .ConfigureAwait(true);
            }
            catch (Exception)
            {
                // A still frame is optional; the caller keeps its video glyph.
                frame = null;
            }

            lock (CacheGate)
            {
                if (Cache.Count >= MaximumCachedItems)
                {
                    Cache.Clear();
                }

                Cache[fullPath] = frame;
            }

            return frame;
        }
        finally
        {
            _ = Gate.Release();
        }
    }

    private static async Task<BitmapSource?> RenderFrameAsync(
        string fullPath,
        ISafeMediaPreviewService previewMedia)
    {
        // The lease validates the final path and keeps a read-only handle while WPF decodes.
        using var lease = previewMedia.Acquire(fullPath);
        if (lease.Metadata.Kind != MediaKind.Video)
        {
            return null;
        }

        var source = lease.CreateVideoSource();
        var player = new MediaPlayer
        {
            ScrubbingEnabled = true,
            IsMuted = true,
            Volume = 0,
        };
        var opened = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, _) => opened.TrySetResult(false);
        try
        {
            player.Open(source);
            var completed = await Task
                .WhenAny(opened.Task, Task.Delay(OpenTimeout))
                .ConfigureAwait(true);
            if (!ReferenceEquals(completed, opened.Task) || !await opened.Task.ConfigureAwait(true))
            {
                return null;
            }

            var width = player.NaturalVideoWidth;
            var height = player.NaturalVideoHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            // Skip the first frames, which are often a fade from black.
            var seek = TimeSpan.FromSeconds(1);
            if (player.NaturalDuration.HasTimeSpan)
            {
                var duration = player.NaturalDuration.TimeSpan;
                seek = TimeSpan.FromTicks(
                    Math.Min(seek.Ticks, Math.Max(0, duration.Ticks / 10)));
            }

            player.Position = seek;
            await Task.Delay(FrameSettleDelay).ConfigureAwait(true);

            var targetWidth = ThumbnailWidth;
            var targetHeight = Math.Max(
                1,
                (int)Math.Round((double)height * targetWidth / width));
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawVideo(player, new Rect(0, 0, targetWidth, targetHeight));
            }

            var bitmap = new RenderTargetBitmap(
                targetWidth,
                targetHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            player.Close();
        }
    }
}
