using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperSourceProviderTests
{
    [Fact]
    public async Task AcquireLeaseAsyncPinsValidatedFinalFileWithoutNetworkEndpoint()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = await CreatePngAsync(directoryPath, "private-wallpaper.png");
            var provider = new LocalFileWallpaperSourceProvider();

            var lease = await provider.AcquireLeaseAsync(CreateReference(mediaPath));
            try
            {
                Assert.Equal(Path.GetFullPath(mediaPath), lease.ResolvedPath);
                Assert.Equal(MediaKind.Image, lease.Metadata.Kind);
                Assert.Equal(MediaFormat.Png, lease.Metadata.Format);
                Assert.Equal(1, lease.Metadata.PixelWidth);
                Assert.Equal(1, lease.Metadata.PixelHeight);
                Assert.NotEqual(0UL, lease.FileIdentity.FileIndex);
                Assert.DoesNotContain(
                    lease.GetType().GetProperties(),
                    property =>
                        property.Name.Contains("Endpoint", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));

                Assert.Throws<IOException>(() =>
                {
                    using var writer = new FileStream(
                        mediaPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                });
            }
            finally
            {
                await lease.DisposeAsync();
            }

            await using var writable = new FileStream(
                mediaPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            Assert.True(writable.CanWrite);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task AcquireLeaseAsyncValidatesThroughTheSinglePinnedStream()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = await CreatePngAsync(directoryPath, "wallpaper.png");
            var inspector = new RecordingStreamInspector();
            var provider = new LocalFileWallpaperSourceProvider(inspector);

            await using var lease = await provider.AcquireLeaseAsync(CreateReference(mediaPath));

            Assert.Equal(1, inspector.CallCount);
            Assert.Equal(MediaKind.Image, lease.Reference.LastKnownKind);
            Assert.Equal(lease.ResolvedPath, inspector.MediaName);
            Assert.True(inspector.StreamWasReadable);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task ProviderDiscoveryResolutionAndAdvisoryValidationAreExplicitAndPathSafe()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = await CreatePngAsync(directoryPath, "wallpaper.png");
            var provider = new LocalFileWallpaperSourceProvider();
            var reference = CreateReference(
                Path.Combine(directoryPath, ".", "wallpaper.png"));

            var discovered = await provider.DiscoverAsync();
            var resolved = await provider.ResolveAsync(reference);
            var validated = await provider.ValidateAsync(reference);

            Assert.Empty(discovered);
            Assert.Equal(Path.GetFullPath(mediaPath), resolved.SourceIdentifier);
            Assert.Equal(MediaKind.Image, validated.Reference.LastKnownKind);
            Assert.Equal(MediaFormat.Png, validated.Metadata.Format);
            File.Delete(mediaPath);
            Assert.False(File.Exists(mediaPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task AcquireLeaseAsyncRejectsNetworkAndUnsupportedSources()
    {
        var provider = new LocalFileWallpaperSourceProvider();
        var networkReference = CreateReference(@"\\server\share\wallpaper.png");
        var workshopReference = networkReference with
        {
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "123456",
        };

        await Assert.ThrowsAsync<MediaValidationException>(
            () => provider.AcquireLeaseAsync(networkReference).AsTask());
        await Assert.ThrowsAsync<MediaSourceNotSupportedException>(
            () => provider.AcquireLeaseAsync(workshopReference).AsTask());
    }

    [Fact]
    public async Task AcquireLeaseAsyncResolvesLocalSymbolicLinkToPinnedTargetWhenAvailable()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var targetPath = await CreatePngAsync(directoryPath, "target.png");
            var linkPath = Path.Combine(directoryPath, "link.png");
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                // Windows may require Developer Mode or an elevated token for symbolic links.
                return;
            }

            var provider = new LocalFileWallpaperSourceProvider();
            await using var lease = await provider.AcquireLeaseAsync(CreateReference(linkPath));

            Assert.Equal(Path.GetFullPath(targetPath), lease.ResolvedPath);
            Assert.Throws<IOException>(() => File.Delete(targetPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task AcquireLeaseAsyncAcceptsExtendedLengthLocalDosPath()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = await CreatePngAsync(directoryPath, "wallpaper.png");
            var extendedPath = $@"\\?\{mediaPath}";
            var provider = new LocalFileWallpaperSourceProvider();

            await using var lease = await provider.AcquireLeaseAsync(CreateReference(extendedPath));

            Assert.Equal(Path.GetFullPath(mediaPath), lease.ResolvedPath);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public void CoreAssemblyNoLongerReferencesAspNetCoreOrExportsLoopbackMediaServer()
    {
        var assembly = typeof(LocalFileWallpaperSourceProvider).Assembly;

        Assert.DoesNotContain(
            assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            assembly.GetExportedTypes(),
            type => type.Name.Contains("LoopbackMediaServer", StringComparison.Ordinal));
    }

    [Fact]
    public void MediaReferenceRequiresUuidV7AndNormalizesWorkshopIdentifiers()
    {
        var invalid = new MediaReference
        {
            MediaId = Guid.NewGuid(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = @"C:\Wallpapers\wallpaper.png",
        };
        var workshop = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "000123456",
        };

        Assert.Contains(
            invalid.GetValidationErrors(),
            error => error.Contains("UUIDv7", StringComparison.Ordinal));
        Assert.Equal("123456", workshop.Snapshot().SourceIdentifier);
    }

    private static MediaReference CreateReference(string mediaPath) => new()
    {
        MediaId = Guid.CreateVersion7(),
        SourceKind = MediaSourceKind.LocalFile,
        SourceIdentifier = mediaPath,
        LastKnownKind = MediaKind.None,
    };

    private static async Task<string> CreatePngAsync(string directoryPath, string fileName)
    {
        var mediaPath = Path.Combine(directoryPath, fileName);
        await File.WriteAllBytesAsync(
            mediaPath,
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            ]);
        return mediaPath;
    }

    private static string CreateTemporaryDirectory()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "BackdropForCodex.Core.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

    private static void DeleteTemporaryDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private sealed class RecordingStreamInspector : IMediaStreamInspector
    {
        public int CallCount { get; private set; }

        public string? MediaName { get; private set; }

        public bool StreamWasReadable { get; private set; }

        public Task<MediaFileMetadata> InspectAsync(
            Stream mediaStream,
            string mediaName,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            MediaName = mediaName;
            StreamWasReadable = mediaStream.CanRead;
            return Task.FromResult(
                MediaFileInspector.CreateMetadata(MediaFormat.Png, mediaStream.Length));
        }
    }
}
