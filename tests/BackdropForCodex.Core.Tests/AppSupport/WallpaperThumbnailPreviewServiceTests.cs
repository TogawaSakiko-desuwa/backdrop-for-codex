using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

[Collection("Wpf")]
public sealed class WallpaperThumbnailPreviewServiceTests
{
    [Fact]
    public async Task LoadAsyncRejectsAnOversizedDeclaredDimensionAndReleasesTheLease()
    {
        var thumbnail = PatchPngDimensions(
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
            width: 100_000,
            height: 1);
        var provider = new ThumbnailProvider(thumbnail);
        var service = CreateService(provider);

        var exception = await Assert.ThrowsAsync<WallpaperSourceCapabilityException>(
            () => service.LoadAsync(CreateReference("1001"), decodePixelWidth: 240));

        Assert.Contains("dimension", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(provider.LeaseDisposed);
    }

    [Fact]
    public async Task LoadAsyncRejectsAnOversizedDeclaredPixelCount()
    {
        var thumbnail = PatchPngDimensions(
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
            width: 20_000,
            height: 20_000);
        var provider = new ThumbnailProvider(thumbnail);
        var service = CreateService(provider);

        var exception = await Assert.ThrowsAsync<WallpaperSourceCapabilityException>(
            () => service.LoadAsync(CreateReference("1002"), decodePixelWidth: 240));

        Assert.Contains("pixel count", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(provider.LeaseDisposed);
    }

    [Fact]
    public async Task CacheBudgetUsesTheDecodedPixelFormatsActualBitsPerPixel()
    {
        var thumbnail = CreateIndexedPng(width: 240, height: 4_000);
        var provider = new ThumbnailProvider(thumbnail);
        var service = CreateService(provider);
        var references = Enumerable.Range(2_000, WallpaperThumbnailPreviewService.MaximumCachedItems)
            .Select(index => CreateReference(index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        foreach (var reference in references)
        {
            var preview = await service.LoadAsync(reference, decodePixelWidth: 240);
            Assert.NotNull(preview);
            Assert.Equal(1, preview.Format.BitsPerPixel);
        }

        var cachedFirst = await service.LoadAsync(references[0], decodePixelWidth: 240);

        Assert.NotNull(cachedFirst);
        Assert.Equal(WallpaperThumbnailPreviewService.MaximumCachedItems, provider.AcquisitionCount);
    }

    [Fact]
    public async Task LoadAsyncRejectsAnOversizedProjectedDecodeDimension()
    {
        var thumbnail = PatchPngDimensions(
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
            width: 500,
            height: 10_000);
        var provider = new ThumbnailProvider(thumbnail);
        var service = CreateService(provider);

        var exception = await Assert.ThrowsAsync<WallpaperSourceCapabilityException>(
            () => service.LoadAsync(CreateReference("1003"), decodePixelWidth: 240));

        Assert.Contains("decoded dimension", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(provider.LeaseDisposed);
    }

    [Fact]
    public async Task LoadAsyncReturnsABoundedFrozenPreviewForANormalJpeg()
    {
        var provider = new ThumbnailProvider(CreateJpeg(width: 320, height: 160));
        var service = CreateService(provider);

        var preview = await service.LoadAsync(
            CreateReference("1004"),
            decodePixelWidth: 240);

        Assert.NotNull(preview);
        Assert.Equal(240, preview.PixelWidth);
        Assert.Equal(120, preview.PixelHeight);
        Assert.True(preview.IsFrozen);
        Assert.True(provider.LeaseDisposed);
    }

    [Fact]
    public async Task RefreshingTheLibraryReloadsAChangedThumbnailForTheSameStableIdentity()
    {
        var provider = new ThumbnailProvider(CreateJpeg(width: 32, height: 16));
        var registry = new WallpaperSourceProviderRegistry([provider]);
        var service = new WallpaperThumbnailPreviewService(registry);
        using var library = new WallpaperSourceLibraryViewModel(registry, service);

        await library.RefreshAsync();
        var firstItem = Assert.Single(library.Items);
        await firstItem.EnsureThumbnailAsync();
        Assert.Equal(
            120,
            Assert.IsAssignableFrom<BitmapSource>(firstItem.Thumbnail).PixelHeight);

        provider.Content = CreateJpeg(width: 48, height: 16);
        await library.RefreshAsync();
        var refreshedItem = Assert.Single(library.Items);
        await refreshedItem.EnsureThumbnailAsync();

        Assert.Equal(
            80,
            Assert.IsAssignableFrom<BitmapSource>(refreshedItem.Thumbnail).PixelHeight);
        Assert.Equal(2, provider.AcquisitionCount);
    }

    [Fact]
    public async Task AThumbnailLoadFromAnOlderGenerationCannotHideRefreshedContent()
    {
        var provider = new ThumbnailProvider(CreateJpeg(width: 32, height: 16));
        var registry = new WallpaperSourceProviderRegistry([provider]);
        var service = new WallpaperThumbnailPreviewService(registry);
        using var library = new WallpaperSourceLibraryViewModel(registry, service);
        await library.RefreshAsync();
        provider.BlockNextAcquisition();

        var oldGenerationLoad = service.LoadAsync(
            CreateReference("1004"),
            decodePixelWidth: 240);
        await provider.AcquisitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Content = CreateJpeg(width: 48, height: 16);

        await library.RefreshAsync();
        provider.ReleaseAcquisition();
        var oldGenerationPreview = await oldGenerationLoad;
        Assert.Equal(
            120,
            Assert.IsAssignableFrom<BitmapSource>(oldGenerationPreview).PixelHeight);

        var currentGenerationPreview = await service.LoadAsync(
            CreateReference("1004"),
            decodePixelWidth: 240);

        Assert.Equal(
            80,
            Assert.IsAssignableFrom<BitmapSource>(currentGenerationPreview).PixelHeight);
        Assert.Equal(2, provider.AcquisitionCount);
    }

    [Fact]
    public async Task AFirstLibraryRefreshInvalidatesAnUnknownIdentityLoadThatStartedEarlier()
    {
        var provider = new ThumbnailProvider(CreateJpeg(width: 32, height: 16));
        var registry = new WallpaperSourceProviderRegistry([provider]);
        var service = new WallpaperThumbnailPreviewService(registry);
        using var library = new WallpaperSourceLibraryViewModel(registry, service);
        provider.BlockNextAcquisition();

        var oldGenerationLoad = service.LoadAsync(
            CreateReference("9999"),
            decodePixelWidth: 240);
        await provider.AcquisitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Content = CreateJpeg(width: 48, height: 16);

        await library.RefreshAsync();
        provider.ReleaseAcquisition();
        var oldGenerationPreview = await oldGenerationLoad;
        Assert.Equal(
            120,
            Assert.IsAssignableFrom<BitmapSource>(oldGenerationPreview).PixelHeight);

        var currentGenerationPreview = await service.LoadAsync(
            CreateReference("9999"),
            decodePixelWidth: 240);

        Assert.Equal(
            80,
            Assert.IsAssignableFrom<BitmapSource>(currentGenerationPreview).PixelHeight);
        Assert.Equal(2, provider.AcquisitionCount);
    }

    [Fact]
    public async Task AFailedProviderRefreshKeepsItsStableThumbnailWithoutReacquiring()
    {
        var provider = new ThumbnailProvider(CreateJpeg(width: 32, height: 16));
        var registry = new WallpaperSourceProviderRegistry([provider]);
        var service = new WallpaperThumbnailPreviewService(registry);
        using var library = new WallpaperSourceLibraryViewModel(registry, service);
        await library.RefreshAsync();
        var firstItem = Assert.Single(library.Items);
        await firstItem.EnsureThumbnailAsync();
        Assert.Equal(1, provider.AcquisitionCount);

        provider.DiscoveryFailure = new WallpaperEngineUnavailableException(
            WallpaperEngineAvailabilityReason.NotInstalled);
        provider.ThumbnailFailure = new WallpaperEngineUnavailableException(
            WallpaperEngineAvailabilityReason.NotInstalled);
        await library.RefreshAsync();
        var staleItem = Assert.Single(library.Items);
        Assert.Equal(WallpaperSourceAvailability.Stale, staleItem.Availability);

        await staleItem.EnsureThumbnailAsync();

        Assert.Equal(
            120,
            Assert.IsAssignableFrom<BitmapSource>(staleItem.Thumbnail).PixelHeight);
        Assert.Equal(1, provider.AcquisitionCount);
    }

    private static WallpaperThumbnailPreviewService CreateService(
        ThumbnailProvider provider) =>
        new(new WallpaperSourceProviderRegistry([provider]));

    private static MediaReference CreateReference(string publishedFileId) =>
        new()
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = publishedFileId,
            LastKnownContentKind = WallpaperContentKind.Image,
        };

    private static byte[] PatchPngDimensions(byte[] png, uint width, uint height)
    {
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(
            png.AsSpan(29, 4),
            CalculateCrc32(png.AsSpan(12, 17)));
        return png;
    }

    private static byte[] CreateIndexedPng(int width, int height)
    {
        byte[]? encoded = null;
        StaTest.Run(
            () =>
            {
                var stride = checked((width + 7) / 8);
                var pixels = new byte[checked(stride * height)];
                var bitmap = BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    PixelFormats.Indexed1,
                    new BitmapPalette([Colors.Black, Colors.White]),
                    pixels,
                    stride);
                bitmap.Freeze();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                encoded = stream.ToArray();
            });
        return encoded ?? throw new InvalidOperationException("PNG encoding did not complete.");
    }

    private static byte[] CreateJpeg(int width, int height)
    {
        byte[]? encoded = null;
        StaTest.Run(
            () =>
            {
                const int bytesPerPixel = 3;
                var stride = checked(width * bytesPerPixel);
                var pixels = new byte[checked(stride * height)];
                var bitmap = BitmapSource.Create(
                    width,
                    height,
                    96,
                    96,
                    PixelFormats.Bgr24,
                    palette: null,
                    pixels,
                    stride);
                bitmap.Freeze();
                var encoder = new JpegBitmapEncoder
                {
                    QualityLevel = 85,
                };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                encoded = stream.ToArray();
            });
        return encoded ?? throw new InvalidOperationException("JPEG encoding did not complete.");
    }

    private static uint CalculateCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (crc >> 1) ^ 0xEDB88320u
                    : crc >> 1;
            }
        }

        return ~crc;
    }

