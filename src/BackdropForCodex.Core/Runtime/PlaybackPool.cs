using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Runtime;

/// <summary>
/// Owns the lease for the one foreground wallpaper supported by the 1.3 runtime.
/// </summary>
public interface IPlaybackPool : IAsyncDisposable
{
    IMediaLease? ActiveLease { get; }

    ValueTask ActivateAsync(
        IMediaLease lease,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);
}

public sealed class SingleSlotPlaybackPool : IPlaybackPool
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IMediaLease? _activeLease;
    private int _disposed;

    public IMediaLease? ActiveLease => Volatile.Read(ref _activeLease);

    public async ValueTask ActivateAsync(
        IMediaLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var previous = Interlocked.Exchange(ref _activeLease, lease);
            if (previous is not null && !ReferenceEquals(previous, lease))
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var lease = Interlocked.Exchange(ref _activeLease, null);
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lease = Interlocked.Exchange(ref _activeLease, null);
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            // Keep the private semaphore alive so callers admitted immediately before the
            // disposed transition can acquire it, observe the in-gate check, and unwind.
        }

        GC.SuppressFinalize(this);
    }
}
