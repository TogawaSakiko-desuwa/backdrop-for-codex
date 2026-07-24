using System.IO;
using System.Windows.Media.Imaging;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.App.Services.Media;

/// <summary>
/// A validated media lease for management-side previews. Consumers may only obtain a preview URI
/// or decoded bitmap after the source provider has pinned and validated the final local file.
/// </summary>
public interface ISafeMediaPreviewLease : IDisposable, IAsyncDisposable
{
    MediaFileMetadata Metadata { get; }

    BitmapSource LoadBitmap(int decodePixelWidth);

    Uri CreateVideoSource();
}

public interface ISafeMediaPreviewService
{
    ISafeMediaPreviewLease Acquire(string mediaPath);

    bool IsAvailable(string mediaPath);
}

/// <summary>
/// Routes every management-side media probe and decode through the same provider boundary used by
/// the runtime. The synchronous surface is intentional because WPF converters and dependency
/// property callbacks are synchronous; the provider itself performs only a bounded header read.
/// </summary>
public sealed class SafeMediaPreviewService : ISafeMediaPreviewService
{
    private readonly IWallpaperSourceProvider _sourceProvider;

    public SafeMediaPreviewService(IWallpaperSourceProvider? sourceProvider = null)
    {
        _sourceProvider = sourceProvider ?? new LocalFileWallpaperSourceProvider();
        if (_sourceProvider.SourceKind != MediaSourceKind.LocalFile)
        {
            throw new ArgumentException(
                "The preview service requires a local-file source provider.",
                nameof(sourceProvider));
        }
    }

    public static SafeMediaPreviewService Shared { get; } = new();

    public ISafeMediaPreviewLease Acquire(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        var reference = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = mediaPath,
            LastKnownKind = MediaKind.None,
        };
        var lease = _sourceProvider
            .AcquireLeaseAsync(reference)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return new SafeMediaPreviewLease(lease);
    }

    public bool IsAvailable(string mediaPath)
    {
        try
        {
            using var lease = Acquire(mediaPath);
            return lease.Metadata.Kind is MediaKind.Image or MediaKind.Video;
        }
        catch (Exception exception) when (IsExpectedValidationFailure(exception))
        {
            return false;
        }
    }

    internal static (int Width, int Height) CalculateDecodePixelSize(
        MediaFileMetadata metadata,
        int maximumSideLength)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSideLength);
        if (metadata.Kind != MediaKind.Image ||
            metadata.PixelWidth is not int sourceWidth ||
            metadata.PixelHeight is not int sourceHeight ||
            sourceWidth <= 0 ||
            sourceHeight <= 0 ||
            sourceWidth > MediaFileInspector.MaximumImageDimension ||
            sourceHeight > MediaFileInspector.MaximumImageDimension ||
            (long)sourceWidth * sourceHeight > MediaFileInspector.MaximumImagePixelCount)
        {
            throw new MediaValidationException(
                "The validated image dimensions are unavailable or outside the preview limits.");
        }

        var scale = Math.Min(
            1d,
            Math.Min(
                (double)maximumSideLength / sourceWidth,
                (double)maximumSideLength / sourceHeight));
        return (
            Math.Max(1, (int)Math.Floor(sourceWidth * scale)),
            Math.Max(1, (int)Math.Floor(sourceHeight * scale)));
    }

    private static bool IsExpectedValidationFailure(Exception exception) => exception is
        MediaValidationException or
        MediaReferenceValidationException or
        MediaSourceNotSupportedException or
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        PlatformNotSupportedException or
        ArgumentException;

    private sealed class SafeMediaPreviewLease(IMediaLease lease) : ISafeMediaPreviewLease
    {
        private IMediaLease? _lease = lease ?? throw new ArgumentNullException(nameof(lease));

        public MediaFileMetadata Metadata => GetLease().Metadata;

        public BitmapSource LoadBitmap(int decodePixelWidth)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodePixelWidth);

            var activeLease = GetLease();
            if (activeLease.Metadata.Kind != MediaKind.Image)
            {
                throw new InvalidOperationException("Only validated images can be decoded as bitmaps.");
            }

            var decodeSize = CalculateDecodePixelSize(
                activeLease.Metadata,
                decodePixelWidth);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodeSize.Width;
            bitmap.DecodePixelHeight = decodeSize.Height;
            bitmap.UriSource = CreateFileUri(activeLease.ResolvedPath);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        public Uri CreateVideoSource()
        {
            var activeLease = GetLease();
            if (activeLease.Metadata.Kind != MediaKind.Video)
            {
                throw new InvalidOperationException("Only validated videos can be used as video sources.");
            }

            return CreateFileUri(activeLease.ResolvedPath);
        }

        public void Dispose() =>
            DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();

        public async ValueTask DisposeAsync()
        {
            var activeLease = Interlocked.Exchange(ref _lease, null);
            if (activeLease is not null)
            {
                await activeLease.DisposeAsync().ConfigureAwait(false);
            }

            GC.SuppressFinalize(this);
        }

        private IMediaLease GetLease() =>
            Volatile.Read(ref _lease) ??
            throw new ObjectDisposedException(nameof(SafeMediaPreviewLease));

        private static Uri CreateFileUri(string path) =>
            new UriBuilder(Uri.UriSchemeFile, string.Empty)
            {
                Path = path,
            }.Uri;
    }
}
