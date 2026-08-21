using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Runtime;
using PuppeteerSharp;
using PuppeteerSharp.Cdp;

namespace BackdropForCodex.Core.Dynamic;

/// <summary>
/// Establishes a generation-scoped encoded wallpaper stream on the sole page authorized by an
/// immutable <see cref="VerifiedCdpEndpoint"/>. This component owns only its CDP connection and
/// page stream; it disconnects from Codex only after owned DOM cleanup is confirmed and has no
/// operation that closes the browser.
/// </summary>
public sealed class PuppeteerDynamicWallpaperPageSessionFactory :
    IDynamicWallpaperPageSessionFactory,
    IDynamicWallpaperInitialPresentationReadinessSource,
    IRetryableDynamicWallpaperPageSessionCleanup,
    IAsyncDisposable
{
    private readonly IDynamicWallpaperBrowserConnector _connector;
    private readonly IDynamicWallpaperHeartbeatScheduler _heartbeatScheduler;
    private readonly InitialPageReadinessGate _readinessGate;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private FailedPageSessionCleanupOwner? _retainedCleanup;
    private int _disposeStarted;
    private int _disposed;

    public PuppeteerDynamicWallpaperPageSessionFactory()
        : this(
            new PuppeteerDynamicWallpaperBrowserConnector(),
            new PeriodicDynamicWallpaperHeartbeatScheduler(
                InjectionScriptBuilder.HeartbeatInterval),
            new InitialPageReadinessGate())
    {
    }

    internal PuppeteerDynamicWallpaperPageSessionFactory(
        IDynamicWallpaperBrowserConnector connector,
        IDynamicWallpaperHeartbeatScheduler heartbeatScheduler)
        : this(connector, heartbeatScheduler, new InitialPageReadinessGate())
    {
    }

    internal PuppeteerDynamicWallpaperPageSessionFactory(
        IDynamicWallpaperBrowserConnector connector,
        IDynamicWallpaperHeartbeatScheduler heartbeatScheduler,
        InitialPageReadinessGate readinessGate)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _heartbeatScheduler = heartbeatScheduler ??
            throw new ArgumentNullException(nameof(heartbeatScheduler));
        _readinessGate = readinessGate ?? throw new ArgumentNullException(nameof(readinessGate));
    }

    async ValueTask<DynamicWallpaperInitialPresentationReadiness>
        IDynamicWallpaperInitialPresentationReadinessSource.WaitForInitialPresentationAsync(
            VerifiedCdpEndpoint endpoint,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0 ||
                Volatile.Read(ref _disposed) != 0,
                this);
            await ReleaseRetainedCleanupCoreAsync().ConfigureAwait(false);
            return await WaitForInitialPresentationCoreAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<DynamicWallpaperActivationResult> StartAsync(
        VerifiedCdpEndpoint endpoint,
        EncodedWallpaperStreamBuffer buffer,
        DynamicWallpaperInjectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(options);

        var pendingBuffer = buffer;
        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeStarted) != 0 ||
                Volatile.Read(ref _disposed) != 0,
                this);
            await ReleaseRetainedCleanupCoreAsync().ConfigureAwait(false);
            pendingBuffer = null;
            return await StartCoreAsync(endpoint, buffer, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception primaryFailure) when (pendingBuffer is not null)
        {
            try
            {
                await pendingBuffer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new DynamicWallpaperPageSessionException(
                    "The page session could not begin and its incoming buffer cleanup failed.",
                    primaryFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
        finally
        {
            if (gateAcquired)
            {
                _operationGate.Release();
            }
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

            await ReleaseRetainedCleanupCoreAsync().ConfigureAwait(false);
            Volatile.Write(ref _disposed, 1);
            GC.SuppressFinalize(this);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    async ValueTask IRetryableDynamicWallpaperPageSessionCleanup.ReleaseRetainedCleanupAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await ReleaseRetainedCleanupCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<DynamicWallpaperActivationResult> StartCoreAsync(
        VerifiedCdpEndpoint endpoint,
        EncodedWallpaperStreamBuffer buffer,
        DynamicWallpaperInjectionOptions options,
        CancellationToken cancellationToken)
    {
        var cleanupOwner = new FailedPageSessionCleanupOwner(buffer);

        IDynamicWallpaperBrowserConnection? connection = null;
        EncodedWallpaperPageStreamLease? pageStream = null;
        InjectionCapabilityState? capabilityStateOwner = null;
        InjectionCapabilityState? stagedCapabilityState = null;
        try
        {
            if (buffer.Descriptor.Generation != options.Generation)
            {
                throw new ArgumentException(
                    "The encoded stream and page options must share one generation.",
                    nameof(options));
            }

            connection = await _connector
                .ConnectAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
            if (connection is null)
            {
                throw new InvalidOperationException(
                    "The verified browser connector returned no connection.");
            }

            cleanupOwner.SetConnection(connection);

            var initialVerification = await connection
                .VerifySolePageAsync(cancellationToken)
                .ConfigureAwait(false);
            var page = RequireSolePage(initialVerification);
            RequireExpectedTargetIdentity(page, options.ExpectedPageIdentity);
            var evidence = await page
                .ObservePresentationEvidenceAsync(cancellationToken)
                .ConfigureAwait(false);
            PresentationContractSnapshot presentation;
            CompatibilityCapabilities capabilities;
            if (options.LockedPresentationContract is null)
            {
                var contract = PresentationContractCatalog.Match(
                    evidence,
                    finalizeBaselineFallback: true);
                presentation = contract.Snapshot;
                capabilities = contract.Capabilities;
            }
            else
            {
                presentation = options.LockedPresentationContract;
                var rawCapabilities = options.CapabilityCeiling!.DowngradeWith(
                    PresentationContractCatalog.Observe(presentation, evidence));
                capabilityStateOwner = options.CapabilityState;
                if (capabilityStateOwner is null)
                {
                    // Public locked options are caller assertions, not a trusted generation
                    // context. Apply current evidence strictly and never grant recovery grace.
                    capabilities = rawCapabilities;
                }
                else
                {
                    if (options.RequireCurrentGlobalBaseline &&
                        !rawCapabilities.CanInjectGlobalWallpaper)
                    {
                        throw new WallpaperPresentationContractException(
                            "The verified Codex page did not satisfy the current global presentation baseline.");
                    }

                    stagedCapabilityState = capabilityStateOwner.CreateStagedCopy();
                    stagedCapabilityState.Begin(
                        rawCapabilities,
                        continuesCurrentGeneration: true);
                    capabilities = stagedCapabilityState.Current;
                }
            }

            if (presentation.MatchState == ContractMatchState.GlobalBaselineFailed ||
                !capabilities.CanInjectGlobalWallpaper)
            {
                throw new WallpaperPresentationContractException(
                    "The verified Codex page did not satisfy the global presentation baseline.");
            }

            await RequireSameSolePageAsync(connection, page, cancellationToken)
                .ConfigureAwait(false);
            RequireExpectedTargetIdentity(page, options.ExpectedPageIdentity);
            var innerSink = page.CreateEncodedStreamSink(capabilities);
            var verifiedSink = new RevalidatingEncodedWallpaperPageSink(
                connection,
                page,
                innerSink);
            cleanupOwner.TransferPageResourcesToStream();
            try
            {
                pageStream = await EncodedWallpaperPageStreamLease
                    .StartAsync(buffer, verifiedSink, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RetainedEncodedWallpaperPageStreamStartException exception)
            {
                if (stagedCapabilityState is not null)
                {
                    capabilityStateOwner!.Abandon(stagedCapabilityState);
                    stagedCapabilityState = null;
                }

                cleanupOwner.SetPageStream(exception.CleanupOwner);
                _retainedCleanup = cleanupOwner;
                throw CreateRetainedCleanupFailure(
                    exception.PrimaryFailure,
                    exception.CleanupFailure);
            }

            cleanupOwner.SetPageStream(pageStream);

            var lease = new DynamicWallpaperPageSessionLease(
                pageStream,
                connection,
                page,
                _heartbeatScheduler);
            var result = new DynamicWallpaperActivationResult(
                lease,
                presentation,
                capabilities);
            if (stagedCapabilityState is not null)
            {
                var committed = capabilityStateOwner!.Commit(stagedCapabilityState);
                if (committed.Current != capabilities)
                {
                    throw new InvalidOperationException(
                        "The page compatibility transaction changed before activation completed.");
                }

                stagedCapabilityState = null;
            }

            cleanupOwner.TransferToActiveLease();
            pageStream = null;
            connection = null;
            return result;
        }
        catch (Exception exception) when (!ReferenceEquals(_retainedCleanup, cleanupOwner))
        {
            if (stagedCapabilityState is not null)
            {
                capabilityStateOwner!.Abandon(stagedCapabilityState);
            }

            try
            {
                await cleanupOwner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                _retainedCleanup = cleanupOwner;
                throw CreateRetainedCleanupFailure(exception, cleanupFailure);
            }

            RethrowStartupFailure(exception);
            throw;
        }
    }

    private async ValueTask<DynamicWallpaperInitialPresentationReadiness>
        WaitForInitialPresentationCoreAsync(
            VerifiedCdpEndpoint endpoint,
            CancellationToken cancellationToken)
    {
        IDynamicWallpaperBrowserConnection? connection = null;
        Exception? primaryFailure = null;
        try
        {
            connection = await _connector
                .ConnectAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
            if (connection is null)
            {
                throw new InvalidOperationException(
                    "The verified browser connector returned no connection.");
            }

            IDynamicWallpaperVerifiedPage? expectedPage = null;
            string? expectedTargetIdentity = null;
            DynamicWallpaperInitialPresentationReadiness? readiness = null;
            DynamicWallpaperPageVerification? latestVerification = null;
            var latestWasExpectedSolePage = false;
            var latestGlobalBaselineAvailable = false;

            async Task<PageApplyResult> ObserveAdvancedContractAsync(
                CancellationToken token)
            {
                var verification = await connection
                    .VerifySolePageAsync(token)
                    .ConfigureAwait(false);
                if (!verification.TryGetSolePage(out var page))
                {
                    latestVerification = verification;
                    latestWasExpectedSolePage = false;
                    latestGlobalBaselineAvailable = false;
                    return new PageApplyResult(
                        verification.EligiblePageCount,
                        AppliedCount: 0,
                        IsAmbiguous: verification.EligiblePageCount > 1,
                        AmbiguousTargetsObserved: verification.EligiblePageCount > 1);
                }

                PinOrValidateReadinessTarget(
                    page,
                    ref expectedPage,
                    ref expectedTargetIdentity);
                var evidence = await page
                    .ObservePresentationEvidenceAsync(token)
                    .ConfigureAwait(false);
                // Publish only a completed observation. A deadline cancellation during the DOM
                // probe must not erase the last complete weak sample used to decide final fallback.
                latestVerification = verification;
                latestWasExpectedSolePage = true;
                latestGlobalBaselineAvailable = evidence.GlobalStructure;
                var decision = PresentationContractCatalog.Match(
                    evidence,
                    finalizeBaselineFallback: false);
                if (!decision.IsFinalized)
                {
                    return new PageApplyResult(
                        EligibleCount: 1,
                        AppliedCount: 0,
                        IsAmbiguous: false,
                        AmbiguousTargetsObserved: false);
                }

                await RequireSameSolePageAsync(connection, page, token)
                    .ConfigureAwait(false);
                readiness = new DynamicWallpaperInitialPresentationReadiness(
                    expectedTargetIdentity!,
                    decision.Snapshot,
                    decision.Capabilities);
                return new PageApplyResult(
                    EligibleCount: 1,
                    AppliedCount: 1,
                    IsAmbiguous: false,
                    AmbiguousTargetsObserved: false);
            }

            var result = await _readinessGate
                .WaitAsync(ObserveAdvancedContractAsync, cancellationToken)
                .ConfigureAwait(false);
            if (result.AppliedCount != 0)
            {
                return readiness!;
            }

            if (latestWasExpectedSolePage && latestGlobalBaselineAvailable)
            {
                result = await _readinessGate
                    .RunFinalAttemptAsync(
                        async token =>
                        {
                            var verification = await connection
                                .VerifySolePageAsync(token)
                                .ConfigureAwait(false);
                            latestVerification = verification;
                            var page = RequireSolePage(verification);
                            PinOrValidateReadinessTarget(
                                page,
                                ref expectedPage,
                                ref expectedTargetIdentity);
                            var evidence = await page
                                .ObservePresentationEvidenceAsync(token)
                                .ConfigureAwait(false);
                            var decision = PresentationContractCatalog.Match(
                                evidence,
                                finalizeBaselineFallback: true);
                            if (decision.Snapshot.MatchState ==
                                    ContractMatchState.GlobalBaselineFailed ||
                                !decision.Capabilities.CanInjectGlobalWallpaper)
                            {
                                return new PageApplyResult(
                                    EligibleCount: 1,
                                    AppliedCount: 0,
                                    IsAmbiguous: false,
                                    AmbiguousTargetsObserved: false);
                            }

                            await RequireSameSolePageAsync(connection, page, token)
                                .ConfigureAwait(false);
                            readiness = new DynamicWallpaperInitialPresentationReadiness(
                                expectedTargetIdentity!,
                                decision.Snapshot,
                                decision.Capabilities);
                            return new PageApplyResult(
                                EligibleCount: 1,
                                AppliedCount: 1,
                                IsAmbiguous: false,
                                AmbiguousTargetsObserved: false);
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.AppliedCount != 0)
                {
                    return readiness!;
                }

                throw new WallpaperPresentationContractException(
                    "The verified Codex page did not satisfy the global presentation baseline.");
            }

            if (latestVerification?.EligiblePageCount > 1)
            {
                throw new DynamicWallpaperPageSessionException(
                    "More than one eligible Codex work page remained available during presentation readiness.",
                    DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven);
            }

            if (latestWasExpectedSolePage)
            {
                throw new WallpaperPresentationContractException(
                    "The verified Codex page did not satisfy the global presentation baseline.");
            }

            throw new DynamicWallpaperPageSessionException(
                "The verified endpoint did not expose an eligible Codex work page during presentation readiness.");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            RethrowStartupFailure(exception);
            throw;
        }
        finally
        {
            if (connection is not null)
            {
                await DisconnectReadinessConnectionAsync(connection, primaryFailure)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask DisconnectReadinessConnectionAsync(
        IDynamicWallpaperBrowserConnection connection,
        Exception? primaryFailure)
    {
        try
        {
            await connection.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (primaryFailure is not null)
        {
            throw new DynamicWallpaperPageSessionException(
                "The presentation readiness check failed and its read-only connection could not be released.",
                primaryFailure,
                cleanupFailure);
        }
        catch (Exception cleanupFailure)
        {
            throw new DynamicWallpaperPageSessionException(
                "The read-only presentation connection could not be released.",
                DynamicWallpaperPageSessionFailureKind.CleanupNotProven,
                cleanupFailure);
        }
    }

    private static void PinOrValidateReadinessTarget(
        IDynamicWallpaperVerifiedPage page,
        ref IDynamicWallpaperVerifiedPage? expectedPage,
        ref string? expectedTargetIdentity)
    {
        ArgumentNullException.ThrowIfNull(page);
        var targetIdentity = page.StableIdentity;
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIdentity);
        if (expectedPage is null)
        {
            expectedPage = page;
            expectedTargetIdentity = targetIdentity;
            return;
        }

        if (!ReferenceEquals(expectedPage, page) ||
            !string.Equals(expectedTargetIdentity, targetIdentity, StringComparison.Ordinal))
        {
            throw new DynamicWallpaperPageSessionException(
                "The verified Codex target changed identity during presentation readiness.",
                DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven);
        }
    }

    private static IDynamicWallpaperVerifiedPage RequireSolePage(
        DynamicWallpaperPageVerification verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (verification.TryGetSolePage(out var page))
        {
            return page;
        }

        if (verification.EligiblePageCount > 1)
        {
            throw new DynamicWallpaperPageSessionException(
                "More than one eligible Codex work page was detected.",
                DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven);
        }

        throw new DynamicWallpaperPageSessionException(
            "The verified endpoint did not expose an eligible Codex work page.");
    }

    private static void RequireExpectedTargetIdentity(
        IDynamicWallpaperVerifiedPage page,
        string? expectedTargetIdentity)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (expectedTargetIdentity is null)
        {
            return;
        }

        if (!string.Equals(
                page.StableIdentity,
                expectedTargetIdentity,
                StringComparison.Ordinal))
        {
            throw new DynamicWallpaperPageSessionException(
                "The verified Codex target did not match the read-only presentation preflight.",
                DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven);
        }
    }

    private static async ValueTask RequireSameSolePageAsync(
        IDynamicWallpaperBrowserConnection connection,
        IDynamicWallpaperVerifiedPage expectedPage,
        CancellationToken cancellationToken)
    {
        var verification = await connection
            .VerifySolePageAsync(cancellationToken)
            .ConfigureAwait(false);
        if (verification.EligiblePageCount > 1)
        {
            throw new DynamicWallpaperPageSessionException(
                "More than one eligible Codex work page was detected.",
                DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven);
        }

        if (!verification.TryGetSolePage(out var currentPage))
        {
            throw new DynamicWallpaperPageSessionException(
                "The previously verified Codex target is temporarily unavailable.");
        }

        if (!ReferenceEquals(currentPage, expectedPage))
        {
            throw new DynamicWallpaperPageSessionException(
                "The previously verified Codex target changed identity.",
                DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven);
        }
    }

    private async ValueTask ReleaseRetainedCleanupCoreAsync()
    {
        var retained = _retainedCleanup;
        if (retained is null)
        {
            return;
        }

        await retained.DisposeAsync().ConfigureAwait(false);
        if (ReferenceEquals(_retainedCleanup, retained))
        {
            _retainedCleanup = null;
        }
    }

    private static DynamicWallpaperPageSessionException CreateRetainedCleanupFailure(
        Exception primaryFailure,
        Exception cleanupFailure) =>
        new(
            "The dynamic wallpaper page session failed to start and retained page cleanup " +
            "must succeed before the verified browser connection can be released.",
            primaryFailure,
            cleanupFailure);

    private static void RethrowStartupFailure(Exception exception)
    {
        if (exception is OperationCanceledException or
            WallpaperInjectionException or
            DynamicWallpaperUnavailableException or
            ArgumentException)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        throw new DynamicWallpaperPageSessionException(
            "The dynamic wallpaper page session could not attach to the verified Codex page.",
            exception);
    }

    private sealed class FailedPageSessionCleanupOwner : IAsyncDisposable
    {
        private EncodedWallpaperStreamBuffer? _buffer;
        private EncodedWallpaperPageStreamLease? _pageStream;
        private IDynamicWallpaperBrowserConnection? _connection;

        internal FailedPageSessionCleanupOwner(EncodedWallpaperStreamBuffer buffer) =>
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

        internal void SetConnection(IDynamicWallpaperBrowserConnection connection) =>
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));

        internal void TransferPageResourcesToStream() => _buffer = null;

        internal void SetPageStream(EncodedWallpaperPageStreamLease pageStream) =>
            _pageStream = pageStream ?? throw new ArgumentNullException(nameof(pageStream));

        internal void TransferToActiveLease()
        {
            _pageStream = null;
            _connection = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_pageStream is not null)
            {
                await _pageStream.DisposeAsync().ConfigureAwait(false);
                _pageStream = null;
            }

            if (_buffer is not null)
            {
                await _buffer.DisposeAsync().ConfigureAwait(false);
                _buffer = null;
            }

            if (_connection is not null)
            {
                await _connection.DisconnectAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
    }

    private sealed class RevalidatingEncodedWallpaperPageSink :
        IEncodedWallpaperPageSink
    {
        private readonly IDynamicWallpaperBrowserConnection _connection;
        private readonly IDynamicWallpaperVerifiedPage _page;
        private readonly IEncodedWallpaperPageSink _inner;
        private readonly SemaphoreSlim _disposeGate = new(1, 1);
        private int _disposed;

        internal RevalidatingEncodedWallpaperPageSink(
            IDynamicWallpaperBrowserConnection connection,
            IDynamicWallpaperVerifiedPage page,
            IEncodedWallpaperPageSink inner)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public async Task<EncodedWallpaperPrepareReceipt> PrepareAsync(
            DynamicWallpaperInjectionOptions options,
            EncodedWallpaperStreamDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            await RequireSameSolePageAsync(_connection, _page, cancellationToken)
                .ConfigureAwait(false);
            return await _inner
                .PrepareAsync(options, descriptor, cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<EncodedWallpaperAppendReceipt> AppendAsync(
            EncodedWallpaperSegment segment,
            CancellationToken cancellationToken = default) =>
            _inner.AppendAsync(segment, cancellationToken);

        public async Task<bool> SetPausedAsync(
            long generation,
            bool paused,
            CancellationToken cancellationToken = default)
        {
            await RequireSameSolePageAsync(_connection, _page, cancellationToken)
                .ConfigureAwait(false);
            return await _inner
                .SetPausedAsync(generation, paused, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<bool> CleanupAsync(
            long generation,
            CancellationToken cancellationToken = default)
        {
            if (!await _connection
                    .IsPageStillVerifiedAsync(_page, cancellationToken)
                    .ConfigureAwait(false))
            {
                return false;
            }

            return await _inner.CleanupAsync(generation, cancellationToken)
                .ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            await _disposeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                await _inner.DisposeAsync().ConfigureAwait(false);
                Volatile.Write(ref _disposed, 1);
            }
            finally
            {
                _disposeGate.Release();
            }
        }
    }

    private sealed class DynamicWallpaperPageSessionLease :
        IDynamicWallpaperPagePlaybackLease,
        IActiveWallpaperHealthSource
    {
        private const int MaximumConsecutiveHeartbeatFailures = 3;

        private EncodedWallpaperPageStreamLease? _pageStream;
        private IDynamicWallpaperBrowserConnection? _connection;
        private readonly IDynamicWallpaperVerifiedPage _page;
        private readonly IDynamicWallpaperHeartbeatScheduler _heartbeatScheduler;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _disposeGate = new(1, 1);
        private readonly Task _completion;
        private readonly long _generation;
        private int _disposeRequested;
        private int _disposed;

        internal DynamicWallpaperPageSessionLease(
            EncodedWallpaperPageStreamLease pageStream,
            IDynamicWallpaperBrowserConnection connection,
            IDynamicWallpaperVerifiedPage page,
            IDynamicWallpaperHeartbeatScheduler heartbeatScheduler)
        {
            _pageStream = pageStream ?? throw new ArgumentNullException(nameof(pageStream));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _heartbeatScheduler = heartbeatScheduler ??
                throw new ArgumentNullException(nameof(heartbeatScheduler));
            if (pageStream.DeliveryKind != ActiveWallpaperDeliveryKind.DynamicStream)
            {
                throw new ArgumentException(
                    "The page stream must represent dynamic delivery.",
                    nameof(pageStream));
            }

            _generation = pageStream.Generation;
            _completion = MonitorAsync();
        }

        public long Generation => _generation;

        public ActiveWallpaperDeliveryKind DeliveryKind =>
            ActiveWallpaperDeliveryKind.DynamicStream;

        public Task Completion => _completion;

        public long LastAcknowledgedKeyFrameSequence =>
            RequirePageStream().LastAcknowledgedKeyFrameSequence;

        public ValueTask SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default) =>
            RequirePageStream().SetPausedAsync(paused, cancellationToken);

        public ValueTask WaitForBufferedSegmentsAsync(
            CancellationToken cancellationToken = default) =>
            RequirePageStream().WaitForBufferedSegmentsAsync(cancellationToken);

        public ValueTask WaitForKeyFrameAfterAsync(
            long sequence,
            CancellationToken cancellationToken = default) =>
            RequirePageStream().WaitForKeyFrameAfterAsync(sequence, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Volatile.Write(ref _disposeRequested, 1);
            await _disposeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                if (!_stop.IsCancellationRequested)
                {
                    await _stop.CancelAsync().ConfigureAwait(false);
                }

                if (_pageStream is not null)
                {
                    await _pageStream.DisposeAsync().ConfigureAwait(false);
                    _pageStream = null;
                }

                try
                {
                    await _completion.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Completion is the public health channel. Disposal owns cleanup separately.
                }

                if (_connection is not null)
                {
                    await _connection.DisconnectAsync().ConfigureAwait(false);
                    _connection = null;
                }

                Volatile.Write(ref _disposed, 1);
                GC.SuppressFinalize(this);
            }
            finally
            {
                _disposeGate.Release();
            }
        }

        private EncodedWallpaperPageStreamLease RequirePageStream()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposeRequested) != 0 ||
                Volatile.Read(ref _disposed) != 0,
                this);
            return _pageStream ?? throw new ObjectDisposedException(GetType().Name);
        }

        private async Task MonitorAsync()
        {
            var consecutiveHeartbeatFailures = 0;
            while (true)
            {
                var pageStream = _pageStream;
                var connection = _connection;
                if (pageStream is null || connection is null)
                {
                    return;
                }

                var heartbeatWait = _heartbeatScheduler
                    .WaitAsync(_stop.Token)
                    .AsTask();
                var completed = await Task
                    .WhenAny(pageStream.Completion, heartbeatWait)
                    .ConfigureAwait(false);
                if (ReferenceEquals(completed, pageStream.Completion))
                {
                    await pageStream.Completion.ConfigureAwait(false);
                    return;
                }

                await heartbeatWait.ConfigureAwait(false);
                Exception? transportFailure = null;
                var heartbeatAccepted = false;
                try
                {
                    var verification = await connection
                        .VerifySolePageAsync(_stop.Token)
                        .ConfigureAwait(false);
                    if (verification.EligiblePageCount > 1)
                    {
                        throw new DynamicWallpaperPageSessionException(
                            "More than one eligible Codex work page was detected.",
                            DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven);
                    }

                    if (verification.TryGetSolePage(out var currentPage))
                    {
                        if (!ReferenceEquals(currentPage, _page))
                        {
                            throw new DynamicWallpaperPageSessionException(
                                "The previously verified Codex target changed identity.",
                                DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven);
                        }

                        heartbeatAccepted = await _page
                            .HeartbeatAsync(Generation, _stop.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException and
                        not DynamicWallpaperPageSessionException)
                {
                    transportFailure = exception;
                }

                if (!heartbeatAccepted)
                {
                    consecutiveHeartbeatFailures++;
                    if (consecutiveHeartbeatFailures < MaximumConsecutiveHeartbeatFailures)
                    {
                        continue;
                    }

                    throw transportFailure is null
                        ? new DynamicWallpaperPageSessionException(
                            "The verified Codex page repeatedly rejected the dynamic wallpaper heartbeat.")
                        : new DynamicWallpaperPageSessionException(
                            "The dynamic wallpaper heartbeat repeatedly lost its verified Codex connection.",
                            transportFailure);
                }

                consecutiveHeartbeatFailures = 0;
            }
        }
    }
}

public enum DynamicWallpaperPageSessionFailureKind
{
    Transient = 0,
    PageOwnershipNotProven,
    CleanupNotProven,
}

public sealed class DynamicWallpaperPageSessionException : WallpaperInjectionException
{
    public DynamicWallpaperPageSessionException(string message)
        : this(message, DynamicWallpaperPageSessionFailureKind.Transient)
    {
    }

    public DynamicWallpaperPageSessionException(
        string message,
        DynamicWallpaperPageSessionFailureKind failureKind)
        : base(message)
    {
        if (!Enum.IsDefined(failureKind))
        {
            throw new ArgumentOutOfRangeException(nameof(failureKind));
        }

        FailureKind = failureKind;
    }

    public DynamicWallpaperPageSessionException(string message, Exception primaryFailure)
        : this(
            message,
            DynamicWallpaperPageSessionFailureKind.Transient,
            primaryFailure)
    {
    }

    public DynamicWallpaperPageSessionException(
        string message,
        DynamicWallpaperPageSessionFailureKind failureKind,
        Exception primaryFailure)
        : this(message, failureKind)
    {
        ArgumentNullException.ThrowIfNull(primaryFailure);
        PrimaryDiagnosticExceptionType = primaryFailure.GetType().Name;
        PrimaryDiagnosticHResult = primaryFailure.HResult;
    }

    internal DynamicWallpaperPageSessionException(
        string message,
        Exception primaryFailure,
        Exception cleanupFailure)
        : this(
            message,
            DynamicWallpaperPageSessionFailureKind.CleanupNotProven,
            primaryFailure)
    {
        ArgumentNullException.ThrowIfNull(cleanupFailure);
        CleanupDiagnosticExceptionType = cleanupFailure.GetType().Name;
        CleanupDiagnosticHResult = cleanupFailure.HResult;
    }

    /// <summary>
    /// Gets the stable recovery boundary represented by this failure. Transient transport and
    /// compatibility failures may be retried locally; page ownership and cleanup failures are
    /// terminal because continuing could mutate or abandon resources outside the verified page.
    /// </summary>
    public DynamicWallpaperPageSessionFailureKind FailureKind { get; }

    /// <summary>
    /// Gets the path-free CLR type name of the primary CDP boundary failure. The original
    /// exception, message, stack and endpoint details are deliberately not retained.
    /// </summary>
    public string? PrimaryDiagnosticExceptionType { get; }

    /// <summary>
    /// Gets the path-free HRESULT reported by the primary CDP boundary failure.
    /// </summary>
    public int? PrimaryDiagnosticHResult { get; }

    /// <summary>
    /// Gets the path-free CLR type name of a secondary cleanup failure, when cleanup also failed.
    /// </summary>
    public string? CleanupDiagnosticExceptionType { get; }

    /// <summary>
    /// Gets the path-free HRESULT reported by a secondary cleanup failure, when present.
    /// </summary>
    public int? CleanupDiagnosticHResult { get; }
}

internal interface IDynamicWallpaperBrowserConnector
{
    ValueTask<IDynamicWallpaperBrowserConnection> ConnectAsync(
        VerifiedCdpEndpoint endpoint,
        CancellationToken cancellationToken = default);
}

internal interface IDynamicWallpaperBrowserConnection
{
    ValueTask<DynamicWallpaperPageVerification> VerifySolePageAsync(
        CancellationToken cancellationToken = default);

    ValueTask<bool> IsPageStillVerifiedAsync(
        IDynamicWallpaperVerifiedPage page,
        CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync();
}

internal interface IDynamicWallpaperVerifiedPage
{
    string StableIdentity { get; }

    ValueTask<PresentationEvidence> ObservePresentationEvidenceAsync(
        CancellationToken cancellationToken = default);

    IEncodedWallpaperPageSink CreateEncodedStreamSink(
        CompatibilityCapabilities capabilities);

    ValueTask<bool> HeartbeatAsync(
        long generation,
        CancellationToken cancellationToken = default);
}

internal sealed class DynamicWallpaperPageVerification
{
    private DynamicWallpaperPageVerification(
        int eligiblePageCount,
        IDynamicWallpaperVerifiedPage? solePage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(eligiblePageCount);
        if ((eligiblePageCount == 1) != (solePage is not null))
        {
            throw new ArgumentException(
                "A sole page must be supplied exactly when one eligible page was found.",
                nameof(solePage));
        }

        EligiblePageCount = eligiblePageCount;
        SolePage = solePage;
    }

    internal int EligiblePageCount { get; }

    private IDynamicWallpaperVerifiedPage? SolePage { get; }

    internal static DynamicWallpaperPageVerification None { get; } = new(0, null);

    internal static DynamicWallpaperPageVerification Sole(
        IDynamicWallpaperVerifiedPage page) =>
        new(1, page ?? throw new ArgumentNullException(nameof(page)));

    internal static DynamicWallpaperPageVerification Ambiguous(int eligiblePageCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(eligiblePageCount, 2);

        return new DynamicWallpaperPageVerification(eligiblePageCount, null);
    }

    internal bool TryGetSolePage(
        [NotNullWhen(true)] out IDynamicWallpaperVerifiedPage? page)
    {
        page = SolePage;
        return page is not null;
    }
}

internal interface IDynamicWallpaperHeartbeatScheduler
{
    ValueTask WaitAsync(CancellationToken cancellationToken);
}

internal sealed class PeriodicDynamicWallpaperHeartbeatScheduler :
    IDynamicWallpaperHeartbeatScheduler
{
    private readonly TimeSpan _interval;

    internal PeriodicDynamicWallpaperHeartbeatScheduler(TimeSpan interval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        _interval = interval;
    }

    public ValueTask WaitAsync(CancellationToken cancellationToken) =>
        new(Task.Delay(_interval, cancellationToken));
}

internal sealed class PuppeteerDynamicWallpaperBrowserConnector :
    IDynamicWallpaperBrowserConnector
{
    public async ValueTask<IDynamicWallpaperBrowserConnection> ConnectAsync(
        VerifiedCdpEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var connectTask = Puppeteer.ConnectAsync(new ConnectOptions
        {
            BrowserWSEndpoint = endpoint.BrowserWebSocketUri.AbsoluteUri,
            DefaultViewport = null,
            ProtocolTimeout = 15_000,
            AcceptInsecureCerts = false,
            NetworkEnabled = false,
        });
        try
        {
            var browser = await connectTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new PuppeteerDynamicWallpaperBrowserConnection(browser, endpoint);
        }
        catch
        {
            _ = DisconnectLateConnectionAsync(connectTask);
            throw;
        }
    }

    private static async Task DisconnectLateConnectionAsync(Task<IBrowser> connectTask)
    {
        try
        {
            var browser = await connectTask.ConfigureAwait(false);
            browser.Disconnect();
        }
        catch (Exception)
        {
            // Observe a late connect failure. Never close the Codex-owned browser.
        }
    }
}

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The connection gate must remain valid for queued fail-closed calls after disconnect.")]
internal sealed class PuppeteerDynamicWallpaperBrowserConnection :
    IDynamicWallpaperBrowserConnection
{
    private readonly IBrowser _browser;
    private readonly VerifiedCdpEndpoint _endpoint;
    private readonly VerifiedCodexPageSelector _selector = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly Dictionary<IPage, PuppeteerDynamicWallpaperVerifiedPage> _pages =
        new((IEqualityComparer<IPage>)ReferenceEqualityComparer.Instance);
    private int _disconnectRequested;
    private int _disconnected;

    internal PuppeteerDynamicWallpaperBrowserConnection(
        IBrowser browser,
        VerifiedCdpEndpoint endpoint)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public async ValueTask<DynamicWallpaperPageVerification> VerifySolePageAsync(
        CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disconnectRequested) != 0 ||
                Volatile.Read(ref _disconnected) != 0 ||
                !_browser.IsConnected)
            {
                return DynamicWallpaperPageVerification.None;
            }

            var scan = await _selector
                .ScanAsync(_browser, _endpoint, cancellationToken)
                .ConfigureAwait(false);
            if (Volatile.Read(ref _disconnectRequested) != 0 ||
                Volatile.Read(ref _disconnected) != 0 ||
                !_browser.IsConnected)
            {
                return DynamicWallpaperPageVerification.None;
            }

            foreach (var stalePage in _pages.Keys
                         .Where(page => !scan.ActivePages.Contains(page))
                         .ToArray())
            {
                _pages.Remove(stalePage);
            }

            if (!VerifiedCodexPageSelector.TrySelectSoleEligiblePage(
                    scan.EligiblePages,
                    out var selectedPage))
            {
                return scan.EligiblePages.Count > 1
                    ? DynamicWallpaperPageVerification.Ambiguous(scan.EligiblePages.Count)
                    : DynamicWallpaperPageVerification.None;
            }

            if (!_pages.TryGetValue(selectedPage, out var page))
            {
                page = new PuppeteerDynamicWallpaperVerifiedPage(selectedPage);
                _pages.Add(selectedPage, page);
            }

            return DynamicWallpaperPageVerification.Sole(page);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask<bool> IsPageStillVerifiedAsync(
        IDynamicWallpaperVerifiedPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disconnectRequested) != 0 ||
                Volatile.Read(ref _disconnected) != 0 ||
                !_browser.IsConnected ||
                page is not PuppeteerDynamicWallpaperVerifiedPage puppeteerPage ||
                !_pages.Values.Any(candidate => ReferenceEquals(candidate, page)))
            {
                return false;
            }

            var isVerified = await _selector
                .IsEligibleVerifiedPageAsync(
                    puppeteerPage.Page,
                    _endpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            return isVerified &&
                   Volatile.Read(ref _disconnectRequested) == 0 &&
                   Volatile.Read(ref _disconnected) == 0 &&
                   _browser.IsConnected;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _disconnectRequested, 1) != 0 &&
            Volatile.Read(ref _disconnected) != 0)
        {
            return;
        }

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disconnected) != 0)
            {
                return;
            }

            try
            {
                _browser.Disconnect();
            }
            catch (PuppeteerException)
            {
                // A broken CDP socket needs no further work. Never close the owned browser.
            }

            _selector.Reset();
            _pages.Clear();
            Volatile.Write(ref _disconnected, 1);
        }
        finally
        {
            _stateGate.Release();
        }
    }
}

