using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using BackdropForCodex.Core.Runtime;

namespace BackdropForCodex.Core.Media;

internal readonly record struct WallpaperEngineWindowPlacement(int X, int Y);

internal sealed record WallpaperEngineWindowBaseline
{
    internal WallpaperEngineWindowBaseline(IEnumerable<nint> windowHandles)
    {
        ArgumentNullException.ThrowIfNull(windowHandles);
        WindowHandles = new ReadOnlyCollection<nint>(
            windowHandles.Where(handle => handle != 0).Distinct().ToArray());
    }

    internal IReadOnlyList<nint> WindowHandles { get; }
}

internal sealed record WallpaperEngineVerifiedWindow
{
    internal WallpaperEngineVerifiedWindow(
        nint windowHandle,
        int processId,
        DateTimeOffset processStartTimeUtc,
        string processPath,
        WallpaperEngineOwnedWindowName windowName)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        ArgumentOutOfRangeException.ThrowIfEqual(processStartTimeUtc, default);
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowName.Value, nameof(windowName));
        if (!Path.IsPathFullyQualified(processPath))
        {
            throw new ArgumentException(
                "The renderer process path must be fully qualified.",
                nameof(processPath));
        }

        WindowHandle = windowHandle;
        ProcessId = processId;
        ProcessStartTimeUtc = processStartTimeUtc;
        ProcessPath = Path.GetFullPath(processPath);
        WindowName = windowName;
    }

    internal nint WindowHandle { get; }

    internal int ProcessId { get; }

    internal DateTimeOffset ProcessStartTimeUtc { get; }

    internal string ProcessPath { get; }

    internal WallpaperEngineOwnedWindowName WindowName { get; }

    public override string ToString() =>
        "WallpaperEngineVerifiedWindow { WindowHandle = <redacted>, ProcessId = <redacted>, " +
        "ProcessStartTimeUtc = <redacted>, ProcessPath = <redacted>, WindowName = <redacted> }";
}

internal interface IWallpaperEngineControlClient
{
    ValueTask EnsureRunningAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken);

    ValueTask OpenWindowAsync(
        WallpaperEngineInstallation installation,
        string launchPath,
        WallpaperEngineOwnedWindowName windowName,
        WallpaperEngineWindowOptions options,
        WallpaperEngineWindowPlacement placement,
        CancellationToken cancellationToken);

    ValueTask<string?> QueryWindowWallpaperAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineOwnedWindowName windowName,
        CancellationToken cancellationToken);

    ValueTask CloseWindowAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineOwnedWindowName windowName,
        CancellationToken cancellationToken);
}

internal interface IWallpaperEngineOwnedWindowVerifier
{
    ValueTask<WallpaperEngineWindowBaseline> CaptureBaselineAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken);

    ValueTask<WallpaperEngineVerifiedWindow> WaitForOwnedWindowAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineOwnedWindowName windowName,
        WallpaperEngineWindowBaseline baseline,
        CancellationToken cancellationToken);

    ValueTask RevalidateOwnedWindowAsync(
        WallpaperEngineVerifiedWindow expectedWindow,
        CancellationToken cancellationToken);

    ValueTask ConfirmOwnedWindowAbsentAsync(
        WallpaperEngineInstallation installation,
        WallpaperEngineOwnedWindowName windowName,
        WallpaperEngineWindowBaseline baseline,
        CancellationToken cancellationToken);
}

internal interface IWallpaperEngineAudioIsolation
{
    ValueTask<IWallpaperEngineAudioBaseline> CaptureBaselineAsync(
        WallpaperEngineInstallation installation,
        CancellationToken cancellationToken);

    ValueTask<IWallpaperEngineMutedAudioLease> AcquireMutedSessionAsync(
        WallpaperEngineVerifiedWindow window,
        IWallpaperEngineAudioBaseline baseline,
        CancellationToken cancellationToken);
}

internal interface IWallpaperEngineAudioBaseline : IAsyncDisposable
{
    bool InitialSilenceIsProven { get; }
}

internal interface IWallpaperEngineMutedAudioLease : IAsyncDisposable
{
    Task<WallpaperEnginePlatformUnavailableReason> IsolationLost { get; }
}

