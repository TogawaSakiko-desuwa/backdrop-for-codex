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
    ISafeMediaPreviewLease Acquire(MediaReference reference);

    ISafeMediaPreviewLease Acquire(string mediaPath);

    bool IsAvailable(MediaReference reference);

    bool IsAvailable(string mediaPath);
}

/// <summary>
/// Routes every management-side media probe and decode through the same provider boundary used by
/// the runtime. The synchronous surface is intentional because WPF converters and dependency
/// property callbacks are synchronous; the provider itself performs only a bounded header read.
/// </summary>
public sealed class SafeMediaPreviewService : ISafeMediaPreviewService
{
    private readonly IWallpaperSourceProviderRegistry _sourceRegistry;

    public SafeMediaPreviewService(IWallpaperSourceProviderRegistry sourceRegistry)
    {
        _sourceRegistry = sourceRegistry ??
            throw new ArgumentNullException(nameof(sourceRegistry));
    }

    public IWallpaperSourceProviderRegistry SourceRegistry => _sourceRegistry;

    public ISafeMediaPreviewLease Acquire(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        return Acquire(
            new MediaReference
            {
                MediaId = Guid.CreateVersion7(),
                SourceKind = MediaSourceKind.LocalFile,
                SourceIdentifier = mediaPath,
                LastKnownKind = MediaKind.None,
            });
    }

    public ISafeMediaPreviewLease Acquire(MediaReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return AcquireAsync(reference)
            .GetAwaiter()
            .GetResult();
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

    public bool IsAvailable(MediaReference reference)
    {
        try
        {
            using var lease = Acquire(reference);
            return lease.Metadata.Kind is MediaKind.Image or MediaKind.Video;
        }
        catch (Exception exception) when (IsExpectedValidationFailure(exception))
        {
            return false;
        }
    }

    private async Task<ISafeMediaPreviewLease> AcquireAsync(MediaReference reference)
    {
        var resolution = await _sourceRegistry
            .ResolveRequiredAsync(reference)
            .ConfigureAwait(false);
        var provider = resolution.Descriptor.DeliveryKind switch
        {
            WallpaperDeliveryKind.DirectMedia =>
                _sourceRegistry.GetRequiredDirectMediaProvider(resolution),
            WallpaperDeliveryKind.WallpaperEngineWindow =>
                throw new WallpaperRendererUnavailableException(resolution.Descriptor),
            WallpaperDeliveryKind.Unsupported =>
                throw new WallpaperContentNotSupportedException(resolution.Descriptor),
            _ => throw new WallpaperSourceCapabilityException(
                "The wallpaper source declared an unknown preview delivery path."),
        };
        var lease = await provider
            .AcquireDirectMediaLeaseAsync(resolution.CanonicalReference)
            .ConfigureAwait(false);
        try
        {
            var acquiredResolution = new WallpaperSourceResolution(
                lease.Reference,
                resolution.Descriptor,
                lease.Metadata);
            if (acquiredResolution.CanonicalReference.MediaId !=
                resolution.CanonicalReference.MediaId)
            {
                throw new WallpaperSourceCapabilityException(
                    "The preview provider acquired a different source identity.");
            }

            return new SafeMediaPreviewLease(lease);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
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
        WallpaperSourceCapabilityException or
        WallpaperEngineProjectUnavailableException or
        WallpaperRendererUnavailableException or
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        PlatformNotSupportedException or
        ArgumentException;

    private sealed class SafeMediaPreviewLease(IDirectMediaLease lease) : ISafeMediaPreviewLease
    {
        private IDirectMediaLease? _lease = lease ?? throw new ArgumentNullException(nameof(lease));

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

        private IDirectMediaLease GetLease() =>
            Volatile.Read(ref _lease) ??
            throw new ObjectDisposedException(nameof(SafeMediaPreviewLease));

        private static Uri CreateFileUri(string path) =>
            new UriBuilder(Uri.UriSchemeFile, string.Empty)
            {
                Path = path,
            }.Uri;
    }
}
