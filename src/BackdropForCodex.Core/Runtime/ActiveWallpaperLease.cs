using System.Runtime.ExceptionServices;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.Core.Runtime;

public enum ActiveWallpaperDeliveryKind
{
    DirectMedia = 0,
    DynamicStream,
}

/// <summary>
/// Common lifetime boundary for directly pinned media and dynamic renderer streams.
/// </summary>
public interface IActiveWallpaperLease : IAsyncDisposable
{
    long Generation { get; }

    ActiveWallpaperDeliveryKind DeliveryKind { get; }
}

public interface IPausableActiveWallpaperLease
{
    ValueTask SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default);
}

public interface IActiveWallpaperHealthSource
{
    Task Completion { get; }
}

public interface IActiveWallpaperMediaIdentity
{
    Guid? MediaId { get; }
}

/// <summary>
/// Adapts the existing validated file lease into the common active-wallpaper lifetime without
/// weakening or duplicating its retained-handle authorization.
/// </summary>
public sealed class DirectMediaActiveWallpaperLease : IActiveWallpaperLease
{
    private readonly IDirectMediaLease _mediaLease;
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private int _cleanupCompleted;

    public DirectMediaActiveWallpaperLease(
        long generation,
        IDirectMediaLease mediaLease)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Generation = generation;
        _mediaLease = mediaLease ?? throw new ArgumentNullException(nameof(mediaLease));
    }

    public long Generation { get; }

    public ActiveWallpaperDeliveryKind DeliveryKind =>
        ActiveWallpaperDeliveryKind.DirectMedia;

    public IDirectMediaLease MediaLease => _mediaLease;

    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _cleanupCompleted) != 0)
            {
                return;
            }

            await _mediaLease.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _cleanupCompleted, 1);
            GC.SuppressFinalize(this);
        }
        finally
        {
            _disposeGate.Release();
        }
    }
}