internal interface IWallpaperEngineOwnedWindowJournal
{
    ValueTask RecordAsync(
        WallpaperEngineOwnedWindowName windowName,
        CancellationToken cancellationToken);

    ValueTask ClearAsync(
        WallpaperEngineOwnedWindowName windowName,
        CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates a single, uniquely named Wallpaper Engine pop-out. Platform adapters are kept
/// behind narrow contracts so no CLI exit code, window title, or audio-session heuristic can be
/// mistaken for the complete ownership proof.
/// </summary>
internal sealed class WallpaperEngineWindowRenderer : IWallpaperEngineWindowRenderer
{
    private readonly IWallpaperEngineInstallationLocator _installationLocator;
    private readonly IWallpaperEngineControlClient _controlClient;
    private readonly IWallpaperEngineOwnedWindowVerifier _windowVerifier;
    private readonly IWallpaperEngineAudioIsolation? _audioIsolation;
    private readonly IWallpaperEngineOwnedWindowJournal _windowJournal;
    private readonly IWallpaperEnginePopOutPlacement _popOutPlacement;
    private readonly WallpaperEngineWindowPlacement? _fixedInitialPlacement;
    private long _generation;

    internal WallpaperEngineWindowRenderer(
        IWallpaperEngineInstallationLocator installationLocator,
        IWallpaperEngineControlClient controlClient,
        IWallpaperEngineOwnedWindowVerifier windowVerifier,
        IWallpaperEngineOwnedWindowJournal windowJournal,
        IWallpaperEnginePopOutPlacement popOutPlacement)
        : this(
            installationLocator,
            controlClient,
            windowVerifier,
            audioIsolation: null,
            windowJournal,
            popOutPlacement,
            fixedInitialPlacement: null)
    {
    }

    internal WallpaperEngineWindowRenderer(
        IWallpaperEngineInstallationLocator installationLocator,
        IWallpaperEngineControlClient controlClient,
        IWallpaperEngineOwnedWindowVerifier windowVerifier,
        IWallpaperEngineAudioIsolation? audioIsolation,
        IWallpaperEngineOwnedWindowJournal windowJournal,
        IWallpaperEnginePopOutPlacement popOutPlacement)
        : this(
            installationLocator,
            controlClient,
            windowVerifier,
            audioIsolation,
            windowJournal,
            popOutPlacement,
            fixedInitialPlacement: null)
    {
    }

    internal WallpaperEngineWindowRenderer(
        IWallpaperEngineInstallationLocator installationLocator,
        IWallpaperEngineControlClient controlClient,
        IWallpaperEngineOwnedWindowVerifier windowVerifier,
        IWallpaperEngineAudioIsolation? audioIsolation,
        IWallpaperEngineOwnedWindowJournal windowJournal,
        IWallpaperEnginePopOutPlacement popOutPlacement,
        WallpaperEngineWindowPlacement fixedInitialPlacement)
        : this(
            installationLocator,
            controlClient,
            windowVerifier,
            audioIsolation,
            windowJournal,
            popOutPlacement,
            (WallpaperEngineWindowPlacement?)fixedInitialPlacement)
    {
    }

    private WallpaperEngineWindowRenderer(
        IWallpaperEngineInstallationLocator installationLocator,
        IWallpaperEngineControlClient controlClient,
        IWallpaperEngineOwnedWindowVerifier windowVerifier,
        IWallpaperEngineAudioIsolation? audioIsolation,
        IWallpaperEngineOwnedWindowJournal windowJournal,
        IWallpaperEnginePopOutPlacement popOutPlacement,
        WallpaperEngineWindowPlacement? fixedInitialPlacement)
    {
        _installationLocator = installationLocator ??
            throw new ArgumentNullException(nameof(installationLocator));
        _controlClient = controlClient ??
            throw new ArgumentNullException(nameof(controlClient));
        _windowVerifier = windowVerifier ??
            throw new ArgumentNullException(nameof(windowVerifier));
        _audioIsolation = audioIsolation;
        _windowJournal = windowJournal ??
            throw new ArgumentNullException(nameof(windowJournal));
        _popOutPlacement = popOutPlacement ??
            throw new ArgumentNullException(nameof(popOutPlacement));
        _fixedInitialPlacement = fixedInitialPlacement;
    }

    public WallpaperDeliveryCapabilities Capabilities =>
        WallpaperDeliveryCapabilities.DynamicFrames;

    public async ValueTask<IWallpaperEngineWindowLease> StartAsync(
        IWallpaperEngineProjectLease projectLease,
        WallpaperEngineWindowOptions options,
        CancellationToken cancellationToken = default)
    {
        WallpaperEngineProjectLeaseContract.Validate(projectLease);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var initialPlacement = _fixedInitialPlacement ??
            _popOutPlacement.GetInitialPlacement(options);

        var installation = await _installationLocator
            .LocateAsync(cancellationToken)
            .ConfigureAwait(false);
        var windowName = WallpaperEngineOwnedWindowName.Create(
            Interlocked.Increment(ref _generation));
        var lease = new OwnedWindowLease(
            installation,
            projectLease.LaunchPath,
            options,
            windowName,
            initialPlacement,
            _controlClient,
            _windowVerifier,
            _audioIsolation,
            _windowJournal,
            _popOutPlacement);
        await lease.StartAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    private sealed class OwnedWindowLease :
        IWallpaperEngineWindowLease,
        IActiveWallpaperHealthSource
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly WallpaperEngineInstallation _installation;
        private readonly string _launchPath;
        private readonly WallpaperEngineWindowOptions _options;
        private readonly WallpaperEngineOwnedWindowName _windowName;
        private readonly WallpaperEngineWindowPlacement _placement;
        private readonly IWallpaperEngineControlClient _controlClient;
        private readonly IWallpaperEngineOwnedWindowVerifier _windowVerifier;
        private readonly IWallpaperEngineAudioIsolation? _audioIsolation;
        private readonly IWallpaperEngineOwnedWindowJournal _windowJournal;
        private readonly IWallpaperEnginePopOutPlacement _popOutPlacement;
        private SanitizedAudioBaselineLifetime? _audioBaselineLifetime;
        private IWallpaperEngineMutedAudioLease? _mutedAudioLease;
        private IWallpaperEnginePopOutPlacementLease? _placementLease;
        private WallpaperEngineWindowBaseline? _baseline;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WallpaperEngineWindowCaptureTarget? _captureTarget;
        private nint _windowHandle;
        private bool _windowMayExist;
        private bool _journaled;
        private bool _disposed;

        internal OwnedWindowLease(
            WallpaperEngineInstallation installation,
            string launchPath,
            WallpaperEngineWindowOptions options,
            WallpaperEngineOwnedWindowName windowName,
            WallpaperEngineWindowPlacement placement,
            IWallpaperEngineControlClient controlClient,
            IWallpaperEngineOwnedWindowVerifier windowVerifier,
            IWallpaperEngineAudioIsolation? audioIsolation,
            IWallpaperEngineOwnedWindowJournal windowJournal,
            IWallpaperEnginePopOutPlacement popOutPlacement)
        {
            _installation = installation;
            _launchPath = launchPath;
            _options = options;
            _windowName = windowName;
            _placement = placement;
            _controlClient = controlClient;
            _windowVerifier = windowVerifier;
            _audioIsolation = audioIsolation;
            _windowJournal = windowJournal;
            _popOutPlacement = popOutPlacement;
        }

        private nint WindowHandle => Volatile.Read(ref _windowHandle);

        public WallpaperEngineWindowCaptureTarget? CaptureTarget =>
            Volatile.Read(ref _captureTarget);

        public Task Completion => _completion.Task;

        internal async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (paused)
                {
                    await StopCoreAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (WindowHandle == 0)
                {
                    await StartCoreAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _disposed = true;
                try
                {
                    await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _completion.TrySetCanceled();
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async ValueTask StartCoreAsync(CancellationToken cancellationToken)
        {
            if (WindowHandle != 0)
            {
                return;
            }

            var baseline = await _windowVerifier
                .CaptureBaselineAsync(_installation, cancellationToken)
                .ConfigureAwait(false);
            _baseline = baseline;
            await _windowJournal
                .RecordAsync(_windowName, cancellationToken)
                .ConfigureAwait(false);
            _journaled = true;
            try
            {
                await _controlClient
                    .EnsureRunningAsync(_installation, cancellationToken)
                    .ConfigureAwait(false);
                IWallpaperEngineAudioBaseline? audioBaseline = null;
                if (_audioIsolation is not null)
                {
                    audioBaseline = await _audioIsolation
                        .CaptureBaselineAsync(_installation, cancellationToken)
                        .ConfigureAwait(false);
                    if (audioBaseline is null)
                    {
                        throw new WallpaperEnginePlatformUnavailableException(
                            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven);
                    }

                    _audioBaselineLifetime = new SanitizedAudioBaselineLifetime(audioBaseline);
                    if (!audioBaseline.InitialSilenceIsProven)
                    {
                        throw new WallpaperEnginePlatformUnavailableException(
                            WallpaperEnginePlatformUnavailableReason
                                .InitialAudioSilenceNotProven);
                    }
                }

                _windowMayExist = true;
                await _controlClient
                    .OpenWindowAsync(
                        _installation,
                        _launchPath,
                        _windowName,
                        _options,
                        _placement,
                        cancellationToken)
                    .ConfigureAwait(false);
                var verifiedWindow = await _windowVerifier
                    .WaitForOwnedWindowAsync(
                        _installation,
                        _windowName,
                        baseline,
                        cancellationToken)
                    .ConfigureAwait(false);
                _placementLease = await _popOutPlacement
                    .PlaceAsync(verifiedWindow, _options, cancellationToken)
                    .ConfigureAwait(false);
                if (_placementLease is null)
                {
                    throw new WallpaperSourceCapabilityException(
                        "The Wallpaper Engine pop-out was not confined to a verified placement.");
                }

                _ = ObservePlacementHealthAsync(_placementLease);

                cancellationToken.ThrowIfCancellationRequested();

                if (_audioIsolation is not null)
                {
                    _mutedAudioLease = await _audioIsolation
                        .AcquireMutedSessionAsync(
                            verifiedWindow,
                            audioBaseline!,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (_mutedAudioLease is null)
                    {
                        throw new WallpaperSourceCapabilityException(
                            "The Wallpaper Engine pop-out did not provide an isolated muted session.");
                    }

                    _ = ObserveAudioIsolationAsync(_mutedAudioLease);
                }
                cancellationToken.ThrowIfCancellationRequested();

                var reportedPath = await _controlClient
                    .QueryWindowWallpaperAsync(
                        _installation,
                        _windowName,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(reportedPath) &&
                    !PathsIdentifySameEntry(_launchPath, reportedPath))
                {
                    throw new WallpaperSourceCapabilityException(
                        "The Wallpaper Engine pop-out did not report the authorized project.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(
                    ref _captureTarget,
                    new WallpaperEngineWindowCaptureTarget(verifiedWindow, _windowVerifier));
                Volatile.Write(ref _windowHandle, verifiedWindow.WindowHandle);
                await ReleaseAudioBaselineAsync().ConfigureAwait(false);
            }
            catch (Exception startFailure)
            {
                try
                {
                    await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "Wallpaper Engine pop-out startup and cleanup both failed.",
                        startFailure,
                        cleanupFailure);
                }

                ExceptionDispatchInfo.Capture(startFailure).Throw();
                throw;
            }
        }

        private async Task ObserveAudioIsolationAsync(
            IWallpaperEngineMutedAudioLease audioLease)
        {
            try
            {
                var reason = await audioLease.IsolationLost.ConfigureAwait(false);
                if (ReferenceEquals(Volatile.Read(ref _mutedAudioLease), audioLease))
                {
                    _completion.TrySetException(
                        new WallpaperEnginePlatformUnavailableException(reason));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(Volatile.Read(ref _mutedAudioLease), audioLease))
                {
                    _completion.TrySetException(exception);
                }
            }
        }

        private async Task ObservePlacementHealthAsync(
            IWallpaperEnginePopOutPlacementLease placementLease)
        {
            try
            {
                await placementLease.Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(Volatile.Read(ref _placementLease), placementLease))
                {
                    _completion.TrySetException(exception);
                }
            }
        }

        private async ValueTask StopCoreAsync(CancellationToken cancellationToken)
        {
            var failures = new List<Exception>();
            Volatile.Read(ref _captureTarget)?.Revoke();
            var windowClosed = !_windowMayExist;
            if (_windowMayExist)
            {
                try
                {
                    await _controlClient
                        .CloseWindowAsync(
                            _installation,
                            _windowName,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (_placementLease is not null)
                    {
                        await _placementLease
                            .MarkClosedAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var baseline = _baseline ??
                        throw new WallpaperEnginePlatformUnavailableException(
                            WallpaperEnginePlatformUnavailableReason
                                .WindowOwnershipNotProven);
                    await _windowVerifier
                        .ConfirmOwnedWindowAbsentAsync(
                            _installation,
                            _windowName,
                            baseline,
                            cancellationToken)
                        .ConfigureAwait(false);

                    _windowMayExist = false;
                    Volatile.Write(ref _captureTarget, null);
                    Volatile.Write(ref _windowHandle, 0);
                    _baseline = null;
                    windowClosed = true;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (!windowClosed)
            {
                ThrowCleanupFailures(failures);
                return;
            }

            var placementLease = _placementLease;
            if (placementLease is not null)
            {
                try
                {
                    await placementLease.DisposeAsync().ConfigureAwait(false);
                    _placementLease = null;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            var mutedAudioLease = _mutedAudioLease;
            if (mutedAudioLease is not null)
            {
                try
                {
                    await mutedAudioLease.DisposeAsync().ConfigureAwait(false);
                    _mutedAudioLease = null;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (_audioBaselineLifetime is not null)
            {
                try
                {
                    await ReleaseAudioBaselineAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (_journaled &&
                windowClosed &&
                _placementLease is null &&
                _mutedAudioLease is null &&
                _audioBaselineLifetime is null)
            {
                try
                {
                    await _windowJournal
                        .ClearAsync(_windowName, cancellationToken)
                        .ConfigureAwait(false);
                    _journaled = false;
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            ThrowCleanupFailures(failures);
        }

        private async ValueTask ReleaseAudioBaselineAsync()
        {
            var audioBaselineLifetime = _audioBaselineLifetime;
            if (audioBaselineLifetime is null)
            {
                return;
            }

            await audioBaselineLifetime.DisposeAsync().ConfigureAwait(false);
            _audioBaselineLifetime = null;
        }

        private static void ThrowCleanupFailures(List<Exception> failures)
        {
            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(
                    "Wallpaper Engine pop-out cleanup encountered multiple failures.",
                    failures);
            }
        }

        private static bool PathsIdentifySameEntry(
            string expectedPath,
            string? reportedPath)
        {
            if (string.IsNullOrWhiteSpace(reportedPath) ||
                !Path.IsPathFullyQualified(reportedPath))
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(expectedPath),
                    Path.GetFullPath(reportedPath.Trim()),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        private sealed class SanitizedAudioBaselineLifetime(
            IWallpaperEngineAudioBaseline baseline) : IAsyncDisposable
        {
            private readonly SemaphoreSlim _gate = new(1, 1);
            private IWallpaperEngineAudioBaseline? _baseline = baseline ??
                throw new ArgumentNullException(nameof(baseline));

            public async ValueTask DisposeAsync()
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var ownedBaseline = _baseline;
                    if (ownedBaseline is null)
                    {
                        return;
                    }

                    try
                    {
                        await ownedBaseline.DisposeAsync().ConfigureAwait(false);
                        _baseline = null;
                    }
                    catch (Exception)
                    {
                        throw new WallpaperEnginePlatformUnavailableException(
                            WallpaperEnginePlatformUnavailableReason.AudioIsolationNotProven);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }

            public override string ToString() =>
                $"{nameof(SanitizedAudioBaselineLifetime)} {{ Baseline = <redacted> }}";
        }
    }
}