internal sealed class PuppeteerDynamicWallpaperVerifiedPage :
    IDynamicWallpaperVerifiedPage
{
    private readonly IPage _page;

    internal PuppeteerDynamicWallpaperVerifiedPage(IPage page) =>
        _page = page ?? throw new ArgumentNullException(nameof(page));

    internal IPage Page => _page;

    // PuppeteerSharp exposes the protocol target id only through this compatibility member. Reading
    // it is side-effect free and lets a later CDP connection prove it selected the exact same target.
#pragma warning disable CS0618
    public string StableIdentity => _page.Target is CdpTarget target
        ? target.TargetId
        : throw new DynamicWallpaperPageSessionException(
            "The verified Codex page did not expose a stable CDP target identity.",
            DynamicWallpaperPageSessionFailureKind.PageOwnershipNotProven);
#pragma warning restore CS0618

    public async ValueTask<PresentationEvidence> ObservePresentationEvidenceAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await _page
                .EvaluateExpressionAsync<string>(PresentationEvidenceScriptBuilder.Build())
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return PresentationEvidenceScriptBuilder.Parse(json);
        }
        catch (PuppeteerException)
        {
            return PresentationEvidence.Unavailable;
        }
        catch (JsonException)
        {
            return PresentationEvidence.Unavailable;
        }
    }

    public IEncodedWallpaperPageSink CreateEncodedStreamSink(
        CompatibilityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return new PuppeteerEncodedWallpaperPageSink(_page, capabilities);
    }

    public async ValueTask<bool> HeartbeatAsync(
        long generation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _page
                .EvaluateExpressionAsync<bool>(
                    InjectionScriptBuilder.BuildHeartbeat(generation))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PuppeteerException)
        {
            return false;
        }
    }
}
