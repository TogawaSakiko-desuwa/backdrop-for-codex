using System.IO;
using System.Windows.Media.Imaging;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.App.Services.Media;

public interface IWallpaperThumbnailPreviewService
{
    Task<BitmapSource?> LoadAsync(
        MediaReference reference,
        int decodePixelWidth,
        CancellationToken cancellationToken = default);
}

internal interface IWallpaperThumbnailCacheInvalidation
{
    void AdvanceGenerations(IReadOnlyCollection<MediaSourceKind> sourceKinds);
}

public sealed class NullWallpaperThumbnailPreviewService
    : IWallpaperThumbnailPreviewService
{
    public static NullWallpaperThumbnailPreviewService Instance { get; } = new();

    private NullWallpaperThumbnailPreviewService()
    {
    }

    public Task<BitmapSource?> LoadAsync(
        MediaReference reference,
        int decodePixelWidth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodePixelWidth);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<BitmapSource?>(null);
    }
}

public sealed class WallpaperThumbnailPreviewService
    : IWallpaperThumbnailPreviewService,
      IWallpaperThumbnailCacheInvalidation
{
    public const int MaximumCachedItems = 64;
    public const long MaximumCachedBytes = 48L * 1024 * 1024;
    public const int MinimumDecodePixelWidth = 160;
    public const int MaximumDecodePixelWidth = 240;
    public const int MaximumSourcePixelDimension = 65_535;
    public const long MaximumSourcePixelCount = 100_000_000;
    public const int MaximumDecodedPixelDimension = 4_096;

    private readonly IWallpaperSourceProviderRegistry _sourceRegistry;
    private readonly object _cacheGate = new();
    private readonly Dictionary<ThumbnailCacheKey, LinkedListNode<ThumbnailCacheEntry>> _cache =
        new(ThumbnailCacheKeyComparer.Instance);
    private readonly LinkedList<ThumbnailCacheEntry> _lru = [];
    private readonly Dictionary<MediaSourceKind, long> _sourceGenerations = [];
    private long _nextGeneration;
    private long _cachedBytes;

    public WallpaperThumbnailPreviewService(IWallpaperSourceProviderRegistry sourceRegistry)
    {
        _sourceRegistry = sourceRegistry ??
            throw new ArgumentNullException(nameof(sourceRegistry));
    }

    internal int CachedItemCount
    {
        get
        {
            lock (_cacheGate)
            {
                return _cache.Count;
            }
        }
    }

    internal long CachedByteCount
    {
        get
        {
            lock (_cacheGate)
            {
                return _cachedBytes;
            }
        }
    }

    public async Task<BitmapSource?> LoadAsync(
        MediaReference reference,
        int decodePixelWidth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var snapshot = reference.Snapshot();
        var width = Math.Clamp(
            decodePixelWidth,
            MinimumDecodePixelWidth,
            MaximumDecodePixelWidth);
        var identity = new WallpaperThumbnailIdentity(
            snapshot.SourceKind,
            snapshot.SourceIdentifier);
        ThumbnailCacheKey key;
        lock (_cacheGate)
        {
            key = new ThumbnailCacheKey(
                identity,
                GetGenerationLocked(identity.SourceKind),
                width);
        }

        if (TryGetCached(key, out var cached))
        {
            return cached;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var provider = _sourceRegistry.GetRequired(snapshot.SourceKind);
        if (provider is not IWallpaperThumbnailSourceProvider thumbnailProvider)
        {
            return null;
        }

        await using var lease = await thumbnailProvider
            .AcquireThumbnailLeaseAsync(snapshot, cancellationToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateDeclaredGeometry(lease.ContentStream, width);
        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = Decode(lease.ContentStream, width);
        cancellationToken.ThrowIfCancellationRequested();
        AddCached(key, bitmap);
        return bitmap;
    }

    void IWallpaperThumbnailCacheInvalidation.AdvanceGenerations(
        IReadOnlyCollection<MediaSourceKind> sourceKinds)
    {
        ArgumentNullException.ThrowIfNull(sourceKinds);
        var invalidatedKinds = sourceKinds.ToHashSet();
        if (invalidatedKinds.Count == 0)
        {
            return;
        }

        lock (_cacheGate)
        {
            var nextGeneration = checked(_nextGeneration + 1);
            foreach (var node in _cache.Values.ToArray())
            {
                if (invalidatedKinds.Contains(node.Value.Key.Identity.SourceKind))
                {
                    RemoveCachedNodeLocked(node);
                }
            }

            foreach (var sourceKind in invalidatedKinds)
            {
                _sourceGenerations[sourceKind] = nextGeneration;
            }

            _nextGeneration = nextGeneration;
        }
    }

    private static void ValidateDeclaredGeometry(
        Stream stream,
        int decodePixelWidth)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new WallpaperSourceCapabilityException(
                "The thumbnail lease did not expose a readable, seekable stream.");
        }

        stream.Position = 0;
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat |
                BitmapCreateOptions.DelayCreation,
            BitmapCacheOption.OnDemand);
        var frame = decoder.Frames.FirstOrDefault() ??
            throw new WallpaperSourceCapabilityException(
                "The thumbnail decoder returned no image frame.");
        if (frame.PixelWidth <= 0 ||
            frame.PixelHeight <= 0 ||
            frame.PixelWidth > MaximumSourcePixelDimension ||
            frame.PixelHeight > MaximumSourcePixelDimension)
        {
            throw new WallpaperSourceCapabilityException(
                "The thumbnail's declared dimensions exceed supported limits.");
        }

        var sourcePixelCount = checked((long)frame.PixelWidth * frame.PixelHeight);
        if (sourcePixelCount > MaximumSourcePixelCount)
        {
            throw new WallpaperSourceCapabilityException(
                "The thumbnail's declared pixel count exceeds supported limits.");
        }

        var projectedPixelHeight = checked(
            (long)Math.Ceiling(
                (double)frame.PixelHeight * decodePixelWidth / frame.PixelWidth));
        if (decodePixelWidth > MaximumDecodedPixelDimension ||
            projectedPixelHeight > MaximumDecodedPixelDimension)
        {
            throw new WallpaperSourceCapabilityException(
                "The thumbnail's projected decoded dimensions exceed supported limits.");
        }

        var bitsPerPixel = frame.Format.BitsPerPixel;
        if (bitsPerPixel <= 0)
        {
            throw new WallpaperSourceCapabilityException(
                "The thumbnail exposed an unsupported pixel format.");
        }

        var projectedBitsPerRow = checked((long)decodePixelWidth * bitsPerPixel);
        var projectedBytesPerRow = checked((projectedBitsPerRow + 7) / 8);
        var projectedByteCount = checked(projectedBytesPerRow * projectedPixelHeight);
        if (projectedByteCount > MaximumCachedBytes)
        {
            throw new WallpaperSourceCapabilityException(
                "The thumbnail's projected decoded byte count exceeds supported limits.");
        }
    }

    private static BitmapImage Decode(Stream stream, int decodePixelWidth)
    {
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new WallpaperSourceCapabilityException(
                "The thumbnail lease did not expose a readable, seekable stream.");
        }

        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            throw new WallpaperSourceCapabilityException(
                "The thumbnail decoder returned invalid dimensions.");
        }

        return bitmap;
    }

    private bool TryGetCached(ThumbnailCacheKey key, out BitmapSource? bitmap)
    {
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(key, out var node))
            {
                bitmap = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return true;
        }
    }

    private void AddCached(ThumbnailCacheKey key, BitmapSource bitmap)
    {
        var bitsPerPixel = bitmap.Format.BitsPerPixel;
        if (bitsPerPixel <= 0)
        {
            throw new WallpaperSourceCapabilityException(
                "The decoded thumbnail exposed an unsupported pixel format.");
        }

        var bitsPerRow = checked((long)bitmap.PixelWidth * bitsPerPixel);
        var bytesPerRow = checked((bitsPerRow + 7) / 8);
        var byteCount = checked(bytesPerRow * bitmap.PixelHeight);
        if (byteCount > MaximumCachedBytes)
        {
            return;
        }

        lock (_cacheGate)
        {
            if (key.Generation != GetGenerationLocked(key.Identity.SourceKind))
            {
                return;
            }

            if (_cache.TryGetValue(key, out var existing))
            {
                RemoveCachedNodeLocked(existing);
            }

            var entry = new ThumbnailCacheEntry(key, bitmap, byteCount);
            var node = _lru.AddFirst(entry);
            _cache.Add(key, node);
            _cachedBytes += byteCount;
            while (_cache.Count > MaximumCachedItems ||
                   _cachedBytes > MaximumCachedBytes)
            {
                var last = _lru.Last;
                if (last is null)
                {
                    break;
                }

                RemoveCachedNodeLocked(last);
            }
        }
    }

    private void RemoveCachedNodeLocked(
        LinkedListNode<ThumbnailCacheEntry> node)
    {
        _lru.Remove(node);
        _cache.Remove(node.Value.Key);
        _cachedBytes -= node.Value.ByteCount;
    }

    private long GetGenerationLocked(MediaSourceKind sourceKind) =>
        _sourceGenerations.GetValueOrDefault(sourceKind);

    private readonly record struct ThumbnailCacheKey(
        WallpaperThumbnailIdentity Identity,
        long Generation,
        int DecodePixelWidth);

    private sealed record ThumbnailCacheEntry(
        ThumbnailCacheKey Key,
        BitmapSource Bitmap,
        long ByteCount);

    private readonly record struct WallpaperThumbnailIdentity(
        MediaSourceKind SourceKind,
        string SourceIdentifier);

    private sealed class WallpaperThumbnailIdentityComparer
        : IEqualityComparer<WallpaperThumbnailIdentity>
    {
        public static WallpaperThumbnailIdentityComparer Instance { get; } = new();

        public bool Equals(
            WallpaperThumbnailIdentity left,
            WallpaperThumbnailIdentity right) =>
            left.SourceKind == right.SourceKind &&
            string.Equals(
                left.SourceIdentifier,
                right.SourceIdentifier,
                left.SourceKind == MediaSourceKind.WallpaperEngineLocalProject
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);

        public int GetHashCode(WallpaperThumbnailIdentity identity)
        {
            var identifierComparer =
                identity.SourceKind == MediaSourceKind.WallpaperEngineLocalProject
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;
            return HashCode.Combine(
                identity.SourceKind,
                identifierComparer.GetHashCode(identity.SourceIdentifier));
        }
    }

    private sealed class ThumbnailCacheKeyComparer
        : IEqualityComparer<ThumbnailCacheKey>
    {
        public static ThumbnailCacheKeyComparer Instance { get; } = new();

        public bool Equals(ThumbnailCacheKey left, ThumbnailCacheKey right) =>
            left.Generation == right.Generation &&
            left.DecodePixelWidth == right.DecodePixelWidth &&
            WallpaperThumbnailIdentityComparer.Instance.Equals(
                left.Identity,
                right.Identity);

        public int GetHashCode(ThumbnailCacheKey key) =>
            HashCode.Combine(
                WallpaperThumbnailIdentityComparer.Instance.GetHashCode(key.Identity),
                key.Generation,
                key.DecodePixelWidth);
    }
}
