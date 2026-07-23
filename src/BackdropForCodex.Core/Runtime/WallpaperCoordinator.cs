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

/// <summary>
/// Owns the complete enhanced-launch lifecycle. It never terminates Codex and never attaches to a
/// Codex process that predates this coordinator instance.
/// </summary>
public sealed class WallpaperCoordinator : IAsyncDisposable
{
    public const string RemoteDebuggingArguments =
        "--remote-debugging-address=127.0.0.1 --remote-debugging-port=0";

    private readonly IInstalledCodexPackageLocator _packageLocator;
    private readonly ICodexProcessSnapshotSource _processSource;
    private readonly IApplicationActivationManager _activationManager;
    private readonly ICdpEndpointDiscoveryService _endpointDiscovery;
    private readonly IMediaFileInspector _mediaInspector;
    private readonly ILoopbackMediaServer _mediaServer;
    private readonly IWallpaperInjectionSession _injectionSession;
    private readonly IWallpaperInjectionHealthSource? _injectionHealthSource;
    private readonly ISettingsStore _settingsStore;
    private readonly WallpaperCoordinatorOptions _options;
    private readonly IDisposable? _ownedTransport;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _backgroundTaskSync = new();
    private Task _injectionFaultTask = Task.CompletedTask;
    private VerifiedCdpEndpoint? _endpoint;
    private uint _activationProcessId;
    private DateTimeOffset? _activationProcessStartTimeUtc;
    private long _generation;
    private bool _launchedByThisCoordinator;
    private bool _paused;
    private int _disposed;

    public WallpaperCoordinator(
        IInstalledCodexPackageLocator packageLocator,
        ICodexProcessSnapshotSource processSource,
        IApplicationActivationManager activationManager,
        ICdpEndpointDiscoveryService endpointDiscovery,
        IMediaFileInspector mediaInspector,
        ILoopbackMediaServer mediaServer,
        IWallpaperInjectionSession injectionSession,
        ISettingsStore settingsStore,
        WallpaperCoordinatorOptions? options = null)
        : this(
            packageLocator,
            processSource,
            activationManager,
            endpointDiscovery,
            mediaInspector,
            mediaServer,
            injectionSession,
            settingsStore,
            options,
            ownedTransport: null)
    {
    }

    private WallpaperCoordinator(
        IInstalledCodexPackageLocator packageLocator,
        ICodexProcessSnapshotSource processSource,
        IApplicationActivationManager activationManager,
        ICdpEndpointDiscoveryService endpointDiscovery,
        IMediaFileInspector mediaInspector,
        ILoopbackMediaServer mediaServer,
        IWallpaperInjectionSession injectionSession,
        ISettingsStore settingsStore,
        WallpaperCoordinatorOptions? options,
        IDisposable? ownedTransport)
    {
        _packageLocator = packageLocator ?? throw new ArgumentNullException(nameof(packageLocator));
        _processSource = processSource ?? throw new ArgumentNullException(nameof(processSource));
        _activationManager = activationManager ?? throw new ArgumentNullException(nameof(activationManager));
        _endpointDiscovery = endpointDiscovery ?? throw new ArgumentNullException(nameof(endpointDiscovery));
        _mediaInspector = mediaInspector ?? throw new ArgumentNullException(nameof(mediaInspector));
        _mediaServer = mediaServer ?? throw new ArgumentNullException(nameof(mediaServer));
        _injectionSession = injectionSession ?? throw new ArgumentNullException(nameof(injectionSession));
        _injectionHealthSource = injectionSession as IWallpaperInjectionHealthSource;
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _options = options ?? WallpaperCoordinatorOptions.Default;
        _options.Validate();
        _ownedTransport = ownedTransport;
        if (_injectionHealthSource is not null)
        {
            _injectionHealthSource.HealthFaulted += InjectionSession_HealthFaulted;
        }

        Status = new WallpaperRuntimeStatusChangedEventArgs(
            WallpaperRuntimePhase.Idle,
            "Wallpaper runtime is idle.");
    }

