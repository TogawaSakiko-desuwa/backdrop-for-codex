using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Runtime;

/// <summary>
/// Owns the lease for the one foreground wallpaper supported by the runtime.
/// </summary>
public interface IPlaybackPool : IAsyncDisposable
{
    IMediaLease? ActiveLease { get; }

    /// <summary>
    /// Identifies the operation that owns the active slot, when ownership-aware operations
    /// are supported by the implementation.
    /// </summary>
    PlaybackOwnershipToken? ActiveOwnership => null;

    ValueTask ActivateAsync(
        IMediaLease lease,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfers the lease into the slot under a caller-created, unique ownership token.
    /// </summary>
    ValueTask ActivateOwnedAsync(
        IMediaLease lease,
        PlaybackOwnershipToken ownership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ownership.ThrowIfEmpty(nameof(ownership));
        throw new NotSupportedException(
            "This playback pool does not implement ownership-aware activation.");
    }

    /// <summary>
    /// Releases the slot only when it is still owned by <paramref name="ownership"/>.
    /// </summary>
    ValueTask<bool> ReleaseOwnedAsync(
        PlaybackOwnershipToken ownership,
        CancellationToken cancellationToken = default)
    {
        ownership.ThrowIfEmpty(nameof(ownership));
        return ValueTask.FromResult(false);
    }
}

public readonly record struct PlaybackOwnershipToken(Guid Value)
{
    public bool IsEmpty => Value == Guid.Empty;

    public static PlaybackOwnershipToken Create() =>
        new(Guid.CreateVersion7());

    internal void ThrowIfEmpty(string parameterName)
    {
        if (IsEmpty)
        {
            throw new ArgumentException(
                "The playback ownership token cannot be empty.",
                parameterName);
        }
    }
}

public sealed class SingleSlotPlaybackPool : IPlaybackPool
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PlaybackSlot? _activeSlot;
    private int _disposed;

    public IMediaLease? ActiveLease => Volatile.Read(ref _activeSlot)?.Lease;

    public PlaybackOwnershipToken? ActiveOwnership =>
        Volatile.Read(ref _activeSlot)?.Ownership;

    public ValueTask ActivateAsync(
        IMediaLease lease,
        CancellationToken cancellationToken = default) =>
        ActivateOwnedAsync(
            lease,
            PlaybackOwnershipToken.Create(),
            cancellationToken);

    public async ValueTask ActivateOwnedAsync(
        IMediaLease lease,
        PlaybackOwnershipToken ownership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ownership.ThrowIfEmpty(nameof(ownership));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var next = new PlaybackSlot(ownership, lease);
            var previous = Interlocked.Exchange(ref _activeSlot, next);
            if (previous is not null && !ReferenceEquals(previous.Lease, lease))
            {
                await previous.Lease.DisposeAsync().ConfigureAwait(false);
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
            var slot = Interlocked.Exchange(ref _activeSlot, null);
            if (slot is not null)
            {
                await slot.Lease.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> ReleaseOwnedAsync(
        PlaybackOwnershipToken ownership,
        CancellationToken cancellationToken = default)
    {
        ownership.ThrowIfEmpty(nameof(ownership));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var active = Volatile.Read(ref _activeSlot);
            if (active is null || active.Ownership != ownership)
            {
                return false;
            }

            var slot = Interlocked.Exchange(ref _activeSlot, null);
            if (slot is not null)
            {
                await slot.Lease.DisposeAsync().ConfigureAwait(false);
            }

            return true;
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
            var slot = Interlocked.Exchange(ref _activeSlot, null);
            if (slot is not null)
            {
                await slot.Lease.DisposeAsync().ConfigureAwait(false);
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

    private sealed record PlaybackSlot(
        PlaybackOwnershipToken Ownership,
        IMediaLease Lease);
}
