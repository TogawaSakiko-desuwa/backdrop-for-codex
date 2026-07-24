using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using Xunit;

namespace BackdropForCodex.Core.Tests.Runtime;

public sealed class PlaybackPoolTests
{
    [Fact]
    public async Task ActivateAsyncKeepsExactlyOneLeaseAndReleasesPreviousSlot()
    {
        var events = new List<string>();
        var first = new FakeLease("first", events);
        var second = new FakeLease("second", events);
        await using var pool = new SingleSlotPlaybackPool();

        await pool.ActivateAsync(first);
        await pool.ActivateAsync(second);

        Assert.Same(second, pool.ActiveLease);
        Assert.Equal(["dispose:first"], events);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);
    }

    [Fact]
    public async Task ReleaseAsyncClearsSlotAndIsIdempotent()
    {
        var lease = new FakeLease("active", []);
        await using var pool = new SingleSlotPlaybackPool();
        await pool.ActivateAsync(lease);

        await pool.ReleaseAsync();
        await pool.ReleaseAsync();

        Assert.Null(pool.ActiveLease);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsyncReleasesActiveLeaseAndRejectsNewWork()
    {
        var lease = new FakeLease("active", []);
        var pool = new SingleSlotPlaybackPool();
        await pool.ActivateAsync(lease);

        await pool.DisposeAsync();
        await pool.DisposeAsync();

        Assert.Equal(1, lease.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => pool.ActivateAsync(new FakeLease("late", [])).AsTask());
    }

    [Fact]
    public async Task DisposeAsyncLetsActiveAndQueuedOperationsUnwindWithoutStranding()
    {
        var active = new BlockingDisposeLease("active");
        var replacement = new FakeLease("replacement", []);
        var pool = new SingleSlotPlaybackPool();
        await pool.ActivateAsync(active);

        var replaceTask = pool.ActivateAsync(replacement).AsTask();
        await active.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedReleaseTask = pool.ReleaseAsync().AsTask();
        var disposeTask = pool.DisposeAsync().AsTask();

        active.AllowDispose.TrySetResult();

        await replaceTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => queuedReleaseTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, active.DisposeCount);
        Assert.Equal(1, replacement.DisposeCount);
        Assert.Null(pool.ActiveLease);
    }

    internal sealed class FakeLease(string name, List<string> events) : IMediaLease
    {
        public int DisposeCount { get; private set; }

        public MediaReference Reference { get; } = new()
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = $@"C:\Wallpapers\{name}.png",
            LastKnownKind = MediaKind.Image,
        };

        public string ResolvedPath => Reference.SourceIdentifier;

        public LocalFileIdentity FileIdentity { get; } = new(1, 1);

        public MediaFileMetadata Metadata { get; } =
            MediaFileInspector.CreateMetadata(MediaFormat.Png, 128);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            events.Add($"dispose:{name}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingDisposeLease(string name) : IMediaLease
    {
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCount { get; private set; }

        public MediaReference Reference { get; } = new()
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = $@"C:\Wallpapers\{name}.png",
            LastKnownKind = MediaKind.Image,
        };

        public string ResolvedPath => Reference.SourceIdentifier;

        public LocalFileIdentity FileIdentity { get; } = new(1, 2);

        public MediaFileMetadata Metadata { get; } =
            MediaFileInspector.CreateMetadata(MediaFormat.Png, 128);

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            DisposeStarted.TrySetResult();
            await AllowDispose.Task.ConfigureAwait(false);
        }
    }
}
