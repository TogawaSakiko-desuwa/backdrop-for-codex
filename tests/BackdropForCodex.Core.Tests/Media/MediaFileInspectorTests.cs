using System.Buffers.Binary;
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
    public async Task StreamInspectionValidatesExtensionAndReturnsPathFreeMetadata()
    {
        var inspector = new MediaFileInspector();
        foreach (var sample in Samples())
        {
            await using var stream = new MemoryStream(sample.Bytes, writable: false);

            var metadata = await inspector.InspectAsync(
                stream,
                $"wallpaper{sample.Extension}");

            Assert.Equal(sample.Format, metadata.Format);
            Assert.Equal(sample.Kind, metadata.Kind);
            Assert.Equal(sample.ContentType, metadata.ContentType);
            Assert.Equal(sample.Bytes.LongLength, metadata.ContentLength);
            Assert.Equal(sample.PixelWidth, metadata.PixelWidth);
            Assert.Equal(sample.PixelHeight, metadata.PixelHeight);
            Assert.DoesNotContain(
                metadata.GetType().GetProperties(),
                property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task StreamInspectionRejectsExtensionAndSignatureMismatchWithoutEchoingName()
    {
        const string privateName = "private-wallpaper.png";
        await using var stream = new MemoryStream(JpegBytes(800, 600), writable: false);
        var inspector = new MediaFileInspector();

        var exception = await Assert.ThrowsAsync<MediaValidationException>(
            () => inspector.InspectAsync(stream, privateName));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(privateName, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamInspectionRejectsUnsupportedAndEmptyMedia()
    {
        var inspector = new MediaFileInspector();
        await using var unsupported = new MemoryStream([0x47, 0x49, 0x46], writable: false);
        await using var empty = new MemoryStream();

        await Assert.ThrowsAsync<MediaValidationException>(
            () => inspector.InspectAsync(unsupported, "wallpaper.gif"));
        await Assert.ThrowsAsync<MediaValidationException>(
            () => inspector.InspectAsync(empty, "wallpaper.png"));
    }

    [Fact]
    public async Task StreamInspectionRejectsMatroskaRenamedAsWebM()
    {
        byte[] matroskaHeader =
        [
            0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x82, 0x88,
            0x6D, 0x61, 0x74, 0x72, 0x6F, 0x73, 0x6B, 0x61,
        ];
        await using var stream = new MemoryStream(matroskaHeader, writable: false);
        var inspector = new MediaFileInspector();

        await Assert.ThrowsAsync<MediaValidationException>(
            () => inspector.InspectAsync(stream, "wallpaper.webm"));
    }

    [Fact]
    public async Task StreamInspectionRejectsMissingOrOutOfBoundsImageDimensions()
    {
        byte[][] invalidImages =
        [
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            PngBytes(0, 1),
            PngBytes((uint)MediaFileInspector.MaximumImageDimension + 1, 1),
            PngBytes(8192, 4097),
            [0xFF, 0xD8, 0xFF, 0xD9],
            CreateWebPVp8(0, 1),
        ];
        var inspector = new MediaFileInspector();

        foreach (var invalidImage in invalidImages)
        {
            await using var stream = new MemoryStream(invalidImage, writable: false);
            await Assert.ThrowsAsync<MediaValidationException>(
                () => inspector.InspectAsync(
                    stream,
                    GetMediaName(invalidImage)));
        }
    }

    [Fact]
    public async Task StreamInspectionAcceptsExactImageDimensionAndPixelBoundaries()
    {
        var bytes = PngBytes(
            MediaFileInspector.MaximumImageDimension,
            checked((uint)(
                MediaFileInspector.MaximumImagePixelCount /
                MediaFileInspector.MaximumImageDimension)));
        await using var stream = new MemoryStream(bytes, writable: false);
        var inspector = new MediaFileInspector();

        var metadata = await inspector.InspectAsync(stream, "wallpaper.png");

        Assert.Equal(MediaFileInspector.MaximumImageDimension, metadata.PixelWidth);
        Assert.Equal(1024, metadata.PixelHeight);
    }

    [Fact]
    public void PublicInspectorSurfaceDoesNotExposeAPathOpeningOverload()
    {
        Assert.DoesNotContain(
            typeof(MediaFileInspector).Assembly.GetExportedTypes(),
            type => type.Name == "IMediaFileInspector");
        Assert.DoesNotContain(
            typeof(MediaFileInspector).GetMethods(),
            method =>
                method.Name == nameof(MediaFileInspector.InspectAsync) &&
                method.GetParameters() is [{ ParameterType: var parameterType }, ..] &&
                parameterType == typeof(string));
    }

    [Fact]
    public void TryDetectFormatRejectsUnknownData()
    {
        var detected = MediaFileInspector.TryDetectFormat("not media"u8, out _);

        Assert.False(detected);
    }

    [Theory]
    [InlineData(MediaFileInspector.MaximumImageLength, true)]
    [InlineData(MediaFileInspector.MaximumImageLength + 1, false)]
    public async Task StreamInspectionEnforcesImageSizeLimit(long contentLength, bool accepted)
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = Path.Combine(directoryPath, "wallpaper.png");
            await using var stream = new FileStream(
                mediaPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            await stream.WriteAsync(PngBytes(1920, 1080));
            stream.SetLength(contentLength);
            stream.Position = 0;
            var inspector = new MediaFileInspector();

            if (accepted)
            {
                var metadata = await inspector.InspectAsync(stream, "wallpaper.png");
                Assert.Equal(contentLength, metadata.ContentLength);
                Assert.Equal(1920, metadata.PixelWidth);
                Assert.Equal(1080, metadata.PixelHeight);
            }
            else
            {
                var exception = await Assert.ThrowsAsync<MediaValidationException>(
                    () => inspector.InspectAsync(stream, "wallpaper.png"));
                Assert.Contains("512 MiB", exception.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Theory]
    [InlineData(MediaFileInspector.MaximumVideoLength, true)]
    [InlineData(MediaFileInspector.MaximumVideoLength + 1, false)]
    public async Task StreamInspectionEnforcesVideoSizeLimit(long contentLength, bool accepted)
    {
        var directoryPath = CreateTemporaryDirectory();
        try
        {
            var mediaPath = Path.Combine(directoryPath, "wallpaper.mp4");
            await using var stream = new FileStream(
                mediaPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            await stream.WriteAsync(Mp4Bytes());
            stream.SetLength(contentLength);
            stream.Position = 0;
            var inspector = new MediaFileInspector();

            if (accepted)
            {
                var metadata = await inspector.InspectAsync(stream, "wallpaper.mp4");
                Assert.Equal(contentLength, metadata.ContentLength);
                Assert.Null(metadata.PixelWidth);
                Assert.Null(metadata.PixelHeight);
            }
            else
            {
                var exception = await Assert.ThrowsAsync<MediaValidationException>(
                    () => inspector.InspectAsync(stream, "wallpaper.mp4"));
                Assert.Contains("8 GiB", exception.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(directoryPath);
        }
    }

    [Fact]
    public async Task StreamInspectionLeavesPinnedStreamOpenAndRewound()
    {
        await using var stream = new MemoryStream(PngBytes(640, 480), writable: false);
        var inspector = new MediaFileInspector();

        var metadata = await inspector.InspectAsync(stream, "wallpaper.png");

        Assert.Equal(MediaKind.Image, metadata.Kind);
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
    }

    private static IEnumerable<MediaSample> Samples()
    {
        yield return new MediaSample(
            ".png",
            PngBytes(640, 480),
            MediaFormat.Png,
            MediaKind.Image,
            "image/png",
            640,
            480);
        yield return new MediaSample(
            ".jpeg",
            JpegBytes(800, 600),
            MediaFormat.Jpeg,
            MediaKind.Image,
            "image/jpeg",
            800,
            600);
        yield return new MediaSample(
            ".webp",
            CreateWebPVp8(320, 240),
            MediaFormat.WebP,
            MediaKind.Image,
            "image/webp",
            320,
            240);
        yield return new MediaSample(
            ".webp",
            CreateWebPVp8L(1024, 512),
            MediaFormat.WebP,
            MediaKind.Image,
            "image/webp",
            1024,
            512);
        yield return new MediaSample(
            ".webp",
            CreateWebPVp8X(1920, 1080),
            MediaFormat.WebP,
            MediaKind.Image,
            "image/webp",
            1920,
            1080);
        yield return new MediaSample(
            ".mp4",
            Mp4Bytes(),
            MediaFormat.Mp4,
            MediaKind.Video,
            "video/mp4",
            null,
            null);
        yield return new MediaSample(
            ".webm",
            [
                0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x82, 0x84,
                0x77, 0x65, 0x62, 0x6D, 0x42, 0x87, 0x81, 0x04,
            ],
            MediaFormat.WebM,
            MediaKind.Video,
            "video/webm",
            null,
            null);
    }

    private static byte[] PngBytes(uint width, uint height)
    {
        var bytes = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static string GetMediaName(ReadOnlySpan<byte> bytes) =>
        MediaFileInspector.TryDetectFormat(bytes, out var format)
            ? format switch
            {
                MediaFormat.Jpeg => "wallpaper.jpg",
                MediaFormat.WebP => "wallpaper.webp",
                _ => "wallpaper.png",
            }
            : "wallpaper.png";

    private static byte[] JpegBytes(ushort width, ushort height) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x06, 0x11, 0x22, 0x33, 0x44,
        0xFF, 0xC0, 0x00, 0x0B, 0x08,
        (byte)(height >> 8), (byte)height,
        (byte)(width >> 8), (byte)width,
        0x01, 0x01, 0x11, 0x00,
    ];

    private static byte[] CreateWebPVp8(ushort width, ushort height)
    {
        var payload = new byte[10];
        payload[3] = 0x9D;
        payload[4] = 0x01;
        payload[5] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), width);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), height);
        return CreateWebP("VP8 "u8, payload);
    }

    private static byte[] CreateWebPVp8L(ushort width, ushort height)
    {
        var payload = new byte[5];
        payload[0] = 0x2F;
        var dimensionBits =
            ((uint)width - 1) |
            (((uint)height - 1) << 14);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), dimensionBits);
        return CreateWebP("VP8L"u8, payload);
    }

    private static byte[] CreateWebPVp8X(uint width, uint height)
    {
        var payload = new byte[10];
        WriteUInt24LittleEndian(payload.AsSpan(4, 3), width - 1);
        WriteUInt24LittleEndian(payload.AsSpan(7, 3), height - 1);
        return CreateWebP("VP8X"u8, payload);
    }

    private static byte[] CreateWebP(
        ReadOnlySpan<byte> chunkType,
        ReadOnlySpan<byte> payload)
    {
        var paddedPayloadLength = payload.Length + (payload.Length & 1);
        var bytes = new byte[20 + paddedPayloadLength];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4, 4),
            checked((uint)bytes.Length - 8));
        "WEBP"u8.CopyTo(bytes.AsSpan(8, 4));
        chunkType.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(16, 4),
            checked((uint)payload.Length));
        payload.CopyTo(bytes.AsSpan(20));
        return bytes;
    }

    private static byte[] Mp4Bytes() =>
    [
        0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70,
        0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x00, 0x00,
        0x69, 0x73, 0x6F, 0x6D,
    ];

    private static void WriteUInt24LittleEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
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

    private sealed record MediaSample(
        string Extension,
        byte[] Bytes,
        MediaFormat Format,
        MediaKind Kind,
        string ContentType,
        int? PixelWidth,
        int? PixelHeight);
}
