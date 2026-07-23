using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BackdropForCodex.App.Converters;

/// <summary>
/// Decodes small image previews into a bounded process-memory cache. It never writes thumbnails.
/// </summary>
public sealed class MediaThumbnailConverter : IValueConverter
{
    private const int MaximumCachedThumbnails = 32;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, ImageSource> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is not string path || !IsImage(path) || !File.Exists(path))
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
            var thumbnail = new BitmapImage();
            thumbnail.BeginInit();
            thumbnail.CacheOption = BitmapCacheOption.OnLoad;
            thumbnail.DecodePixelWidth = 112;
            thumbnail.UriSource = new Uri(path, UriKind.Absolute);
            thumbnail.EndInit();
            thumbnail.Freeze();

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
