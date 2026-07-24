using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class SafeMediaPreviewServiceTests
{
    [Fact]
    public void Acquire_DelegatesToLocalProviderAndDisposesItsPinnedLease()
    {
        var provider = new RecordingSourceProvider();
        var service = new SafeMediaPreviewService(provider);

        using (var lease = service.Acquire(@"C:\wallpapers\sky.png"))
        {
            Assert.NotNull(provider.AcquiredReference);
            Assert.Equal(7, provider.AcquiredReference.MediaId.Version);
            Assert.Equal(MediaSourceKind.LocalFile, provider.AcquiredReference.SourceKind);
            Assert.Equal(
                Path.GetFullPath(@"C:\wallpapers\sky.png"),
                provider.AcquiredReference.SourceIdentifier);
            Assert.Equal(MediaKind.Image, lease.Metadata.Kind);
            Assert.False(provider.LeaseDisposed);
        }

        Assert.True(provider.LeaseDisposed);
    }

    [Fact]
    public void IsAvailable_MapsReferenceValidationFailureToUnavailable()
    {
        var provider = new RecordingSourceProvider
        {
            Failure = new MediaReferenceValidationException("Invalid reference."),
        };
        var service = new SafeMediaPreviewService(provider);

        var available = service.IsAvailable(@"C:\wallpapers\sky.png");

        Assert.False(available);
    }

    [Fact]
    public void Constructor_RejectsNonLocalProvider()
    {
        var provider = new RecordingSourceProvider
        {
            SourceKindOverride = MediaSourceKind.WallpaperEngineLocalProject,
        };

        Assert.Throws<ArgumentException>(() => new SafeMediaPreviewService(provider));
    }

    [Theory]
    [InlineData(4000, 2000, 1600, 1600, 800)]
    [InlineData(800, 1600, 112, 56, 112)]
    [InlineData(80, 40, 112, 80, 40)]
    public void CalculateDecodePixelSizeBoundsBothDimensionsWithoutUpscaling(
        int sourceWidth,
        int sourceHeight,
        int maximumSideLength,
        int expectedWidth,
        int expectedHeight)
    {
        var metadata = new MediaFileMetadata(
            MediaFormat.Png,
            MediaKind.Image,
            "image/png",
            128,
            sourceWidth,
            sourceHeight);

        var actual = SafeMediaPreviewService.CalculateDecodePixelSize(
            metadata,
            maximumSideLength);

        Assert.Equal((expectedWidth, expectedHeight), actual);
    }

    [Fact]
    public void LoadBitmapRejectsMissingDimensionsBeforeOpeningTheResolvedPath()
    {
        var provider = new RecordingSourceProvider
        {
            Metadata = new MediaFileMetadata(
                MediaFormat.Png,
                MediaKind.Image,
                "image/png",
                128),
        };
        var service = new SafeMediaPreviewService(provider);
        using var lease = service.Acquire(@"C:\does-not-exist\wallpaper.png");

        Assert.Throws<MediaValidationException>(() => lease.LoadBitmap(112));
    }

    private sealed class RecordingSourceProvider : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKindOverride { get; init; } = MediaSourceKind.LocalFile;

        public Exception? Failure { get; init; }

        public MediaFileMetadata Metadata { get; init; } =
            new(MediaFormat.Png, MediaKind.Image, "image/png", 128, 4000, 2000);

        public MediaReference? AcquiredReference { get; private set; }

        public bool LeaseDisposed { get; private set; }

        public MediaSourceKind SourceKind => SourceKindOverride;

        public ValueTask<IMediaLease> AcquireLeaseAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            AcquiredReference = reference.Snapshot();
            return ValueTask.FromResult<IMediaLease>(
                new RecordingLease(
                    AcquiredReference,
                    Metadata,
                    () => LeaseDisposed = true));
        }
    }

    private sealed class RecordingLease(
        MediaReference reference,
        MediaFileMetadata metadata,
        Action onDispose) : IMediaLease
    {
        public MediaReference Reference { get; } = reference;

        public string ResolvedPath => Reference.SourceIdentifier;

        public LocalFileIdentity FileIdentity { get; } = new(123, 456);

        public MediaFileMetadata Metadata { get; } = metadata;

        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
