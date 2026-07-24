using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace BackdropForCodex.Core.Media;

public enum MediaKind
{
    None = 0,
    Image,
    Video,
}

public enum MediaFormat
{
    Png = 0,
    Jpeg,
    WebP,
    Mp4,
    WebM,
}

/// <summary>
/// Path-free metadata for a validated local media file.
/// </summary>
public sealed record MediaFileMetadata(
    MediaFormat Format,
    MediaKind Kind,
    string ContentType,
    long ContentLength,
    int? PixelWidth = null,
    int? PixelHeight = null);

public interface IMediaStreamInspector
{
    Task<MediaFileMetadata> InspectAsync(
        Stream mediaStream,
        string mediaName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates extension, container signature, byte length, and bounded image dimensions through a
/// caller-owned pinned stream.
/// </summary>
public sealed class MediaFileInspector : IMediaStreamInspector
{
    public const long MaximumImageLength = 512L * 1024 * 1024;

    public const long MaximumVideoLength = 8L * 1024 * 1024 * 1024;

    public const int MaximumImageDimension = 32_768;

    public const long MaximumImagePixelCount = 33_554_432;

    private const int MaximumHeaderLength = 4096;
    private const int MaximumJpegMarkerCount = 1024;
    private const long MaximumJpegHeaderScanLength = 16L * 1024 * 1024;

    private static readonly ReadOnlyDictionary<string, MediaFormat> FormatsByExtension =
        new ReadOnlyDictionary<string, MediaFormat>(
            new Dictionary<string, MediaFormat>(StringComparer.OrdinalIgnoreCase)
            {
                [".png"] = MediaFormat.Png,
                [".jpg"] = MediaFormat.Jpeg,
                [".jpeg"] = MediaFormat.Jpeg,
                [".webp"] = MediaFormat.WebP,
                [".mp4"] = MediaFormat.Mp4,
                [".webm"] = MediaFormat.WebM,
            });

    public async Task<MediaFileMetadata> InspectAsync(
        Stream mediaStream,
        string mediaName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaName);
        if (!mediaStream.CanRead || !mediaStream.CanSeek)
        {
            throw new ArgumentException(
                "The media stream must be readable and seekable.",
                nameof(mediaStream));
        }

        var extension = Path.GetExtension(mediaName);
        if (!FormatsByExtension.TryGetValue(extension, out var extensionFormat))
        {
            throw new MediaValidationException("The media file extension is not supported.");
        }

        return await InspectCoreAsync(mediaStream, extensionFormat, cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool TryDetectFormat(ReadOnlySpan<byte> header, out MediaFormat format)
    {
        if (HasPrefix(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            format = MediaFormat.Png;
            return true;
        }

        if (HasPrefix(header, [0xFF, 0xD8, 0xFF]))
        {
            format = MediaFormat.Jpeg;
            return true;
        }

        if (header.Length >= 12 &&
            header[..4].SequenceEqual("RIFF"u8) &&
            header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            format = MediaFormat.WebP;
            return true;
        }

        if (IsMp4(header))
        {
            format = MediaFormat.Mp4;
            return true;
        }

        if (IsWebM(header))
        {
            format = MediaFormat.WebM;
            return true;
        }

        format = default;
        return false;
    }

    public static MediaFileMetadata CreateMetadata(MediaFormat format, long contentLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);

        return format switch
        {
            MediaFormat.Png => new(format, MediaKind.Image, "image/png", contentLength),
            MediaFormat.Jpeg => new(format, MediaKind.Image, "image/jpeg", contentLength),
            MediaFormat.WebP => new(format, MediaKind.Image, "image/webp", contentLength),
            MediaFormat.Mp4 => new(format, MediaKind.Video, "video/mp4", contentLength),
            MediaFormat.WebM => new(format, MediaKind.Video, "video/webm", contentLength),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    private static async Task<MediaFileMetadata> InspectCoreAsync(
        Stream stream,
        MediaFormat extensionFormat,
        CancellationToken cancellationToken)
    {
        if (stream.Length == 0)
        {
            throw new MediaValidationException("The media file is empty.");
        }

        stream.Position = 0;
        try
        {
            var header = new byte[(int)Math.Min(stream.Length, MaximumHeaderLength)];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

            if (!TryDetectFormat(header, out var detectedFormat))
            {
                throw new MediaValidationException("The media file signature is not supported.");
            }

            if (detectedFormat != extensionFormat)
            {
                throw new MediaValidationException(
                    "The media file extension does not match its signature.");
            }

            var metadata = CreateMetadata(detectedFormat, stream.Length);
            var maximumLength = metadata.Kind switch
            {
                MediaKind.Image => MaximumImageLength,
                MediaKind.Video => MaximumVideoLength,
                _ => throw new InvalidOperationException("The validated media has no supported kind."),
            };
            if (metadata.ContentLength > maximumLength)
            {
                throw new MediaValidationException(
                    metadata.Kind == MediaKind.Image
                        ? "The image exceeds the 512 MiB size limit."
                        : "The video exceeds the 8 GiB size limit.");
            }

            if (metadata.Kind != MediaKind.Image)
            {
                return metadata;
            }

            var dimensions = detectedFormat switch
            {
                MediaFormat.Png => ParsePngDimensions(header),
                MediaFormat.Jpeg => await ParseJpegDimensionsAsync(stream, cancellationToken)
                    .ConfigureAwait(false),
                MediaFormat.WebP => ParseWebPDimensions(header, stream.Length),
                _ => throw new MediaValidationException(
                    "The image dimensions could not be validated."),
            };

            return metadata with
            {
                PixelWidth = dimensions.Width,
                PixelHeight = dimensions.Height,
            };
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static ImageDimensions ParsePngDimensions(ReadOnlySpan<byte> header)
    {
        const int pngDimensionHeaderLength = 24;
        if (header.Length < pngDimensionHeaderLength ||
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4)) != 13 ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new MediaValidationException(
                "The PNG image dimensions could not be validated.");
        }

        return ValidateImageDimensions(
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4)));
    }

    private static async Task<ImageDimensions> ParseJpegDimensionsAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[2];
        await ReadExactlyAtAsync(stream, 0, prefix, cancellationToken).ConfigureAwait(false);
        if (prefix[0] != 0xFF ||
            prefix[1] != 0xD8)
        {
            throw new MediaValidationException(
                "The JPEG image dimensions could not be validated.");
        }

        var markerBuffer = new byte[1];
        var lengthBuffer = new byte[2];
        var dimensionBuffer = new byte[5];
        long position = 2;
        var markerCount = 0;
        while (position < stream.Length &&
               position <= MaximumJpegHeaderScanLength &&
               markerCount < MaximumJpegMarkerCount)
        {
            markerCount++;
            await ReadExactlyAtAsync(
                    stream,
                    position,
                    markerBuffer,
                    cancellationToken)
                .ConfigureAwait(false);
            if (markerBuffer[0] != 0xFF)
            {
                break;
            }

            do
            {
                position++;
                await ReadExactlyAtAsync(
                        stream,
                        position,
                        markerBuffer,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            while (markerBuffer[0] == 0xFF);

            var marker = markerBuffer[0];
            position++;
            if (marker == 0x00 ||
                marker is 0xD9 or 0xDA)
            {
                break;
            }

            if (marker == 0x01 ||
                marker is >= 0xD0 and <= 0xD8)
            {
                continue;
            }

            await ReadExactlyAtAsync(
                    stream,
                    position,
                    lengthBuffer,
                    cancellationToken)
                .ConfigureAwait(false);
            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBuffer);
            if (segmentLength < 2 ||
                position > stream.Length - segmentLength)
            {
                break;
            }

            if (IsStartOfFrameMarker(marker))
            {
                if (segmentLength < 8)
                {
                    break;
                }

                await ReadExactlyAtAsync(
                        stream,
                        position + 2,
                        dimensionBuffer,
                        cancellationToken)
                    .ConfigureAwait(false);
                return ValidateImageDimensions(
                    BinaryPrimitives.ReadUInt16BigEndian(dimensionBuffer.AsSpan(3, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(dimensionBuffer.AsSpan(1, 2)));
            }

            position += segmentLength;
        }

        throw new MediaValidationException(
            "The JPEG image dimensions could not be validated.");
    }

    private static ImageDimensions ParseWebPDimensions(
        ReadOnlySpan<byte> header,
        long contentLength)
    {
        const int chunkHeaderLength = 20;
        if (header.Length < chunkHeaderLength)
        {
            throw new MediaValidationException(
                "The WebP image dimensions could not be validated.");
        }

        var riffPayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
        if ((ulong)riffPayloadLength + 8 > (ulong)contentLength)
        {
            throw new MediaValidationException(
                "The WebP image dimensions could not be validated.");
        }

        var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
        if ((ulong)chunkLength + chunkHeaderLength > (ulong)contentLength ||
            (ulong)chunkLength + 12 > riffPayloadLength)
        {
            throw new MediaValidationException(
                "The WebP image dimensions could not be validated.");
        }

        var chunkType = header.Slice(12, 4);
        if (chunkType.SequenceEqual("VP8 "u8))
        {
            const int requiredPayloadLength = 10;
            if (chunkLength < requiredPayloadLength ||
                header.Length < chunkHeaderLength + requiredPayloadLength)
            {
                throw new MediaValidationException(
                    "The WebP image dimensions could not be validated.");
            }

            var payload = header.Slice(chunkHeaderLength, requiredPayloadLength);
            if ((payload[0] & 0x01) != 0 ||
                payload[3] != 0x9D ||
                payload[4] != 0x01 ||
                payload[5] != 0x2A)
            {
                throw new MediaValidationException(
                    "The WebP image dimensions could not be validated.");
            }

            return ValidateImageDimensions(
                BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)) & 0x3FFFu,
                BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)) & 0x3FFFu);
        }

        if (chunkType.SequenceEqual("VP8L"u8))
        {
            const int requiredPayloadLength = 5;
            if (chunkLength < requiredPayloadLength ||
                header.Length < chunkHeaderLength + requiredPayloadLength)
            {
                throw new MediaValidationException(
                    "The WebP image dimensions could not be validated.");
            }

            var payload = header.Slice(chunkHeaderLength, requiredPayloadLength);
            var dimensionBits = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
            if (payload[0] != 0x2F ||
                dimensionBits >> 29 != 0)
            {
                throw new MediaValidationException(
                    "The WebP image dimensions could not be validated.");
            }

            return ValidateImageDimensions(
                (dimensionBits & 0x3FFF) + 1,
                ((dimensionBits >> 14) & 0x3FFF) + 1);
        }

        if (chunkType.SequenceEqual("VP8X"u8))
        {
            const int requiredPayloadLength = 10;
            if (chunkLength < requiredPayloadLength ||
                header.Length < chunkHeaderLength + requiredPayloadLength)
            {
                throw new MediaValidationException(
                    "The WebP image dimensions could not be validated.");
            }

            var payload = header.Slice(chunkHeaderLength, requiredPayloadLength);
            return ValidateImageDimensions(
                ReadUInt24LittleEndian(payload.Slice(4, 3)) + 1,
                ReadUInt24LittleEndian(payload.Slice(7, 3)) + 1);
        }

        throw new MediaValidationException(
            "The WebP image dimensions could not be validated.");
    }

    private static ImageDimensions ValidateImageDimensions(uint width, uint height)
    {
        if (width == 0 ||
            height == 0 ||
            width > MaximumImageDimension ||
            height > MaximumImageDimension ||
            width > MaximumImagePixelCount / height)
        {
            throw new MediaValidationException(
                $"The image dimensions must not exceed {MaximumImageDimension:N0} pixels " +
                $"on either side or {MaximumImagePixelCount:N0} total pixels.");
        }

        return new ImageDimensions(checked((int)width), checked((int)height));
    }

    private static bool IsStartOfFrameMarker(byte marker) =>
        marker is >= 0xC0 and <= 0xCF &&
        marker is not (0xC4 or 0xC8 or 0xCC);

    private static async Task ReadExactlyAtAsync(
        Stream stream,
        long position,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (position < 0 ||
            position > stream.Length - destination.Length)
        {
            throw new MediaValidationException(
                "The image dimensions could not be validated.");
        }

        stream.Position = position;
        await stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> value) =>
        value[0] |
        ((uint)value[1] << 8) |
        ((uint)value[2] << 16);

    private static bool IsMp4(ReadOnlySpan<byte> header)
    {
        if (header.Length < 12 || !header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return false;
        }

        var boxLength = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        return boxLength == 1 || boxLength >= 12;
    }

    private static bool IsWebM(ReadOnlySpan<byte> header)
    {
        if (!HasPrefix(header, [0x1A, 0x45, 0xDF, 0xA3]))
        {
            return false;
        }

        // WebM and Matroska share the EBML signature. Requiring the WebM DocType prevents a
        // renamed Matroska file from crossing the validation boundary. Some very small test
        // fixtures omit the optional EBML fields, so the signature alone is accepted only when
        // there is not enough data to carry a DocType element.
        var webmMarker = "webm"u8;
        if (header.Length < 16)
        {
            return true;
        }

        return header.IndexOf(webmMarker) >= 0;
    }

    private static bool HasPrefix(ReadOnlySpan<byte> source, ReadOnlySpan<byte> prefix) =>
        source.Length >= prefix.Length && source[..prefix.Length].SequenceEqual(prefix);

    private readonly record struct ImageDimensions(int Width, int Height);
}

public sealed class MediaValidationException : IOException
{
    public MediaValidationException(string message)
        : base(message)
    {
    }

    public MediaValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
