using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.Core.Runtime;

/// <summary>
/// Tracks caller-owned resources and the entry-state snapshot for one serialized activation
/// attempt. The context never escapes the coordinator operation gate.
/// </summary>
internal sealed class ActivationAttemptContext
{
    public ActivationAttemptContext(
        RuntimeActivationRequest request,
        WallpaperRuntimeSurface previousSurface,
        SettingsV2? previousActiveSnapshot,
        CancellationToken callerCancellation)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        CallerCancellation = callerCancellation;
        PreviousSurface = previousSurface ??
            throw new ArgumentNullException(nameof(previousSurface));
        PreviousActiveSnapshot = previousActiveSnapshot;
    }

    public RuntimeActivationRequest Request { get; }

    public CancellationToken CallerCancellation { get; }

    public WallpaperRuntimeSurface PreviousSurface { get; }

    public SettingsV2? PreviousActiveSnapshot { get; }

    public VerifiedCodexIdentity? Identity { get; private set; }

    public IDirectMediaLease? PendingLease { get; private set; }

    public bool RuntimeMutationStarted { get; private set; }

    public long? Generation { get; private set; }

    public PlaybackOwnershipToken? RequestedPlaybackOwnership { get; private set; }

    public bool PlaybackLeasePublished { get; private set; }

    public PlaybackOwnershipToken? ObservedPlaybackOwnership { get; private set; }

    public void SetIdentity(VerifiedCodexIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (Identity is not null)
        {
            throw new InvalidOperationException(
                "The activation attempt already has a verified Codex identity.");
        }

        Identity = identity;
    }

    public VerifiedCodexIdentity RequireIdentity() =>
        Identity ??
        throw new InvalidOperationException(
            "The activation attempt has no verified Codex identity.");

    public void AcceptPendingLease(IDirectMediaLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (PendingLease is not null || PlaybackLeasePublished)
        {
            throw new InvalidOperationException(
                "The activation attempt already owns or published a media lease.");
        }

        PendingLease = lease;
    }

    public IDirectMediaLease RequirePendingLease() =>
        PendingLease ??
        throw new InvalidOperationException("No validated media lease is available.");

    public void SetGeneration(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);

        if (Generation is not null)
        {
            throw new InvalidOperationException(
                "The activation attempt already has an injection generation.");
        }

        Generation = generation;
    }

    public void MarkRuntimeMutationStarted()
    {
        if (Generation is null)
        {
            throw new InvalidOperationException(
                "An injection generation is required before runtime mutation begins.");
        }

        RuntimeMutationStarted = true;
    }

    public PlaybackOwnershipToken BeginPlaybackTransfer()
    {
        _ = RequirePendingLease();
        if (RequestedPlaybackOwnership is not null)
        {
            throw new InvalidOperationException(
                "The activation attempt already requested playback ownership.");
        }

        var ownership = PlaybackOwnershipToken.Create();
        RequestedPlaybackOwnership = ownership;
        return ownership;
    }

    public bool ObservePlaybackPublication(IPlaybackPool playbackPool)
    {
        ArgumentNullException.ThrowIfNull(playbackPool);
        var pendingLease = RequirePendingLease();
        var requestedOwnership = RequestedPlaybackOwnership ??
            throw new InvalidOperationException(
                "Playback ownership must be requested before publication is observed.");
        var activeLease = playbackPool.ActiveLease;
        var activeOwnership = playbackPool.ActiveOwnership;
        if (!ReferenceEquals(activeLease, pendingLease))
        {
            return false;
        }

        PendingLease = null;
        PlaybackLeasePublished = true;
        ObservedPlaybackOwnership = activeOwnership;
        return activeOwnership == requestedOwnership;
    }

    public async ValueTask<Exception?> TryDisposePendingLeaseAsync()
    {
        var pendingLease = PendingLease;
        PendingLease = null;
        if (pendingLease is null)
        {
            return null;
        }

        try
        {
            await pendingLease.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
