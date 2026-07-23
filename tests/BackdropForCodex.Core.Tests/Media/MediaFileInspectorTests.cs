using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class MediaFileInspectorTests
{
    [Fact]
    public void TryDetectFormatRecognizesEverySupportedMagicNumber()
    {
        foreach (var sample in Samples())
        {
            var detected = MediaFileInspector.TryDetectFormat(sample.Bytes, out var format);

            Assert.True(detected, sample.Extension);
            Assert.Equal(sample.Format, format);
        }
    }

    [Fact]
    public async Task InspectAsyncValidatesExtensionAndReturnsPathFreeMetadata()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var inspector = new MediaFileInspector();
            foreach (var sample in Samples())
            {
                var mediaPath = Path.Combine(directoryPath, $"wallpaper{sample.Extension}");
                await File.WriteAllBytesAsync(mediaPath, sample.Bytes);

                var metadata = await inspector.InspectAsync(mediaPath);

                Assert.Equal(sample.Format, metadata.Format);
                Assert.Equal(sample.Kind, metadata.Kind);
                Assert.Equal(sample.ContentType, metadata.ContentType);
                Assert.Equal(sample.Bytes.LongLength, metadata.ContentLength);
                Assert.DoesNotContain(
                    metadata.GetType().GetProperties(),
                    property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task InspectAsyncRejectsExtensionAndSignatureMismatchWithoutEchoingPath()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = Path.Combine(directoryPath, "private-wallpaper.png");
            await File.WriteAllBytesAsync(mediaPath, JpegBytes());
            var inspector = new MediaFileInspector();

            var exception = await Assert.ThrowsAsync<MediaValidationException>(
                () => inspector.InspectAsync(mediaPath));

            Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(mediaPath, exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task InspectAsyncRejectsUnsupportedAndEmptyFiles()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var unsupportedPath = Path.Combine(directoryPath, "wallpaper.gif");
            var emptyPath = Path.Combine(directoryPath, "wallpaper.png");
            await File.WriteAllBytesAsync(unsupportedPath, [0x47, 0x49, 0x46]);
            await File.WriteAllBytesAsync(emptyPath, []);
            var inspector = new MediaFileInspector();

            await Assert.ThrowsAsync<MediaValidationException>(() => inspector.InspectAsync(unsupportedPath));
            await Assert.ThrowsAsync<MediaValidationException>(() => inspector.InspectAsync(emptyPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task InspectAsyncRejectsMatroskaRenamedAsWebM()
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = Path.Combine(directoryPath, "wallpaper.webm");
            var matroskaHeader = new byte[]
            {
                0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x82, 0x88,
                0x6D, 0x61, 0x74, 0x72, 0x6F, 0x73, 0x6B, 0x61,
            };
            await File.WriteAllBytesAsync(mediaPath, matroskaHeader);
            var inspector = new MediaFileInspector();

            await Assert.ThrowsAsync<MediaValidationException>(() => inspector.InspectAsync(mediaPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public void TryDetectFormatRejectsUnknownData()
    {
        var detected = MediaFileInspector.TryDetectFormat("not media"u8, out _);

        Assert.False(detected);
    }

    private static IEnumerable<MediaSample> Samples()
    {
        yield return new MediaSample(
            ".png",
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            MediaFormat.Png,
            MediaKind.Image,
            "image/png");
        yield return new MediaSample(
            ".jpeg",
            JpegBytes(),
            MediaFormat.Jpeg,
            MediaKind.Image,
            "image/jpeg");
        yield return new MediaSample(
            ".webp",
            [
                0x52, 0x49, 0x46, 0x46, 0x08, 0x00, 0x00, 0x00,
                0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20,
            ],
            MediaFormat.WebP,
            MediaKind.Image,
            "image/webp");
        yield return new MediaSample(
            ".mp4",
            [
                0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70,
                0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x00, 0x00,
                0x69, 0x73, 0x6F, 0x6D,
            ],
            MediaFormat.Mp4,
            MediaKind.Video,
            "video/mp4");
        yield return new MediaSample(
            ".webm",
            [
                0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x82, 0x84,
                0x77, 0x65, 0x62, 0x6D, 0x42, 0x87, 0x81, 0x04,
            ],
            MediaFormat.WebM,
            MediaKind.Video,
            "video/webm");
    }

    private static byte[] JpegBytes() => [0xFF, 0xD8, 0xFF, 0xE0];

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

    private sealed record MediaSample(
        string Extension,
        byte[] Bytes,
        MediaFormat Format,
        MediaKind Kind,
        string ContentType);
}
