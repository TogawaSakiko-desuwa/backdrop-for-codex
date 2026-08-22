namespace BackdropForCodex.Core.Media;

public enum WallpaperContentKind
{
    Unknown = 0,
    Image,
    Video,
    Scene,
    Web,
    Application,
}

public enum WallpaperDeliveryKind
{
    Unsupported = 0,
    DirectMedia,
    WallpaperEngineWindow,
}

[Flags]
public enum WallpaperDeliveryCapabilities
{
    None = 0,
    DynamicFrames = 1 << 0,
    Audio = 1 << 1,
    PointerInput = 1 << 2,
}

/// <summary>
/// An immutable, provider-independent description of one discoverable wallpaper source.
/// Construction rejects source and delivery combinations that the runtime cannot safely route.
/// </summary>
public sealed record WallpaperSourceDescriptor
{
    public const int MaximumDisplayNameLength = 256;

    private const WallpaperDeliveryCapabilities AllCapabilities =
        WallpaperDeliveryCapabilities.DynamicFrames |
        WallpaperDeliveryCapabilities.Audio |
        WallpaperDeliveryCapabilities.PointerInput;

    public WallpaperSourceDescriptor(
        MediaSourceKind sourceKind,
        string sourceIdentifier,
        string displayName,
        WallpaperContentKind contentKind,
        WallpaperDeliveryKind deliveryKind,
        WallpaperDeliveryCapabilities requiredCapabilities)
    {
        if (!Enum.IsDefined(sourceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        ArgumentNullException.ThrowIfNull(sourceIdentifier);
        if (string.IsNullOrWhiteSpace(sourceIdentifier))
        {
            throw new ArgumentException(
                "The wallpaper source identifier is required.",
                nameof(sourceIdentifier));
        }

        if (sourceIdentifier.Length > MediaReference.MaximumSourceIdentifierLength)
        {
            throw new ArgumentException(
                $"The wallpaper source identifier cannot exceed " +
                $"{MediaReference.MaximumSourceIdentifierLength} characters.",
                nameof(sourceIdentifier));
        }

        ArgumentNullException.ThrowIfNull(displayName);
        if (displayName.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentException(
                $"The wallpaper source display name cannot exceed " +
                $"{MaximumDisplayNameLength} characters.",
                nameof(displayName));
        }

        displayName = WallpaperDisplayNameSanitizer.Sanitize(displayName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException(
                "The wallpaper source display name is required.",
                nameof(displayName));
        }

        if (!Enum.IsDefined(contentKind))
        {
            throw new ArgumentOutOfRangeException(nameof(contentKind));
        }

        if (!Enum.IsDefined(deliveryKind))
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryKind));
        }

        if ((requiredCapabilities & ~AllCapabilities) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredCapabilities));
        }

        ValidateDeliveryContract(contentKind, deliveryKind, requiredCapabilities);

        SourceKind = sourceKind;
        SourceIdentifier = WallpaperSourceIdentifier.Canonicalize(
            sourceKind,
            sourceIdentifier);
        DisplayName = displayName;
        ContentKind = contentKind;
        DeliveryKind = deliveryKind;
        RequiredCapabilities = requiredCapabilities;
    }

    public MediaSourceKind SourceKind { get; }

    public string SourceIdentifier { get; }

    public string DisplayName { get; }

    public WallpaperContentKind ContentKind { get; }

    public WallpaperDeliveryKind DeliveryKind { get; }

    public WallpaperDeliveryCapabilities RequiredCapabilities { get; }

    private static void ValidateDeliveryContract(
        WallpaperContentKind contentKind,
        WallpaperDeliveryKind deliveryKind,
        WallpaperDeliveryCapabilities requiredCapabilities)
    {
        var expectedDeliveryKind = contentKind switch
        {
            WallpaperContentKind.Image or WallpaperContentKind.Video =>
                WallpaperDeliveryKind.DirectMedia,
            WallpaperContentKind.Scene or WallpaperContentKind.Web =>
                WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperContentKind.Unknown or WallpaperContentKind.Application =>
                WallpaperDeliveryKind.Unsupported,
            _ => throw new ArgumentOutOfRangeException(nameof(contentKind)),
        };
        if (deliveryKind != expectedDeliveryKind)
        {
            throw new WallpaperSourceCapabilityException(
                $"{contentKind} wallpaper content requires {expectedDeliveryKind} delivery.");
        }

        var expectedCapabilities = deliveryKind switch
        {
            WallpaperDeliveryKind.Unsupported or WallpaperDeliveryKind.DirectMedia =>
                WallpaperDeliveryCapabilities.None,
            WallpaperDeliveryKind.WallpaperEngineWindow =>
                WallpaperDeliveryCapabilities.DynamicFrames,
            _ => throw new ArgumentOutOfRangeException(nameof(deliveryKind)),
        };
        if (requiredCapabilities != expectedCapabilities)
        {
            throw new WallpaperSourceCapabilityException(
                $"{deliveryKind} delivery requires exactly {expectedCapabilities} capabilities.");
        }
    }
}

