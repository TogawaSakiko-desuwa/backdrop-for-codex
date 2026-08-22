using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using System.Runtime.ExceptionServices;

namespace BackdropForCodex.Core.Dynamic;

internal interface ICaptureFrameDeadlineScheduler
{
    ValueTask WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemCaptureFrameDeadlineScheduler : ICaptureFrameDeadlineScheduler
{
    internal static SystemCaptureFrameDeadlineScheduler Instance { get; } = new();

    private SystemCaptureFrameDeadlineScheduler()
    {
    }

    public async ValueTask WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
}

internal interface IWallpaperFramePacerClock
{
    TimeSpan GetMonotonicNow();

    ValueTask WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemWallpaperFramePacerClock : IWallpaperFramePacerClock
{
    internal static SystemWallpaperFramePacerClock Instance { get; } = new();

    private SystemWallpaperFramePacerClock()
    {
    }

    public TimeSpan GetMonotonicNow() => System.Diagnostics.Stopwatch.GetElapsedTime(
        startingTimestamp: 0,
        endingTimestamp: System.Diagnostics.Stopwatch.GetTimestamp());

    public async ValueTask WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Composes the owned Wallpaper Engine window, exact-window capture, fragmented MP4 encoder and
/// verified page stream. A returned lease owns the project lease; failed starts leave it with the
/// caller and retain any resource whose ordered cleanup must be retried before another activation.
/// </summary>
public sealed class DynamicWallpaperActivationFactory :
    IDynamicWallpaperActivationFactory,
    IAsyncDisposable
{
    public const string H264MimeType = "video/mp4; codecs=\"avc1.640028\"";
    private static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NativeOwnerReleaseTimeout = TimeSpan.FromSeconds(5);

    private readonly IWallpaperEngineWindowRenderer _windowRenderer;
    private readonly IWallpaperWindowCaptureFactory _captureFactory;
    private readonly IFragmentedMp4WallpaperEncoderFactory _encoderFactory;
    private readonly IDynamicWallpaperPageSessionFactory _pageSessionFactory;
    private readonly TimeSpan _startupTimeout;
    private readonly ICaptureFrameDeadlineScheduler _frameDeadlineScheduler;
    private readonly IWallpaperFramePacerClock _framePacerClock;
    private readonly ICaptureFrameDeadlineScheduler _nativeOwnerDeadlineScheduler;
    private readonly DetachedNativeCleanupRegistry _detachedNativeCleanup = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private FailedDynamicPipelineCleanupOwner? _retainedCleanup;
    private bool _pageSessionFactoryDisposed;
    private int _disposeStarted;
    private int _disposed;

    public DynamicWallpaperActivationFactory(
        IWallpaperEngineWindowRenderer windowRenderer,
        IWallpaperWindowCaptureFactory captureFactory,
        IFragmentedMp4WallpaperEncoderFactory encoderFactory,
        IDynamicWallpaperPageSessionFactory pageSessionFactory)
        : this(
            windowRenderer,
            captureFactory,
            encoderFactory,
            pageSessionFactory,
            DefaultStartupTimeout,
            SystemCaptureFrameDeadlineScheduler.Instance,
            SystemWallpaperFramePacerClock.Instance)
    {
    }

    internal DynamicWallpaperActivationFactory(
        IWallpaperEngineWindowRenderer windowRenderer,
        IWallpaperWindowCaptureFactory captureFactory,
        IFragmentedMp4WallpaperEncoderFactory encoderFactory,
        IDynamicWallpaperPageSessionFactory pageSessionFactory,
        TimeSpan startupTimeout)
        : this(
            windowRenderer,
            captureFactory,
            encoderFactory,
            pageSessionFactory,
            startupTimeout,
            SystemCaptureFrameDeadlineScheduler.Instance,
            SystemWallpaperFramePacerClock.Instance)
    {
    }

    internal DynamicWallpaperActivationFactory(
        IWallpaperEngineWindowRenderer windowRenderer,
        IWallpaperWindowCaptureFactory captureFactory,
        IFragmentedMp4WallpaperEncoderFactory encoderFactory,
        IDynamicWallpaperPageSessionFactory pageSessionFactory,
        TimeSpan startupTimeout,
        ICaptureFrameDeadlineScheduler frameDeadlineScheduler)
        : this(
            windowRenderer,
            captureFactory,
            encoderFactory,
            pageSessionFactory,
            startupTimeout,
            frameDeadlineScheduler,
            SystemWallpaperFramePacerClock.Instance)
    {
    }

    internal DynamicWallpaperActivationFactory(
        IWallpaperEngineWindowRenderer windowRenderer,
        IWallpaperWindowCaptureFactory captureFactory,
        IFragmentedMp4WallpaperEncoderFactory encoderFactory,
        IDynamicWallpaperPageSessionFactory pageSessionFactory,
        TimeSpan startupTimeout,
        ICaptureFrameDeadlineScheduler frameDeadlineScheduler,
        IWallpaperFramePacerClock framePacerClock,
        ICaptureFrameDeadlineScheduler? nativeOwnerDeadlineScheduler = null)
    {
        WallpaperEngineWindowRendererContract.Validate(windowRenderer);
        _windowRenderer = windowRenderer;
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _encoderFactory = encoderFactory ?? throw new ArgumentNullException(nameof(encoderFactory));
        _pageSessionFactory = pageSessionFactory ??
            throw new ArgumentNullException(nameof(pageSessionFactory));
        if (startupTimeout <= TimeSpan.Zero || startupTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(startupTimeout));
        }

        _startupTimeout = startupTimeout;
        _frameDeadlineScheduler = frameDeadlineScheduler ??
            throw new ArgumentNullException(nameof(frameDeadlineScheduler));
        _framePacerClock = framePacerClock ??
            throw new ArgumentNullException(nameof(framePacerClock));
        _nativeOwnerDeadlineScheduler = nativeOwnerDeadlineScheduler ??
            SystemCaptureFrameDeadlineScheduler.Instance;
    }

    public async ValueTask<DynamicWallpaperCapability> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposalStarted();
        var capture = await _captureFactory
            .ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!capture.IsAvailable)
        {
            return capture;
        }

