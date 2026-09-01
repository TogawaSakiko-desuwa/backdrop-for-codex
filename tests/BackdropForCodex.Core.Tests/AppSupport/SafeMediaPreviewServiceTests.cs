using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class SafeMediaPreviewServiceTests
{
    [Fact]
    public void ApplicationCompositionSharesOneRegistryWithPreview()
    {
        Assert.Same(
            AppWallpaperSources.Registry,
            AppWallpaperSources.Preview.SourceRegistry);
    }

    [Fact]
    public void ApplicationCompositionRegistersLocalAndWallpaperEngineNamespaces()
    {
        Assert.Equal(
            [
                MediaSourceKind.LocalFile,
                MediaSourceKind.WallpaperEngineLocalProject,
                MediaSourceKind.WallpaperEngineWorkshopProject,
            ],
            AppWallpaperSources.Registry.SourceKinds);
    }

    [Fact]
    public void Acquire_DelegatesToLocalProviderAndDisposesItsPinnedLease()
    {
        var provider = new RecordingSourceProvider();
        var service = CreateService(provider);

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
    public void Acquire_RegistersMonitoringOnlyWhileTheValidatedLeaseIsHeld()
    {
        var provider = new RecordingSourceProvider
        {
            ResolvedPathOverride = @"C:\resolved-wallpapers\sky.png",
        };
        (string SourceIdentifier, string ValidatedPath)? registration = null;
        var service = new SafeMediaPreviewService(
            new WallpaperSourceProviderRegistry([provider]),
            (sourceIdentifier, validatedPath) =>
            {
                Assert.False(provider.LeaseDisposed);
                registration = (sourceIdentifier, validatedPath);
            });

        using (service.Acquire(@"C:\wallpapers\sky.png"))
        {
            Assert.True(registration.HasValue);
            var tracked = registration.Value;
            Assert.Equal(Path.GetFullPath(@"C:\wallpapers\sky.png"), tracked.SourceIdentifier);
            Assert.Equal(
                Path.GetFullPath(@"C:\resolved-wallpapers\sky.png"),
                tracked.ValidatedPath);
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
        var registrationCount = 0;
        var service = new SafeMediaPreviewService(
            new WallpaperSourceProviderRegistry([provider]),
            (_, _) => registrationCount++);

        var available = service.IsAvailable(@"C:\wallpapers\sky.png");

        Assert.False(available);
        Assert.Equal(0, registrationCount);
    }

    [Fact]
    public void IsAvailable_MapsMissingWallpaperEngineProjectToUnavailable()
    {
        var provider = new RecordingSourceProvider
        {
            SourceKindOverride = MediaSourceKind.WallpaperEngineWorkshopProject,
            Failure = new WallpaperEngineProjectUnavailableException(
                MediaSourceKind.WallpaperEngineWorkshopProject,
                WallpaperEngineProjectUnavailableReason.NotFound),
        };
        var service = CreateService(provider);
        var reference = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "123456",
            LastKnownKind = MediaKind.Video,
        };

        Assert.False(service.IsAvailable(reference));
    }

    [Fact]
    public void IsAvailable_ReturnsFalseWhenLocalProviderIsNotRegistered()
    {
        var provider = new RecordingSourceProvider
        {
            SourceKindOverride = MediaSourceKind.WallpaperEngineLocalProject,
        };
        var service = CreateService(provider);

        Assert.False(service.IsAvailable(@"C:\wallpapers\sky.png"));
    }

    [Fact]
    public void Acquire_MediaReferenceRoutesWorkshopVideoThroughRegisteredProvider()
    {
        var provider = new RecordingSourceProvider
        {
            SourceKindOverride = MediaSourceKind.WallpaperEngineWorkshopProject,
            Metadata = new MediaFileMetadata(
                MediaFormat.Mp4,
                MediaKind.Video,
                "video/mp4",
                128),
        };
        var service = CreateService(provider);
        var reference = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "123456",
            LastKnownKind = MediaKind.Video,
        };

        using var lease = service.Acquire(reference);

        Assert.NotNull(provider.AcquiredReference);
        Assert.Equal(reference.MediaId, provider.AcquiredReference.MediaId);
        Assert.Equal(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            provider.AcquiredReference.SourceKind);
        Assert.Equal("123456", provider.AcquiredReference.SourceIdentifier);
        Assert.Equal(MediaKind.Video, lease.Metadata.Kind);
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
        var service = CreateService(provider);
        using var lease = service.Acquire(@"C:\does-not-exist\wallpaper.png");

        Assert.Throws<MediaValidationException>(() => lease.LoadBitmap(112));
    }

    private static SafeMediaPreviewService CreateService(
        IWallpaperSourceProvider provider) =>
        new(new WallpaperSourceProviderRegistry([provider]));

    private sealed class RecordingSourceProvider : IDirectMediaSourceProvider
    {
        public MediaSourceKind SourceKindOverride { get; init; } = MediaSourceKind.LocalFile;

        public Exception? Failure { get; init; }

        public string? ResolvedPathOverride { get; init; }

        public MediaFileMetadata Metadata { get; init; } =
            new(MediaFormat.Png, MediaKind.Image, "image/png", 128, 4000, 2000);

        public MediaReference? AcquiredReference { get; private set; }

        public bool LeaseDisposed { get; private set; }

        public MediaSourceKind SourceKind => SourceKindOverride;

        public ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<WallpaperSourceDescriptor>>([]);
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            var snapshot = reference.Snapshot();
            if (SourceKind == MediaSourceKind.LocalFile &&
                ResolvedPathOverride is { } resolvedPath)
            {
                snapshot = snapshot with
                {
                    SourceIdentifier = Path.GetFullPath(resolvedPath),
                };
            }
            var descriptor = new WallpaperSourceDescriptor(
                SourceKind,
                snapshot.SourceIdentifier,
                "Preview media",
                Metadata.Kind == MediaKind.Video
                    ? WallpaperContentKind.Video
                    : WallpaperContentKind.Image,
                WallpaperDeliveryKind.DirectMedia,
                WallpaperDeliveryCapabilities.None);
            return ValueTask.FromResult(
                new WallpaperSourceResolution(snapshot, descriptor, Metadata));
        }

        public ValueTask<IDirectMediaLease> AcquireDirectMediaLeaseAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquiredReference = reference.Snapshot();
            return ValueTask.FromResult<IDirectMediaLease>(
                new RecordingLease(
                    AcquiredReference,
                    Metadata,
                    ResolvedPathOverride,
                    () => LeaseDisposed = true));
        }
    }

    private sealed class RecordingLease(
        MediaReference reference,
        MediaFileMetadata metadata,
        string? resolvedPath,
        Action onDispose) : IDirectMediaLease
    {
        public MediaReference Reference { get; } = reference;

        public string ResolvedPath { get; } = resolvedPath is null
            ? reference.SourceIdentifier
            : Path.GetFullPath(resolvedPath);

        public LocalFileIdentity FileIdentity { get; } = new(123, 456);

        public MediaFileMetadata Metadata { get; } = metadata;

        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