/// <summary>
/// Binds a source description to a canonical durable reference and, for direct media only,
/// validated file metadata. The constructor rechecks provider output at the trust boundary.
/// </summary>
public sealed record WallpaperSourceResolution
{
    public WallpaperSourceResolution(
        MediaReference canonicalReference,
        WallpaperSourceDescriptor descriptor,
        MediaFileMetadata? directMediaMetadata)
    {
        ArgumentNullException.ThrowIfNull(canonicalReference);
        ArgumentNullException.ThrowIfNull(descriptor);

        var snapshot = canonicalReference with
        {
            LastKnownKind = descriptor.ContentKind switch
            {
                WallpaperContentKind.Image => MediaKind.Image,
                WallpaperContentKind.Video => MediaKind.Video,
                _ => MediaKind.None,
            },
            LastKnownContentKind = descriptor.ContentKind,
            LastKnownDisplayName = descriptor.DisplayName,
        };
        snapshot = snapshot.Snapshot();
        if (snapshot.SourceKind != descriptor.SourceKind ||
            !WallpaperSourceIdentifier.AreEqual(
                descriptor.SourceKind,
                snapshot.SourceIdentifier,
                descriptor.SourceIdentifier))
        {
            throw new ArgumentException(
                "The canonical reference does not identify the described wallpaper source.",
                nameof(canonicalReference));
        }

        if (descriptor.DeliveryKind == WallpaperDeliveryKind.DirectMedia)
        {
            if (directMediaMetadata is null)
            {
                throw new WallpaperSourceCapabilityException(
                    "Direct media delivery requires validated media metadata.");
            }

            ValidateDirectMediaMetadata(descriptor.ContentKind, directMediaMetadata);
        }
        else if (directMediaMetadata is not null)
        {
            throw new WallpaperSourceCapabilityException(
                "Only direct media delivery can carry media file metadata.");
        }

        CanonicalReference = snapshot;
        Descriptor = descriptor;
        DirectMediaMetadata = directMediaMetadata;
    }

    public MediaReference CanonicalReference { get; }

    public WallpaperSourceDescriptor Descriptor { get; }

    public MediaFileMetadata? DirectMediaMetadata { get; }

    private static void ValidateDirectMediaMetadata(
        WallpaperContentKind contentKind,
        MediaFileMetadata metadata)
    {
        MediaFileMetadata expectedMetadata;
        try
        {
            expectedMetadata = MediaFileInspector.CreateMetadata(
                metadata.Format,
                metadata.ContentLength);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new WallpaperSourceCapabilityException(
                "Direct media metadata contains an unsupported format.",
                exception);
        }

        var expectedContentKind = expectedMetadata.Kind switch
        {
            MediaKind.Image => WallpaperContentKind.Image,
            MediaKind.Video => WallpaperContentKind.Video,
            _ => WallpaperContentKind.Unknown,
        };
        if (contentKind != expectedContentKind ||
            metadata.Kind != expectedMetadata.Kind ||
            !string.Equals(
                metadata.ContentType,
                expectedMetadata.ContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WallpaperSourceCapabilityException(
                "Direct media metadata does not match the described content and format.");
        }
    }
}

public sealed class WallpaperSourceCapabilityException : InvalidOperationException
{
    public WallpaperSourceCapabilityException(string message)
        : base(message)
    {
    }

    public WallpaperSourceCapabilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WallpaperRendererUnavailableException : InvalidOperationException
{
    public WallpaperRendererUnavailableException(WallpaperSourceDescriptor descriptor)
        : base("The required Wallpaper Engine window renderer is not available.")
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public WallpaperSourceDescriptor Descriptor { get; }
}

public sealed class WallpaperContentNotSupportedException : NotSupportedException
{
    public WallpaperContentNotSupportedException(WallpaperSourceDescriptor descriptor)
        : base($"{descriptor?.ContentKind} wallpaper content is not supported.")
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
    }

    public WallpaperSourceDescriptor Descriptor { get; }
}

internal static class WallpaperSourceIdentifier
{
    public static string Canonicalize(MediaSourceKind sourceKind, string sourceIdentifier) =>
        new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = sourceKind,
            SourceIdentifier = sourceIdentifier,
            LastKnownKind = MediaKind.None,
        }.Snapshot().SourceIdentifier;

    public static bool AreEqual(
        MediaSourceKind sourceKind,
        string left,
        string right) =>
        string.Equals(
            left,
            right,
            sourceKind is MediaSourceKind.LocalFile or
                MediaSourceKind.WallpaperEngineLocalProject
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
