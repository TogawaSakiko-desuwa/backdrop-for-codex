using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.Media;

public sealed class WallpaperEngineRenderingContractsTests
{
    [Fact]
    public async Task RendererContractPassesTheOwnedProjectLeaseToTheWindowImplementation()
    {
        var lease = new RecordingProjectLease(
            CreateResolution(WallpaperContentKind.Scene),
            @"C:\WallpaperEngine\projects\scene\project.json");
        var renderer = new RecordingRenderer();
        var options = new WallpaperEngineWindowOptions(1920, 1080);

        WallpaperEngineProjectLeaseContract.Validate(lease);
        WallpaperEngineWindowRendererContract.Validate(renderer);
        await using var window = await renderer.StartAsync(lease, options);

        Assert.Same(lease, renderer.ProjectLease);
        Assert.Equal(lease.LaunchPath, renderer.ProjectLease?.LaunchPath);
        Assert.Same(options, renderer.Options);
        Assert.Null(window.CaptureTarget);
    }

    [Theory]
    [InlineData(WallpaperContentKind.Scene, @"C:\WallpaperEngine\scene\project.json")]
    [InlineData(WallpaperContentKind.Web, @"C:\WallpaperEngine\web\index.html")]
    public void ProjectLeaseContractAcceptsOnlyWindowContentWithQualifiedRedactedPath(
        WallpaperContentKind contentKind,
        string launchPath)
    {
        var lease = new RecordingProjectLease(CreateResolution(contentKind), launchPath);

        WallpaperEngineProjectLeaseContract.Validate(lease);

        Assert.DoesNotContain(launchPath, lease.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectAndRendererContractsRejectCapabilityDisguises()
    {
        var directLease = new RecordingProjectLease(
            CreateDirectResolution(),
            @"C:\WallpaperEngine\wallpaper.mp4");
        var relativeLease = new RecordingProjectLease(
            CreateResolution(WallpaperContentKind.Web),
            @"relative\index.html");
        var renderer = new RecordingRenderer
        {
            CapabilitiesOverride =
                WallpaperDeliveryCapabilities.DynamicFrames |
                WallpaperDeliveryCapabilities.Audio,
        };

        Assert.Throws<WallpaperSourceCapabilityException>(
            () => WallpaperEngineProjectLeaseContract.Validate(directLease));
        Assert.Throws<WallpaperSourceCapabilityException>(
            () => WallpaperEngineProjectLeaseContract.Validate(relativeLease));
        Assert.Throws<WallpaperSourceCapabilityException>(
            () => WallpaperEngineWindowRendererContract.Validate(renderer));
    }

    [Fact]
    public async Task CaptureTargetFailsClosedWhenRevokedDuringIdentityRevalidation()
    {
        var authority = new BlockingCaptureAuthority(new nint(42));
        var target = new WallpaperEngineWindowCaptureTarget(authority);
        var revalidation = target.RevalidateAsync(default).AsTask();
        await authority.Entered;

        target.Revoke();
        authority.Release();

        var exception = await Assert.ThrowsAsync<WallpaperEnginePlatformUnavailableException>(
            () => revalidation);
        Assert.Equal(
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            exception.Reason);
        Assert.DoesNotContain("42", target.ToString(), StringComparison.Ordinal);
    }

    private static WallpaperSourceResolution CreateResolution(
        WallpaperContentKind contentKind)
    {
        var reference = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineLocalProject,
            SourceIdentifier = @"C:\WallpaperEngine\projects\wallpaper\project.json",
            LastKnownKind = MediaKind.None,
        };
        var descriptor = new WallpaperSourceDescriptor(
            reference.SourceKind,
            reference.SourceIdentifier,
            "Wallpaper Engine project",
            contentKind,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.DynamicFrames);
        return new WallpaperSourceResolution(reference, descriptor, directMediaMetadata: null);
    }

    private static WallpaperSourceResolution CreateDirectResolution()
    {
        var reference = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = @"C:\Wallpapers\wallpaper.mp4",
            LastKnownKind = MediaKind.Video,
        };
        var descriptor = new WallpaperSourceDescriptor(
            reference.SourceKind,
            reference.SourceIdentifier,
            "Video",
            WallpaperContentKind.Video,
            WallpaperDeliveryKind.DirectMedia,
            WallpaperDeliveryCapabilities.None);
        return new WallpaperSourceResolution(
            reference,
            descriptor,
            MediaFileInspector.CreateMetadata(MediaFormat.Mp4, contentLength: 1));
    }

    private sealed class RecordingProjectLease(
        WallpaperSourceResolution resolution,
        string launchPath) : IWallpaperEngineProjectLease
    {
        public WallpaperSourceResolution Resolution { get; } = resolution;

        public string LaunchPath { get; } = launchPath;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override string ToString() =>
            $"{nameof(RecordingProjectLease)} {{ LaunchPath = <redacted> }}";
    }

    private sealed class RecordingRenderer : IWallpaperEngineWindowRenderer
    {
        public WallpaperDeliveryCapabilities CapabilitiesOverride { get; init; } =
            WallpaperDeliveryCapabilities.DynamicFrames;

        public WallpaperDeliveryCapabilities Capabilities => CapabilitiesOverride;

        public IWallpaperEngineProjectLease? ProjectLease { get; private set; }

        public WallpaperEngineWindowOptions? Options { get; private set; }

        public ValueTask<IWallpaperEngineWindowLease> StartAsync(
            IWallpaperEngineProjectLease projectLease,
            WallpaperEngineWindowOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectLease = projectLease;
            Options = options;
            return ValueTask.FromResult<IWallpaperEngineWindowLease>(
                new RecordingWindowLease());
        }
    }

    private sealed class RecordingWindowLease : IWallpaperEngineWindowLease
    {
        public WallpaperEngineWindowCaptureTarget? CaptureTarget => null;

        public ValueTask SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingCaptureAuthority(nint windowHandle)
        : IWallpaperEngineWindowCaptureAuthority
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        public async ValueTask<nint> RevalidateAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return windowHandle;
        }
    }
}