        return await _encoderFactory
            .ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<DynamicWallpaperActivationResult> ActivateAsync(
        DynamicWallpaperActivationRequest request,
        IWallpaperEngineProjectLease projectLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        WallpaperEngineProjectLeaseContract.Validate(projectLease);
        EnsureSameProject(request.Resolution, projectLease.Resolution);
        cancellationToken.ThrowIfCancellationRequested();

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposalStarted();
            await ReleaseRetainedCleanupAsync().ConfigureAwait(false);
            _detachedNativeCleanup.ThrowIfPending();
            return await ActivateCoreAsync(request, projectLease, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Volatile.Write(ref _disposeStarted, 1);
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            await ReleaseRetainedCleanupAsync().ConfigureAwait(false);
            _detachedNativeCleanup.ThrowIfPending();
            if (!_pageSessionFactoryDisposed &&
                _pageSessionFactory is IAsyncDisposable disposablePageSessionFactory)
            {
                await disposablePageSessionFactory.DisposeAsync().ConfigureAwait(false);
                _pageSessionFactoryDisposed = true;
            }

            Volatile.Write(ref _disposed, 1);
            GC.SuppressFinalize(this);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<DynamicWallpaperActivationResult> ActivateCoreAsync(
        DynamicWallpaperActivationRequest request,
        IWallpaperEngineProjectLease projectLease,
        CancellationToken cancellationToken)
    {
        var initialReadiness = _pageSessionFactory is
            IDynamicWallpaperInitialPresentationReadinessSource readinessSource
                ? await readinessSource
                    .WaitForInitialPresentationAsync(request.Endpoint, cancellationToken)
                    .ConfigureAwait(false)
                : null;
        Exception? lastRecoverableFailure = null;
        var profiles = DynamicWallpaperRenderProfiles.InPreferenceOrder;
        for (var profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            var profile = profiles[profileIndex];
            InjectionCapabilityState? stagedInitialCapabilityState = initialReadiness?
                .CapabilityState
                .CreateStagedCopy();
            try
            {
                var pipeline = await StartProfileWithTimeoutAsync(
                        request,
                        projectLease,
                        profile,
                        initialReadiness?.Presentation,
                        initialReadiness?.Capabilities,
                        stagedInitialCapabilityState,
                        initialReadiness?.TargetIdentity,
                        requireCurrentGlobalBaseline: initialReadiness is not null,
                        cancellationToken)
                    .ConfigureAwait(false);
                InjectionCapabilityState capabilityState;
                if (initialReadiness is null)
                {
                    capabilityState = new InjectionCapabilityState();
                    capabilityState.Begin(
                        pipeline.Capabilities,
                        continuesCurrentGeneration: false);
                }
                else
                {
                    if (pipeline.Presentation != initialReadiness.Presentation ||
                        pipeline.Capabilities != stagedInitialCapabilityState!.Current)
                    {
                        throw new InvalidOperationException(
                            "The initial pipeline did not preserve its read-only presentation preflight.");
                    }

                    _ = initialReadiness.CapabilityState.Commit(
                        stagedInitialCapabilityState);
                    stagedInitialCapabilityState = null;
                    capabilityState = initialReadiness.CapabilityState;
                }

                var active = new AdaptiveDynamicWallpaperLease(
                    request.Generation,
                    request.Resolution.CanonicalReference.MediaId,
                    projectLease,
                    pipeline,
                    capabilityState,
                    profileIndex,
                    profiles.Count - 1,
                    (
                        targetProfileIndex,
                        lockedPresentationContract,
                        capabilityCeiling,
                        recoveryCapabilityState,
                        token) =>
                        StartRecoveryProfileAsync(
                            request,
                            projectLease,
                            profiles[targetProfileIndex],
                            lockedPresentationContract,
                            capabilityCeiling,
                            recoveryCapabilityState,
                            initialReadiness?.TargetIdentity,
                            requireCurrentGlobalBaseline: false,
                            token));
                return new DynamicWallpaperActivationResult(
                    active,
                    pipeline.Presentation,
                    pipeline.Capabilities);
            }
            catch (Exception exception)
                when (profileIndex < profiles.Count - 1 &&
                    CanRecoverLocally(exception))
            {
                if (stagedInitialCapabilityState is not null)
                {
                    initialReadiness!.CapabilityState.Abandon(
                        stagedInitialCapabilityState);
                }

                lastRecoverableFailure = exception;
            }
            catch
            {
                if (stagedInitialCapabilityState is not null)
                {
                    initialReadiness!.CapabilityState.Abandon(
                        stagedInitialCapabilityState);
                }

                throw;
            }
        }

        throw lastRecoverableFailure ?? new DynamicWallpaperUnavailableException(
            DynamicWallpaperCapabilityReasonCode.EncodingFailed);
    }

    private async ValueTask<DynamicPipelineLease> StartProfileWithTimeoutAsync(
        DynamicWallpaperActivationRequest request,
        IWallpaperEngineProjectLease projectLease,
        DynamicWallpaperRenderProfile profile,
        PresentationContractSnapshot? lockedPresentationContract,
        CompatibilityCapabilities? capabilityCeiling,
        InjectionCapabilityState? capabilityState,
        string? expectedPageIdentity,
        bool requireCurrentGlobalBaseline,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_startupTimeout);
        try
        {
            return await StartProfileAsync(
                    request,
                    projectLease,
                    profile,
                    lockedPresentationContract,
                    capabilityCeiling,
                    capabilityState,
                    expectedPageIdentity,
                    requireCurrentGlobalBaseline,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.DynamicStartupTimedOut);
        }
    }

    private async ValueTask<DynamicPipelineLease> StartRecoveryProfileAsync(
        DynamicWallpaperActivationRequest request,
        IWallpaperEngineProjectLease projectLease,
        DynamicWallpaperRenderProfile profile,
        PresentationContractSnapshot lockedPresentationContract,
        CompatibilityCapabilities capabilityCeiling,
        InjectionCapabilityState capabilityState,
        string? expectedPageIdentity,
        bool requireCurrentGlobalBaseline,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposalStarted();
            await ReleaseRetainedCleanupAsync().ConfigureAwait(false);
            return await StartProfileWithTimeoutAsync(
                    request,
                    projectLease,
                    profile,
                    lockedPresentationContract,
                    capabilityCeiling,
                    capabilityState,
                    expectedPageIdentity,
                    requireCurrentGlobalBaseline,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask ReleaseRetainedCleanupAsync()
    {
        List<Exception>? failures = null;
        if (_pageSessionFactory is IRetryableDynamicWallpaperPageSessionCleanup pageCleanup)
        {
            try
            {
                await pageCleanup.ReleaseRetainedCleanupAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        var retained = _retainedCleanup;
        if (retained is not null)
        {
            try
            {
                await retained.DisposeAsync().ConfigureAwait(false);
                if (ReferenceEquals(_retainedCleanup, retained))
                {
                    _retainedCleanup = null;
                }
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        ThrowCleanupFailures(failures);
    }

    private async ValueTask<Exception?> ReleaseFailedStartupResourceBeforeDeadlineAsync(
        FailedDynamicPipelineCleanupOwner cleanupOwner,
        IAsyncDisposable resource,
        DetachedNativeReleaseTarget target)
    {
        var completion = Task.Run(
            async () =>
            {
                try
                {
                    await resource.DisposeAsync().ConfigureAwait(false);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            },
            CancellationToken.None);
        var releaseAttempt = new DetachedNativeReleaseAttempt(target, completion);
        using var deadlineCancellation = new CancellationTokenSource();
        var deadline = _nativeOwnerDeadlineScheduler
            .WaitAsync(NativeOwnerReleaseTimeout, deadlineCancellation.Token)
            .AsTask();
        var completed = await Task.WhenAny(completion, deadline).ConfigureAwait(false);
        if (ReferenceEquals(completed, completion) && !deadline.IsCompleted)
        {
            await deadlineCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await deadline.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested)
            {
            }

            var failure = await completion.ConfigureAwait(false);
            if (failure is null)
            {
                MarkReleased();
            }

            return failure;
        }

        await deadline.ConfigureAwait(false);
        var window = cleanupOwner._window ?? throw new InvalidOperationException(
            "Failed startup cleanup lost its owned window before native detachment.");
        var detachedOwner = new DetachedNativeCleanupOwner(
            completion,
            pendingRead: null,
            releaseAttempt,
            directFrame: null,
            latestFrameSlot: null,
            pendingFrameCleanup: null,
            frames: null,
            capture: null,
            cleanupOwner._pendingEncoder,
            window,
            cancellations: []);
        _detachedNativeCleanup.Register(
            detachedOwner,
            () =>
            {
                cleanupOwner._pendingEncoder = null;
                cleanupOwner._window = null;
            });
        return new DetachedNativeCleanupPendingException();

        void MarkReleased()
        {
            switch (target)
            {
                case DetachedNativeReleaseTarget.Encoder
                    when ReferenceEquals(cleanupOwner._pendingEncoder, resource):
                    cleanupOwner._pendingEncoder = null;
                    break;
                case DetachedNativeReleaseTarget.Window
                    when ReferenceEquals(cleanupOwner._window, resource):
                    cleanupOwner._window = null;
                    break;
            }
        }
    }

    private static void ThrowCleanupFailures(List<Exception>? failures)
    {
        if (failures is [var failure])
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (failures is { Count: > 1 })
        {
            throw new AggregateException(
                "One or more retained dynamic wallpaper resources could not be released.",
                failures);
        }
    }

    private void ThrowIfDisposalStarted() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0 ||
            Volatile.Read(ref _disposed) != 0,
            this);

    private async ValueTask<DynamicPipelineLease> StartProfileAsync(
        DynamicWallpaperActivationRequest request,
        IWallpaperEngineProjectLease projectLease,
        DynamicWallpaperRenderProfile profile,
        PresentationContractSnapshot? lockedPresentationContract,
        CompatibilityCapabilities? capabilityCeiling,
        InjectionCapabilityState? capabilityState,
        string? expectedPageIdentity,
        bool requireCurrentGlobalBaseline,
        CancellationToken cancellationToken)
    {
        var cleanupOwner = new FailedDynamicPipelineCleanupOwner(this);
        try
        {
            var window = await _windowRenderer
                .StartAsync(
                    projectLease,
                    new WallpaperEngineWindowOptions(profile.Width, profile.Height),
                    cancellationToken)
                .ConfigureAwait(false);
            cleanupOwner.SetWindow(window);
            if (window.CaptureTarget is null)
            {
                throw new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable);
            }

            var descriptor = new EncodedWallpaperStreamDescriptor(
                request.Generation,
                H264MimeType,
                profile.Width,
                profile.Height,
                profile.FrameRate);
            var encoder = await _encoderFactory
                .StartAsync(descriptor, cancellationToken)
                .ConfigureAwait(false);
            cleanupOwner.SetPendingEncoder(encoder);
            if (encoder is not ICapturePauseBoundaryFragmentedMp4WallpaperEncoder pauseBoundary)
            {
                throw new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.EncodingFailed);
            }

            var buffer = new EncodedWallpaperStreamBuffer(descriptor);
            cleanupOwner.SetBuffer(buffer);
            var captureEncoder = cleanupOwner.TransferEncoderToCapture();
            CaptureEncodingLease captureAndEncoder;
            try
            {
                captureAndEncoder = await CaptureEncodingLease
                    .StartAsync(
                        _captureFactory,
                        window,
                        profile,
                        captureEncoder,
                        pauseBoundary,
                        buffer,
                        _frameDeadlineScheduler,
                        _framePacerClock,
                        _nativeOwnerDeadlineScheduler,
                        NativeOwnerReleaseTimeout,
                        _detachedNativeCleanup,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RetainedCaptureEncodingStartException exception)
            {
                cleanupOwner.SetCaptureAndEncoder(exception.CleanupOwner);
                cleanupOwner.DeferCaptureCleanupUntilNextOperation();
                throw new AggregateException(
                    "Capture startup failed and its retained resources require cleanup.",
                    exception.PrimaryFailure,
                    exception.CleanupFailure);
            }

            cleanupOwner.SetCaptureAndEncoder(captureAndEncoder);
            var pageBuffer = cleanupOwner.TransferBufferToPageSession();
            var pageActivation = await _pageSessionFactory
                .StartAsync(
                    request.Endpoint,
                    pageBuffer,
                    CreateInjectionOptions(
                        request.Generation,
                        request.Profile,
                        lockedPresentationContract,
                        capabilityCeiling,
                        capabilityState,
                        expectedPageIdentity,
                        requireCurrentGlobalBaseline,
                        request.MutationSignal),
                    cancellationToken)
                .ConfigureAwait(false);
            var pageStream = pageActivation.Lease;
            cleanupOwner.SetPageStream(pageStream);
            ValidatePageActivation(
                pageActivation,
                request.Generation,
                lockedPresentationContract,
                capabilityCeiling);

            var active = new DynamicPipelineLease(
                pageStream,
                captureAndEncoder,
                window,
                pageActivation.Presentation,
                pageActivation.Capabilities);
            cleanupOwner.TransferToActiveLease();
            return active;
        }
        catch (Exception primaryFailure)
        {
            var failures = new List<Exception> { primaryFailure };
            try
            {
                await cleanupOwner.DisposeAsync().ConfigureAwait(false);
                if (cleanupOwner.HasRetainedResources)
                {
                    _retainedCleanup = cleanupOwner;
                }
            }
            catch (Exception cleanupFailure)
            {
                _retainedCleanup = cleanupOwner;
                if (cleanupFailure is AggregateException aggregateCleanup)
                {
                    failures.AddRange(aggregateCleanup.InnerExceptions);
                }
                else
                {
                    failures.Add(cleanupFailure);
                }
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }

            throw new AggregateException(
                "The dynamic wallpaper failed to start and one or more owned resources " +
                "could not be released.",
                failures);
        }
    }

    private static DynamicWallpaperInjectionOptions CreateInjectionOptions(
        long generation,
        WallpaperProfile profile,
        PresentationContractSnapshot? lockedPresentationContract,
        CompatibilityCapabilities? capabilityCeiling,
        InjectionCapabilityState? capabilityState,
        string? expectedPageIdentity,
        bool requireCurrentGlobalBaseline,
        RuntimeMutationSignal? mutationSignal)
    {
        var objectFit = profile.Fit switch
        {
            WallpaperFit.Cover => WallpaperObjectFit.Cover,
            WallpaperFit.Contain => WallpaperObjectFit.Contain,
            WallpaperFit.Stretch => WallpaperObjectFit.Fill,
            _ => throw new WallpaperSourceCapabilityException(
                "The wallpaper fit is not supported by dynamic delivery."),
        };
        var glass = new GlassEffectOptions(
            opacity: profile.PanelOpacity,
            blurPixels: profile.BlurPx);
        var composition = new WallpaperCompositionOptions(
            profile.FocusX,
            profile.FocusY,
            Math.Min(
                profile.DarkOverlay,
                WallpaperCompositionOptions.MaximumOverlayOpacity),
            Math.Min(
                profile.LightOverlay,
                WallpaperCompositionOptions.MaximumOverlayOpacity));

        return capabilityState is null
            ? new DynamicWallpaperInjectionOptions(
                generation,
                objectFit,
                mediaOpacity: 1,
                glass,
                composition,
                lockedPresentationContract,
                capabilityCeiling,
                mutationSignal)
            : new DynamicWallpaperInjectionOptions(
                generation,
                objectFit,
                mediaOpacity: 1,
                glass,
                composition,
                lockedPresentationContract!,
                capabilityCeiling!,
                capabilityState,
                requireCurrentGlobalBaseline,
                expectedPageIdentity,
                mutationSignal);
    }

    private static void ValidatePageActivation(
        DynamicWallpaperActivationResult pageActivation,
        long expectedGeneration,
        PresentationContractSnapshot? lockedPresentationContract,
        CompatibilityCapabilities? capabilityCeiling)
    {
        if (pageActivation.Lease.Generation != expectedGeneration)
        {
            throw new InvalidOperationException(
                "The dynamic page session returned a lease for a different generation.");
        }

        if (lockedPresentationContract is null)
        {
            return;
        }

        if (pageActivation.Presentation != lockedPresentationContract)
        {
            throw new InvalidOperationException(
                "The dynamic page session replaced the presentation contract locked for recovery.");
        }

        if (capabilityCeiling!.DowngradeWith(pageActivation.Capabilities) !=
            pageActivation.Capabilities)
        {
            throw new InvalidOperationException(
                "The dynamic page session re-enabled a capability disabled for this generation.");
        }
    }

    private static void EnsureSameProject(
        WallpaperSourceResolution requested,
        WallpaperSourceResolution leased)
    {
        if (requested.CanonicalReference.MediaId != leased.CanonicalReference.MediaId ||
            requested.Descriptor.SourceKind != leased.Descriptor.SourceKind ||
            !WallpaperSourceIdentifier.AreEqual(
                requested.Descriptor.SourceKind,
                requested.CanonicalReference.SourceIdentifier,
                leased.CanonicalReference.SourceIdentifier) ||
            requested.Descriptor.ContentKind != leased.Descriptor.ContentKind ||
            requested.Descriptor.DeliveryKind != leased.Descriptor.DeliveryKind)
        {
            throw new WallpaperSourceCapabilityException(
                "The project lease does not authorize the requested dynamic wallpaper.");
        }
    }

    private static bool CanRecoverLocally(Exception exception)
    {
        if (ContainsTerminalRecoveryBoundaryFailure(exception))
        {
            return false;
        }

        return exception switch
        {
            EndOfStreamException => true,
            IOException => true,
            TimeoutException => true,
            System.Runtime.InteropServices.COMException => true,
            DynamicWallpaperPageSessionException
            {
                FailureKind: DynamicWallpaperPageSessionFailureKind.Transient,
                CleanupDiagnosticExceptionType: null,
            } => true,
            WallpaperEnginePlatformUnavailableException
            {
                Reason:
                    WallpaperEnginePlatformUnavailableReason.StartupNotProven or
                    WallpaperEnginePlatformUnavailableReason.CommandTimedOut or
                    WallpaperEnginePlatformUnavailableReason.CommandOutputInvalid or
                    WallpaperEnginePlatformUnavailableReason.CommandRejected or
                    WallpaperEnginePlatformUnavailableReason.ProcessBoundaryUnavailable or
                    WallpaperEnginePlatformUnavailableReason.WindowPlacementNotProven,
            } => true,
            DynamicWallpaperUnavailableException
            {
                ReasonCode:
                    DynamicWallpaperCapabilityReasonCode.CaptureApiUnavailable or
                    DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable or
                    DynamicWallpaperCapabilityReasonCode.CapturedFrameSizeMismatch or
                    DynamicWallpaperCapabilityReasonCode.GraphicsDeviceUnavailable or
                    DynamicWallpaperCapabilityReasonCode.HardwareEncoderUnavailable or
                    DynamicWallpaperCapabilityReasonCode.CodecUnavailable or
                    DynamicWallpaperCapabilityReasonCode.EncodingFailed or
                    DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable or
                    DynamicWallpaperCapabilityReasonCode.FallbackRenderTierUnavailable or
                    DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure or
                    DynamicWallpaperCapabilityReasonCode.DynamicStartupTimedOut or
                    DynamicWallpaperCapabilityReasonCode.WallpaperEngineWindowUnavailable or
                    DynamicWallpaperCapabilityReasonCode.WallpaperEnginePlacementUnavailable,
            } => true,
            _ => false,
        };
    }

    private static bool ContainsTerminalRecoveryBoundaryFailure(Exception exception) =>
        exception switch
        {
            WallpaperEnginePlatformUnavailableException
            {
                Reason: WallpaperEnginePlatformUnavailableReason.WindowOwnershipNotProven,
            } => true,
            DynamicWallpaperPageSessionException
            {
                FailureKind:
                    DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven or
                    DynamicWallpaperPageSessionFailureKind.CleanupNotProven,
            } => true,
            DynamicWallpaperPageSessionException
            {
                CleanupDiagnosticExceptionType: not null,
            } => true,
            AggregateException aggregate => aggregate.InnerExceptions.Any(
                ContainsTerminalRecoveryBoundaryFailure),
            { InnerException: { } innerException } =>
                ContainsTerminalRecoveryBoundaryFailure(innerException),
            _ => false,
        };

    private sealed class AdaptiveDynamicWallpaperLease :
        IActiveWallpaperLease,
        IActiveWallpaperMediaIdentity,
        IPausableActiveWallpaperLease,
        IActiveWallpaperHealthSource,
        IWallpaperInjectionCapabilitySource
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private IWallpaperEngineProjectLease? _project;
        private readonly Guid _mediaId;
        private readonly Task _completion;
        private DynamicPipelineLease? _pipeline;
        private readonly PresentationContractSnapshot _presentationContract;
        private readonly InjectionCapabilityState _capabilityState;
        private CompatibilityCapabilities _capabilities;
        private readonly Func<
            int,
            PresentationContractSnapshot,
            CompatibilityCapabilities,
            InjectionCapabilityState,
            CancellationToken,
            ValueTask<DynamicPipelineLease>>
            _startRecoveryProfile;
        private readonly int _lastProfileIndex;
        private int _profileIndex;
        private int _lowestTierRestartCount;
        private bool _paused;
        private int _disposeStarted;
        private int _disposed;

        internal AdaptiveDynamicWallpaperLease(
            long generation,
            Guid mediaId,
            IWallpaperEngineProjectLease project,
            DynamicPipelineLease pipeline,
            InjectionCapabilityState capabilityState,
            int profileIndex,
            int lastProfileIndex,
            Func<
                int,
                PresentationContractSnapshot,
                CompatibilityCapabilities,
                InjectionCapabilityState,
                CancellationToken,
                ValueTask<DynamicPipelineLease>> startRecoveryProfile)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
            if (mediaId == Guid.Empty)
            {
                throw new ArgumentException(
                    "The dynamic wallpaper media ID cannot be empty.",
                    nameof(mediaId));
            }

            _project = project ?? throw new ArgumentNullException(nameof(project));
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            if (pipeline.Generation != generation)
            {
                throw new ArgumentException(
                    "The dynamic pipeline belongs to a different generation.",
                    nameof(pipeline));
            }

            _presentationContract = pipeline.Presentation;
            _capabilityState = capabilityState ??
                throw new ArgumentNullException(nameof(capabilityState));
            if (_capabilityState.Current != pipeline.Capabilities)
            {
                throw new ArgumentException(
                    "The dynamic compatibility state must begin at the active pipeline capabilities.",
                    nameof(capabilityState));
            }

            _capabilities = pipeline.Capabilities;
            Generation = generation;
            _mediaId = mediaId;
            if (profileIndex < 0 || profileIndex > lastProfileIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(profileIndex));
            }

            _profileIndex = profileIndex;
            _lastProfileIndex = lastProfileIndex;
            _startRecoveryProfile = startRecoveryProfile ??
                throw new ArgumentNullException(nameof(startRecoveryProfile));
            _completion = MonitorAsync();
        }

        public event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>?
            CapabilitiesChanged;

        public long Generation { get; }

        public Guid? MediaId => _mediaId;

        public ActiveWallpaperDeliveryKind DeliveryKind =>
            ActiveWallpaperDeliveryKind.DynamicStream;

        public Task Completion => _completion;

        public CompatibilityCapabilities Capabilities => Volatile.Read(ref _capabilities);

        public PresentationContractSnapshot PresentationContract => _presentationContract;

        public async ValueTask SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0 ||
                Volatile.Read(ref _disposed) != 0,
                this);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposeStarted) != 0 ||
                    Volatile.Read(ref _disposed) != 0,
                    this);
                var pipeline = _pipeline ?? throw new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.EncodingFailed);
                await pipeline.SetPausedAsync(paused, cancellationToken).ConfigureAwait(false);
                _paused = paused;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
            {
                CapabilitiesChanged = null;
                await _stop.CancelAsync().ConfigureAwait(false);
            }

