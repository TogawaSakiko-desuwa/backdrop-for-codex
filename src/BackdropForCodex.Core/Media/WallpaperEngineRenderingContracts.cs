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

/// <summary>
/// Opaque authority for one previously verified Wallpaper Engine pop-out. Consumers cannot read
/// or manufacture its HWND/process identity; the capture boundary must revalidate that identity
/// every time a new capture session is created.
/// </summary>
public sealed class WallpaperEngineWindowCaptureTarget
{
    private readonly IWallpaperEngineWindowCaptureAuthority _authority;
    private int _revoked;

    internal WallpaperEngineWindowCaptureTarget(
        WallpaperEngineVerifiedWindow expectedWindow,
        IWallpaperEngineOwnedWindowVerifier verifier)
        : this(new OwnedWindowCaptureAuthority(expectedWindow, verifier))
    {
    }

    internal WallpaperEngineWindowCaptureTarget(
        IWallpaperEngineWindowCaptureAuthority authority)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    internal async ValueTask<nint> RevalidateAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfRevoked();
        var windowHandle = await _authority
            .RevalidateAsync(cancellationToken)
            .ConfigureAwait(false);
        ThrowIfRevoked();
        return windowHandle;
    }

    internal void Revoke() => Interlocked.Exchange(ref _revoked, 1);

    public override string ToString() =>
        $"{nameof(WallpaperEngineWindowCaptureTarget)} {{ Identity = <redacted> }}";

    private void ThrowIfRevoked()
    {
        if (Volatile.Read(ref _revoked) != 0)
        {
            throw new WallpaperEnginePlatformUnavailableException(
                WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven);
        }
    }

    private sealed class OwnedWindowCaptureAuthority
        : IWallpaperEngineWindowCaptureAuthority
    {
        private readonly WallpaperEngineVerifiedWindow _expectedWindow;
        private readonly IWallpaperEngineOwnedWindowVerifier _verifier;

        internal OwnedWindowCaptureAuthority(
            WallpaperEngineVerifiedWindow expectedWindow,
            IWallpaperEngineOwnedWindowVerifier verifier)
        {
            _expectedWindow = expectedWindow ??
                throw new ArgumentNullException(nameof(expectedWindow));
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        }

        public async ValueTask<nint> RevalidateAsync(CancellationToken cancellationToken)
        {
            await _verifier
                .RevalidateOwnedWindowAsync(_expectedWindow, cancellationToken)
                .ConfigureAwait(false);
            return _expectedWindow.WindowHandle;
        }
    }
}

internal interface IWallpaperEngineWindowCaptureAuthority
{
    ValueTask<nint> RevalidateAsync(CancellationToken cancellationToken);
}

public interface IWallpaperEngineWindowLease : IAsyncDisposable
{
    /// <summary>
    /// Current opaque capture authority. Cleanup revokes it before closing the pop-out and clears
    /// it once closure is proven; a failed cleanup may therefore retain only a revoked token.
    /// A raw HWND is never sufficient authorization for Windows Graphics Capture.
    /// </summary>
    WallpaperEngineWindowCaptureTarget? CaptureTarget { get; }

    ValueTask SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Starts an owned Wallpaper Engine child window. The lease exposes only a revocable capture
/// authority; raw window and process identity stay inside the renderer boundary.
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
