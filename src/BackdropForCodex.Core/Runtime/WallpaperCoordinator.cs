using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using System.Runtime.ExceptionServices;

namespace BackdropForCodex.Core.Runtime;

public enum WallpaperRuntimePhase
{
    Idle = 0,
    Validating,
    LaunchingCodex,
    DiscoveringEndpoint,
    Applying,
    Active,
    Paused,
    Stopping,
    Faulted,
    Disposed,
}

public sealed class WallpaperRuntimeStatusChangedEventArgs : EventArgs
{
    public WallpaperRuntimeStatusChangedEventArgs(
        WallpaperRuntimePhase phase,
        string detail,
        long revision = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        Phase = phase;
        Detail = detail;
        Revision = revision;
    }

    public WallpaperRuntimePhase Phase { get; }

    public string Detail { get; }

    public long Revision { get; }
}

public sealed record WallpaperCoordinatorOptions
{
    public static WallpaperCoordinatorOptions Default { get; } = new();

    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public void Validate()
    {
        if (DiscoveryTimeout <= TimeSpan.Zero || DiscoveryTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(DiscoveryTimeout),
                "The discovery timeout must be between zero and two minutes.");
        }

        if (DiscoveryInterval <= TimeSpan.Zero || DiscoveryInterval > DiscoveryTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DiscoveryInterval),
                "The discovery interval must be positive and no greater than the timeout.");
        }
    }
}

public interface ICdpEndpointDiscoveryService
{
    ValueTask<CdpDiscoveryResult> DiscoverAsync(
        VerifiedCodexIdentity identity,
        CancellationToken cancellationToken = default);
}

public sealed class CdpEndpointDiscoveryService : ICdpEndpointDiscoveryService
{
    private readonly CdpEndpointDiscovery _discovery;

    public CdpEndpointDiscoveryService(CdpEndpointDiscovery discovery)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    public ValueTask<CdpDiscoveryResult> DiscoverAsync(
        VerifiedCodexIdentity identity,
        CancellationToken cancellationToken = default) =>
        _discovery.DiscoverAsync(identity, cancellationToken);
}

public interface IWallpaperRuntime : IAsyncDisposable
{
    event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? StatusChanged;

    WallpaperRuntimeStatusChangedEventArgs Status { get; }

    bool IsActive { get; }

    bool IsPaused { get; }

    WallpaperRuntimeSurface Surface { get; }

    SettingsV2? ActiveSnapshot { get; }

    Task<RuntimeActivationResult> ActivateAsync(
        RuntimeActivationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes a runtime-equivalent canonical snapshot and activation revision without
    /// creating a new injection generation. A null result means the runtime no longer has
    /// an equivalent active surface and the caller must perform a normal activation.
    /// </summary>
    Task<RuntimeActivationResult?> TryPromoteActiveSnapshotAsync(
        RuntimeActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<RuntimeActivationResult?>(null);
    }

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default);

    Task<RuntimeActivationResult> RestoreOfficialAsync(
        long revision,
        CancellationToken cancellationToken = default);
}

public interface IWallpaperRuntimeCapabilitySource
{
    event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>? CapabilitiesChanged;

    CompatibilityCapabilities Capabilities { get; }

    WallpaperCompatibilitySnapshot Compatibility { get; }
}