            List<Exception>? failures = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                if (_pipeline is not null)
                {
                    try
                    {
                        await _pipeline.DisposeAsync().ConfigureAwait(false);
                        _pipeline = null;
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }

                if (_pipeline is null && _project is not null)
                {
                    try
                    {
                        await _project.DisposeAsync().ConfigureAwait(false);
                        _project = null;
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }

                if (_pipeline is null && _project is null)
                {
                    Volatile.Write(ref _disposed, 1);
                }
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                await _completion.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Completion is the public health channel; disposal still owns every resource.
            }
            GC.SuppressFinalize(this);

            if (failures is [var failure])
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(
                    "One or more adaptive wallpaper resources could not be released.",
                    failures);
            }

        }

        private async Task MonitorAsync()
        {
            while (true)
            {
                DynamicPipelineLease pipeline;
                await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    pipeline = _pipeline ?? throw new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.EncodingFailed);
                }
                finally
                {
                    _gate.Release();
                }

                try
                {
                    await pipeline.Completion
                        .WaitAsync(_stop.Token)
                        .ConfigureAwait(false);
                    throw new EndOfStreamException(
                        "The active dynamic wallpaper pipeline ended unexpectedly.");
                }
                catch (Exception exception) when (CanRecoverLocally(exception))
                {
                    await RecoverPipelineAsync(pipeline).ConfigureAwait(false);
                }
            }
        }

        private async Task RecoverPipelineAsync(DynamicPipelineLease failedPipeline)
        {
            WallpaperInjectionCapabilitiesChangedEventArgs? capabilityChange = null;
            await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_pipeline, failedPipeline))
                {
                    return;
                }

                var targetProfileIndex = Math.Min(_profileIndex + 1, _lastProfileIndex);
                var delayBeforeAttempt = targetProfileIndex == _profileIndex;
                if (delayBeforeAttempt)
                {
                    var delay = GetLowestTierRestartDelay(_lowestTierRestartCount++);
                    await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                }
                else
                {
                    _lowestTierRestartCount = 0;
                }

                await failedPipeline.DisposeAsync().ConfigureAwait(false);
                _pipeline = null;
                while (true)
                {
                    DynamicPipelineLease? replacement = null;
                    InjectionCapabilityState? stagedCapabilityState = null;
                    try
                    {
                        var currentCapabilities = Volatile.Read(ref _capabilities);
                        stagedCapabilityState = _capabilityState.CreateStagedCopy();
                        replacement = await _startRecoveryProfile(
                                targetProfileIndex,
                                _presentationContract,
                                currentCapabilities,
                                stagedCapabilityState,
                                _stop.Token)
                            .ConfigureAwait(false);
                        ValidateReplacement(replacement, currentCapabilities);
                        if (_paused)
                        {
                            await replacement.SetPausedAsync(paused: true, _stop.Token)
                                .ConfigureAwait(false);
                        }

                        var nextCapabilities = stagedCapabilityState.Current;
                        if (replacement.Capabilities != nextCapabilities)
                        {
                            throw new InvalidOperationException(
                                "A recovery pipeline did not use the generation compatibility state.");
                        }

                        var capabilityTransition =
                            _capabilityState.Commit(stagedCapabilityState);
                        stagedCapabilityState = null;
                        _pipeline = replacement;
                        replacement = null;
                        _profileIndex = targetProfileIndex;
                        Volatile.Write(ref _capabilities, capabilityTransition.Current);
                        if (capabilityTransition.Current != capabilityTransition.Previous)
                        {
                            capabilityChange = new WallpaperInjectionCapabilitiesChangedEventArgs(
                                Generation,
                                capabilityTransition.Previous,
                                capabilityTransition.Current,
                                _presentationContract);
                        }

                        break;
                    }
                    catch (Exception exception)
                    {
                        if (stagedCapabilityState is not null)
                        {
                            _capabilityState.Abandon(stagedCapabilityState);
                        }

                        Exception? cleanupFailure = null;
                        if (replacement is not null)
                        {
                            try
                            {
                                await replacement.DisposeAsync().ConfigureAwait(false);
                            }
                            catch (Exception cleanupException)
                            {
                                cleanupFailure = cleanupException;
                            }
                        }

                        if (cleanupFailure is not null)
                        {
                            throw new AggregateException(
                                "A recovery pipeline failed and could not be released.",
                                exception,
                                cleanupFailure);
                        }

                        if (!CanRecoverLocally(exception))
                        {
                            throw;
                        }

                        var failedProfileIndex = targetProfileIndex;
                        targetProfileIndex = Math.Min(
                            targetProfileIndex + 1,
                            _lastProfileIndex);
                        if (failedProfileIndex == _lastProfileIndex)
                        {
                            var delay = GetLowestTierRestartDelay(
                                _lowestTierRestartCount++);
                            await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            if (capabilityChange is not null)
            {
                PublishCapabilitiesChanged(capabilityChange);
            }
        }

        private void ValidateReplacement(
            DynamicPipelineLease replacement,
            CompatibilityCapabilities currentCapabilities)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            if (replacement.Generation != Generation)
            {
                throw new InvalidOperationException(
                    "A recovery pipeline belongs to a different wallpaper generation.");
            }

            if (replacement.Presentation != _presentationContract)
            {
                throw new InvalidOperationException(
                    "A recovery pipeline attempted to replace the locked presentation contract.");
            }

            if (currentCapabilities.DowngradeWith(replacement.Capabilities) !=
                replacement.Capabilities)
            {
                throw new InvalidOperationException(
                    "A recovery pipeline attempted to re-enable a disabled capability.");
            }
        }

        private void PublishCapabilitiesChanged(
            WallpaperInjectionCapabilitiesChangedEventArgs eventArgs)
        {
            var handlers = CapabilitiesChanged;
            if (handlers is null)
            {
                return;
            }

            foreach (EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs> handler in
                     handlers.GetInvocationList())
            {
                try
                {
                    handler(this, eventArgs);
                }
                catch (Exception)
                {
                    // Compatibility observers cannot interrupt pipeline recovery or cleanup.
                }
            }
        }

        private static TimeSpan GetLowestTierRestartDelay(int restartCount) =>
            restartCount switch
            {
                <= 0 => TimeSpan.FromMilliseconds(250),
                1 => TimeSpan.FromSeconds(1),
                _ => TimeSpan.FromSeconds(5),
            };

    }

    private sealed class FailedDynamicPipelineCleanupOwner : IAsyncDisposable
    {
        private readonly DynamicWallpaperActivationFactory _factory;
        private IActiveWallpaperLease? _pageStream;
        private CaptureEncodingLease? _captureAndEncoder;
        internal IFragmentedMp4WallpaperEncoder? _pendingEncoder;
        private EncodedWallpaperStreamBuffer? _buffer;
        internal IWallpaperEngineWindowLease? _window;
        private bool _deferCaptureCleanup;

        internal FailedDynamicPipelineCleanupOwner(
            DynamicWallpaperActivationFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        internal bool HasRetainedResources =>
            _pageStream is not null ||
            _captureAndEncoder is not null ||
            _pendingEncoder is not null ||
            _buffer is not null ||
            _window is not null;

        internal void SetPageStream(IActiveWallpaperLease pageStream) =>
            _pageStream = pageStream ?? throw new ArgumentNullException(nameof(pageStream));

        internal void SetCaptureAndEncoder(CaptureEncodingLease captureAndEncoder) =>
            _captureAndEncoder = captureAndEncoder ??
                throw new ArgumentNullException(nameof(captureAndEncoder));

        internal void DeferCaptureCleanupUntilNextOperation() =>
            _deferCaptureCleanup = true;

        internal void SetPendingEncoder(IFragmentedMp4WallpaperEncoder encoder) =>
            _pendingEncoder = encoder ?? throw new ArgumentNullException(nameof(encoder));

        internal IFragmentedMp4WallpaperEncoder TransferEncoderToCapture()
        {
            var encoder = _pendingEncoder ?? throw new InvalidOperationException(
                "The encoder is not owned by this failed-start cleanup boundary.");
            _pendingEncoder = null;
            return encoder;
        }

        internal void SetBuffer(EncodedWallpaperStreamBuffer buffer) =>
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

        internal void SetWindow(IWallpaperEngineWindowLease window) =>
            _window = window ?? throw new ArgumentNullException(nameof(window));

        internal EncodedWallpaperStreamBuffer TransferBufferToPageSession()
        {
            var buffer = _buffer ?? throw new InvalidOperationException(
                "The encoded buffer is not owned by this failed-start cleanup boundary.");
            _buffer = null;
            return buffer;
        }

        internal void TransferToActiveLease()
        {
            _pageStream = null;
            _captureAndEncoder = null;
            _pendingEncoder = null;
            _buffer = null;
            _window = null;
        }

        public async ValueTask DisposeAsync()
        {
            List<Exception>? failures = null;
            await TryReleaseAsync(
                () => _pageStream,
                () => _pageStream = null).ConfigureAwait(false);
            if (_deferCaptureCleanup)
            {
                _deferCaptureCleanup = false;
            }
            else if (_captureAndEncoder is not null)
            {
                var captureAndEncoder = _captureAndEncoder;
                var captureOwnsWindowCleanup = captureAndEncoder.OwnsWindowCleanup;
                try
                {
                    await captureAndEncoder.DisposeAsync().ConfigureAwait(false);
                    _captureAndEncoder = null;
                    if (captureOwnsWindowCleanup)
                    {
                        // CaptureEncodingLease owned and released the same window reference.
                        _window = null;
                    }
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }

                if (captureAndEncoder.NativeOwnershipDetached)
                {
                    // The detached owner atomically took the same window. Clear only this
                    // duplicate reference; disposing it here would race the in-flight native task.
                    _captureAndEncoder = null;
                    _window = null;
                }
            }

            if (_captureAndEncoder is null)
            {
                if (_pendingEncoder is { } pendingEncoder)
                {
                    var releaseFailure = await _factory
                        .ReleaseFailedStartupResourceBeforeDeadlineAsync(
                            this,
                            pendingEncoder,
                            DetachedNativeReleaseTarget.Encoder)
                        .ConfigureAwait(false);
                    if (releaseFailure is not null)
                    {
                        (failures ??= []).Add(releaseFailure);
                    }
                }

                if (_pendingEncoder is null)
                {
                    await TryReleaseAsync(
                        () => _buffer,
                        () => _buffer = null).ConfigureAwait(false);
                    if (_buffer is null && _window is { } window)
                    {
                        var releaseFailure = await _factory
                            .ReleaseFailedStartupResourceBeforeDeadlineAsync(
                                this,
                                window,
                                DetachedNativeReleaseTarget.Window)
                            .ConfigureAwait(false);
                        if (releaseFailure is not null)
                        {
                            (failures ??= []).Add(releaseFailure);
                        }
                    }
                }
            }

            ThrowCleanupFailures(failures);

            async ValueTask TryReleaseAsync<T>(Func<T?> get, Action released)
                where T : class, IAsyncDisposable
            {
                var resource = get();
                if (resource is null)
                {
                    return;
                }

                try
                {
                    await resource.DisposeAsync().ConfigureAwait(false);
                    released();
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }
        }
    }

    private sealed class DynamicPipelineLease :
        IAsyncDisposable,
        IPausableActiveWallpaperLease,
        IActiveWallpaperHealthSource
    {
        private static readonly TimeSpan PageTransitionTimeout = TimeSpan.FromSeconds(5);

        private IActiveWallpaperLease? _pageStream;
        private IDynamicWallpaperPagePlaybackLease? _pagePlayback;
        private CaptureEncodingLease? _captureAndEncoder;
        private IWallpaperEngineWindowLease? _window;
        private readonly SemaphoreSlim _pauseGate = new(1, 1);
        private readonly Task _completion;
        private readonly long _generation;
        private readonly PresentationContractSnapshot _presentation;
        private readonly CompatibilityCapabilities _capabilities;
        private long _pausedAtKeyFrameSequence;
        private bool _paused;
        private int _disposeStarted;
        private int _disposed;

        internal DynamicPipelineLease(
            IActiveWallpaperLease pageStream,
            CaptureEncodingLease captureAndEncoder,
            IWallpaperEngineWindowLease window,
            PresentationContractSnapshot presentation,
            CompatibilityCapabilities capabilities)
        {
            _pageStream = pageStream ?? throw new ArgumentNullException(nameof(pageStream));
            _pagePlayback = pageStream as IDynamicWallpaperPagePlaybackLease ??
                throw new ArgumentException(
                    "The page stream lease must support acknowledged playback transitions.",
                    nameof(pageStream));
            _captureAndEncoder = captureAndEncoder ??
                throw new ArgumentNullException(nameof(captureAndEncoder));
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
            _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
            if (pageStream.DeliveryKind != ActiveWallpaperDeliveryKind.DynamicStream)
            {
                throw new ArgumentException(
                    "The page stream lease must represent dynamic delivery.",
                    nameof(pageStream));
            }

            _generation = pageStream.Generation;
            _completion = ObserveHealthAsync(pageStream, captureAndEncoder, window);
        }

        internal long Generation => _generation;

        internal PresentationContractSnapshot Presentation => _presentation;

        internal CompatibilityCapabilities Capabilities => _capabilities;

        public Task Completion => _completion;

        public async ValueTask SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0 ||
                Volatile.Read(ref _disposed) != 0,
                this);
            await _pauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposeStarted) != 0 ||
                    Volatile.Read(ref _disposed) != 0,
                    this);
                if (_paused == paused)
                {
                    return;
                }

                if (paused)
                {
                    await PauseAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ResumeAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _pauseGate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _disposeStarted, 1);
            List<Exception>? failures = null;
            await _pauseGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                if (_pageStream is not null)
                {
                    try
                    {
                        await _pageStream.DisposeAsync().ConfigureAwait(false);
                        _pageStream = null;
                        _pagePlayback = null;
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }

                if (_captureAndEncoder is not null)
                {
                    try
                    {
                        await _captureAndEncoder.DisposeAsync().ConfigureAwait(false);
                        _captureAndEncoder = null;
                        // CaptureEncodingLease is the single logical owner of the duplicated
                        // window reference and has already released it (possibly via a detached
                        // cleanup owner). Clear this non-owning pipeline reference only.
                        _window = null;
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }

                if (_pageStream is null && _captureAndEncoder is null && _window is null)
                {
                    Volatile.Write(ref _disposed, 1);
                }
            }
            finally
            {
                _pauseGate.Release();
            }

            GC.SuppressFinalize(this);

            if (failures is [var failure])
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(
                    "One or more dynamic pipeline resources could not be released.",
                    failures);
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

        private async ValueTask PauseAsync(CancellationToken cancellationToken)
        {
            var pagePlayback = _pagePlayback ?? throw new ObjectDisposedException(GetType().Name);
            var captureAndEncoder = _captureAndEncoder ??
                throw new ObjectDisposedException(GetType().Name);
            await pagePlayback.SetPausedAsync(paused: true, cancellationToken)
                .ConfigureAwait(false);
            _paused = true;
            try
            {
                await captureAndEncoder.SetPausedAsync(paused: true, cancellationToken)
                    .ConfigureAwait(false);
                using var deadline =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(PageTransitionTimeout);
                try
                {
                    await pagePlayback
                        .WaitForBufferedSegmentsAsync(deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.SustainedStreamBackpressure);
                }

                _pausedAtKeyFrameSequence =
                    pagePlayback.LastAcknowledgedKeyFrameSequence;
            }
            catch (Exception failure)
            {
                var rollbackFailure = await TryRestoreActivePipelineAsync()
                    .ConfigureAwait(false);
                if (rollbackFailure is not null)
                {
                    throw new AggregateException(
                        "The dynamic wallpaper could not enter or roll back its paused state.",
                        failure,
                        rollbackFailure);
                }

                _paused = false;
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private async ValueTask ResumeAsync(CancellationToken cancellationToken)
        {
            var pagePlayback = _pagePlayback ?? throw new ObjectDisposedException(GetType().Name);
            var captureAndEncoder = _captureAndEncoder ??
                throw new ObjectDisposedException(GetType().Name);
            try
            {
                await captureAndEncoder.SetPausedAsync(paused: false, cancellationToken)
                    .ConfigureAwait(false);
                using var deadline =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(PageTransitionTimeout);
                try
                {
                    await pagePlayback
                        .WaitForKeyFrameAfterAsync(
                            _pausedAtKeyFrameSequence,
                            deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.DynamicStartupTimedOut);
                }

                await pagePlayback.SetPausedAsync(paused: false, cancellationToken)
                    .ConfigureAwait(false);
                _paused = false;
            }
            catch (Exception failure)
            {
                var rollbackFailure = await TryRestorePausedPipelineAsync()
                    .ConfigureAwait(false);
                if (rollbackFailure is not null)
                {
                    throw new AggregateException(
                        "The dynamic wallpaper could not resume or restore its paused state.",
                        failure,
                        rollbackFailure);
                }

                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private async ValueTask<Exception?> TryRestoreActivePipelineAsync()
        {
            try
            {
                var captureAndEncoder = _captureAndEncoder ??
                    throw new ObjectDisposedException(GetType().Name);
                var pagePlayback = _pagePlayback ??
                    throw new ObjectDisposedException(GetType().Name);
                await captureAndEncoder.SetPausedAsync(paused: false, CancellationToken.None)
                    .ConfigureAwait(false);
                await pagePlayback.SetPausedAsync(paused: false, CancellationToken.None)
                    .ConfigureAwait(false);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private async ValueTask<Exception?> TryRestorePausedPipelineAsync()
        {
            List<Exception>? failures = null;
            try
            {
                var captureAndEncoder = _captureAndEncoder ??
                    throw new ObjectDisposedException(GetType().Name);
                await captureAndEncoder.SetPausedAsync(paused: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                var pagePlayback = _pagePlayback ??
                    throw new ObjectDisposedException(GetType().Name);
                await pagePlayback.SetPausedAsync(paused: true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            return failures switch
            {
                null => null,
                [var failure] => failure,
                _ => new AggregateException(failures),
            };
        }
    }

    /// <summary>
    /// Keeps native operations which ignored cancellation strongly reachable after their active
    /// pipeline has detached. The owner, not the failed pipeline, performs the eventual cleanup;
    /// this lets the actor recover without pretending that the native resources were released.
    /// </summary>
    private sealed class DetachedNativeCleanupRegistry
    {
        private readonly object _sync = new();
        private readonly HashSet<DetachedNativeCleanupOwner> _owners = [];

        internal void Register(
            DetachedNativeCleanupOwner owner,
            Action transferOwnership)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(transferOwnership);
            lock (_sync)
            {
                if (!_owners.Add(owner))
                {
                    throw new InvalidOperationException(
                        "The detached native cleanup owner was already registered.");
                }
            }

            try
            {
                transferOwnership();
            }
            catch
            {
                Remove(owner);
                throw;
            }

            owner.Start(Remove);
        }

        internal void ThrowIfPending()
        {
            int pending;
            int failed;
            lock (_sync)
            {
                pending = _owners.Count;
                failed = _owners.Count(owner => owner.LastCleanupFailure is not null);
            }

            if (pending != 0)
            {
                throw new InvalidOperationException(
                    $"{pending} detached native cleanup owner(s) are still active" +
                    (failed == 0 ? "." : $"; {failed} last cleanup attempt(s) failed."));
            }
        }

        private void Remove(DetachedNativeCleanupOwner owner)
        {
            lock (_sync)
            {
                _owners.Remove(owner);
            }
        }
    }

    private sealed class DetachedNativeCleanupOwner
    {
        private static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromSeconds(1);

        private readonly Task _nativeOperation;
        private readonly Task<bool>? _pendingRead;
        private readonly DetachedNativeReleaseAttempt? _releaseAttempt;
        private IWallpaperCapturedFrame? _directFrame;
        private IWallpaperCapturedFrame? _lateFrame;
        private LatestWallpaperCapturedFrameSlot? _latestFrameSlot;
        private LatestWallpaperCapturedFrameSlot? _pendingFrameCleanup;
        private IAsyncEnumerator<IWallpaperCapturedFrame>? _frames;
        private IWallpaperWindowCaptureSession? _capture;
        private IFragmentedMp4WallpaperEncoder? _encoder;
        private IWallpaperEngineWindowLease? _window;
        private readonly List<CancellationTokenSource> _cancellations;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _nativeOperationsObserved;
        private int _started;

        internal DetachedNativeCleanupOwner(
            Task nativeOperation,
            Task<bool>? pendingRead,
            DetachedNativeReleaseAttempt? releaseAttempt,
            IWallpaperCapturedFrame? directFrame,
            LatestWallpaperCapturedFrameSlot? latestFrameSlot,
            LatestWallpaperCapturedFrameSlot? pendingFrameCleanup,
            IAsyncEnumerator<IWallpaperCapturedFrame>? frames,
            IWallpaperWindowCaptureSession? capture,
            IFragmentedMp4WallpaperEncoder? encoder,
            IWallpaperEngineWindowLease window,
            IEnumerable<CancellationTokenSource?> cancellations)
        {
            _nativeOperation = nativeOperation ??
                throw new ArgumentNullException(nameof(nativeOperation));
            _pendingRead = pendingRead;
            _releaseAttempt = releaseAttempt;
            _directFrame = directFrame;
            _latestFrameSlot = latestFrameSlot;
            _pendingFrameCleanup = pendingFrameCleanup;
            _frames = frames;
            _capture = capture;
            _encoder = encoder;
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _cancellations = cancellations
                .OfType<CancellationTokenSource>()
                .Distinct()
                .ToList();
        }

        internal Exception? LastCleanupFailure { get; private set; }

        internal Task Completion => _completion.Task;

        internal void Start(Action<DetachedNativeCleanupOwner> completed)
        {
            ArgumentNullException.ThrowIfNull(completed);
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The detached native cleanup owner was already started.");
            }

            _ = Task.Run(() => RunAsync(completed));
        }

        private async Task RunAsync(Action<DetachedNativeCleanupOwner> completed)
        {
            while (true)
            {
                try
                {
                    if (!_nativeOperationsObserved)
                    {
                        await ObserveNativeOperationsAsync().ConfigureAwait(false);
                        _nativeOperationsObserved = true;
                    }

                    var failures = await TryReleaseResourcesAsync().ConfigureAwait(false);
                    if (!HasResources)
                    {
                        completed(this);
                        LastCleanupFailure = null;
                        _completion.TrySetResult();
                        return;
                    }

                    LastCleanupFailure = failures switch
                    {
                        [var failure] => failure,
                        { Count: > 1 } => new AggregateException(
                            "Detached native resources could not be released.",
                            failures),
                        _ => new InvalidOperationException(
                            "Detached native cleanup retained resources without a diagnostic."),
                    };
                }
                catch (Exception exception)
                {
                    LastCleanupFailure = exception;
                }

                await Task.Delay(CleanupRetryDelay).ConfigureAwait(false);
            }
        }

        private async Task ObserveNativeOperationsAsync()
        {
            await ObserveAsync(_nativeOperation).ConfigureAwait(false);
            if (_pendingRead is not null &&
                !ReferenceEquals(_pendingRead, _nativeOperation))
            {
                await ObserveAsync(_pendingRead).ConfigureAwait(false);
            }

            if (_pendingRead is { Status: TaskStatus.RanToCompletion } &&
                _pendingRead.Result &&
                _frames is not null)
            {
                try
                {
                    _lateFrame = _frames?.Current;
                }
                catch (Exception exception)
                {
                    LastCleanupFailure = exception;
                }
            }

            if (_releaseAttempt is { Completion.Status: TaskStatus.RanToCompletion } attempt)
            {
                var failure = attempt.Completion.Result;
                if (failure is null)
                {
                    MarkReleaseAttemptCompleted(attempt.Target);
                }
                else
                {
                    LastCleanupFailure = failure;
                }
            }

            static async Task ObserveAsync(Task operation)
            {
                try
                {
                    await operation.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The pipeline health channel already carries the operation failure. The
                    // detached owner observes it here so cleanup can proceed without an
                    // unobserved-task escalation.
                }
            }
        }

        private bool HasResources =>
            _directFrame is not null ||
            _lateFrame is not null ||
            _latestFrameSlot is not null ||
            _pendingFrameCleanup is not null ||
            _frames is not null ||
            _capture is not null ||
            _encoder is not null ||
            _window is not null ||
            _cancellations.Count != 0;

        private async ValueTask<List<Exception>> TryReleaseResourcesAsync()
        {
            List<Exception> failures = [];
            await TryReleaseAsync(
                () => _directFrame,
                () => _directFrame = null).ConfigureAwait(false);
            if (_directFrame is not null)
            {
                return failures;
            }

            await TryReleaseAsync(
                () => _lateFrame,
                () => _lateFrame = null).ConfigureAwait(false);
            if (_lateFrame is not null)
            {
                return failures;
            }

            await TryReleaseAsync(
                () => _latestFrameSlot,
                () => _latestFrameSlot = null).ConfigureAwait(false);
            if (_latestFrameSlot is not null)
            {
                return failures;
            }

            await TryReleaseAsync(
                () => _pendingFrameCleanup,
                () => _pendingFrameCleanup = null).ConfigureAwait(false);
            if (_pendingFrameCleanup is not null)
            {
                return failures;
            }

            await TryReleaseAsync(
                () => _frames,
                () => _frames = null).ConfigureAwait(false);
            if (_frames is not null)
            {
                return failures;
            }

            await TryReleaseAsync(
                () => _capture,
                () => _capture = null).ConfigureAwait(false);
            if (_capture is not null)
            {
                return failures;
            }

            await TryReleaseAsync(
                () => _encoder,
                () => _encoder = null).ConfigureAwait(false);
            if (_encoder is not null)
            {
                return failures;
            }

            await TryReleaseAsync(
                () => _window,
                () => _window = null).ConfigureAwait(false);
            if (_window is not null)
            {
                return failures;
            }

            for (var index = _cancellations.Count - 1; index >= 0; index--)
            {
                try
                {
                    _cancellations[index].Dispose();
                    _cancellations.RemoveAt(index);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            return failures;

            async ValueTask TryReleaseAsync<T>(Func<T?> get, Action released)
                where T : class, IAsyncDisposable
            {
                var resource = get();
                if (resource is null)
                {
                    return;
                }

                try
                {
                    await resource.DisposeAsync().ConfigureAwait(false);
                    released();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }

        private void MarkReleaseAttemptCompleted(DetachedNativeReleaseTarget target)
        {
            switch (target)
            {
                case DetachedNativeReleaseTarget.LatestFrameSlot:
                    _latestFrameSlot = null;
                    break;
                case DetachedNativeReleaseTarget.PendingFrameCleanup:
                    _pendingFrameCleanup = null;
                    break;
                case DetachedNativeReleaseTarget.Capture:
                    _capture = null;
                    break;
                case DetachedNativeReleaseTarget.Encoder:
                    _encoder = null;
                    break;
                case DetachedNativeReleaseTarget.Window:
                    _window = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target));
            }
        }
    }

    private enum DetachedNativeReleaseTarget
    {
        LatestFrameSlot,
        PendingFrameCleanup,
        Capture,
        Encoder,
        Window,
    }

    private sealed record DetachedNativeReleaseAttempt(
        DetachedNativeReleaseTarget Target,
        Task<Exception?> Completion);

    private sealed class DetachedNativeCleanupPendingException : InvalidOperationException
    {
        internal DetachedNativeCleanupPendingException()
            : base(
                "A native capture or encoding operation ignored cancellation; its complete " +
                "resource bundle is quarantined until the detached cleanup owner finishes.")
        {
        }
    }

    private sealed class RetainedCaptureEncodingStartException : Exception
    {
        internal RetainedCaptureEncodingStartException(
            Exception primaryFailure,
            Exception cleanupFailure,
            CaptureEncodingLease cleanupOwner)
            : base(
                "Capture startup failed and capture/encoder cleanup must be retried.",
                primaryFailure)
        {
            PrimaryFailure = primaryFailure;
            CleanupFailure = cleanupFailure;
            CleanupOwner = cleanupOwner;
        }

        internal Exception PrimaryFailure { get; }

        internal Exception CleanupFailure { get; }

        internal CaptureEncodingLease CleanupOwner { get; }
    }

    private sealed class CaptureEncodingLease :
        IAsyncDisposable,
        IPausableActiveWallpaperLease,
        IActiveWallpaperHealthSource
    {
        private static readonly TimeSpan FrameLivenessDeadline = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan PumpLocalCleanupRetryDelay = TimeSpan.FromSeconds(1);

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _ownershipSync = new();
        private readonly IWallpaperWindowCaptureFactory _captureFactory;
        private IWallpaperEngineWindowLease? _window;
        private readonly DynamicWallpaperRenderProfile _profile;
        private IFragmentedMp4WallpaperEncoder? _encoder;
        private readonly ICapturePauseBoundaryFragmentedMp4WallpaperEncoder _pauseBoundary;
        private readonly EncodedWallpaperStreamBuffer _buffer;
        private readonly ICaptureFrameDeadlineScheduler _frameDeadlineScheduler;
        private readonly IWallpaperFramePacerClock _framePacerClock;
        private readonly ICaptureFrameDeadlineScheduler _nativeOwnerDeadlineScheduler;
        private readonly TimeSpan _nativeOwnerReleaseTimeout;
        private readonly DetachedNativeCleanupRegistry _detachedNativeCleanup;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _captureCancellation;
        private Task? _captureCancellationRequest;
        private IWallpaperWindowCaptureSession? _capture;
        private Task? _pump;
        private LatestWallpaperCapturedFrameSlot? _latestFrameSlot;
        private LatestWallpaperCapturedFrameSlot? _pendingFrameCleanup;
        private LatestWallpaperCapturedFrameSlot? _detachedPumpFrameCleanup;
        private DetachedNativeCleanupOwner? _detachedNativeOwner;
        private bool _paused;
        private bool _ownsWindowCleanup;
        private int _nativeOwnershipDetached;
        private int _pumpRetainsLocalOwnership;
        private int _disposeStarted;
        private int _disposed;

        private CaptureEncodingLease(
            IWallpaperWindowCaptureFactory captureFactory,
            IWallpaperEngineWindowLease window,
            DynamicWallpaperRenderProfile profile,
            IFragmentedMp4WallpaperEncoder encoder,
            ICapturePauseBoundaryFragmentedMp4WallpaperEncoder pauseBoundary,
            EncodedWallpaperStreamBuffer buffer,
            ICaptureFrameDeadlineScheduler frameDeadlineScheduler,
            IWallpaperFramePacerClock framePacerClock,
            ICaptureFrameDeadlineScheduler nativeOwnerDeadlineScheduler,
            TimeSpan nativeOwnerReleaseTimeout,
            DetachedNativeCleanupRegistry detachedNativeCleanup)
        {
            _captureFactory = captureFactory;
            _window = window;
            _profile = profile;
            _encoder = encoder;
            _pauseBoundary = pauseBoundary;
            _buffer = buffer;
            _frameDeadlineScheduler = frameDeadlineScheduler;
            _framePacerClock = framePacerClock;
            _nativeOwnerDeadlineScheduler = nativeOwnerDeadlineScheduler;
            _nativeOwnerReleaseTimeout = nativeOwnerReleaseTimeout;
            _detachedNativeCleanup = detachedNativeCleanup;
        }

        public Task Completion => _completion.Task;

        internal bool NativeOwnershipDetached =>
            Volatile.Read(ref _nativeOwnershipDetached) != 0;

        internal bool OwnsWindowCleanup =>
            _ownsWindowCleanup || NativeOwnershipDetached;

        private bool ShouldReleasePumpLocalResources =>
            Volatile.Read(ref _nativeOwnershipDetached) == 0 ||
            Volatile.Read(ref _pumpRetainsLocalOwnership) != 0;

        private bool PumpLocalsBelongToDetachedPump =>
            Volatile.Read(ref _nativeOwnershipDetached) != 0 &&
            Volatile.Read(ref _pumpRetainsLocalOwnership) != 0;

        internal static async ValueTask<CaptureEncodingLease> StartAsync(
            IWallpaperWindowCaptureFactory captureFactory,
            IWallpaperEngineWindowLease window,
            DynamicWallpaperRenderProfile profile,
            IFragmentedMp4WallpaperEncoder encoder,
            ICapturePauseBoundaryFragmentedMp4WallpaperEncoder pauseBoundary,
            EncodedWallpaperStreamBuffer buffer,
            ICaptureFrameDeadlineScheduler frameDeadlineScheduler,
            IWallpaperFramePacerClock framePacerClock,
            ICaptureFrameDeadlineScheduler nativeOwnerDeadlineScheduler,
            TimeSpan nativeOwnerReleaseTimeout,
            DetachedNativeCleanupRegistry detachedNativeCleanup,
            CancellationToken cancellationToken)
        {
            var lease = new CaptureEncodingLease(
                captureFactory,
                window,
                profile,
                encoder,
                pauseBoundary,
                buffer,
                frameDeadlineScheduler,
                framePacerClock,
                nativeOwnerDeadlineScheduler,
                nativeOwnerReleaseTimeout,
                detachedNativeCleanup);
            try
            {
                await lease.StartCaptureAsync(cancellationToken).ConfigureAwait(false);
                lease._ownsWindowCleanup = true;
                return lease;
            }
            catch (Exception startFailure)
            {
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    throw new RetainedCaptureEncodingStartException(
                        startFailure,
                        cleanupFailure,
                        lease);
                }

                ExceptionDispatchInfo.Capture(startFailure).Throw();
                throw;
            }
        }

        public async ValueTask SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0 ||
                Volatile.Read(ref _disposed) != 0,
                this);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposeStarted) != 0 ||
                    Volatile.Read(ref _disposed) != 0,
                    this);
                if (_paused == paused)
                {
                    return;
                }

                var window = _window ?? throw new ObjectDisposedException(GetType().Name);

                if (paused)
                {
                    try
                    {
                        await StopCaptureAsync().ConfigureAwait(false);
                        await _pauseBoundary
                            .DiscardPendingFragmentForCapturePauseAsync(cancellationToken)
                            .ConfigureAwait(false);
                        await window.SetPausedAsync(true, cancellationToken)
                            .ConfigureAwait(false);
                        _paused = true;
                        return;
                    }
                    catch (Exception failure)
                    {
                        try
                        {
                            await StartCaptureAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception rollbackFailure)
                        {
                            throw new AggregateException(
                                "The capture session could not pause or roll back safely.",
                                failure,
                                rollbackFailure);
                        }

                        ExceptionDispatchInfo.Capture(failure).Throw();
                    }
                }

                await window.SetPausedAsync(false, cancellationToken).ConfigureAwait(false);
                try
                {
                    await StartCaptureAsync(cancellationToken).ConfigureAwait(false);
                    _paused = false;
                }
                catch
                {
                    await window.SetPausedAsync(true, CancellationToken.None)
                        .ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _disposeStarted, 1);
            List<Exception>? failures = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                try
                {
                    await StopCaptureAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }

                if (_capture is null &&
                    _pump is null &&
                    _latestFrameSlot is null &&
                    _pendingFrameCleanup is null &&
                    _detachedNativeOwner is null &&
                    _encoder is not null)
                {
                    var encoder = _encoder;
                    var encoderFailure = await ReleaseOuterResourceBeforeDeadlineAsync(
                            encoder,
                            DetachedNativeReleaseTarget.Encoder,
                            () =>
                            {
                                if (ReferenceEquals(_encoder, encoder))
                                {
                                    _encoder = null;
                                }
                            })
                        .ConfigureAwait(false);
                    if (encoderFailure is not null)
                    {
                        (failures ??= []).Add(encoderFailure);
                    }
                }

                if (_capture is null &&
                    _pump is null &&
                    _latestFrameSlot is null &&
                    _pendingFrameCleanup is null &&
                    _detachedNativeOwner is null &&
                    _encoder is null &&
                    _ownsWindowCleanup &&
                    _window is not null)
                {
                    var window = _window;
                    var windowFailure = await ReleaseOuterResourceBeforeDeadlineAsync(
                            window,
                            DetachedNativeReleaseTarget.Window,
                            () =>
                            {
                                if (ReferenceEquals(_window, window))
                                {
                                    _window = null;
                                }
                            })
                        .ConfigureAwait(false);
                    if (windowFailure is not null)
                    {
                        (failures ??= []).Add(windowFailure);
                    }
                }

                if (!_ownsWindowCleanup &&
                    _capture is null &&
                    _pump is null &&
                    _latestFrameSlot is null &&
                    _pendingFrameCleanup is null &&
                    _detachedNativeOwner is null &&
                    _encoder is null)
                {
                    // StartCaptureAsync did not return the lease to its caller, so the outer
                    // startup cleanup boundary remains the logical owner of this duplicate.
                    _window = null;
                }

                _completion.TrySetCanceled();
                if (_capture is null &&
                    _pump is null &&
                    _latestFrameSlot is null &&
                    _pendingFrameCleanup is null &&
                    _detachedNativeOwner is null &&
                    _encoder is null &&
                    _window is null)
                {
                    Volatile.Write(ref _disposed, 1);
                }
            }
            finally
            {
                _gate.Release();
            }

            if (failures is [var failure])
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(
                    "The capture and encoder resources could not be released.",
                    failures);
            }
        }

        private async ValueTask StartCaptureAsync(CancellationToken cancellationToken)
        {
            if (_capture is not null ||
                _pump is not null ||
                _latestFrameSlot is not null ||
                _pendingFrameCleanup is not null)
            {
                throw new InvalidOperationException(
                    "The previous capture session has not been released.");
            }

            var encoder = _encoder ?? throw new ObjectDisposedException(GetType().Name);
            var window = _window ?? throw new ObjectDisposedException(GetType().Name);
            var captureTarget = window.CaptureTarget;
            if (captureTarget is null)
            {
                throw new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.CaptureTargetUnavailable);
            }

            var request = new WallpaperWindowCaptureRequest(
                encoder.Descriptor.Generation,
                captureTarget,
                _profile.Width,
                _profile.Height,
                _profile.CaptureFrameRate);
            var capture = await _captureFactory
                .StartAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var stop = new CancellationTokenSource();
            var latestFrameSlot = _profile.CaptureFrameRate > _profile.FrameRate
                ? new LatestWallpaperCapturedFrameSlot()
                : null;
            _capture = capture;
            _captureCancellation = stop;
            _captureCancellationRequest = null;
            _latestFrameSlot = latestFrameSlot;
            _pump = PumpAsync(capture, encoder, latestFrameSlot, stop.Token);
        }

        private async ValueTask StopCaptureAsync()
        {
            var stop = _captureCancellation;
            var pump = _pump;
            List<Exception>? failures = null;
            var stopCancellation = _captureCancellationRequest;
            if (stop is not null && stopCancellation is null)
            {
                stopCancellation = RequestCancellationAsync(stop);
                _captureCancellationRequest = stopCancellation;
            }

            if (_detachedNativeOwner is null &&
                (pump is not null || stopCancellation is not null))
            {
                var stoppingOwner = pump is null
                    ? stopCancellation!
                    : stopCancellation is null
                        ? pump
                        : Task.WhenAll(pump, stopCancellation);
                if (!await WaitForNativeOwnerAsync(stoppingOwner).ConfigureAwait(false))
                {
                    if (_detachedNativeOwner is null)
                    {
                        try
                        {
                            DetachNativeOwnership(
                                stoppingOwner,
                                pendingRead: null,
                                directFrame: null,
                                frames: null,
                                readCancellation: null,
                                encodeCancellation: null,
                                readDeadlineCancellation: null,
                                pumpRetainsLocalOwnership: pump is not null);
                        }
                        catch (InvalidOperationException) when (
                            _detachedNativeOwner is not null)
                        {
                            // The pump won the ownership race and transferred the same outer
                            // resources while this stop deadline was expiring.
                        }
                    }
                }
                else
                {
                    if (pump is not null)
                    {
                        try
                        {
                            await pump.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (
                            stop?.IsCancellationRequested == true)
                        {
                        }
                        catch (Exception exception)
                        {
                            (failures ??= []).Add(exception);
                        }

                        if (ReferenceEquals(_pump, pump))
                        {
                            _pump = null;
                        }
                    }

                    if (stopCancellation is not null)
                    {
                        try
                        {
                            await stopCancellation.ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            (failures ??= []).Add(exception);
                        }

                        if (ReferenceEquals(_captureCancellationRequest, stopCancellation))
                        {
                            _captureCancellationRequest = null;
                        }
                    }
                }
            }

            var detachedOwner = _detachedNativeOwner;
            if (detachedOwner is not null)
            {
                if (detachedOwner.Completion.IsCompleted)
                {
                    await detachedOwner.Completion.ConfigureAwait(false);
                    _detachedNativeOwner = null;
                    _detachedPumpFrameCleanup = null;
                    if (_pump?.IsCompleted == true)
                    {
                        _pump = null;
                    }
                }
                else
                {
                    (failures ??= []).Add(new DetachedNativeCleanupPendingException());
                }
            }
            else
            {
                var latestFrameSlot = _latestFrameSlot;
                if (latestFrameSlot is not null)
                {
                    var releaseFailure = await ReleaseOuterResourceBeforeDeadlineAsync(
                            latestFrameSlot,
                            DetachedNativeReleaseTarget.LatestFrameSlot,
                            () =>
                            {
                                if (ReferenceEquals(_latestFrameSlot, latestFrameSlot))
                                {
                                    _latestFrameSlot = null;
                                }
                            })
                        .ConfigureAwait(false);
                    if (releaseFailure is not null)
                    {
                        (failures ??= []).Add(releaseFailure);
                    }
                }

                if (_detachedNativeOwner is null &&
                    _latestFrameSlot is null)
                {
                    var pendingFrameCleanup = _pendingFrameCleanup;
                    if (pendingFrameCleanup is not null)
                    {
                        var releaseFailure = await ReleaseOuterResourceBeforeDeadlineAsync(
                                pendingFrameCleanup,
                                DetachedNativeReleaseTarget.PendingFrameCleanup,
                                () =>
                                {
                                    if (ReferenceEquals(
                                            _pendingFrameCleanup,
                                            pendingFrameCleanup))
                                    {
                                        _pendingFrameCleanup = null;
                                    }
                                })
                            .ConfigureAwait(false);
                        if (releaseFailure is not null)
                        {
                            (failures ??= []).Add(releaseFailure);
                        }
                    }
                }

                if (_detachedNativeOwner is null &&
                    _latestFrameSlot is null &&
                    _pendingFrameCleanup is null)
                {
                    var capture = _capture;
                    if (capture is not null)
                    {
                        var releaseFailure = await ReleaseOuterResourceBeforeDeadlineAsync(
                                capture,
                                DetachedNativeReleaseTarget.Capture,
                                () =>
                                {
                                    if (ReferenceEquals(_capture, capture))
                                    {
                                        _capture = null;
                                    }
                                })
                            .ConfigureAwait(false);
                        if (releaseFailure is not null)
                        {
                            (failures ??= []).Add(releaseFailure);
                        }
                    }
                }
            }

            if (_pump is null && ReferenceEquals(_captureCancellation, stop))
            {
                _captureCancellation = null;
                _captureCancellationRequest = null;
                stop?.Dispose();
            }
            if (failures is [var failure])
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(
                    "The capture session could not be stopped cleanly.",
                    failures);
            }
        }

        private async ValueTask<Exception?> ReleaseOuterResourceBeforeDeadlineAsync(
            IAsyncDisposable resource,
            DetachedNativeReleaseTarget target,
            Action markReleased)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ArgumentNullException.ThrowIfNull(markReleased);
            var completion = Task.Run(
                async () =>
                {
                    try
                    {
                        await resource.DisposeAsync().ConfigureAwait(false);
                        return null;
                    }
                    catch (Exception exception)
                    {
                        return exception;
                    }
                },
                CancellationToken.None);
            var releaseAttempt = new DetachedNativeReleaseAttempt(target, completion);
            if (!await WaitForNativeOwnerAsync(completion).ConfigureAwait(false))
            {
                DetachNativeOwnership(
                    completion,
                    pendingRead: null,
                    directFrame: null,
                    frames: null,
                    readCancellation: null,
                    encodeCancellation: null,
                    readDeadlineCancellation: null,
                    releaseAttempt: releaseAttempt);
                return new DetachedNativeCleanupPendingException();
            }

            var failure = await completion.ConfigureAwait(false);
            if (failure is null)
            {
                markReleased();
            }

            return failure;
        }

        private async Task PumpAsync(
            IWallpaperWindowCaptureSession capture,
            IFragmentedMp4WallpaperEncoder encoder,
            LatestWallpaperCapturedFrameSlot? latestFrameSlot,
            CancellationToken cancellationToken)
        {
            var readCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var frames = capture
                .ReadFramesAsync(readCancellation.Token)
                .GetAsyncEnumerator(readCancellation.Token);
            try
            {
                if (latestFrameSlot is null)
                {
                    await PumpDirectFramesAsync(
                            frames,
                            readCancellation,
                            encoder,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await PumpPacedFallbackFramesAsync(
                            frames,
                            readCancellation,
                            encoder,
                            latestFrameSlot,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                PublishHealthFailure(exception);
            }
            finally
            {
                if (ShouldReleasePumpLocalResources)
                {
                    try
                    {
                        await ReleasePumpLocalWithRetryAsync(frames).ConfigureAwait(false);
                    }
                    finally
                    {
                        readCancellation.Dispose();
                    }
                }
            }
        }

        private async Task PumpDirectFramesAsync(
            IAsyncEnumerator<IWallpaperCapturedFrame> frames,
            CancellationTokenSource readCancellation,
            IFragmentedMp4WallpaperEncoder encoder,
            CancellationToken cancellationToken)
        {
            while (await MoveNextBeforeDeadlineAsync(
                    frames,
                    readCancellation,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                var frame = frames.Current;
                try
                {
                    await EncodeDirectFrameBeforeDeadlineAsync(
                            encoder,
                            frame,
                            frames,
                            readCancellation,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    if (ShouldReleasePumpLocalResources)
                    {
                        await ReleasePumpLocalWithRetryAsync(frame).ConfigureAwait(false);
                    }
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new EndOfStreamException(
                    "The owned Wallpaper Engine capture ended unexpectedly.");
            }
        }

        private async ValueTask EncodeDirectFrameBeforeDeadlineAsync(
            IFragmentedMp4WallpaperEncoder encoder,
            IWallpaperCapturedFrame frame,
            IAsyncEnumerator<IWallpaperCapturedFrame> frames,
            CancellationTokenSource readCancellation,
            CancellationToken cancellationToken)
        {
            var encodeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var deadlineCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var encode = Task.Run(
                    async () => await encoder
                        .EncodeAsync(frame, _buffer, encodeCancellation.Token)
                        .ConfigureAwait(false),
                    CancellationToken.None);
                var deadline = _frameDeadlineScheduler
                    .WaitAsync(FrameLivenessDeadline, deadlineCancellation.Token)
                    .AsTask();
                var completed = await Task.WhenAny(encode, deadline).ConfigureAwait(false);
                if (ReferenceEquals(completed, encode))
                {
                    await CancelAndObserveWaitAsync(deadlineCancellation, deadline)
                        .ConfigureAwait(false);
                    await encode.ConfigureAwait(false);
                    return;
                }

                try
                {
                    await deadline.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    var canceledRequest = RequestCancellationAsync(encodeCancellation);
                    var cancellationFailure = await ObserveCanceledEncodingAsync(
                            encode,
                            canceledRequest,
                            frame,
                            frames,
                            readCancellation,
                            pendingRead: null,
                            readDeadlineCancellation: null,
                            encodeCancellation)
                        .ConfigureAwait(false);
                    if (cancellationFailure is not null)
                    {
                        ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
                    }

                    throw;
                }

                var failure = CreateRenderTierFailure();
                PublishHealthFailure(failure);
                var deadlineCancellationRequest = RequestCancellationAsync(encodeCancellation);
                var encodeFailure = await ObserveCanceledEncodingAsync(
                        encode,
                        deadlineCancellationRequest,
                        frame,
                        frames,
                        readCancellation,
                        pendingRead: null,
                        readDeadlineCancellation: null,
                        encodeCancellation)
                    .ConfigureAwait(false);
                if (encodeFailure is DetachedNativeCleanupPendingException)
                {
                    ExceptionDispatchInfo.Capture(encodeFailure).Throw();
                }

                throw encodeFailure is null
                    ? failure
                    : new DynamicWallpaperUnavailableException(
                        CreateRenderTierFailure().ReasonCode,
                        encodeFailure);
            }
            finally
            {
                if (ShouldReleasePumpLocalResources)
                {
                    encodeCancellation.Dispose();
                }
            }
        }

        private async Task PumpPacedFallbackFramesAsync(
            IAsyncEnumerator<IWallpaperCapturedFrame> frames,
            CancellationTokenSource readCancellation,
            IFragmentedMp4WallpaperEncoder encoder,
            LatestWallpaperCapturedFrameSlot latestFrameSlot,
            CancellationToken cancellationToken)
        {
            if (!await MoveNextBeforeDeadlineAsync(
                    frames,
                    readCancellation,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new EndOfStreamException(
                    "The owned Wallpaper Engine capture ended unexpectedly.");
            }

            await latestFrameSlot.ReplaceAsync(frames.Current).ConfigureAwait(false);
            await EncodeFallbackFrameBeforeDeadlineAsync(
                    encoder,
                    latestFrameSlot.Current ?? throw new InvalidOperationException(
                        "The fallback frame slot did not retain its first frame."),
                    frames,
                    readCancellation,
                    pendingRead: null,
                    readDeadlineCancellation: null,
                    cancellationToken)
                .ConfigureAwait(false);

            var pacer = new FixedPointWallpaperFramePacer(_profile.FrameRate);
            var cadenceMonitor = new SustainedWallpaperCadenceMonitor(
                FrameLivenessDeadline);
            pacer.Start(_framePacerClock.GetMonotonicNow());
            Task<bool>? pendingRead = null;
            CancellationTokenSource? readDeadlineCancellation = null;
            Task? readDeadline = null;
            Task? tick = null;
            try
            {
                StartPendingRead();
                while (true)
                {
                    for (var drained = 0;
                         drained < WindowsGraphicsCaptureFactory.BufferedFrameCapacity &&
                         pendingRead!.IsCompleted;
                         drained++)
                    {
                        await AdoptCompletedReadAsync().ConfigureAwait(false);
                    }

                    if (tick is null)
                    {
                        var now = _framePacerClock.GetMonotonicNow();
                        if (pacer.TryTakeDueSample(
                                now,
                                out var delay,
                                out var reanchored))
                        {
                            await EncodePacedSampleAsync(reanchored).ConfigureAwait(false);
                            continue;
                        }

                        tick = _framePacerClock
                            .WaitAsync(delay, cancellationToken)
                            .AsTask();
                    }

                    _ = await Task.WhenAny(pendingRead!, readDeadline!, tick)
                        .ConfigureAwait(false);
                    if (pendingRead!.IsCompleted)
                    {
                        await AdoptCompletedReadAsync().ConfigureAwait(false);
                    }

                    if (readDeadline!.IsCompleted)
                    {
                        await readDeadline.ConfigureAwait(false);
                        if (_profile == DynamicWallpaperRenderProfiles.Compatibility)
                        {
                            readDeadlineCancellation!.Dispose();
                            readDeadlineCancellation =
                                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            readDeadline = _frameDeadlineScheduler
                                .WaitAsync(
                                    FrameLivenessDeadline,
                                    readDeadlineCancellation.Token)
                                .AsTask();
                            continue;
                        }

                        var failure = CreateRenderTierFailure();
                        PublishHealthFailure(failure);
                        var cancellationRequest = RequestCancellationAsync(readCancellation);
                        await ObserveCanceledMoveNextAsync(
                                frames,
                                pendingRead!,
                                readCancellation,
                                cancellationRequest)
                            .ConfigureAwait(false);
                        throw failure;
                    }

                    if (tick.IsCompleted)
                    {
                        await tick.ConfigureAwait(false);
                        tick = null;
                    }
                }
            }
            finally
            {
                if (ShouldReleasePumpLocalResources)
                {
                    if (readDeadlineCancellation is not null && readDeadline is not null)
                    {
                        await CancelAndObserveWaitAsync(
                                readDeadlineCancellation,
                                readDeadline)
                            .ConfigureAwait(false);
                        readDeadlineCancellation.Dispose();
                    }

                    var cancellationRequest = RequestCancellationAsync(readCancellation);
                    if (pendingRead is not null)
                    {
                        await ObserveCanceledMoveNextAsync(
                                frames,
                                pendingRead,
                                readCancellation,
                                cancellationRequest)
                            .ConfigureAwait(false);
                    }
                    else if (!await WaitForNativeOwnerAsync(cancellationRequest)
                                 .ConfigureAwait(false))
                    {
                        if (!PumpLocalsBelongToDetachedPump)
                        {
                            try
                            {
                                DetachNativeOwnership(
                                    cancellationRequest,
                                    pendingRead: null,
                                    directFrame: null,
                                    frames,
                                    readCancellation,
                                    encodeCancellation: null,
                                    readDeadlineCancellation);
                            }
                            catch (InvalidOperationException) when (
                                PumpLocalsBelongToDetachedPump)
                            {
                            }
                        }

                        if (PumpLocalsBelongToDetachedPump)
                        {
                            await cancellationRequest.ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await cancellationRequest.ConfigureAwait(false);
                    }
                }
            }

            void StartPendingRead()
            {
                readDeadlineCancellation?.Dispose();
                pendingRead = frames.MoveNextAsync().AsTask();
                readDeadlineCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readDeadline = _frameDeadlineScheduler
                    .WaitAsync(FrameLivenessDeadline, readDeadlineCancellation.Token)
                    .AsTask();
            }

            async ValueTask AdoptCompletedReadAsync()
            {
                await CancelAndObserveWaitAsync(
                        readDeadlineCancellation!,
                        readDeadline!)
                    .ConfigureAwait(false);
                if (!await pendingRead!.ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new EndOfStreamException(
                        "The owned Wallpaper Engine capture ended unexpectedly.");
                }

                await latestFrameSlot.ReplaceAsync(frames.Current).ConfigureAwait(false);
                StartPendingRead();
            }

            async ValueTask EncodePacedSampleAsync(bool pacerReanchored)
            {
                var current = latestFrameSlot.Current ?? throw new EndOfStreamException(
                    "The fallback capture did not retain a replayable frame.");
                await EncodeFallbackFrameBeforeDeadlineAsync(
                        encoder,
                        current,
                        frames,
                        readCancellation,
                        pendingRead,
                        readDeadlineCancellation,
                        cancellationToken)
                    .ConfigureAwait(false);
                var completedAt = _framePacerClock.GetMonotonicNow();
                var isBehind = pacerReanchored ||
                    pacer.ReanchorIfSampleCompletionIsBehind(completedAt);
                if (cadenceMonitor.Observe(isBehind, completedAt) &&
                    _profile != DynamicWallpaperRenderProfiles.Compatibility)
                {
                    throw new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.FallbackRenderTierUnavailable);
                }
            }
        }

        private async ValueTask EncodeFallbackFrameBeforeDeadlineAsync(
            IFragmentedMp4WallpaperEncoder encoder,
            IWallpaperCapturedFrame frame,
            IAsyncEnumerator<IWallpaperCapturedFrame> frames,
            CancellationTokenSource readCancellation,
            Task<bool>? pendingRead,
            CancellationTokenSource? readDeadlineCancellation,
            CancellationToken cancellationToken)
        {
            var encodeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var deadlineCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var encode = Task.Run(
                    async () => await encoder
                        .EncodeAsync(frame, _buffer, encodeCancellation.Token)
                        .ConfigureAwait(false),
                    CancellationToken.None);
                var deadline = _framePacerClock
                    .WaitAsync(FrameLivenessDeadline, deadlineCancellation.Token)
                    .AsTask();
                var completed = await Task.WhenAny(encode, deadline).ConfigureAwait(false);
                if (ReferenceEquals(completed, encode))
                {
                    await CancelAndObserveWaitAsync(deadlineCancellation, deadline)
                        .ConfigureAwait(false);
                    await encode.ConfigureAwait(false);
                    return;
                }

                try
                {
                    await deadline.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    var canceledRequest = RequestCancellationAsync(encodeCancellation);
                    var cancellationFailure = await ObserveCanceledEncodingAsync(
                            encode,
                            canceledRequest,
                            directFrame: null,
                            frames,
                            readCancellation,
                            pendingRead,
                            readDeadlineCancellation,
                            encodeCancellation)
                        .ConfigureAwait(false);
                    if (cancellationFailure is not null)
                    {
                        ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
                    }

                    throw;
                }

                var failure = new DynamicWallpaperUnavailableException(
                    DynamicWallpaperCapabilityReasonCode.FallbackRenderTierUnavailable);
                PublishHealthFailure(failure);
                var deadlineCancellationRequest = RequestCancellationAsync(encodeCancellation);
                var encodeFailure = await ObserveCanceledEncodingAsync(
                        encode,
                        deadlineCancellationRequest,
                        directFrame: null,
                        frames,
                        readCancellation,
                        pendingRead,
                        readDeadlineCancellation,
                        encodeCancellation)
                    .ConfigureAwait(false);
                if (encodeFailure is DetachedNativeCleanupPendingException)
                {
                    ExceptionDispatchInfo.Capture(encodeFailure).Throw();
                }

                throw encodeFailure is null
                    ? failure
                    : new DynamicWallpaperUnavailableException(
                        DynamicWallpaperCapabilityReasonCode.FallbackRenderTierUnavailable,
                        encodeFailure);
            }
            finally
            {
                if (ShouldReleasePumpLocalResources)
                {
                    encodeCancellation.Dispose();
                }
            }
        }

        private static async ValueTask CancelAndObserveWaitAsync(
            CancellationTokenSource cancellation,
            Task wait)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await wait.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        private static Task RequestCancellationAsync(
            CancellationTokenSource cancellation)
        {
            ArgumentNullException.ThrowIfNull(cancellation);
            return Task.Run(
                async () => await cancellation.CancelAsync().ConfigureAwait(false),
                CancellationToken.None);
        }

        private async ValueTask ReleasePumpLocalWithRetryAsync(IAsyncDisposable resource)
        {
            while (true)
            {
                try
                {
                    await resource.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                catch (Exception exception)
                {
                    PublishHealthFailure(exception);
                    await Task.Delay(PumpLocalCleanupRetryDelay)
                        .ConfigureAwait(false);
                }
            }
        }

        private async ValueTask<Exception?> ObserveCanceledEncodingAsync(
            Task encode,
            Task cancellationRequest,
            IWallpaperCapturedFrame? directFrame,
            IAsyncEnumerator<IWallpaperCapturedFrame> frames,
            CancellationTokenSource readCancellation,
            Task<bool>? pendingRead,
            CancellationTokenSource? readDeadlineCancellation,
            CancellationTokenSource encodeCancellation)
        {
            var nativeOwner = Task.WhenAll(encode, cancellationRequest);
            if (!await WaitForNativeOwnerAsync(nativeOwner).ConfigureAwait(false))
            {
                if (!PumpLocalsBelongToDetachedPump)
                {
                    try
                    {
                        DetachNativeOwnership(
                            nativeOwner,
                            pendingRead,
                            directFrame,
                            frames,
                            readCancellation,
                            encodeCancellation,
                            readDeadlineCancellation);
                    }
                    catch (InvalidOperationException) when (PumpLocalsBelongToDetachedPump)
                    {
                        // StopCaptureAsync transferred the whole pump while this inner native
                        // deadline was expiring. The detached pump remains the unique owner of
                        // these locals and may wait without blocking the actor.
                    }
                }

                if (!PumpLocalsBelongToDetachedPump)
                {
                    return new DetachedNativeCleanupPendingException();
                }
            }

            Exception? cancellationFailure = null;
            try
            {
                await cancellationRequest.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }

            try
            {
                await encode.ConfigureAwait(false);
                return cancellationFailure;
            }
            catch (OperationCanceledException) when (encodeCancellation.IsCancellationRequested)
            {
                return cancellationFailure;
            }
            catch (Exception exception)
            {
                return cancellationFailure is null
                    ? exception
                    : new AggregateException(cancellationFailure, exception);
            }
        }

        private async ValueTask<bool> WaitForNativeOwnerAsync(Task nativeOperation)
        {
            using var deadlineCancellation = new CancellationTokenSource();
            var deadline = _nativeOwnerDeadlineScheduler
                .WaitAsync(_nativeOwnerReleaseTimeout, deadlineCancellation.Token)
                .AsTask();
            var completed = await Task.WhenAny(nativeOperation, deadline).ConfigureAwait(false);
            if (ReferenceEquals(completed, nativeOperation) && !deadline.IsCompleted)
            {
                await CancelAndObserveWaitAsync(deadlineCancellation, deadline)
                    .ConfigureAwait(false);
                return true;
            }

            await deadline.ConfigureAwait(false);
            return false;
        }

        private void DetachNativeOwnership(
            Task nativeOperation,
            Task<bool>? pendingRead,
            IWallpaperCapturedFrame? directFrame,
            IAsyncEnumerator<IWallpaperCapturedFrame>? frames,
            CancellationTokenSource? readCancellation,
            CancellationTokenSource? encodeCancellation,
            CancellationTokenSource? readDeadlineCancellation,
            bool pumpRetainsLocalOwnership = false,
            DetachedNativeReleaseAttempt? releaseAttempt = null)
        {
            lock (_ownershipSync)
            {
                if (Volatile.Read(ref _nativeOwnershipDetached) != 0)
                {
                    throw new InvalidOperationException(
                        "The native capture/encoding ownership was already detached.");
                }

                var capture = _capture;
                var encoder = _encoder;
                var window = _window ?? throw new InvalidOperationException(
                    "The owned wallpaper window was lost before native ownership detachment.");
                var stop = _captureCancellation;
                var cancellations = new CancellationTokenSource?[]
                    {
                        stop,
                        readCancellation,
                        encodeCancellation,
                        readDeadlineCancellation,
                    }
                    .OfType<CancellationTokenSource>()
                    .Distinct()
                    .ToArray();
                var ownedOperations = new List<Task>
                {
                    nativeOperation,
                };
                if (_captureCancellationRequest is not null)
                {
                    ownedOperations.Add(_captureCancellationRequest);
                }

                ownedOperations.AddRange(cancellations.Select(RequestCancellationAsync));
                var pendingFrameCleanup = _pendingFrameCleanup;
                if (pumpRetainsLocalOwnership && pendingFrameCleanup is null)
                {
                    pendingFrameCleanup = new LatestWallpaperCapturedFrameSlot();
                }

                var detachedOwner = new DetachedNativeCleanupOwner(
                    Task.WhenAll(ownedOperations),
                    pendingRead,
                    releaseAttempt,
                    directFrame,
                    _latestFrameSlot,
                    pendingFrameCleanup,
                    frames,
                    capture,
                    encoder,
                    window,
                    cancellations);
                _detachedNativeCleanup.Register(
                    detachedOwner,
                    () =>
                    {
                        _detachedNativeOwner = detachedOwner;
                        _capture = null;
                        _captureCancellation = null;
                        _captureCancellationRequest = null;
                        _encoder = null;
                        _window = null;
                        _latestFrameSlot = null;
                        _pendingFrameCleanup = null;
                        _detachedPumpFrameCleanup = pumpRetainsLocalOwnership
                            ? pendingFrameCleanup
                            : null;
                        Volatile.Write(
                            ref _pumpRetainsLocalOwnership,
                            pumpRetainsLocalOwnership ? 1 : 0);
                        Volatile.Write(ref _nativeOwnershipDetached, 1);
                    });
            }
        }

        private async ValueTask<bool> MoveNextBeforeDeadlineAsync(
            IAsyncEnumerator<IWallpaperCapturedFrame> frames,
            CancellationTokenSource readCancellation,
            CancellationToken cancellationToken)
        {
            using var deadlineCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var moveNext = frames.MoveNextAsync().AsTask();
            var deadline = _frameDeadlineScheduler
                .WaitAsync(FrameLivenessDeadline, deadlineCancellation.Token)
                .AsTask();
            var completed = await Task.WhenAny(moveNext, deadline).ConfigureAwait(false);
            if (ReferenceEquals(completed, moveNext))
            {
                await deadlineCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await deadline.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    deadlineCancellation.IsCancellationRequested)
                {
                }

                return await moveNext.ConfigureAwait(false);
            }

            try
            {
                await deadline.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var canceledRequest = RequestCancellationAsync(readCancellation);
                await ObserveCanceledMoveNextAsync(
                        frames,
                        moveNext,
                        readCancellation,
                        canceledRequest)
                    .ConfigureAwait(false);
                throw;
            }

            var failure = CreateRenderTierFailure();
            PublishHealthFailure(failure);
            var deadlineCancellationRequest = RequestCancellationAsync(readCancellation);
            await ObserveCanceledMoveNextAsync(
                    frames,
                    moveNext,
                    readCancellation,
                    deadlineCancellationRequest)
                .ConfigureAwait(false);
            throw failure;
        }

        private async ValueTask ObserveCanceledMoveNextAsync(
            IAsyncEnumerator<IWallpaperCapturedFrame> frames,
            Task<bool> moveNext,
            CancellationTokenSource readCancellation,
            Task cancellationRequest)
        {
            var nativeOwner = Task.WhenAll(moveNext, cancellationRequest);
            if (!await WaitForNativeOwnerAsync(nativeOwner).ConfigureAwait(false))
            {
                if (!PumpLocalsBelongToDetachedPump)
                {
                    try
                    {
                        DetachNativeOwnership(
                            nativeOwner,
                            moveNext,
                            directFrame: null,
                            frames,
                            readCancellation,
                            encodeCancellation: null,
                            readDeadlineCancellation: null);
                    }
                    catch (InvalidOperationException) when (PumpLocalsBelongToDetachedPump)
                    {
                        // The detached outer pump owns the enumerator and read cancellation.
                    }
                }

                if (!PumpLocalsBelongToDetachedPump)
                {
                    throw new DetachedNativeCleanupPendingException();
                }
            }

            Exception? cancellationFailure = null;
            try
            {
                await cancellationRequest.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cancellationFailure = exception;
            }

            try
            {
                if (await moveNext.ConfigureAwait(false))
                {
                    var pendingFrameCleanup = PumpLocalsBelongToDetachedPump
                        ? _detachedPumpFrameCleanup ?? throw new InvalidOperationException(
                            "The detached pump lost its shared late-frame cleanup slot.")
                        : _pendingFrameCleanup ??= new LatestWallpaperCapturedFrameSlot();
                    await pendingFrameCleanup.ReplaceAsync(frames.Current).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
            {
            }

            if (cancellationFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cancellationFailure).Throw();
            }
        }

        private DynamicWallpaperUnavailableException CreateRenderTierFailure() =>
            new(
                _profile == DynamicWallpaperRenderProfiles.Primary
                    ? DynamicWallpaperCapabilityReasonCode.PrimaryRenderTierUnavailable
                    : DynamicWallpaperCapabilityReasonCode.FallbackRenderTierUnavailable);

        private void PublishHealthFailure(Exception failure)
        {
            _completion.TrySetException(failure);
            _buffer.Complete(failure);
        }
    }
}
