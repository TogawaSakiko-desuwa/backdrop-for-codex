using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Runtime;

public sealed class ActivationAttemptContextTests
{
    [Fact]
    public void PendingProjectLeaseMustAuthorizeTheExactCanonicalSourceIdentifier()
    {
        var requested = Resolution("123456789");
        var leased = Resolution("987654321", requested.CanonicalReference.MediaId);
        var context = new ActivationAttemptContext(
            RuntimeActivationRequest.Create(1, SettingsV3.CreateDefault()),
            WallpaperRuntimeSurface.Official(),
            previousActiveSnapshot: null,
            CancellationToken.None);

        Assert.Throws<WallpaperSourceCapabilityException>(() =>
            context.AcceptPendingProjectLease(requested, new FakeProjectLease(leased)));

        Assert.Null(context.PendingProjectLease);
        Assert.Null(context.DynamicResolution);
    }

    [Fact]
    public async Task FailedPendingProjectCleanupRetainsOwnershipForRetry()
    {
        var resolution = Resolution("123456789");
        var lease = new FailOnceProjectLease(resolution);
        var context = new ActivationAttemptContext(
            RuntimeActivationRequest.Create(1, SettingsV3.CreateDefault()),
            WallpaperRuntimeSurface.Official(),
            previousActiveSnapshot: null,
            CancellationToken.None);
        context.AcceptPendingProjectLease(resolution, lease);

        var firstFailure = await context.TryDisposePendingLeaseAsync();

        Assert.IsType<InvalidOperationException>(firstFailure);
        Assert.Same(lease, context.PendingProjectLease);
        Assert.Same(resolution, context.DynamicResolution);

        Assert.Null(await context.TryDisposePendingLeaseAsync());
        Assert.Null(context.PendingProjectLease);
        Assert.Null(context.DynamicResolution);
        Assert.Equal(2, lease.DisposeAttempts);
    }

    [Fact]
    public async Task TakingFailedProjectCleanupTransfersSoleOwnershipToTheRetryOwner()
    {
        var resolution = Resolution("123456789");
        var lease = new FailOnceProjectLease(resolution);
        var context = new ActivationAttemptContext(
            RuntimeActivationRequest.Create(1, SettingsV3.CreateDefault()),
            WallpaperRuntimeSurface.Official(),
            previousActiveSnapshot: null,
            CancellationToken.None);
        context.AcceptPendingProjectLease(resolution, lease);
        Assert.IsType<InvalidOperationException>(
            await context.TryDisposePendingLeaseAsync());

        var cleanupOwner = Assert.IsType<PendingActivationResourceOwner>(
            context.TakePendingCleanupOwner());

        Assert.Null(context.PendingProjectLease);
        Assert.Null(context.DynamicResolution);
        Assert.Null(await context.TryDisposePendingLeaseAsync());
        Assert.Equal(1, lease.DisposeAttempts);

        await cleanupOwner.DisposeAsync();
        await cleanupOwner.DisposeAsync();

        Assert.Equal(2, lease.DisposeAttempts);
    }

    private static WallpaperSourceResolution Resolution(
        string identifier,
        Guid? mediaId = null)
    {
        var reference = new MediaReference
        {
            MediaId = mediaId ?? Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = identifier,
            LastKnownContentKind = WallpaperContentKind.Scene,
            LastKnownDisplayName = "Fixture scene",
        };
        return new WallpaperSourceResolution(
            reference,
            new WallpaperSourceDescriptor(
                reference.SourceKind,
                reference.SourceIdentifier,
                "Fixture scene",
                WallpaperContentKind.Scene,
                WallpaperDeliveryKind.WallpaperEngineWindow,
                WallpaperDeliveryCapabilities.DynamicFrames),
            directMediaMetadata: null);
    }

    private sealed class FakeProjectLease(WallpaperSourceResolution resolution)
        : IWallpaperEngineProjectLease
    {
        public WallpaperSourceResolution Resolution { get; } = resolution;

        public string LaunchPath { get; } = @"C:\Fixtures\scene.pkg";

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailOnceProjectLease(WallpaperSourceResolution resolution)
        : IWallpaperEngineProjectLease
    {
        public WallpaperSourceResolution Resolution { get; } = resolution;

        public string LaunchPath { get; } = @"C:\Fixtures\scene.pkg";

        internal int DisposeAttempts { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            return DisposeAttempts == 1
                ? ValueTask.FromException(
                    new InvalidOperationException("Synthetic project cleanup failure."))
                : ValueTask.CompletedTask;
        }
    }
}
