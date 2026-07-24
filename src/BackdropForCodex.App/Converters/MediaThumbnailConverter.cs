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
    private readonly ISafeMediaPreviewService _previewMedia;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, ImageSource> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public MediaThumbnailConverter()
        : this(SafeMediaPreviewService.Shared)
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
        if (value is not string path || !IsImage(path))
        {
            return null;
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        try
        {
            using var lease = _previewMedia.Acquire(path);
            var thumbnail = lease.LoadBitmap(decodePixelWidth: 112);

            lock (_cacheLock)
            {
                if (_cache.Count >= MaximumCachedThumbnails)
                {
                    _cache.Clear();
                }

                _cache[path] = thumbnail;
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
            return null;
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
}
