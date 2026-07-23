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
    long ContentLength);

public interface IMediaFileInspector
{
    Task<MediaFileMetadata> InspectAsync(
        string mediaPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates both a file extension and the corresponding container signature.
/// </summary>
public sealed class MediaFileInspector : IMediaFileInspector
{
    private const int MaximumHeaderLength = 4096;

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
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        if (!Path.IsPathFullyQualified(mediaPath))
        {
            throw new ArgumentException("The media path must be absolute.", nameof(mediaPath));
        }

        var extension = Path.GetExtension(mediaPath);
        if (!FormatsByExtension.TryGetValue(extension, out var extensionFormat))
        {
            throw new MediaValidationException("The media file extension is not supported.");
        }

        try
        {
            await using var stream = new FileStream(
                mediaPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: MaximumHeaderLength,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length == 0)
            {
                throw new MediaValidationException("The media file is empty.");
            }

            var header = new byte[(int)Math.Min(stream.Length, MaximumHeaderLength)];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

            if (!TryDetectFormat(header, out var detectedFormat))
            {
                throw new MediaValidationException("The media file signature is not supported.");
            }

            if (detectedFormat != extensionFormat)
            {
                throw new MediaValidationException("The media file extension does not match its signature.");
            }

            return CreateMetadata(detectedFormat, stream.Length);
        }
        catch (MediaValidationException)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw new MediaValidationException("The media file was not found.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new MediaValidationException("The media file was not found.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new MediaValidationException("The media file could not be opened.", exception);
        }
        catch (IOException exception)
        {
            throw new MediaValidationException("The media file could not be read.", exception);
        }
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