/// <summary>
/// Owns the complete enhanced-launch lifecycle. It never terminates Codex and never attaches to a
/// Codex process that predates this coordinator instance.
/// </summary>
public sealed class WallpaperCoordinator :
    IWallpaperRuntime,
    IWallpaperRuntimeCapabilitySource
{
    public const string RemoteDebuggingArguments =
        "--remote-debugging-address=127.0.0.1 --remote-debugging-port=0";

    private readonly IInstalledCodexPackageLocator _packageLocator;
    private readonly ICodexProcessSnapshotSource _processSource;
    private readonly IApplicationActivationManager _activationManager;
    private readonly ICdpEndpointDiscoveryService _endpointDiscovery;
    private readonly IWallpaperSourceProvider _mediaSourceProvider;
    private readonly IPlaybackPool _playbackPool;
    private readonly IWallpaperInjectionSession _injectionSession;
    private readonly WallpaperInjectionGenerationMonitor _injectionMonitor;
    private readonly WallpaperCoordinatorOptions _options;
    private readonly IDisposable? _ownedTransport;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private VerifiedCdpEndpoint? _endpoint;
    private uint _activationProcessId;
    private DateTimeOffset? _activationProcessStartTimeUtc;
    private SettingsV2? _activeSnapshot;
    private WallpaperRuntimeSurface _surface = WallpaperRuntimeSurface.Disconnected();
    private PlaybackOwnershipToken? _activePlaybackOwnership;
    private long _activeRevision;
    private long _generation;
    private bool _launchedByThisCoordinator;
    private bool _paused;
    private int _disposed;

    public WallpaperCoordinator(
        IInstalledCodexPackageLocator packageLocator,
        ICodexProcessSnapshotSource processSource,
        IApplicationActivationManager activationManager,
        ICdpEndpointDiscoveryService endpointDiscovery,
        IWallpaperSourceProvider mediaSourceProvider,
        IPlaybackPool playbackPool,
        IWallpaperInjectionSession injectionSession,
        WallpaperCoordinatorOptions? options = null)
        : this(
            packageLocator,
            processSource,
            activationManager,
            endpointDiscovery,
            mediaSourceProvider,
            playbackPool,
            injectionSession,
            options,
            ownedTransport: null)
    {
    }

    private WallpaperCoordinator(
        IInstalledCodexPackageLocator packageLocator,
        ICodexProcessSnapshotSource processSource,
        IApplicationActivationManager activationManager,
        ICdpEndpointDiscoveryService endpointDiscovery,
        IWallpaperSourceProvider mediaSourceProvider,
        IPlaybackPool playbackPool,
        IWallpaperInjectionSession injectionSession,
        WallpaperCoordinatorOptions? options,
        IDisposable? ownedTransport)
    {
        _packageLocator = packageLocator ?? throw new ArgumentNullException(nameof(packageLocator));
        _processSource = processSource ?? throw new ArgumentNullException(nameof(processSource));
        _activationManager = activationManager ?? throw new ArgumentNullException(nameof(activationManager));
        _endpointDiscovery = endpointDiscovery ?? throw new ArgumentNullException(nameof(endpointDiscovery));
        _mediaSourceProvider = mediaSourceProvider ??
            throw new ArgumentNullException(nameof(mediaSourceProvider));
        _playbackPool = playbackPool ?? throw new ArgumentNullException(nameof(playbackPool));
        _injectionSession = injectionSession ?? throw new ArgumentNullException(nameof(injectionSession));
        _options = options ?? WallpaperCoordinatorOptions.Default;
        _options.Validate();
        _ownedTransport = ownedTransport;
        _injectionMonitor = new WallpaperInjectionGenerationMonitor(
            injectionSession as IWallpaperInjectionHealthSource,
            injectionSession as IWallpaperInjectionCapabilitySource,
            this,
            HandleInjectionHealthFaultAsync);

        Status = new WallpaperRuntimeStatusChangedEventArgs(
            WallpaperRuntimePhase.Idle,
            "Wallpaper runtime is idle.");
    }

    public event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>? CapabilitiesChanged
    {
        add => _injectionMonitor.CapabilitiesChanged += value;
        remove => _injectionMonitor.CapabilitiesChanged -= value;
    }

    public WallpaperRuntimeStatusChangedEventArgs Status { get; private set; }

    public bool IsActive =>
        _injectionSession.IsActive &&
        _playbackPool.ActiveLease is not null;

    public bool IsPaused => _paused;

    public WallpaperRuntimeSurface Surface => Volatile.Read(ref _surface);

    public SettingsV2? ActiveSnapshot => Volatile.Read(ref _activeSnapshot);

    public CompatibilityCapabilities Capabilities => _injectionMonitor.Capabilities;

    public WallpaperCompatibilitySnapshot Compatibility =>
        _injectionMonitor.Compatibility;

    public static WallpaperCoordinator CreateDefault()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            throw new PlatformNotSupportedException("Backdrop for Codex requires Windows 11.");
        }

        var processes = new WindowsCodexProcessSnapshotSource();
        var candidateSource = new LoopbackTcpCdpEndpointCandidateSource(
            processes,
            new WindowsTcpListenerSnapshotSource());
        var transport = new HttpCdpJsonTransport(
            requestTimeout: TimeSpan.FromMilliseconds(750));
        var discovery = new CdpEndpointDiscoveryService(
            new CdpEndpointDiscovery(candidateSource, transport));

        return new WallpaperCoordinator(
            new InstalledCodexPackageLocator(),
            processes,
            new WindowsApplicationActivationManager(),
            discovery,
            new LocalFileWallpaperSourceProvider(),
            new SingleSlotPlaybackPool(),
            new PuppeteerWallpaperSession(),
            WallpaperCoordinatorOptions.Default,
            transport);
    }

    public async Task<RuntimeActivationResult> ActivateAsync(
        RuntimeActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IMediaLease? pendingLease = null;
        var runtimeMutationStarted = false;
        var previousSurface = Surface;
        var previousActiveSnapshot = ActiveSnapshot;
        try
        {
            ThrowIfDisposed();
            _activeRevision = request.Revision;

            if (request.Media is null)
            {
                return await ApplyOfficialAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!request.SettingsSnapshot.AcceptedCdpRisk)
            {
                var riskError = RuntimeError(
                    "cdp-risk-not-accepted",
                    new CdpRiskNotAcceptedException());
                return RuntimeActivationResult.SavedButNotActivated(
                    request.Revision,
                    previousSurface,
                    previousActiveSnapshot,
                    riskError);
            }

            _injectionMonitor.BeginAttempt();
            Publish(
                WallpaperRuntimePhase.Validating,
                "Validating the Codex package and media file.",
                request.Revision);

            var (_, security) = LocateVerifiedPackage();
            var identity = security.Identity!;
            try
            {
                pendingLease = await _mediaSourceProvider
                    .AcquireLeaseAsync(request.Media, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var error = RuntimeError("media-lease-unavailable", exception);
                var fallbackSurface =
                    previousSurface.Kind == WallpaperRuntimeSurfaceKind.MediaActive
                        ? previousSurface
                        : WallpaperRuntimeSurface.Disconnected(error);
                Volatile.Write(ref _surface, fallbackSurface);
                Publish(
                    fallbackSurface.Kind == WallpaperRuntimeSurfaceKind.MediaActive
                        ? (_paused
                            ? WallpaperRuntimePhase.Paused
                            : WallpaperRuntimePhase.Active)
                        : WallpaperRuntimePhase.Idle,
                    "The saved media could not be reacquired; the previous runtime state was preserved.",
                    request.Revision);
                return RuntimeActivationResult.SavedButNotActivated(
                    request.Revision,
                    fallbackSurface,
                    previousActiveSnapshot,
                    error);
            }

            _injectionMonitor.CaptureSecurity(CodexSecurityResult.InProgress(
                CodexSecurityStage.ProcessIdentity,
                "Validating the activated Codex process.",
                identity));
            var processes = await _processSource
                .GetProcessesAsync(cancellationToken)
                .ConfigureAwait(false);
            var reviewedProcesses = processes
                .Where(process => IsReviewedCodexProcess(process, identity))
                .ToArray();
            var activationProcessIsRunning = _launchedByThisCoordinator &&
                _activationProcessId != 0 &&
                reviewedProcesses.Any(process =>
                    process.ProcessId == _activationProcessId &&
                    (_activationProcessStartTimeUtc is null ||
                     process.StartTimeUtc == _activationProcessStartTimeUtc));
            if (_launchedByThisCoordinator && !activationProcessIsRunning)
            {
                try
                {
                    await StopInjectedContentAndMediaAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _launchedByThisCoordinator = false;
                    _activationProcessId = 0;
                    _activationProcessStartTimeUtc = null;
                    _endpoint = null;
                }
            }

            if (!_launchedByThisCoordinator && reviewedProcesses.Length != 0)
            {
                _injectionMonitor.CaptureSecurity(CodexSecurityResult.Rejected(
                    CodexSecurityStage.ProcessIdentity,
                    CodexSecurityFailureCode.ProcessIdentityMismatch,
                    "A verified Codex process exists but was not launched by this runtime.",
                    identity));
                throw new CodexAlreadyRunningException();
            }

            try
            {
                if (!_launchedByThisCoordinator && !_injectionSession.IsActive)
                {
                    Publish(
                        WallpaperRuntimePhase.LaunchingCodex,
                        "Launching the reviewed Codex MSIX app.",
                        request.Revision);
                    var activation = _activationManager.Activate(identity, RemoteDebuggingArguments);
                    _activationProcessId = activation.ProcessId;
                    _activationProcessStartTimeUtc = null;
                    _launchedByThisCoordinator = true;
                }

                if (_endpoint is null || !_injectionSession.IsActive)
                {
                    _injectionMonitor.CaptureSecurity(CodexSecurityResult.InProgress(
                        CodexSecurityStage.LoopbackEndpoint,
                        "Validating the Codex loopback debugging endpoint.",
                        identity));
                    Publish(
                        WallpaperRuntimePhase.DiscoveringEndpoint,
                        "Waiting for Codex to publish its loopback debugging endpoint.",
                        request.Revision);
                    _endpoint = await DiscoverSingleEndpointAsync(identity, cancellationToken)
                        .ConfigureAwait(false);
                    _activationProcessStartTimeUtc = _endpoint.Candidate.StartTimeUtc;
                }

                _injectionMonitor.CaptureSecurity(CodexSecurityResult.InProgress(
                    CodexSecurityStage.TargetValidation,
                    "Validating the unique Codex work-page target.",
                    identity));
                Publish(
                    WallpaperRuntimePhase.Applying,
                    "Applying the wallpaper to the reviewed Codex page.",
                    request.Revision);
                var leaseToActivate = pendingLease ??
                    throw new InvalidOperationException("No validated media lease is available.");
                var injectionOptions = CreateInjectionOptions(
                    checked(++_generation),
                    leaseToActivate,
                    request.GlobalProfile);
                _injectionMonitor.BeginCapabilityObservation(injectionOptions.Generation);
                runtimeMutationStarted = true;
                await _injectionSession
                    .ApplyAsync(_endpoint, injectionOptions, cancellationToken)
                    .ConfigureAwait(false);
                _injectionMonitor.CaptureSecurity(CodexSecurityResult.Verified(
                    identity,
                    CodexSecurityStage.TargetValidation,
                    "The package, process, endpoint and unique Codex target passed security validation."));

                var ownership = PlaybackOwnershipToken.Create();
                try
                {
                    await _playbackPool
                        .ActivateOwnedAsync(leaseToActivate, ownership, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (ReferenceEquals(_playbackPool.ActiveLease, leaseToActivate) &&
                        _playbackPool.ActiveOwnership == ownership)
                    {
                        _activePlaybackOwnership = ownership;
                        pendingLease = null;
                    }
                }

                // Pause belongs to one injected media generation. A replacement starts from its
                // own default playback state and must not inherit a stale pause from the prior video.
                _injectionMonitor.MarkActive(injectionOptions.Generation);
                _paused = false;
                var activeSnapshot = request.SettingsSnapshot.CreateSnapshot();
                var surface = WallpaperRuntimeSurface.MediaActive(
                    injectionOptions.Generation,
                    request.Media.MediaId,
                    ownership);
                Volatile.Write(ref _activeSnapshot, activeSnapshot);
                Volatile.Write(ref _surface, surface);
                Publish(
                    WallpaperRuntimePhase.Active,
                    "Wallpaper is active.",
                    request.Revision);
                return RuntimeActivationResult.MediaActive(
                    request.Revision,
                    activeSnapshot,
                    surface);
            }
            catch (Exception operationException)
            {
                CaptureTerminalSecurityResult(operationException, identity);
                throw;
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            CaptureTerminalSecurityResult(exception);
            Exception? safetyCleanupFailure = null;
            var cleanupCompleted = false;
            if (runtimeMutationStarted ||
                _injectionMonitor.Compatibility.Security.Status == CodexSecurityStatus.Rejected)
            {
                try
                {
                    await StopInjectedContentAndMediaAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    cleanupCompleted = true;
                }
                catch (Exception cleanupException)
                {
                    safetyCleanupFailure = cleanupException;
                }
            }

            var disposalFailure = await TryDisposeLeaseAsync(pendingLease).ConfigureAwait(false);
            pendingLease = null;
            var failure = safetyCleanupFailure ?? disposalFailure;
            if (failure is not null)
            {
                var error = RuntimeError("activation-cancel-cleanup-failed", failure);
                var surface = CreateFaultedSurface(error);
                Volatile.Write(ref _activeSnapshot, null);
                Volatile.Write(ref _surface, surface);
                _activePlaybackOwnership = surface.PlaybackOwnership;
                Publish(
                    WallpaperRuntimePhase.Faulted,
                    "The wallpaper operation was cancelled and cleanup could not be confirmed.",
                    request.Revision);
                return RuntimeActivationResult.Canceled(
                    request.Revision,
                    surface);
            }

            var canceledSurface = cleanupCompleted
                ? WallpaperRuntimeSurface.Official()
                : previousSurface;
            var canceledActive = cleanupCompleted
                ? null
                : previousActiveSnapshot;
            Volatile.Write(ref _activeSnapshot, canceledActive);
            Volatile.Write(ref _surface, canceledSurface);
            Publish(
                cleanupCompleted ||
                canceledSurface.Kind != WallpaperRuntimeSurfaceKind.MediaActive
                    ? WallpaperRuntimePhase.Idle
                    : (_paused
                        ? WallpaperRuntimePhase.Paused
                        : WallpaperRuntimePhase.Active),
                "The wallpaper operation was cancelled.",
                request.Revision);
            return RuntimeActivationResult.Canceled(
                request.Revision,
                canceledSurface,
                canceledActive);
        }
        catch (Exception exception)
        {
            CaptureTerminalSecurityResult(exception);
            Exception? safetyCleanupFailure = null;
            try
            {
                await StopInjectedContentAndMediaAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                safetyCleanupFailure = cleanupException;
            }

            var disposalFailure = await TryDisposeLeaseAsync(pendingLease).ConfigureAwait(false);
            pendingLease = null;
            var terminalException = safetyCleanupFailure is null && disposalFailure is null
                ? exception
                : new AggregateException(
                    "The wallpaper operation and one or more safety cleanup steps failed.",
                    new[] { exception, safetyCleanupFailure, disposalFailure }
                        .OfType<Exception>());
            var runtimeError = RuntimeError("activation-failed", terminalException);
            var faultedSurface = CreateFaultedSurface(runtimeError);
            Volatile.Write(ref _activeSnapshot, null);
            Volatile.Write(ref _surface, faultedSurface);
            _activePlaybackOwnership = faultedSurface.PlaybackOwnership;
            Publish(WallpaperRuntimePhase.Faulted, exception.Message, request.Revision);
            return RuntimeActivationResult.Failed(
                request.Revision,
                faultedSurface,
                activeSnapshot: null,
                error: runtimeError);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RuntimeActivationResult?> TryPromoteActiveSnapshotAsync(
        RuntimeActivationRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var currentSnapshot = ActiveSnapshot;
            var currentSurface = Surface;
            if (currentSnapshot is null ||
                !SettingsV2Comparer.RuntimeEquivalent(
                    currentSnapshot,
                    request.SettingsSnapshot))
            {
                return null;
            }

            var promotedSnapshot = request.SettingsSnapshot.CreateSnapshot();
            RuntimeActivationResult result;
            if (request.IsOfficial)
            {
                if (currentSurface.Kind != WallpaperRuntimeSurfaceKind.Official)
                {
                    return null;
                }

                Volatile.Write(ref _activeSnapshot, promotedSnapshot);
                _activeRevision = request.Revision;
                Publish(
                    WallpaperRuntimePhase.Idle,
                    "The official background snapshot is current.",
                    request.Revision);
                result = RuntimeActivationResult.Official(
                    request.Revision,
                    promotedSnapshot,
                    currentSurface);
            }
            else
            {
                if (currentSurface.Kind != WallpaperRuntimeSurfaceKind.MediaActive ||
                    currentSurface.Generation is not { } generation ||
                    currentSurface.PlaybackOwnership is not { } ownership ||
                    _activePlaybackOwnership != ownership ||
                    !IsActive)
                {
                    return null;
                }

                var promotedSurface = WallpaperRuntimeSurface.MediaActive(
                    generation,
                    request.Media!.MediaId,
                    ownership);
                Volatile.Write(ref _activeSnapshot, promotedSnapshot);
                Volatile.Write(ref _surface, promotedSurface);
                _activeRevision = request.Revision;
                Publish(
                    _paused
                        ? WallpaperRuntimePhase.Paused
                        : WallpaperRuntimePhase.Active,
                    _paused
                        ? "Wallpaper remains active and paused."
                        : "Wallpaper remains active.",
                    request.Revision);
                result = RuntimeActivationResult.MediaActive(
                    request.Revision,
                    promotedSnapshot,
                    promotedSurface);
            }

            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<RuntimeActivationResult> ApplyOfficialAsync(
        RuntimeActivationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LaunchMode == RuntimeLaunchMode.EnhancedShortcut)
        {
            var (_, security) = LocateVerifiedPackage();
            var identity = security.Identity!;
            Publish(
                WallpaperRuntimePhase.LaunchingCodex,
                "Launching the reviewed Codex MSIX app with its official background.",
                request.Revision);
            _ = _activationManager.Activate(identity);
        }

        Publish(
            WallpaperRuntimePhase.Stopping,
            "Removing owned wallpaper content.",
            request.Revision);
        await StopInjectedContentAndMediaAsync(cancellationToken).ConfigureAwait(false);

        var activeSnapshot = request.SettingsSnapshot.CreateSnapshot();
        var surface = WallpaperRuntimeSurface.Official();
        Volatile.Write(ref _activeSnapshot, activeSnapshot);
        Volatile.Write(ref _surface, surface);
        _activePlaybackOwnership = null;
        _paused = false;
        Publish(
            WallpaperRuntimePhase.Idle,
            "The official Codex background is selected.",
            request.Revision);
        return RuntimeActivationResult.Official(
            request.Revision,
            activeSnapshot,
            surface);
    }

    private (InstalledCodexPackage Package, CodexSecurityResult Security)
        LocateVerifiedPackage()
    {
        InstalledCodexPackage installedPackage;
        try
        {
            installedPackage = _packageLocator.Locate();
        }
        catch (CodexPackageDiscoveryException exception)
        {
            var discoveryFailure = CodexSecurityResult.Rejected(
                CodexSecurityStage.PackageIdentity,
                CodexSecurityFailureCode.PackageDiscoveryFailed,
                "The reviewed official Codex package could not be discovered.");
            _injectionMonitor.CaptureSecurity(discoveryFailure);
            throw new CodexSecurityValidationException(
                discoveryFailure,
                exception);
        }

        _injectionMonitor.CaptureCodexVersion(installedPackage.Descriptor.Version);
        var security = CodexSecurityValidator.Validate(
            installedPackage.Descriptor,
            CodexRuntimeDescriptor.Current);
        _injectionMonitor.CaptureSecurity(security);
        if (!security.IsVerified)
        {
            throw new CodexSecurityValidationException(security);
        }

        return (installedPackage, security);
    }

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsActive)
            {
                throw new WallpaperNotActiveException();
            }

            await _injectionSession.SetPausedAsync(paused, cancellationToken).ConfigureAwait(false);
            _paused = paused;
            Publish(
                paused ? WallpaperRuntimePhase.Paused : WallpaperRuntimePhase.Active,
                paused ? "Wallpaper video playback is paused." : "Wallpaper is active.",
                _activeRevision);
        }
        catch (Exception exception)
        {
            Publish(WallpaperRuntimePhase.Faulted, exception.Message, _activeRevision);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<RuntimeActivationResult> RestoreOfficialAsync(
        long revision,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish(
                WallpaperRuntimePhase.Stopping,
                "Removing owned wallpaper content.",
                revision);
            await StopInjectedContentAndMediaAsync(cancellationToken).ConfigureAwait(false);
            var surface = WallpaperRuntimeSurface.Official();
            Volatile.Write(ref _activeSnapshot, null);
            Volatile.Write(ref _surface, surface);
            _activePlaybackOwnership = null;
            _activeRevision = revision;
            Publish(
                WallpaperRuntimePhase.Idle,
                "The official Codex background has been restored.",
                revision);
            return RuntimeActivationResult.Canceled(revision, surface);
        }
        catch (Exception exception)
        {
            var error = RuntimeError("restore-official-failed", exception);
            var surface = CreateFaultedSurface(error);
            Volatile.Write(ref _surface, surface);
            _activePlaybackOwnership = surface.PlaybackOwnership;
            Publish(WallpaperRuntimePhase.Faulted, exception.Message, revision);
            return RuntimeActivationResult.Failed(
                revision,
                surface,
                ActiveSnapshot,
                error);
        }
        finally
        {
            _paused = false;
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var injectionFaultTask = _injectionMonitor.StopObserving();

        var failures = new List<Exception>();
        try
        {
            await injectionFaultTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Publish(WallpaperRuntimePhase.Stopping, "Removing owned wallpaper content.");
            try
            {
                await StopInjectedContentAndMediaAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                await _injectionSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                await _playbackPool.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                _ownedTransport?.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            _launchedByThisCoordinator = false;
            _activationProcessId = 0;
            _activationProcessStartTimeUtc = null;
            _endpoint = null;
            _paused = false;
            _activePlaybackOwnership = null;
            Volatile.Write(ref _activeSnapshot, null);
            Volatile.Write(
                ref _surface,
                WallpaperRuntimeSurface.Disconnected(
                    new WallpaperRuntimeError(
                        "runtime-disposed",
                        "Wallpaper runtime is disposed.")));
            Publish(WallpaperRuntimePhase.Disposed, "Wallpaper runtime is disposed.");
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }

        GC.SuppressFinalize(this);
        ThrowCollectedExceptions("One or more wallpaper resources could not be disposed.", failures);
    }

    private async Task<VerifiedCdpEndpoint> DiscoverSingleEndpointAsync(
        VerifiedCodexIdentity identity,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_options.DiscoveryTimeout);
        (CodexSecurityStage Stage, CodexSecurityFailureCode FailureCode)?
            lastDeterministicRejection = null;

        try
        {
            while (true)
            {
                var result = await _endpointDiscovery
                    .DiscoverAsync(identity, timeoutCancellation.Token)
                    .ConfigureAwait(false);
                var deterministicRejection = SelectDeterministicDiscoveryRejection(
                    result.Rejections);
                if (deterministicRejection is not null)
                {
                    lastDeterministicRejection = deterministicRejection;
                }

                var activatedMatches = result.Endpoints
                    .Where(endpoint =>
                        endpoint.Candidate.ProcessId == _activationProcessId &&
                        (_activationProcessStartTimeUtc is null ||
                         endpoint.Candidate.StartTimeUtc == _activationProcessStartTimeUtc))
                    .ToArray();
                if (activatedMatches.Length == 1)
                {
                    return activatedMatches[0];
                }

                if (activatedMatches.Length > 1)
                {
                    throw new AmbiguousCdpEndpointException();
                }

                await Task.Delay(_options.DiscoveryInterval, timeoutCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw lastDeterministicRejection is { } rejection
                ? new CdpEndpointTimeoutException(
                    _options.DiscoveryTimeout,
                    rejection.Stage,
                    rejection.FailureCode)
                : new CdpEndpointTimeoutException(_options.DiscoveryTimeout);
        }
    }

    private (CodexSecurityStage Stage, CodexSecurityFailureCode FailureCode)?
        SelectDeterministicDiscoveryRejection(
            IReadOnlyList<CdpEndpointProbe> rejections)
    {
        ArgumentNullException.ThrowIfNull(rejections);
        // Prefer the rejection that reached the furthest security boundary. The secondary
        // typed-code ordering keeps the result deterministic when several ports fail at the
        // same boundary; free-form probe details never participate or leave the process.
        var mapped = rejections
            .Where(probe =>
                probe is not null &&
                probe.Candidate.ProcessId == _activationProcessId &&
                (_activationProcessStartTimeUtc is null ||
                 probe.Candidate.StartTimeUtc == _activationProcessStartTimeUtc))
            .Select(probe => MapDiscoveryRejection(probe.Rejection))
            .Where(rejection => rejection is not null)
            .Select(rejection => rejection!.Value)
            .OrderByDescending(rejection => rejection.Stage)
            .ThenByDescending(rejection => rejection.FailureCode)
            .ToArray();
        return mapped.Length == 0 ? null : mapped[0];
    }

    private static (CodexSecurityStage Stage, CodexSecurityFailureCode FailureCode)?
        MapDiscoveryRejection(CdpEndpointRejection rejection) => rejection switch
        {
            CdpEndpointRejection.NonLoopbackEndpoint => (
                CodexSecurityStage.LoopbackEndpoint,
                CodexSecurityFailureCode.NonLoopbackEndpoint),
            CdpEndpointRejection.ProcessIdentityMismatch => (
                CodexSecurityStage.ProcessIdentity,
                CodexSecurityFailureCode.ProcessIdentityMismatch),
            CdpEndpointRejection.Unreachable => (
                CodexSecurityStage.LoopbackEndpoint,
                CodexSecurityFailureCode.EndpointUnreachable),
            CdpEndpointRejection.MalformedResponse => (
                CodexSecurityStage.BrowserHandshake,
                CodexSecurityFailureCode.MalformedCdpResponse),
            CdpEndpointRejection.UnexpectedBrowser => (
                CodexSecurityStage.BrowserHandshake,
                CodexSecurityFailureCode.UnexpectedBrowser),
            CdpEndpointRejection.BrowserSocketMismatch => (
                CodexSecurityStage.BrowserHandshake,
                CodexSecurityFailureCode.BrowserSocketMismatch),
            CdpEndpointRejection.NoCodexTarget => (
                CodexSecurityStage.TargetValidation,
                CodexSecurityFailureCode.NoCodexTarget),
            CdpEndpointRejection.TargetSocketMismatch => (
                CodexSecurityStage.TargetValidation,
                CodexSecurityFailureCode.TargetSocketMismatch),
            _ => null,
        };

    private async Task StopInjectedContentAndMediaAsync(CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        _injectionMonitor.ClearActive();
        _endpoint = null;
        try
        {
            await _injectionSession.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            // The page must stop consuming the uploaded file before its pinned read lease is
            // released. Caller cancellation cannot interrupt the second half of that sequence.
            if (_activePlaybackOwnership is { } ownership)
            {
                _ = await _playbackPool
                    .ReleaseOwnedAsync(ownership, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await _playbackPool.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        _paused = false;
        _activePlaybackOwnership = _playbackPool.ActiveOwnership;
        ThrowCollectedExceptions("Wallpaper cleanup failed.", failures);
    }

    private async Task<Exception?> TryStopAfterSecurityRejectionAsync(
        bool cleanupAlreadyAttempted)
    {
        if (cleanupAlreadyAttempted ||
            _injectionMonitor.Compatibility.Security.Status !=
                CodexSecurityStatus.Rejected)
        {
            return null;
        }

        try
        {
            await StopInjectedContentAndMediaAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool IsReviewedCodexProcess(
        CodexProcessSnapshot process,
        VerifiedCodexIdentity identity) =>
        process.ProcessId > 0 &&
        identity.IsKnownExecutable(process.ExecutableName) &&
        string.Equals(process.PackageFamilyName, identity.PackageFamilyName, StringComparison.Ordinal) &&
        string.Equals(process.PackageFullName, identity.PackageFullName, StringComparison.Ordinal) &&
        process.StartTimeUtc != default &&
        process.SessionId == WindowsCodexProcessSnapshotSource.CurrentSessionId;

    private async Task HandleInjectionHealthFaultAsync(long generation)
    {
        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            gateAcquired = true;
            if (Volatile.Read(ref _disposed) != 0 ||
                !_injectionMonitor.IsActiveGeneration(generation))
            {
                return;
            }

            var security = _injectionMonitor.Compatibility.Security;
            if (security.Identity is { } identity)
            {
                _injectionMonitor.CaptureSecurity(CodexSecurityResult.Rejected(
                    CodexSecurityStage.TargetValidation,
                    CodexSecurityFailureCode.TargetRevalidationFailed,
                    "The active Codex target or debugging connection failed revalidation.",
                    identity));
            }

            try
            {
                await StopInjectedContentAndMediaAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                // The in-page lease remains the final restoration path after a broken CDP link.
                var cleanupError = RuntimeError("health-cleanup-failed", cleanupException);
                var surface = CreateFaultedSurface(cleanupError);
                Volatile.Write(ref _surface, surface);
                Volatile.Write(ref _activeSnapshot, null);
                _activePlaybackOwnership = surface.PlaybackOwnership;
                Publish(
                    WallpaperRuntimePhase.Faulted,
                    "The wallpaper heartbeat stopped and cleanup could not be confirmed.",
                    _activeRevision);
                return;
            }

            _paused = false;
            _activePlaybackOwnership = null;
            Volatile.Write(ref _activeSnapshot, null);
            Volatile.Write(
                ref _surface,
                WallpaperRuntimeSurface.Disconnected(
                    new WallpaperRuntimeError(
                        "runtime-disconnected",
                        "The active Codex target or debugging connection was lost.")));
            Publish(
                WallpaperRuntimePhase.Faulted,
                "The wallpaper heartbeat stopped after repeated target or connection failures.",
                _activeRevision);
        }
        catch (ObjectDisposedException)
        {
            // Explicit disposal won the race with this background health transition.
        }
        finally
        {
            if (gateAcquired)
            {
                try
                {
                    _operationGate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Explicit disposal completed after this task acquired the gate.
                }
            }
        }
    }

    private static WallpaperInjectionOptions CreateInjectionOptions(
        long generation,
        IMediaLease mediaLease,
        WallpaperProfile profile) => new(
            generation,
            new UriBuilder(Uri.UriSchemeFile, string.Empty)
            {
                Path = mediaLease.ResolvedPath,
            }.Uri,
            mediaLease.ResolvedPath,
            mediaLease.Metadata.ContentLength,
            mediaLease.Metadata.Kind switch
            {
                MediaKind.Image => WallpaperMediaKind.Image,
                MediaKind.Video => WallpaperMediaKind.Video,
                _ => throw new InvalidOperationException("The validated media has no injectable kind."),
            },
            profile.Fit switch
            {
                WallpaperFit.Cover => WallpaperObjectFit.Cover,
                WallpaperFit.Contain => WallpaperObjectFit.Contain,
                WallpaperFit.Stretch => WallpaperObjectFit.Fill,
                _ => throw new InvalidOperationException("The validated wallpaper fit is not injectable."),
            },
            mediaOpacity: 1,
            glass: new GlassEffectOptions(
                opacity: profile.PanelOpacity,
                blurPixels: profile.BlurPx),
            composition: new WallpaperCompositionOptions(
                profile.FocusX,
                profile.FocusY,
                Math.Min(
                    profile.DarkOverlay,
                    WallpaperCompositionOptions.MaximumOverlayOpacity),
                Math.Min(
                    profile.LightOverlay,
                    WallpaperCompositionOptions.MaximumOverlayOpacity)));

    private static async ValueTask<Exception?> TryDisposeLeaseAsync(IMediaLease? lease)
    {
        if (lease is null)
        {
            return null;
        }

        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private void CaptureTerminalSecurityResult(
        Exception exception,
        VerifiedCodexIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var currentSecurity = _injectionMonitor.Compatibility.Security;
        // Already-terminal results are retained. This also prevents failures after target
        // validation (for example, transferring the validated lease into the playback pool)
        // from rewriting successful security evidence.
        if (currentSecurity.Status != CodexSecurityStatus.InProgress)
        {
            return;
        }

        identity ??= currentSecurity.Identity;
        if (identity is null)
        {
            return;
        }

        var result = exception switch
        {
            OperationCanceledException => CodexSecurityResult.Rejected(
                currentSecurity.Stage,
                CodexSecurityFailureCode.ValidationCanceled,
                "Security validation was canceled before the current stage completed.",
                identity),
            AmbiguousCdpEndpointException => CodexSecurityResult.Rejected(
                CodexSecurityStage.LoopbackEndpoint,
                CodexSecurityFailureCode.AmbiguousEndpoint,
                "More than one verified Codex loopback endpoint was discovered.",
                identity),
            CdpEndpointTimeoutException timeoutException => CodexSecurityResult.Rejected(
                timeoutException.SecurityStage,
                timeoutException.FailureCode,
                timeoutException.FailureCode ==
                    CodexSecurityFailureCode.EndpointDiscoveryTimedOut
                    ? "Codex did not publish one verified loopback endpoint before the timeout."
                    : "Codex endpoint discovery ended with a typed security rejection.",
                identity),
            WallpaperBrowserHandshakeException => CodexSecurityResult.Rejected(
                CodexSecurityStage.BrowserHandshake,
                CodexSecurityFailureCode.EndpointUnreachable,
                "The verified Codex browser WebSocket could not be connected.",
                identity),
            WallpaperTargetAmbiguityException => CodexSecurityResult.Rejected(
                CodexSecurityStage.TargetValidation,
                CodexSecurityFailureCode.AmbiguousTarget,
                "More than one verified Codex work-page target was present.",
                identity),
            FinalPageApplyTimeoutException or
            WallpaperPresentationContractException or
            WallpaperMediaLoadException =>
                CodexSecurityResult.Verified(
                    identity,
                    CodexSecurityStage.TargetValidation,
                    "The unique Codex target passed security validation."),
            WallpaperInjectionException => CodexSecurityResult.Rejected(
                CodexSecurityStage.TargetValidation,
                CodexSecurityFailureCode.NoVerifiedTarget,
                "The verified endpoint did not expose one usable Codex work-page target.",
                identity),
            _ => RejectAtCurrentSecurityStage(identity),
        };

        _injectionMonitor.CaptureSecurity(result);
    }

    private CodexSecurityResult RejectAtCurrentSecurityStage(
        VerifiedCodexIdentity identity)
    {
        var stage = _injectionMonitor.Compatibility.Security.Stage;
        return stage switch
        {
            CodexSecurityStage.LoopbackEndpoint or CodexSecurityStage.BrowserHandshake =>
                CodexSecurityResult.Rejected(
                    stage,
                    CodexSecurityFailureCode.EndpointUnreachable,
                    "The verified Codex debugging endpoint could not be used.",
                    identity),
            CodexSecurityStage.TargetValidation => CodexSecurityResult.Rejected(
                stage,
                CodexSecurityFailureCode.TargetRevalidationFailed,
                "The Codex target could not be revalidated.",
                identity),
            _ => CodexSecurityResult.Rejected(
                CodexSecurityStage.ProcessIdentity,
                CodexSecurityFailureCode.NoVerifiedProcess,
                "The activated Codex process could not be verified.",
                identity),
        };
    }

    private static WallpaperRuntimeError RuntimeError(
        string code,
        Exception exception) =>
        WallpaperRuntimeError.FromException(code, exception);

    private WallpaperRuntimeSurface CreateFaultedSurface(
        WallpaperRuntimeError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var activeLease = _playbackPool.ActiveLease;
        var ownership = _playbackPool.ActiveOwnership;
        var mediaId = activeLease?.Reference.MediaId;
        if (activeLease is null)
        {
            ownership = null;
        }

        var ownsInjection = _injectionSession.IsActive;
        return WallpaperRuntimeSurface.Faulted(
            error,
            generation: ownsInjection && _generation > 0
                ? _generation
                : null,
            mediaId: ownership is not null ? mediaId : null,
            playbackOwnership: ownership,
            ownsInjection);
    }

    private void Publish(
        WallpaperRuntimePhase phase,
        string detail,
        long revision = 0)
    {
        var status = new WallpaperRuntimeStatusChangedEventArgs(phase, detail, revision);
        Status = status;
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<WallpaperRuntimeStatusChangedEventArgs> handler in
                 handlers.GetInvocationList())
        {
            try
            {
                handler(this, status);
            }
            catch (Exception)
            {
                // Status observers cannot be allowed to interrupt lifecycle or safety cleanup.
            }
        }
    }

    private static void ThrowCollectedExceptions(string message, List<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(message, failures);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

public sealed class CdpRiskNotAcceptedException : InvalidOperationException
{
    public CdpRiskNotAcceptedException()
        : base("The local Chromium debugging risk acknowledgement is required.")
    {
    }
}

public sealed class WallpaperNotActiveException : InvalidOperationException
{
    public WallpaperNotActiveException()
        : base("The wallpaper is not active.")
    {
    }
}

public sealed class CodexAlreadyRunningException : InvalidOperationException
{
    public CodexAlreadyRunningException()
        : base("Codex is already running and was not launched by this coordinator.")
    {
    }
}

public sealed class CodexSecurityValidationException : InvalidOperationException
{
    public CodexSecurityValidationException(
        CodexSecurityResult result,
        Exception? innerException = null)
        : base(result?.Reason, innerException)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public CodexSecurityResult Result { get; }
}

public sealed class AmbiguousCdpEndpointException : InvalidOperationException
{
    public AmbiguousCdpEndpointException()
        : base("More than one verified Codex debugging endpoint was discovered.")
    {
    }
}

public sealed class CdpEndpointTimeoutException : TimeoutException
{
    public CdpEndpointTimeoutException(TimeSpan timeout)
        : this(
            timeout,
            CodexSecurityStage.LoopbackEndpoint,
            CodexSecurityFailureCode.EndpointDiscoveryTimedOut)
    {
    }

    public CdpEndpointTimeoutException(
        TimeSpan timeout,
        CodexSecurityStage securityStage,
        CodexSecurityFailureCode failureCode)
        : base($"Codex did not publish a verified debugging endpoint within {timeout.TotalSeconds:N0} seconds.")
    {
        if (!Enum.IsDefined(securityStage) ||
            securityStage == CodexSecurityStage.None)
        {
            throw new ArgumentOutOfRangeException(nameof(securityStage));
        }

        if (!Enum.IsDefined(failureCode) ||
            failureCode == CodexSecurityFailureCode.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failureCode));
        }

        SecurityStage = securityStage;
        FailureCode = failureCode;
    }

    public CodexSecurityStage SecurityStage { get; }

    public CodexSecurityFailureCode FailureCode { get; }
}

public sealed class SettingsRecoveryRequiredException : InvalidOperationException
{
    public SettingsRecoveryRequiredException(
        SettingsRecoveryReason reason,
        bool hasVersion1Backup)
        : base("Wallpaper settings require explicit recovery before they can be changed.")
    {
        Reason = reason;
        HasVersion1Backup = hasVersion1Backup;
    }

    public SettingsRecoveryReason Reason { get; }

    public bool HasVersion1Backup { get; }
}

public sealed class FutureSettingsVersionException : InvalidOperationException
{
    public FutureSettingsVersionException(
        int schemaVersion,
        bool hasVersion1Backup = false)
        : base($"Settings schema {schemaVersion} is newer than this application can safely edit.")
    {
        SchemaVersion = schemaVersion;
        HasVersion1Backup = hasVersion1Backup;
    }

    public int SchemaVersion { get; }

    public bool HasVersion1Backup { get; }
}