    private sealed class ThumbnailProvider(byte[] content)
        : IWallpaperThumbnailSourceProvider
    {
        private TaskCompletionSource? _acquisitionRelease;

        public byte[] Content { get; set; } = content;

        public Exception? DiscoveryFailure { get; set; }

        public Exception? ThumbnailFailure { get; set; }

        public int AcquisitionCount { get; private set; }

        public bool LeaseDisposed { get; private set; }

        public TaskCompletionSource AcquisitionEntered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MediaSourceKind SourceKind =>
            MediaSourceKind.WallpaperEngineWorkshopProject;

        public ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DiscoveryFailure is not null)
            {
                return ValueTask.FromException<IReadOnlyList<WallpaperSourceDescriptor>>(
                    DiscoveryFailure);
            }

            return ValueTask.FromResult<IReadOnlyList<WallpaperSourceDescriptor>>(
                [
                    new WallpaperSourceDescriptor(
                        SourceKind,
                        "1004",
                        "Mutable thumbnail",
                        WallpaperContentKind.Image,
                        WallpaperDeliveryKind.DirectMedia,
                        WallpaperDeliveryCapabilities.None),
                ]);
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask<IWallpaperThumbnailLease?> AcquireThumbnailLeaseAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquisitionCount++;
            if (ThumbnailFailure is not null)
            {
                return await ValueTask
                    .FromException<IWallpaperThumbnailLease?>(ThumbnailFailure)
                    .ConfigureAwait(false);
            }

            var contentSnapshot = Content;
            var acquisitionRelease = _acquisitionRelease;
            AcquisitionEntered.TrySetResult();
            if (acquisitionRelease is not null)
            {
                await acquisitionRelease.Task.ConfigureAwait(false);
            }

            return new ThumbnailLease(
                reference.Snapshot(),
                contentSnapshot,
                () => LeaseDisposed = true);
        }

        public void BlockNextAcquisition()
        {
            AcquisitionEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _acquisitionRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseAcquisition()
        {
            _acquisitionRelease?.TrySetResult();
            _acquisitionRelease = null;
        }
    }

    private sealed class ThumbnailLease(
        MediaReference reference,
        byte[] content,
        Action onDispose) : IWallpaperThumbnailLease
    {
        private readonly MemoryStream _stream = new(content, writable: false);

        public MediaReference Reference { get; } = reference;

        public string ContentType => "image/png";

        public long ContentLength => _stream.Length;

        public Stream ContentStream => _stream;

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
