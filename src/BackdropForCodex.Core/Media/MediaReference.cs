using System.Collections.ObjectModel;
using System.Globalization;

namespace BackdropForCodex.Core.Media;

public enum MediaSourceKind
{
    LocalFile = 0,
    WallpaperEngineLocalProject = 1,
    WallpaperEngineWorkshopProject = 2,
}

/// <summary>
/// Durable identity of a wallpaper media source. Validation is deliberately metadata-only and
/// never opens the referenced file or project.
/// </summary>
public sealed record MediaReference
{
    public const int MaximumSourceIdentifierLength = 32767;

    public Guid MediaId { get; init; }

    public MediaSourceKind SourceKind { get; init; }

    public string SourceIdentifier { get; init; } = string.Empty;

    public MediaKind LastKnownKind { get; init; } = MediaKind.None;

    public void Validate()
    {
        var errors = GetValidationErrors();
        if (errors.Count != 0)
        {
            throw new MediaReferenceValidationException(errors);
        }
    }

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (MediaId == Guid.Empty)
        {
            errors.Add("MediaId must not be empty.");
        }
        else if (MediaId.Version != 7)
        {
            errors.Add("MediaId must be a UUIDv7 value.");
        }

        if (!Enum.IsDefined(SourceKind))
        {
            errors.Add("The media source kind is not supported.");
        }

        if (!Enum.IsDefined(LastKnownKind))
        {
            errors.Add("The last known media kind is not supported.");
        }

        if (string.IsNullOrWhiteSpace(SourceIdentifier))
        {
            errors.Add("The media source identifier is required.");
        }
        else if (SourceIdentifier.Length > MaximumSourceIdentifierLength)
        {
            errors.Add(
                $"The media source identifier cannot exceed {MaximumSourceIdentifierLength} characters.");
        }
        else if (Enum.IsDefined(SourceKind))
        {
            try
            {
                _ = NormalizeIdentifier(SourceKind, SourceIdentifier);
            }
            catch (MediaReferenceValidationException exception)
            {
                errors.Add(exception.Message);
            }
        }

        return new ReadOnlyCollection<string>(errors);
    }

    public MediaReference Snapshot()
    {
        Validate();
        return this with
        {
            SourceIdentifier = NormalizeIdentifier(SourceKind, SourceIdentifier),
        };
    }

    private static string NormalizeIdentifier(MediaSourceKind sourceKind, string identifier) =>
        sourceKind switch
        {
            MediaSourceKind.LocalFile or MediaSourceKind.WallpaperEngineLocalProject =>
                NormalizeAbsolutePath(identifier),
            MediaSourceKind.WallpaperEngineWorkshopProject =>
                NormalizeWorkshopIdentifier(identifier),
            _ => throw new MediaReferenceValidationException("The media source kind is not supported."),
        };

    private static string NormalizeAbsolutePath(string identifier)
    {
        if (!Path.IsPathFullyQualified(identifier))
        {
            throw new MediaReferenceValidationException(
                "A local media source identifier must be an absolute path.");
        }

        try
        {
            return Path.GetFullPath(identifier);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new MediaReferenceValidationException(
                "The local media source identifier is not a valid absolute path.",
                exception);
        }
    }

    private static string NormalizeWorkshopIdentifier(string identifier)
    {
        if (!ulong.TryParse(
                identifier,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var publishedFileId) ||
            publishedFileId == 0)
        {
            throw new MediaReferenceValidationException(
                "A Workshop media source identifier must be a positive decimal PublishedFileId.");
        }

        return publishedFileId.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed class MediaReferenceValidationException : Exception
{
    public MediaReferenceValidationException(IReadOnlyList<string> errors)
        : base(string.Join(" ", errors ?? throw new ArgumentNullException(nameof(errors))))
    {
        Errors = new ReadOnlyCollection<string>(errors.ToArray());
    }

    public MediaReferenceValidationException(string message)
        : base(message)
    {
        Errors = [message];
    }

    public MediaReferenceValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [message];
    }

    public IReadOnlyList<string> Errors { get; }
}
