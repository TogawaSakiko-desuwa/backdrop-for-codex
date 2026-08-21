using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Runtime;

/// <summary>
/// Serializes one foreground media slot. Once a lease is published as
/// <see cref="ActiveLease"/>, the pool owns its disposal and callers must treat the property
/// as borrowed. A slot is replaced or cleared only after the former lease confirms disposal;
/// cleanup failure retains the former slot and ownership token so a later operation can retry.
/// </summary>
public interface IPlaybackPool : IAsyncDisposable
{
    IDirectMediaLease? ActiveLease { get; }

    /// <summary>
    /// Identifies the operation that owns the active slot.
    /// </summary>
    PlaybackOwnershipToken? ActiveOwnership { get; }

    /// <summary>
    /// Transfers <paramref name="lease"/> into the active slot and disposes a different prior
    /// lease first. Failure before publication leaves the new lease caller-owned and retains the
    /// prior lease in the slot for a cleanup retry.
    /// </summary>
    ValueTask ActivateAsync(
        IDirectMediaLease lease,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes and then clears the active slot. A disposal failure is propagated while retaining
    /// the slot for a cleanup retry; releasing an empty slot is a no-op.
    /// </summary>
    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfers the lease into the slot under a caller-created, unique ownership token, using
    /// the same ownership and replacement-failure semantics as <see cref="ActivateAsync"/>.
    /// </summary>
    ValueTask ActivateOwnedAsync(
        IDirectMediaLease lease,
        PlaybackOwnershipToken ownership,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes and clears the slot only while it is still owned by
    /// <paramref name="ownership"/>. A false result leaves an empty or newer slot untouched;
    /// disposal failure is propagated while retaining the matching slot for a cleanup retry.
    /// </summary>
    ValueTask<bool> ReleaseOwnedAsync(
        PlaybackOwnershipToken ownership,
        CancellationToken cancellationToken = default);
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

public sealed class SingleSlotPlaybackPool :
    SingleSlotLeasePool<IDirectMediaLease>,
    IPlaybackPool
{
}
