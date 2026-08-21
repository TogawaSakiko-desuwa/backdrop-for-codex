using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using System.Runtime.ExceptionServices;

namespace BackdropForCodex.Core.Dynamic;

/// <summary>
/// Adds the validated Wallpaper Engine process and crash-recovery preflight to the capture,
/// encoder and page-stream activation pipeline. Platform-specific failures are translated into
/// stable, path-free capability reasons at this boundary.
/// </summary>
internal sealed class WallpaperEngineDynamicRuntime :
    IDynamicWallpaperActivationFactory,
    IAsyncDisposable
{
    private readonly IWallpaperEngineInstallationLocator _installationLocator;
    private readonly IWallpaperEngineControlClient _controlClient;
    private readonly IWallpaperEngineOwnedWindowRecovery _recovery;
    private readonly IDynamicWallpaperActivationFactory _inner;
    private readonly IWallpaperEngineAudioIsolation? _audioIsolation;
    private readonly IDisposable? _ownedResource;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private WallpaperEngineOwnedWindowRecoveryResult? _initialRecovery;
    private bool _innerDisposed;
    private bool _ownedResourceDisposed;
    private int _disposeStarted;
    private int _disposed;

    internal WallpaperEngineDynamicRuntime(
        IWallpaperEngineInstallationLocator installationLocator,
        IWallpaperEngineControlClient controlClient,
        IWallpaperEngineOwnedWindowRecovery recovery,
        IDynamicWallpaperActivationFactory inner,
        IDisposable? ownedResource = null,
        IWallpaperEngineAudioIsolation? audioIsolation = null)
    {
        _installationLocator = installationLocator ??
            throw new ArgumentNullException(nameof(installationLocator));
        _controlClient = controlClient ?? throw new ArgumentNullException(nameof(controlClient));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ownedResource = ownedResource;
        _audioIsolation = audioIsolation;
    }

    internal static WallpaperEngineDynamicRuntime CreateDefault(
        IWallpaperEngineInstallationLocator installationLocator)
    {
        ArgumentNullException.ThrowIfNull(installationLocator);
        var controlClient = new WallpaperEngineControlClient();
        var journal = new WindowsWallpaperEngineOwnedWindowJournal();
        var windowVerifier = new WindowsWallpaperEngineOwnedWindowVerifier();
        var recovery = new WallpaperEngineOwnedWindowRecovery(
            installationLocator,
            controlClient,
            windowVerifier,
            journal);
        var renderer = new WallpaperEngineWindowRenderer(
            installationLocator,
            controlClient,
            windowVerifier,
            journal,
            new WindowsWallpaperEnginePopOutPlacement());
        var activation = new DynamicWallpaperActivationFactory(
            renderer,
            new WindowsGraphicsCaptureFactory(),
            new MediaFoundationFragmentedMp4EncoderFactory(),
            new PuppeteerDynamicWallpaperPageSessionFactory());
        return new WallpaperEngineDynamicRuntime(
            installationLocator,
            controlClient,
            recovery,
            activation,
            journal);
    }

    public async ValueTask<DynamicWallpaperCapability> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposalStarted();
        try
        {
            var installation = await _installationLocator
                .LocateAsync(cancellationToken)
                .ConfigureAwait(false);
            await _controlClient
                .EnsureRunningAsync(installation, cancellationToken)
                .ConfigureAwait(false);
            var recovery = await RecoverInitialOwnedWindowsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (recovery.State is WallpaperEngineOwnedWindowRecoveryState.PartiallyRecovered or
                WallpaperEngineOwnedWindowRecoveryState.Deferred)
            {
                return DynamicWallpaperCapability.Unavailable(
                    DynamicWallpaperCapabilityReasonCode.WallpaperEngineRecoveryUnavailable);
            }

            if (_audioIsolation is not null)
            {
                var audioCapability = await ProbeInitialAudioSilenceAsync(
                        installation,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!audioCapability.IsAvailable)
                {
                    return audioCapability;
                }
            }

            return await _inner.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WallpaperEngineUnavailableException exception)
        {
            return DynamicWallpaperCapability.Unavailable(MapInstallationFailure(exception));
        }
        catch (WallpaperEnginePlatformUnavailableException exception)
        {
            return DynamicWallpaperCapability.Unavailable(MapPlatformFailure(exception));
        }
    }

    public async ValueTask<DynamicWallpaperActivationResult> ActivateAsync(
        DynamicWallpaperActivationRequest request,
        IWallpaperEngineProjectLease projectLease,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposalStarted();
        try
        {
            return await _inner
                .ActivateAsync(request, projectLease, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WallpaperEngineUnavailableException exception)
        {
            throw new DynamicWallpaperUnavailableException(
                MapInstallationFailure(exception),
                exception);
        }
        catch (WallpaperEnginePlatformUnavailableException exception)
        {
            throw new DynamicWallpaperUnavailableException(
                MapPlatformFailure(exception),
                exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref _disposeStarted, 1);
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            List<Exception>? failures = null;
            if (!_innerDisposed)
            {
                if (_inner is IAsyncDisposable disposableInner)
                {
                    try
                    {
                        await disposableInner.DisposeAsync().ConfigureAwait(false);
                        _innerDisposed = true;
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }
                else
                {
                    _innerDisposed = true;
                }
            }

            if (!_ownedResourceDisposed)
            {
                try
                {
                    _ownedResource?.Dispose();
                    _ownedResourceDisposed = true;
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            if (_innerDisposed && _ownedResourceDisposed)
            {
                Volatile.Write(ref _disposed, 1);
                GC.SuppressFinalize(this);
            }

            if (failures is [var failure])
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(
                    "One or more dynamic runtime resources could not be released.",
                    failures);
            }
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private void ThrowIfDisposalStarted() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0 ||
            Volatile.Read(ref _disposed) != 0,
            this);

    private static DynamicWallpaperCapabilityReasonCode MapInstallationFailure(
        WallpaperEngineUnavailableException exception) => exception.Reason switch
        {
            WallpaperEngineAvailabilityReason.UnsupportedPlatform =>
                DynamicWallpaperCapabilityReasonCode.UnsupportedOperatingSystem,
            _ => DynamicWallpaperCapabilityReasonCode.WallpaperEngineUnavailable,
        };

    private async ValueTask<WallpaperEngineOwnedWindowRecoveryResult>
        RecoverInitialOwnedWindowsAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _initialRecovery);
        if (cached is not null)
        {
            return cached;
        }

        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _initialRecovery;
            if (cached is not null)
            {
                return cached;
            }

            cached = await _recovery
                .RecoverAsync(cancellationToken)
                .ConfigureAwait(false);
            if (cached.State is WallpaperEngineOwnedWindowRecoveryState.NothingToRecover or
                WallpaperEngineOwnedWindowRecoveryState.Recovered)
            {
                Volatile.Write(ref _initialRecovery, cached);
            }

            return cached;
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private static DynamicWallpaperCapabilityReasonCode MapPlatformFailure(
        WallpaperEnginePlatformUnavailableException exception) => exception.Reason switch
        {
            WallpaperEnginePlatformUnavailableReason.StartupNotProven =>
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineNotRunning,
            WallpaperEnginePlatformUnavailableReason.RecoveryJournalInvalid =>
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineRecoveryUnavailable,
            WallpaperEnginePlatformUnavailableReason.InitialAudioSilenceNotProven =>
                DynamicWallpaperCapabilityReasonCode.InitialAudioSilenceNotProven,
            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven =>
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable,
            WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven =>
                DynamicWallpaperCapabilityReasonCode.WallpaperEnginePlacementUnavailable,
            WallpaperEnginePlatformUnavailableReason.CommandTimedOut or
            WallpaperEnginePlatformUnavailableReason.CommandOutputInvalid or
            WallpaperEnginePlatformUnavailableReason.CommandRejected or
            WallpaperEnginePlatformUnavailableReason.ProcessBoundaryUnavailable or
            WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven =>
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineWindowUnavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(exception)),
        };

    private async ValueTask<DynamicWallpaperCapability> ProbeInitialAudioSilenceAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken)
    {
        IWallpaperEngineAudioBaseline? baseline = null;
        DynamicWallpaperCapability capability;
        try
        {
            baseline = await _audioIsolation!
                .CaptureBaselineAsync(installation, cancellationToken)
                .ConfigureAwait(false);
            capability = baseline is not null && baseline.InitialSilenceIsProven
                ? DynamicWallpaperCapability.Available()
                : DynamicWallpaperCapability.Unavailable(
                    DynamicWallpaperCapabilityReasonCode.InitialAudioSilenceNotProven);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WallpaperEnginePlatformUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            capability = DynamicWallpaperCapability.Unavailable(
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable);
        }

        if (baseline is not null)
        {
            try
            {
                await baseline.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return DynamicWallpaperCapability.Unavailable(
                    DynamicWallpaperCapabilityReasonCode
                        .WallpaperEngineAudioIsolationUnavailable);
            }
        }

        return capability;
    }
}
