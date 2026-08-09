namespace BackdropForCodex.Core.Media;

/// <summary>
/// A caller-owned lifetime token for one validated Wallpaper Engine project. Project files are
/// never interpreted or injected by Backdrop for Codex.
/// </summary>
public interface IWallpaperEngineProjectLease : IAsyncDisposable
{
    WallpaperSourceResolution Resolution { get; }

    /// <summary>
    /// Fully qualified, provider-authorized project entry point consumed only by the owned
    /// renderer. Implementations must redact this path from ToString and diagnostics; it must
    /// never be copied into a Codex injection payload.
    /// </summary>
    string LaunchPath { get; }
}

public interface IWallpaperEngineProjectSourceProvider : IWallpaperSourceProvider
{
    ValueTask<IWallpaperEngineProjectLease> AcquireProjectLeaseAsync(
        MediaReference reference,
        CancellationToken cancellationToken = default);
}

public sealed record WallpaperEngineWindowOptions
{
    public const int MaximumDimension = 16384;

    public WallpaperEngineWindowOptions(int width, int height)
    {
        if (width is <= 0 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height is <= 0 or > MaximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}

public interface IWallpaperEngineWindowLease : IAsyncDisposable
{
    nint WindowHandle { get; }

    ValueTask SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Starts an owned Wallpaper Engine child window. Version one accepts only DynamicFrames; capture
/// and frame transport are intentionally outside this contract until the renderer spike lands.
/// </summary>
public interface IWallpaperEngineWindowRenderer
{
    WallpaperDeliveryCapabilities Capabilities { get; }

    ValueTask<IWallpaperEngineWindowLease> StartAsync(
        IWallpaperEngineProjectLease projectLease,
        WallpaperEngineWindowOptions options,
        CancellationToken cancellationToken = default);
}

public static class WallpaperEngineProjectLeaseContract
{
    public static void Validate(IWallpaperEngineProjectLease projectLease)
    {
        ArgumentNullException.ThrowIfNull(projectLease);
        ArgumentNullException.ThrowIfNull(projectLease.Resolution);
        if (projectLease.Resolution.Descriptor.DeliveryKind !=
                WallpaperDeliveryKind.WallpaperEngineWindow ||
            projectLease.Resolution.Descriptor.ContentKind is not
                (WallpaperContentKind.Scene or WallpaperContentKind.Web))
        {
            throw new WallpaperSourceCapabilityException(
                "A Wallpaper Engine project lease requires a scene or web window resolution.");
        }

        if (string.IsNullOrWhiteSpace(projectLease.LaunchPath) ||
            !Path.IsPathFullyQualified(projectLease.LaunchPath))
        {
            throw new WallpaperSourceCapabilityException(
                "A Wallpaper Engine project lease requires a fully qualified launch path.");
        }
    }
}

public static class WallpaperEngineWindowRendererContract
{
    public const WallpaperDeliveryCapabilities RequiredCapabilities =
        WallpaperDeliveryCapabilities.DynamicFrames;

    public static void Validate(IWallpaperEngineWindowRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if (renderer.Capabilities != RequiredCapabilities)
        {
            throw new WallpaperSourceCapabilityException(
                "The version-one Wallpaper Engine renderer must expose exactly dynamic frames.");
        }
    }
}
