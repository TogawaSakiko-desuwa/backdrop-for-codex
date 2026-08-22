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
    private RuntimeMutationSignal? _mutationSignal;
    private int _runtimeMutationStarted;

    public ActivationAttemptContext(
        RuntimeActivationRequest request,
        WallpaperRuntimeSurface previousSurface,
        SettingsV3? previousActiveSnapshot,
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

    public SettingsV3? PreviousActiveSnapshot { get; }

    public VerifiedCodexIdentity? Identity { get; private set; }

    public IDirectMediaLease? PendingLease { get; private set; }

    public IWallpaperEngineProjectLease? PendingProjectLease { get; private set; }

    public WallpaperSourceResolution? DynamicResolution { get; private set; }

    public IActiveWallpaperLease? PendingActiveLease { get; private set; }

    public RuntimeMutationSignal MutationSignal =>
        _mutationSignal ??
        throw new InvalidOperationException(
            "An injection generation is required before observing runtime mutation.");

    public bool RuntimeMutationStarted =>
        Volatile.Read(ref _runtimeMutationStarted) != 0;

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
        if (PendingLease is not null ||
            PendingProjectLease is not null ||
            PendingActiveLease is not null ||
            PlaybackLeasePublished)
        {
            throw new InvalidOperationException(
                "The activation attempt already owns or published a media lease.");
        }

        PendingLease = lease;
    }

    public void AcceptPendingProjectLease(
        WallpaperSourceResolution resolution,
        IWallpaperEngineProjectLease lease)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(lease);
        if (PendingLease is not null ||
            PendingProjectLease is not null ||
            PendingActiveLease is not null ||
            PlaybackLeasePublished)
        {
            throw new InvalidOperationException(
                "The activation attempt already owns or published a wallpaper lease.");
        }

        WallpaperEngineProjectLeaseContract.Validate(lease);
        if (lease.Resolution.CanonicalReference.MediaId !=
                resolution.CanonicalReference.MediaId ||
            lease.Resolution.Descriptor.SourceKind != resolution.Descriptor.SourceKind ||
            !WallpaperSourceIdentifier.AreEqual(
                resolution.Descriptor.SourceKind,
                lease.Resolution.CanonicalReference.SourceIdentifier,
                resolution.CanonicalReference.SourceIdentifier) ||
            lease.Resolution.Descriptor.ContentKind != resolution.Descriptor.ContentKind ||
            lease.Resolution.Descriptor.DeliveryKind != resolution.Descriptor.DeliveryKind)
        {
            throw new WallpaperSourceCapabilityException(
                "The project provider acquired a different wallpaper source.");
        }

        DynamicResolution = resolution;
        PendingProjectLease = lease;
    }

    public IWallpaperEngineProjectLease RequirePendingProjectLease() =>
        PendingProjectLease ??
        throw new InvalidOperationException("No validated Wallpaper Engine project lease is available.");

    public IDirectMediaLease RequirePendingLease() =>
        PendingLease ??
        throw new InvalidOperationException("No validated media lease is available.");

    public DirectMediaActiveWallpaperLease PromotePendingDirectLease(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        if (PendingActiveLease is not null || PlaybackLeasePublished)
        {
            throw new InvalidOperationException(
                "The activation attempt already owns or published an active wallpaper lease.");
        }

        var mediaLease = RequirePendingLease();
        var activeLease = new DirectMediaActiveWallpaperLease(generation, mediaLease);
        PendingLease = null;
        PendingActiveLease = activeLease;
        return activeLease;
    }

    public IActiveWallpaperLease AcceptPreparedDynamicLease(
        IActiveWallpaperLease activeLease,
        long generation)
    {
        ArgumentNullException.ThrowIfNull(activeLease);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        if (PendingProjectLease is null ||
            PendingActiveLease is not null ||
            PlaybackLeasePublished)
        {
            throw new InvalidOperationException(
                "The activation attempt cannot accept a prepared dynamic wallpaper lease.");
        }

        if (activeLease.Generation != generation ||
            activeLease.DeliveryKind != ActiveWallpaperDeliveryKind.DynamicStream)
        {
            throw new WallpaperSourceCapabilityException(
                "The dynamic activation factory returned a mismatched wallpaper lifetime.");
        }

        // A successful dynamic lifetime has assumed ownership of the borrowed project lease.
        PendingProjectLease = null;
        PendingActiveLease = activeLease;
        return activeLease;
    }

    public void SetGeneration(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);

        if (Generation is not null)
        {
            throw new InvalidOperationException(
                "The activation attempt already has an injection generation.");
        }

        Generation = generation;
        _mutationSignal = new RuntimeMutationSignal(generation, MarkRuntimeMutationStarted);
    }

    public void MarkRuntimeMutationStarted()
    {
        if (Generation is null)
        {
            throw new InvalidOperationException(
                "An injection generation is required before runtime mutation begins.");
        }

        Volatile.Write(ref _runtimeMutationStarted, 1);
    }

    public PlaybackOwnershipToken BeginPlaybackTransfer()
    {
        _ = RequirePendingActiveLease();
        if (RequestedPlaybackOwnership is not null)
        {
            throw new InvalidOperationException(
                "The activation attempt already requested playback ownership.");
        }

        var ownership = PlaybackOwnershipToken.Create();
        RequestedPlaybackOwnership = ownership;
        return ownership;
    }

    public IActiveWallpaperLease RequirePendingActiveLease() =>
        PendingActiveLease ??
        throw new InvalidOperationException("No prepared active wallpaper lease is available.");

    public bool ObservePlaybackPublication(IActiveWallpaperPool playbackPool)
    {
        ArgumentNullException.ThrowIfNull(playbackPool);
        var pendingLease = RequirePendingActiveLease();
        var requestedOwnership = RequestedPlaybackOwnership ??
            throw new InvalidOperationException(
                "Playback ownership must be requested before publication is observed.");
        var activeLease = playbackPool.ActiveLease;
        var activeOwnership = playbackPool.ActiveOwnership;
        if (!ReferenceEquals(activeLease, pendingLease))
        {
            return false;
        }

        PendingActiveLease = null;
        PlaybackLeasePublished = true;
        ObservedPlaybackOwnership = activeOwnership;
        return activeOwnership == requestedOwnership;
    }

    public async ValueTask<Exception?> TryDisposePendingLeaseAsync()
    {
        var pendingActiveLease = PendingActiveLease;
        if (pendingActiveLease is not null)
        {
            try
            {
                await pendingActiveLease.DisposeAsync().ConfigureAwait(false);
                PendingActiveLease = null;
                DynamicResolution = null;
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        var pendingLease = PendingLease;
        var pendingProjectLease = PendingProjectLease;
        var pendingResource = (IAsyncDisposable?)pendingProjectLease ?? pendingLease;
        if (pendingResource is null)
        {
            return null;
        }

        try
        {
            await pendingResource.DisposeAsync().ConfigureAwait(false);
            if (pendingProjectLease is not null)
            {
                PendingProjectLease = null;
                DynamicResolution = null;
            }
            else
            {
                PendingLease = null;
            }

            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    public PendingActivationResourceOwner? TakePendingCleanupOwner()
    {
        IAsyncDisposable? pendingResource = PendingActiveLease;
        if (pendingResource is not null)
        {
            var owner = new PendingActivationResourceOwner(pendingResource);
            PendingActiveLease = null;
            DynamicResolution = null;
            return owner;
        }

        pendingResource = PendingProjectLease;
        if (pendingResource is not null)
        {
            var owner = new PendingActivationResourceOwner(pendingResource);
            PendingProjectLease = null;
            DynamicResolution = null;
            return owner;
        }

        pendingResource = PendingLease;
        if (pendingResource is null)
        {
            return null;
        }

        var directOwner = new PendingActivationResourceOwner(pendingResource);
        PendingLease = null;
        return directOwner;
    }
}

internal sealed class PendingActivationResourceOwner(IAsyncDisposable resource)
    : IAsyncDisposable
{
    private IAsyncDisposable? _resource =
        resource ?? throw new ArgumentNullException(nameof(resource));

    public async ValueTask DisposeAsync()
    {
        var retained = Volatile.Read(ref _resource);
        if (retained is null)
        {
            return;
        }

        await retained.DisposeAsync().ConfigureAwait(false);
        _ = Interlocked.CompareExchange(ref _resource, null, retained);
    }
}

internal sealed class RuntimeMutationSignal(
    long generation,
    Action reportPossibleMutation)
{
    private Action? _reportPossibleMutation =
        reportPossibleMutation ?? throw new ArgumentNullException(nameof(reportPossibleMutation));

    public long Generation { get; } = generation > 0
        ? generation
        : throw new ArgumentOutOfRangeException(nameof(generation));

    public void ReportPossibleMutation() =>
        Interlocked.Exchange(ref _reportPossibleMutation, null)?.Invoke();
}
