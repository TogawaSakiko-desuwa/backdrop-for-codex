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
    public WallpaperRuntimeStatusChangedEventArgs(WallpaperRuntimePhase phase, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Phase = phase;
        Detail = detail;
    }

    public WallpaperRuntimePhase Phase { get; }

    public string Detail { get; }
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
        CodexCompatibilityProfile profile,
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
        CodexCompatibilityProfile profile,
        CancellationToken cancellationToken = default) =>
        _discovery.DiscoverAsync(profile, cancellationToken);
}

public interface IWallpaperRuntime : IAsyncDisposable
{
    event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? StatusChanged;

    WallpaperRuntimeStatusChangedEventArgs Status { get; }

    bool IsActive { get; }

    bool IsPaused { get; }

    Task<SettingsV1> LoadSettingsAsync(CancellationToken cancellationToken = default);

    Task<SettingsV1> SaveSettingsAsync(
        SettingsV1 settings,
        CancellationToken cancellationToken = default);

    Task<SettingsV1> StartOrUpdateAsync(
        SettingsV1 requestedSettings,
        CancellationToken cancellationToken = default);

    Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default);

    Task DisableAsync(CancellationToken cancellationToken = default);
}

public interface IWallpaperRuntimeCapabilitySource
{
    event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>? CapabilitiesChanged;

    CompatibilityCapabilities Capabilities { get; }
}

public interface IWallpaperSettingsRecoveryRuntime
{
    Task<SettingsV1> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken = default);

    Task<SettingsV1> ResetSettingsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the complete enhanced-launch lifecycle. It never terminates Codex and never attaches to a
