namespace BackdropForCodex.Core.Media;

public enum WallpaperEngineProjectUnavailableReason
{
    NotFound = 0,
    AmbiguousIdentity,
    InvalidManifest,
    UnsafePath,
    EntryPointMissing,
    AmbiguousScenePackage,
    InvalidDirectMedia,
    InvalidThumbnail,
}

public sealed class WallpaperEngineProjectUnavailableException : InvalidOperationException
{
    public WallpaperEngineProjectUnavailableException(
        MediaSourceKind sourceKind,
        WallpaperEngineProjectUnavailableReason reason)
        : base(CreateMessage(reason))
    {
        Validate(sourceKind, reason);
        SourceKind = sourceKind;
        Reason = reason;
    }

    public WallpaperEngineProjectUnavailableException(
        MediaSourceKind sourceKind,
        WallpaperEngineProjectUnavailableReason reason,
        Exception innerException)
        : base(CreateMessage(reason))
    {
        ArgumentNullException.ThrowIfNull(innerException);
        Validate(sourceKind, reason);
        SourceKind = sourceKind;
        Reason = reason;
        DiagnosticExceptionType = innerException.GetType().Name;
        DiagnosticHResult = innerException.HResult;
    }

    public MediaSourceKind SourceKind { get; }

    public WallpaperEngineProjectUnavailableReason Reason { get; }

    /// <summary>
    /// The non-user-controlled CLR type name of the provider failure, retained without its message,
    /// stack, or path-bearing inner exception.
    /// </summary>
    public string? DiagnosticExceptionType { get; }

    /// <summary>
    /// The provider failure HRESULT, retained as a path-free diagnostic reason code.
    /// </summary>
    public int? DiagnosticHResult { get; }

    private static void Validate(
        MediaSourceKind sourceKind,
        WallpaperEngineProjectUnavailableReason reason)
    {
        if (sourceKind is not (
                MediaSourceKind.WallpaperEngineLocalProject or
                MediaSourceKind.WallpaperEngineWorkshopProject))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
    }

    private static string CreateMessage(WallpaperEngineProjectUnavailableReason reason) =>
        reason switch
        {
            WallpaperEngineProjectUnavailableReason.NotFound =>
                "The Wallpaper Engine project is no longer installed.",
            WallpaperEngineProjectUnavailableReason.AmbiguousIdentity =>
                "The Wallpaper Engine project identity is ambiguous across Steam libraries.",
            WallpaperEngineProjectUnavailableReason.InvalidManifest =>
                "The Wallpaper Engine project manifest is invalid.",
            WallpaperEngineProjectUnavailableReason.UnsafePath =>
                "The Wallpaper Engine project contains an unsafe local path.",
            WallpaperEngineProjectUnavailableReason.EntryPointMissing =>
                "The Wallpaper Engine project entry point is missing.",
            WallpaperEngineProjectUnavailableReason.AmbiguousScenePackage =>
                "The Wallpaper Engine scene package fallback is ambiguous.",
            WallpaperEngineProjectUnavailableReason.InvalidDirectMedia =>
                "The Wallpaper Engine direct-media entry is invalid.",
            WallpaperEngineProjectUnavailableReason.InvalidThumbnail =>
                "The Wallpaper Engine project thumbnail is invalid.",
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
}

/// <summary>
/// A caller-owned, pinned static preview. The content stream is readable and seekable, and must not
/// be disposed separately from the lease. No provider-derived path is exposed.
/// </summary>
public interface IWallpaperThumbnailLease : IAsyncDisposable
{
    MediaReference Reference { get; }

    string ContentType { get; }

    long ContentLength { get; }

    Stream ContentStream { get; }
}

public interface IWallpaperThumbnailSourceProvider : IWallpaperSourceProvider
{
    ValueTask<IWallpaperThumbnailLease?> AcquireThumbnailLeaseAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default);
}