    public event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? StatusChanged;

    public WallpaperRuntimeStatusChangedEventArgs Status { get; private set; }

    public bool IsActive => _injectionSession.IsActive;

    public bool IsPaused => _paused;

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
            new MediaFileInspector(),
            new LoopbackMediaServer(),
            new PuppeteerWallpaperSession(),
            new SettingsStore(settingsPath),
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
            return await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
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
        var snapshot = settings.Snapshot();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await _settingsStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return snapshot;
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
        try
        {
            ThrowIfDisposed();
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
            var metadata = await _mediaInspector
                .InspectAsync(mediaPath, cancellationToken)
                .ConfigureAwait(false);
            var installedPackage = _packageLocator.Locate();
            var compatibility = CodexCompatibilityCatalog.Evaluate(
                installedPackage.Descriptor,
                CodexRuntimeDescriptor.Current);
            if (!compatibility.IsSupported)
            {
                throw new UnsupportedCodexVersionException(compatibility);
            }

            var profile = compatibility.Profile!;
            var settings = (requestedSettings with
            {
                MediaPath = mediaPath,
                MediaKind = metadata.Kind,
                LastCompatibilityProfileId = profile.Id,
            }).AddRecentMediaPath(mediaPath);
            settings.Validate();
            await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);

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
                var mediaEndpoint = await _mediaServer
                    .StartAsync(mediaPath, cancellationToken)
                    .ConfigureAwait(false);

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
                var injectionOptions = CreateInjectionOptions(
                    checked(++_generation),
                    mediaEndpoint,
                    settings);
                await _injectionSession
                    .ApplyAsync(_endpoint, injectionOptions, cancellationToken)
                    .ConfigureAwait(false);
                // Pause belongs to one injected media generation. A replacement starts from its
                // own default playback state and must not inherit a stale pause from the prior video.
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Publish(WallpaperRuntimePhase.Faulted, "The wallpaper operation was cancelled.");
            throw;
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

    public async Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_injectionSession.IsActive)
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

        if (_injectionHealthSource is not null)
        {
            _injectionHealthSource.HealthFaulted -= InjectionSession_HealthFaulted;
        }

        Task injectionFaultTask;
        lock (_backgroundTaskSync)
        {
            injectionFaultTask = _injectionFaultTask;
        }

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
                await _mediaServer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                _settingsStore.Dispose();
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
            // Once cleanup starts, caller cancellation must not leave the local media server up.
            await _mediaServer.StopAsync(CancellationToken.None).ConfigureAwait(false);
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

    private void InjectionSession_HealthFaulted(
        object? sender,
        WallpaperInjectionHealthFaultedEventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_backgroundTaskSync)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_injectionFaultTask.IsCompleted)
            {
                return;
            }

            _injectionFaultTask = Task.Run(
                () => HandleInjectionHealthFaultAsync(eventArgs.Generation));
        }
    }

    private async Task HandleInjectionHealthFaultAsync(long generation)
    {
        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            gateAcquired = true;
            if (Volatile.Read(ref _disposed) != 0 ||
                _injectionSession.Generation != generation)
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
        LoopbackMediaEndpoint endpoint,
        SettingsV1 settings) => new(
            generation,
            endpoint.Uri,
            settings.MediaPath ?? throw new SettingsValidationException(
                ["A wallpaper media path is required for injection."]),
            endpoint.ContentLength,
            endpoint.Kind switch
            {
                MediaKind.Image => WallpaperMediaKind.Image,
                MediaKind.Video => WallpaperMediaKind.Video,
                _ => throw new InvalidOperationException("The validated media has no injectable kind."),
            },
            settings.Fit == WallpaperFit.Cover
                ? WallpaperObjectFit.Cover
                : WallpaperObjectFit.Contain,
            mediaOpacity: 1,
            glass: new GlassEffectOptions(
                opacity: settings.PanelOpacity,
                blurPixels: settings.BlurPx));

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