/// Codex process that predates this coordinator instance.
/// </summary>
public sealed class WallpaperCoordinator :
    IWallpaperRuntime,
    IWallpaperRuntimeCapabilitySource,
    IWallpaperSettingsRecoveryRuntime
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
    private readonly ISettingsRepository _settingsRepository;
    private readonly WallpaperCoordinatorOptions _options;
    private readonly IDisposable? _ownedTransport;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private VerifiedCdpEndpoint? _endpoint;
    private uint _activationProcessId;
    private DateTimeOffset? _activationProcessStartTimeUtc;
    private SettingsV2? _settingsSnapshot;
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
        ISettingsRepository settingsRepository,
        WallpaperCoordinatorOptions? options = null)
        : this(
            packageLocator,
            processSource,
            activationManager,
            endpointDiscovery,
            mediaSourceProvider,
            playbackPool,
            injectionSession,
            settingsRepository,
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
        ISettingsRepository settingsRepository,
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
        _settingsRepository = settingsRepository ??
            throw new ArgumentNullException(nameof(settingsRepository));
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

    public CompatibilityCapabilities Capabilities => _injectionMonitor.Capabilities;

    public static WallpaperCoordinator CreateDefault(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
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
            new SettingsRepository(settingsPath),
            WallpaperCoordinatorOptions.Default,
            transport);
    }

    public async Task<SettingsV1> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var settings = await GetSettingsSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return ProjectGlobalForLegacyEditor(settings);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SettingsV1> SaveSettingsAsync(
        SettingsV1 settings,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.SnapshotForSave();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var saved = await SaveGlobalSettingsAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
            return ProjectGlobalForLegacyEditor(saved);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SettingsV1> ResetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _settingsSnapshot = await _settingsRepository
                .ResetAsync(cancellationToken)
                .ConfigureAwait(false);
            _injectionMonitor.ResetCapabilities();
            return ProjectGlobalForLegacyEditor(_settingsSnapshot);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SettingsV1> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var loadResult = await _settingsRepository
                .RestoreVersion1BackupAsync(cancellationToken)
                .ConfigureAwait(false);
            _settingsSnapshot = loadResult switch
            {
                SettingsLoadResult.Ready ready => ready.Settings,
                SettingsLoadResult.RecoveryRequired recovery =>
                    throw new SettingsRecoveryRequiredException(
                        recovery.Reason,
                        recovery.HasVersion1Backup),
                SettingsLoadResult.FutureReadOnly future =>
                    throw new FutureSettingsVersionException(
                        future.SchemaVersion,
                        future.HasVersion1Backup),
                _ => throw new InvalidOperationException(
                    "The settings repository returned an unknown recovery state."),
            };
            return ProjectGlobalForLegacyEditor(_settingsSnapshot);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<SettingsV1> StartOrUpdateAsync(
        SettingsV1 requestedSettings,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(requestedSettings);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IMediaLease? pendingLease = null;
        try
        {
            ThrowIfDisposed();
            _injectionMonitor.ResetCapabilities();
            Publish(WallpaperRuntimePhase.Validating, "Validating the Codex package and media file.");

            if (!requestedSettings.AcceptedCdpRisk)
            {
                throw new CdpRiskNotAcceptedException();
            }

            if (string.IsNullOrWhiteSpace(requestedSettings.MediaPath))
            {
                throw new MediaValidationException("A wallpaper media file must be selected.");
            }

            var mediaPath = Path.GetFullPath(requestedSettings.MediaPath);
            var installedPackage = _packageLocator.Locate();
            var compatibility = CodexCompatibilityCatalog.Evaluate(
                installedPackage.Descriptor,
                CodexRuntimeDescriptor.Current);
            _injectionMonitor.CaptureCapabilities(compatibility.Capabilities);
            if (!compatibility.IsSupported)
            {
                throw new UnsupportedCodexVersionException(compatibility);
            }

            var profile = compatibility.Profile!;
            var mediaReference = new MediaReference
            {
                MediaId = Guid.CreateVersion7(),
                SourceKind = MediaSourceKind.LocalFile,
                SourceIdentifier = mediaPath,
                LastKnownKind = requestedSettings.MediaKind,
            }.Snapshot();
            pendingLease = await _mediaSourceProvider
                .AcquireLeaseAsync(mediaReference, cancellationToken)
                .ConfigureAwait(false);

            var settings = (requestedSettings with
            {
                MediaPath = mediaPath,
                MediaKind = pendingLease.Metadata.Kind,
                LastCompatibilityProfileId = profile.Id,
            })
                .AddRecentMediaPath(mediaPath)
                .SnapshotForSave();
            var persistedSettings = await SaveGlobalSettingsAsync(settings, cancellationToken)
                .ConfigureAwait(false);
            settings = ProjectGlobalForLegacyEditor(persistedSettings);

            var processes = await _processSource
                .GetProcessesAsync(cancellationToken)
                .ConfigureAwait(false);
            var reviewedProcesses = processes
                .Where(process => IsReviewedCodexProcess(process, profile))
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
                throw new CodexAlreadyRunningException();
            }

            try
            {
                if (!_launchedByThisCoordinator && !_injectionSession.IsActive)
                {
                    Publish(WallpaperRuntimePhase.LaunchingCodex, "Launching the reviewed Codex MSIX app.");
                    var activation = _activationManager.Activate(profile, RemoteDebuggingArguments);
                    _activationProcessId = activation.ProcessId;
                    _activationProcessStartTimeUtc = null;
                    _launchedByThisCoordinator = true;
                }

                if (_endpoint is null || !_injectionSession.IsActive)
                {
                    Publish(
                        WallpaperRuntimePhase.DiscoveringEndpoint,
                        "Waiting for Codex to publish its loopback debugging endpoint.");
                    _endpoint = await DiscoverSingleEndpointAsync(profile, cancellationToken)
                        .ConfigureAwait(false);
                    _activationProcessStartTimeUtc = _endpoint.Candidate.StartTimeUtc;
                }

                Publish(WallpaperRuntimePhase.Applying, "Applying the wallpaper to the reviewed Codex page.");
                var leaseToActivate = pendingLease ??
                    throw new InvalidOperationException("No validated media lease is available.");
                var injectionOptions = CreateInjectionOptions(
                    checked(++_generation),
                    leaseToActivate,
                    settings);
                _injectionMonitor.BeginCapabilityObservation(injectionOptions.Generation);
                await _injectionSession
                    .ApplyAsync(_endpoint, injectionOptions, cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    await _playbackPool
                        .ActivateAsync(leaseToActivate, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    // ActivateAsync transfers ownership when the slot points at the new lease,
                    // including when disposing the previous slot reports a failure.
                    if (ReferenceEquals(_playbackPool.ActiveLease, leaseToActivate))
                    {
                        pendingLease = null;
                    }
                }

                // Pause belongs to one injected media generation. A replacement starts from its
                // own default playback state and must not inherit a stale pause from the prior video.
                _injectionMonitor.MarkActive(injectionOptions.Generation);
                _paused = false;
                Publish(WallpaperRuntimePhase.Active, "Wallpaper is active.");
                return settings;
            }
            catch (Exception operationException)
            {
                try
                {
                    await StopInjectedContentAndMediaAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "The wallpaper operation and its safety cleanup both failed.",
                        operationException,
                        cleanupException);
                }

                throw;
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            Publish(WallpaperRuntimePhase.Faulted, "The wallpaper operation was cancelled.");
            var disposalFailure = await TryDisposeLeaseAsync(pendingLease).ConfigureAwait(false);
            pendingLease = null;
            if (disposalFailure is not null)
            {
                throw new AggregateException(
                    "The wallpaper operation was cancelled and its pending media lease could not be released.",
                    exception,
                    disposalFailure);
            }

            throw;
        }
        catch (Exception exception)
        {
            Publish(WallpaperRuntimePhase.Faulted, exception.Message);
            var disposalFailure = await TryDisposeLeaseAsync(pendingLease).ConfigureAwait(false);
            pendingLease = null;
            if (disposalFailure is not null)
            {
                throw new AggregateException(
                    "The wallpaper operation and pending media lease cleanup both failed.",
                    exception,
                    disposalFailure);
            }

            throw;
        }
        finally
        {
            _operationGate.Release();
        }
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
                paused ? "Wallpaper video playback is paused." : "Wallpaper is active.");
        }
        catch (Exception exception)
        {
            Publish(WallpaperRuntimePhase.Faulted, exception.Message);
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _injectionMonitor.ResetCapabilities();
            Publish(WallpaperRuntimePhase.Stopping, "Removing owned wallpaper content.");
            await StopInjectedContentAndMediaAsync(cancellationToken).ConfigureAwait(false);
            Publish(WallpaperRuntimePhase.Idle, "The official Codex background has been restored.");
        }
        catch (Exception exception)
        {
            Publish(WallpaperRuntimePhase.Faulted, exception.Message);
            throw;
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
                _settingsRepository.Dispose();
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

    private async Task<SettingsV2> GetSettingsSnapshotAsync(CancellationToken cancellationToken)
    {
        var loadResult = await _settingsRepository
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        _settingsSnapshot = loadResult switch
        {
            SettingsLoadResult.Ready ready => ready.Settings,
            SettingsLoadResult.RecoveryRequired recovery =>
                throw new SettingsRecoveryRequiredException(
                    recovery.Reason,
                    recovery.HasVersion1Backup),
            SettingsLoadResult.FutureReadOnly future =>
                throw new FutureSettingsVersionException(
                    future.SchemaVersion,
                    future.HasVersion1Backup),
            _ => throw new InvalidOperationException("The settings repository returned an unknown state."),
        };
        return _settingsSnapshot;
    }

    private async Task<SettingsV2> SaveGlobalSettingsAsync(
        SettingsV1 globalSettings,
        CancellationToken cancellationToken)
    {
        var current = await GetSettingsSnapshotAsync(cancellationToken).ConfigureAwait(false);
        // The 1.3 editor is intentionally local-file/Global only. Refuse to write through its
        // lossy V1 facade when the current V2 Global selection cannot be represented.
        _ = ProjectGlobalForLegacyEditor(current);
        var updated = SettingsV1Projection.ApplyGlobal(current, globalSettings) with
        {
            AcceptedCdpRisk = globalSettings.AcceptedCdpRisk,
            LastCompatibilityProfileId = globalSettings.LastCompatibilityProfileId,
        };
        var saved = await _settingsRepository
            .SaveAsync(updated, cancellationToken)
            .ConfigureAwait(false);
        _settingsSnapshot = saved;
        return saved;
    }

    private SettingsV1 ProjectGlobalForLegacyEditor(SettingsV2 settings)
    {
        try
        {
            return SettingsV1Projection.ProjectGlobal(settings);
        }
        catch (SettingsProjectionException exception)
        {
            var hasVersion1Backup = _settingsRepository.HasVersion1Backup;
            if (exception.HasVersion1Backup == hasVersion1Backup)
            {
                throw;
            }

            throw new SettingsProjectionException(
                exception.Message,
                hasVersion1Backup,
                exception);
        }
    }

    private async Task<VerifiedCdpEndpoint> DiscoverSingleEndpointAsync(
        CodexCompatibilityProfile profile,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_options.DiscoveryTimeout);

        try
        {
            while (true)
            {
                var result = await _endpointDiscovery
                    .DiscoverAsync(profile, timeoutCancellation.Token)
                    .ConfigureAwait(false);
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
            throw new CdpEndpointTimeoutException(_options.DiscoveryTimeout);
        }
    }

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
            await _playbackPool.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        _paused = false;
        ThrowCollectedExceptions("Wallpaper cleanup failed.", failures);
    }

    private static bool IsReviewedCodexProcess(
        CodexProcessSnapshot process,
        CodexCompatibilityProfile profile) =>
        process.ProcessId > 0 &&
        profile.IsKnownExecutable(process.ExecutableName) &&
        string.Equals(process.PackageFamilyName, profile.PackageFamilyName, StringComparison.Ordinal) &&
        string.Equals(process.PackageFullName, profile.PackageFullName, StringComparison.Ordinal) &&
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

            try
            {
                await StopInjectedContentAndMediaAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The in-page lease remains the final restoration path after a broken CDP link.
            }

            _paused = false;
            Publish(
                WallpaperRuntimePhase.Faulted,
                "The wallpaper heartbeat stopped after repeated target or connection failures.");
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
        SettingsV1 settings) => new(
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
            settings.Fit switch
            {
                WallpaperFit.Cover => WallpaperObjectFit.Cover,
                WallpaperFit.Contain => WallpaperObjectFit.Contain,
                WallpaperFit.Stretch => WallpaperObjectFit.Fill,
                _ => throw new InvalidOperationException("The validated wallpaper fit is not injectable."),
            },
            mediaOpacity: 1,
            glass: new GlassEffectOptions(
                opacity: settings.PanelOpacity,
                blurPixels: settings.BlurPx),
            composition: new WallpaperCompositionOptions(
                settings.FocusX,
                settings.FocusY,
                Math.Min(
                    settings.DarkOverlay,
                    WallpaperCompositionOptions.MaximumOverlayOpacity),
                Math.Min(
                    settings.LightOverlay,
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

    private void Publish(WallpaperRuntimePhase phase, string detail)
    {
        var status = new WallpaperRuntimeStatusChangedEventArgs(phase, detail);
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

public sealed class UnsupportedCodexVersionException : InvalidOperationException
{
    public UnsupportedCodexVersionException(CodexCompatibilityResult result)
        : base(result?.Reason)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public CodexCompatibilityResult Result { get; }
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
        : base($"Codex did not publish a verified debugging endpoint within {timeout.TotalSeconds:N0} seconds.")
    {
    }
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