/// <summary>
/// Owns the dynamic wallpaper resources in their required teardown order. Disposing one resource
/// cannot prevent later resources from being released, and any resource whose cleanup fails stays
/// owned for a later serialized retry.
/// </summary>
public sealed class DynamicActiveWallpaperLease :
    IActiveWallpaperLease,
    IActiveWallpaperMediaIdentity,
    IPausableActiveWallpaperLease,
    IActiveWallpaperHealthSource
{
    private readonly Guid? _mediaId;
    private readonly long _generation;
    private readonly Task _completion;
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private IActiveWallpaperLease? _pageStream;
    private IAsyncDisposable? _captureAndEncoder;
    private IAsyncDisposable? _window;
    private IAsyncDisposable? _project;
    private int _disposeRequested;
    private int _cleanupCompleted;

    public DynamicActiveWallpaperLease(
        IActiveWallpaperLease pageStream,
        IAsyncDisposable captureAndEncoder,
        IAsyncDisposable window,
        IAsyncDisposable project)
        : this(
            mediaId: null,
            pageStream,
            captureAndEncoder,
            window,
            project)
    {
    }

    public DynamicActiveWallpaperLease(
        Guid mediaId,
        IActiveWallpaperLease pageStream,
        IAsyncDisposable captureAndEncoder,
        IAsyncDisposable window,
        IAsyncDisposable project)
        : this(
            (Guid?)(mediaId == Guid.Empty
                ? throw new ArgumentException(
                    "The dynamic wallpaper media ID cannot be empty.",
                    nameof(mediaId))
                : mediaId),
            pageStream,
            captureAndEncoder,
            window,
            project)
    {
    }

    private DynamicActiveWallpaperLease(
        Guid? mediaId,
        IActiveWallpaperLease pageStream,
        IAsyncDisposable captureAndEncoder,
        IAsyncDisposable window,
        IAsyncDisposable project)
    {
        _mediaId = mediaId;
        _pageStream = pageStream ?? throw new ArgumentNullException(nameof(pageStream));
        _captureAndEncoder = captureAndEncoder ??
            throw new ArgumentNullException(nameof(captureAndEncoder));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        if (pageStream.DeliveryKind != ActiveWallpaperDeliveryKind.DynamicStream)
        {
            throw new ArgumentException(
                "The page stream lease must represent dynamic delivery.",
                nameof(pageStream));
        }

        _generation = pageStream.Generation;
        _completion = ObserveHealthAsync(pageStream, captureAndEncoder, window);
    }

    public long Generation => _generation;

    public Guid? MediaId => _mediaId;

    public ActiveWallpaperDeliveryKind DeliveryKind =>
        ActiveWallpaperDeliveryKind.DynamicStream;

    public Task Completion => _completion;

    public ValueTask SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);
        var captureAndEncoder = Volatile.Read(ref _captureAndEncoder);
        if (captureAndEncoder is IPausableActiveWallpaperLease controller)
        {
            return controller.SetPausedAsync(paused, cancellationToken);
        }

        var window = Volatile.Read(ref _window);
        if (window is IWallpaperEngineWindowLease windowLease)
        {
            return windowLease.SetPausedAsync(paused, cancellationToken);
        }

        throw new WallpaperSourceCapabilityException(
            "The dynamic wallpaper lifetime does not expose a pause controller.");
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _cleanupCompleted) != 0)
            {
                return;
            }

            List<Exception>? failures = null;
            await DisposeOneAsync(
                    _pageStream,
                    () => _pageStream = null)
                .ConfigureAwait(false);
            await DisposeOneAsync(
                    _captureAndEncoder,
                    () => _captureAndEncoder = null)
                .ConfigureAwait(false);
            await DisposeOneAsync(
                    _window,
                    () => _window = null)
                .ConfigureAwait(false);
            await DisposeOneAsync(
                    _project,
                    () => _project = null)
                .ConfigureAwait(false);

            if (_pageStream is null &&
                _captureAndEncoder is null &&
                _window is null &&
                _project is null)
            {
                Volatile.Write(ref _cleanupCompleted, 1);
                GC.SuppressFinalize(this);
            }

            if (failures is [var failure])
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(
                    "One or more dynamic wallpaper resources could not be released.",
                    failures);
            }

            async ValueTask DisposeOneAsync(
                IAsyncDisposable? resource,
                Action clear)
            {
                if (resource is null)
                {
                    return;
                }

                try
                {
                    await resource.DisposeAsync().ConfigureAwait(false);
                    clear();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private static async Task ObserveHealthAsync(params IAsyncDisposable[] resources)
    {
        var completions = resources
            .OfType<IActiveWallpaperHealthSource>()
            .Select(resource => resource.Completion)
            .ToArray();
        if (completions.Length == 0)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(completions).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }
}

/// <summary>
/// Owns the single active wallpaper lifetime independently of whether delivery uses a pinned
/// local file or a dynamic renderer stream.
/// </summary>
public interface IActiveWallpaperPool : IAsyncDisposable
{
    IActiveWallpaperLease? ActiveLease { get; }

    PlaybackOwnershipToken? ActiveOwnership { get; }

    ValueTask ActivateAsync(
        IActiveWallpaperLease lease,
        CancellationToken cancellationToken = default);

    ValueTask ActivateOwnedAsync(
        IActiveWallpaperLease lease,
        PlaybackOwnershipToken ownership,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);

    ValueTask<bool> ReleaseOwnedAsync(
        PlaybackOwnershipToken ownership,
        CancellationToken cancellationToken = default);
}

public sealed class SingleSlotActiveWallpaperPool :
    SingleSlotLeasePool<IActiveWallpaperLease>,
    IActiveWallpaperPool
{
}

/// <summary>
/// Serializes a single caller-owned asynchronous lease without imposing a concrete delivery type.
/// </summary>
public class SingleSlotLeasePool<TLease> : IAsyncDisposable
    where TLease : class, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LeaseSlot? _activeSlot;
    private int _disposed;
    private int _cleanupCompleted;

    public TLease? ActiveLease => Volatile.Read(ref _activeSlot)?.Lease;

    public PlaybackOwnershipToken? ActiveOwnership =>
        Volatile.Read(ref _activeSlot)?.Ownership;

    public ValueTask ActivateAsync(
        TLease lease,
        CancellationToken cancellationToken = default) =>
        ActivateOwnedAsync(lease, PlaybackOwnershipToken.Create(), cancellationToken);

    public async ValueTask ActivateOwnedAsync(
        TLease lease,
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
            var next = new LeaseSlot(ownership, lease);
            var previous = Volatile.Read(ref _activeSlot);
            if (previous is not null && !ReferenceEquals(previous.Lease, lease))
            {
                await previous.Lease.DisposeAsync().ConfigureAwait(false);
            }

            Volatile.Write(ref _activeSlot, next);
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
            var slot = Volatile.Read(ref _activeSlot);
            if (slot is not null)
            {
                await slot.Lease.DisposeAsync().ConfigureAwait(false);
                Volatile.Write(ref _activeSlot, null);
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

            await active.Lease.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _activeSlot, null);

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Close admission before waiting for an in-flight slot transition. Operations that were
        // already queued must observe disposal when they acquire the gate instead of running in
        // front of cleanup and publishing or releasing another lease.
        Interlocked.Exchange(ref _disposed, 1);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _cleanupCompleted) != 0)
            {
                return;
            }

            var slot = Volatile.Read(ref _activeSlot);
            if (slot is not null)
            {
                await slot.Lease.DisposeAsync().ConfigureAwait(false);
                Volatile.Write(ref _activeSlot, null);
            }

            Volatile.Write(ref _cleanupCompleted, 1);
        }
        finally
        {
            _gate.Release();
        }

        if (Volatile.Read(ref _cleanupCompleted) != 0)
        {
            GC.SuppressFinalize(this);
        }
    }

    private sealed record LeaseSlot(
        PlaybackOwnershipToken Ownership,
        TLease Lease);
}
