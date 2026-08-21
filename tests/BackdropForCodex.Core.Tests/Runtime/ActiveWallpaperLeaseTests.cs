using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using Xunit;

namespace BackdropForCodex.Core.Tests.Runtime;

public sealed class ActiveWallpaperLeaseTests
{
    [Fact]
    public async Task DirectAdapterRetainsTheValidatedMediaLeaseUntilReleased()
    {
        var events = new List<string>();
        var media = new PlaybackPoolTests.FakeLease("direct-adapter", events);
        var lease = new DirectMediaActiveWallpaperLease(generation: 9, media);

        Assert.Same(media, lease.MediaLease);
        Assert.Equal(ActiveWallpaperDeliveryKind.DirectMedia, lease.DeliveryKind);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(["dispose:direct-adapter"], events);
    }

    [Fact]
    public async Task DirectAdapterRetainsTheValidatedLeaseForACleanupRetry()
    {
        var media = new FailOnceDirectMediaLease();
        var lease = new DirectMediaActiveWallpaperLease(generation: 10, media);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await lease.DisposeAsync());
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(2, media.DisposeAttempts);
    }

    [Fact]
    public async Task GenericPoolPublishesEitherDirectOrDynamicLeaseWithoutStaleRelease()
    {
        var events = new List<string>();
        var direct = new FakeActiveLease(11, ActiveWallpaperDeliveryKind.DirectMedia, "direct", events);
        var dynamic = new FakeActiveLease(12, ActiveWallpaperDeliveryKind.DynamicStream, "dynamic", events);
        var directOwner = PlaybackOwnershipToken.Create();
        var dynamicOwner = PlaybackOwnershipToken.Create();
        await using var pool = new SingleSlotActiveWallpaperPool();
        _ = Assert.IsAssignableFrom<IActiveWallpaperPool>(pool);

        await pool.ActivateOwnedAsync(direct, directOwner);
        await pool.ActivateOwnedAsync(dynamic, dynamicOwner);
        var staleReleased = await pool.ReleaseOwnedAsync(directOwner);

        Assert.False(staleReleased);
        Assert.Same(dynamic, pool.ActiveLease);
        Assert.Equal(dynamicOwner, pool.ActiveOwnership);
        Assert.Equal(["dispose:direct"], events);
    }

    [Fact]
    public async Task DynamicLeaseCleansPageCaptureWindowAndProjectInFixedOrder()
    {
        var events = new List<string>();
        var page = new FakeActiveLease(
            17,
            ActiveWallpaperDeliveryKind.DynamicStream,
            "page",
            events);
        var capture = new FakeResource("capture", events);
        var window = new FakeResource("window", events);
        var project = new FakeResource("project", events);
        var lease = new DynamicActiveWallpaperLease(
            page,
            capture,
            window,
            project);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(
            ["dispose:page", "dispose:capture", "dispose:window", "dispose:project"],
            events);
    }

    [Fact]
    public async Task DynamicLeaseRetainsOnlyFailedResourcesForAConfirmedCleanupRetry()
    {
        var events = new List<string>();
        var page = new FakeActiveLease(
            18,
            ActiveWallpaperDeliveryKind.DynamicStream,
            "page",
            events);
        var capture = new FailOnceResource("capture", events);
        var window = new FakeResource("window", events);
        var project = new FakeResource("project", events);
        var lease = new DynamicActiveWallpaperLease(
            page,
            capture,
            window,
            project);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await lease.DisposeAsync());
        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(
            [
                "dispose:page",
                "dispose-attempt:capture:1",
                "dispose:window",
                "dispose:project",
                "dispose-attempt:capture:2",
            ],
            events);
    }

    [Fact]
    public async Task ReplacementKeepsThePreviousLeaseOwnedWhenCleanupFails()
    {
        var events = new List<string>();
        var previous = new FailOnceActiveLease("previous", events);
        var next = new FakeActiveLease(
            22,
            ActiveWallpaperDeliveryKind.DirectMedia,
            "next",
            events);
        var previousOwner = PlaybackOwnershipToken.Create();
        var nextOwner = PlaybackOwnershipToken.Create();
        var pool = new SingleSlotActiveWallpaperPool();

        await pool.ActivateOwnedAsync(previous, previousOwner);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pool.ActivateOwnedAsync(next, nextOwner));

        Assert.Same(previous, pool.ActiveLease);
        Assert.Equal(previousOwner, pool.ActiveOwnership);
        Assert.Equal(["dispose-attempt:previous:1"], events);

        await pool.ActivateOwnedAsync(next, nextOwner);

        Assert.Same(next, pool.ActiveLease);
        Assert.Equal(nextOwner, pool.ActiveOwnership);
        Assert.Equal(
            ["dispose-attempt:previous:1", "dispose-attempt:previous:2"],
            events);

        await pool.DisposeAsync();
        Assert.Equal("dispose:next", events[^1]);
    }

    [Fact]
    public async Task OwnedReleaseRetainsTheLeaseUntilCleanupIsConfirmed()
    {
        var events = new List<string>();
        var lease = new FailOnceActiveLease("owned", events);
        var owner = PlaybackOwnershipToken.Create();
        var pool = new SingleSlotActiveWallpaperPool();
        await pool.ActivateOwnedAsync(lease, owner);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pool.ReleaseOwnedAsync(owner));

        Assert.Same(lease, pool.ActiveLease);
        Assert.Equal(owner, pool.ActiveOwnership);

        Assert.True(await pool.ReleaseOwnedAsync(owner));
        Assert.Null(pool.ActiveLease);
        Assert.Null(pool.ActiveOwnership);
        Assert.Equal(
            ["dispose-attempt:owned:1", "dispose-attempt:owned:2"],
            events);

        await pool.DisposeAsync();
    }

    [Fact]
    public async Task PoolDisposeCanRetryAFailedActiveLeaseCleanup()
    {
        var events = new List<string>();
        var lease = new FailOnceActiveLease("pool", events);
        var pool = new SingleSlotActiveWallpaperPool();
        await pool.ActivateAsync(lease);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pool.DisposeAsync());

        Assert.Same(lease, pool.ActiveLease);

        await pool.DisposeAsync();
        await pool.DisposeAsync();

        Assert.Null(pool.ActiveLease);
        Assert.Equal(
            ["dispose-attempt:pool:1", "dispose-attempt:pool:2"],
            events);
    }

    private sealed class FakeActiveLease(
        long generation,
        ActiveWallpaperDeliveryKind deliveryKind,
        string name,
        List<string> events) : IActiveWallpaperLease
    {
        public long Generation { get; } = generation;

        public ActiveWallpaperDeliveryKind DeliveryKind { get; } = deliveryKind;

        public ValueTask DisposeAsync()
        {
            events.Add($"dispose:{name}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeResource(string name, List<string> events) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            events.Add($"dispose:{name}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceActiveLease(string name, List<string> events)
        : IActiveWallpaperLease
    {
        private int _disposeAttempts;

        public long Generation => 21;

        public ActiveWallpaperDeliveryKind DeliveryKind =>
            ActiveWallpaperDeliveryKind.DynamicStream;

        public ValueTask DisposeAsync()
        {
            var attempt = Interlocked.Increment(ref _disposeAttempts);
            events.Add($"dispose-attempt:{name}:{attempt}");
            if (attempt == 1)
            {
                throw new InvalidOperationException("Synthetic cleanup failure.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceResource(string name, List<string> events)
        : IAsyncDisposable
    {
        private int _disposeAttempts;

        public ValueTask DisposeAsync()
        {
            var attempt = Interlocked.Increment(ref _disposeAttempts);
            events.Add($"dispose-attempt:{name}:{attempt}");
            if (attempt == 1)
            {
                throw new InvalidOperationException("Synthetic resource cleanup failure.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceDirectMediaLease : IDirectMediaLease
    {
        public int DisposeAttempts { get; private set; }

        public MediaReference Reference { get; } = new()
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = @"C:\Wallpapers\retry.png",
            LastKnownKind = MediaKind.Image,
        };

        public string ResolvedPath => Reference.SourceIdentifier;

        public LocalFileIdentity FileIdentity { get; } = new(1, 3);

        public MediaFileMetadata Metadata { get; } =
            MediaFileInspector.CreateMetadata(MediaFormat.Png, 128);

        public ValueTask DisposeAsync()
        {
            DisposeAttempts++;
            if (DisposeAttempts == 1)
            {
                throw new InvalidOperationException("Synthetic direct cleanup failure.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
