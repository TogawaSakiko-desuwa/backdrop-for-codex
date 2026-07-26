using System.Collections.ObjectModel;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Runtime;

public sealed class WallpaperWorkspaceCoordinatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NewApplyReplacesPendingAndOnlyLatestPendingRuns()
    {
        var repository = new ControllableSettingsRepository(SettingsV2.CreateDefault());
        var runtime = new ControllableRuntime();
        var provider = new ControllableSourceProvider();
        var firstPreflight = new AsyncCheckpoint();
        provider.BeforeResolveAsync = (call, _, _) =>
            call == 1
                ? firstPreflight.WaitAsync(CancellationToken.None)
                : Task.CompletedTask;
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);

        coordinator.ReplaceDraft(CreateMediaSettings("first", 1));
        var first = coordinator.ApplyAsync();
        await firstPreflight.Entered.Task.WaitAsync(TestTimeout);

        coordinator.ReplaceDraft(CreateMediaSettings("second", 2));
        var second = coordinator.ApplyAsync();
        coordinator.ReplaceDraft(CreateMediaSettings("third", 3));
        var third = coordinator.ApplyAsync();
        firstPreflight.Release.TrySetResult();

        var results = await Task.WhenAll(first, second, third)
            .WaitAsync(TestTimeout);

        Assert.Equal(RuntimeActivationOutcome.Superseded, results[0].Outcome);
        Assert.Equal(RuntimeActivationOutcome.Superseded, results[1].Outcome);
        Assert.Equal(RuntimeActivationOutcome.MediaActive, results[2].Outcome);
        Assert.Equal(2, provider.ResolveCount);
        Assert.Single(runtime.Requests);
        Assert.Equal("third", ProfileName(runtime.Requests[0].SettingsSnapshot));
        Assert.Equal("third", ProfileName(coordinator.State.SavedDesired));
        Assert.Equal("third", ProfileName(coordinator.State.ActiveSnapshot!));
    }

    [Fact]
    public async Task RunningApplyExitsBeforeReplacementTouchesRuntime()
    {
        var repository = new ControllableSettingsRepository(SettingsV2.CreateDefault());
        var runtime = new ControllableRuntime();
        var provider = new ControllableSourceProvider();
        var firstRuntime = new AsyncCheckpoint();
        var firstCancellationObserved = NewCompletionSource();
        var secondRuntimeStarted = NewCompletionSource();
        runtime.BeforeActivateAsync = async (call, _, cancellationToken) =>
        {
            if (call == 1)
            {
                using var registration = cancellationToken.Register(
                    () => firstCancellationObserved.TrySetResult());
                await firstRuntime
                    .WaitAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                secondRuntimeStarted.TrySetResult();
            }
        };
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);

        coordinator.ReplaceDraft(CreateMediaSettings("first", 1));
        var first = coordinator.ApplyAsync();
        await firstRuntime.Entered.Task.WaitAsync(TestTimeout);
        coordinator.ReplaceDraft(CreateMediaSettings("second", 2));
        var second = coordinator.ApplyAsync();

        await firstCancellationObserved.Task.WaitAsync(TestTimeout);
        Assert.False(secondRuntimeStarted.Task.IsCompleted);
        Assert.Equal(1, runtime.MaxConcurrentActivations);

        firstRuntime.Release.TrySetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        Assert.Equal(RuntimeActivationOutcome.Superseded, results[0].Outcome);
        Assert.Equal(RuntimeActivationOutcome.MediaActive, results[1].Outcome);
        Assert.True(secondRuntimeStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(1, runtime.MaxConcurrentActivations);
        Assert.Equal(
            ["first", "second"],
            runtime.Requests.Select(request => ProfileName(request.SettingsSnapshot)));
    }

    [Fact]
    public async Task SupersededSaveCommitUpdatesDesiredButDoesNotActivate()
    {
        var repository = new ControllableSettingsRepository(SettingsV2.CreateDefault());
        var runtime = new ControllableRuntime();
        var provider = new ControllableSourceProvider();
        var firstSave = new AsyncCheckpoint();
        var secondPreflight = new AsyncCheckpoint();
        repository.BeforeSaveAsync = (call, _, _) =>
            call == 1
                ? firstSave.WaitAsync(CancellationToken.None)
                : Task.CompletedTask;
        provider.BeforeResolveAsync = (call, _, _) =>
            call == 2
                ? secondPreflight.WaitAsync(CancellationToken.None)
                : Task.CompletedTask;
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);

        coordinator.ReplaceDraft(CreateMediaSettings("committed-first", 1));
        var first = coordinator.ApplyAsync();
        await firstSave.Entered.Task.WaitAsync(TestTimeout);
        coordinator.ReplaceDraft(CreateMediaSettings("latest", 2));
        var second = coordinator.ApplyAsync();
        firstSave.Release.TrySetResult();

        var firstResult = await first.WaitAsync(TestTimeout);
        await secondPreflight.Entered.Task.WaitAsync(TestTimeout);

        Assert.Equal(RuntimeActivationOutcome.Superseded, firstResult.Outcome);
        Assert.Equal("committed-first", ProfileName(coordinator.State.SavedDesired));
        Assert.Equal("latest", ProfileName(coordinator.State.Draft));
        Assert.Null(coordinator.State.ActiveSnapshot);
        Assert.Empty(runtime.Requests);

        secondPreflight.Release.TrySetResult();
        var secondResult = await second.WaitAsync(TestTimeout);
        Assert.Equal(RuntimeActivationOutcome.MediaActive, secondResult.Outcome);
        Assert.Equal("latest", ProfileName(coordinator.State.SavedDesired));
        Assert.Equal("latest", ProfileName(coordinator.State.ActiveSnapshot!));
    }

    [Fact]
    public async Task RuntimeEquivalentApplyPromotesSavedSnapshotWithoutNewGeneration()
    {
        var initial = CreateMediaSettings("before rename", 1);
        var repository = new ControllableSettingsRepository(initial);
        var runtime = new ControllableRuntime(initial, initialGeneration: 41);
        var provider = new ControllableSourceProvider();
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);
        var renamed = RenameGlobalProfile(initial, "after rename");

        coordinator.ReplaceDraft(renamed);
        var result = await coordinator.ApplyAsync().WaitAsync(TestTimeout);

        Assert.Equal(RuntimeActivationOutcome.MediaActive, result.Outcome);
        Assert.Empty(runtime.Requests);
        Assert.Equal(0, runtime.ActivateCount);
        Assert.Equal(1, runtime.PromoteCount);
        Assert.Equal(result.Revision, runtime.ActiveRevision);
        Assert.Equal(41, coordinator.State.RuntimeSurface.Generation);
        Assert.Equal("after rename", ProfileName(coordinator.State.SavedDesired));
        Assert.Equal("after rename", ProfileName(coordinator.State.ActiveSnapshot!));
        Assert.Equal("after rename", ProfileName(runtime.ActiveSnapshot!));
        Assert.True(
            SettingsV2Comparer.DurableEquals(
                coordinator.State.SavedDesired,
                coordinator.State.ActiveSnapshot!));
    }

    [Fact]
    public async Task EmptyProfileSkipsRiskAndPreflightAndActivatesOfficial()
    {
        var official = SettingsV2.CreateDefault();
        var repository = new ControllableSettingsRepository(official);
        var runtime = new ControllableRuntime();
        var provider = new ControllableSourceProvider();
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);

        var result = await coordinator.ApplyAsync().WaitAsync(TestTimeout);

        Assert.Equal(RuntimeActivationOutcome.Official, result.Outcome);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Official, result.Surface.Kind);
        Assert.False(coordinator.State.SavedDesired.AcceptedCdpRisk);
        Assert.NotNull(coordinator.State.ActiveSnapshot);
        Assert.Null(
            coordinator.State.ActiveSnapshot!
                .ResolveProfile(SemanticRegion.Global)
                .MediaId);
        Assert.Equal(0, provider.ResolveCount);
        Assert.Equal(0, provider.ValidateCount);
        var request = Assert.Single(runtime.Requests);
        Assert.True(request.IsOfficial);
    }

    [Fact]
    public async Task StaleRuntimeSuccessCannotPublishOverNewerRevision()
    {
        var repository = new ControllableSettingsRepository(SettingsV2.CreateDefault());
        var runtime = new ControllableRuntime();
        var provider = new ControllableSourceProvider();
        var firstRuntime = new AsyncCheckpoint();
        var secondRuntime = new AsyncCheckpoint();
        runtime.BeforeActivateAsync = (call, _, _) =>
            call == 1
                ? firstRuntime.WaitAsync(CancellationToken.None)
                : secondRuntime.WaitAsync(CancellationToken.None);
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);

        coordinator.ReplaceDraft(CreateMediaSettings("stale", 1));
        var first = coordinator.ApplyAsync();
        await firstRuntime.Entered.Task.WaitAsync(TestTimeout);
        coordinator.ReplaceDraft(CreateMediaSettings("latest", 2));
        var second = coordinator.ApplyAsync();
        firstRuntime.Release.TrySetResult();

        var firstResult = await first.WaitAsync(TestTimeout);
        await secondRuntime.Entered.Task.WaitAsync(TestTimeout);

        Assert.Equal(RuntimeActivationOutcome.Superseded, firstResult.Outcome);
        Assert.Equal(2, coordinator.State.LatestRevision);
        Assert.Null(coordinator.State.ActiveSnapshot);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Official,
            coordinator.State.RuntimeSurface.Kind);

        secondRuntime.Release.TrySetResult();
        var secondResult = await second.WaitAsync(TestTimeout);
        Assert.Equal(RuntimeActivationOutcome.MediaActive, secondResult.Outcome);
        Assert.Equal("latest", ProfileName(coordinator.State.ActiveSnapshot!));
        Assert.Equal(2, coordinator.State.RuntimeSurface.Generation);
    }

    [Fact]
    public async Task CancelStopsRunningApplyWithoutSavingOrRestoringOfficial()
    {
        var initial = SettingsV2.CreateDefault();
        var repository = new ControllableSettingsRepository(initial);
        var runtime = new ControllableRuntime();
        var provider = new ControllableSourceProvider();
        var preflight = new AsyncCheckpoint();
        provider.BeforeResolveAsync = (_, _, cancellationToken) =>
            preflight.WaitAsync(cancellationToken);
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);
        coordinator.ReplaceDraft(CreateMediaSettings("cancel me", 1));
        var apply = coordinator.ApplyAsync();
        await preflight.Entered.Task.WaitAsync(TestTimeout);

        coordinator.CancelLatestApply();
        var result = await apply.WaitAsync(TestTimeout);

        Assert.Equal(RuntimeActivationOutcome.Canceled, result.Outcome);
        Assert.Equal(0, repository.SaveCount);
        Assert.Empty(runtime.Requests);
        Assert.True(
            SettingsV2Comparer.DurableEquals(
                initial,
                coordinator.State.SavedDesired));
        Assert.Equal(
            WallpaperWorkspacePhase.Idle,
            coordinator.State.Phase);
    }

    [Fact]
    public async Task CancelRemovesPendingImmediatelyAndCleansACompletedRunningMutation()
    {
        var repository = new ControllableSettingsRepository(SettingsV2.CreateDefault());
        var runtime = new ControllableRuntime();
        var provider = new ControllableSourceProvider();
        var runningRuntime = new AsyncCheckpoint();
        runtime.BeforeActivateAsync = (call, _, _) =>
            call == 1
                ? runningRuntime.WaitAsync(CancellationToken.None)
                : Task.CompletedTask;
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);

        coordinator.ReplaceDraft(CreateMediaSettings("running", 1));
        var running = coordinator.ApplyAsync();
        await runningRuntime.Entered.Task.WaitAsync(TestTimeout);
        coordinator.ReplaceDraft(CreateMediaSettings("pending", 2));
        var pending = coordinator.ApplyAsync();

        coordinator.CancelLatestApply();

        Assert.True(pending.IsCompleted);
        Assert.Equal(
            RuntimeActivationOutcome.Canceled,
            (await pending.WaitAsync(TestTimeout)).Outcome);

        runningRuntime.Release.TrySetResult();
        var runningResult = await running.WaitAsync(TestTimeout);

        Assert.Equal(RuntimeActivationOutcome.Superseded, runningResult.Outcome);
        Assert.Equal(1, runtime.RestoreOfficialCount);
        Assert.Null(runtime.ActiveSnapshot);
        Assert.Null(coordinator.State.ActiveSnapshot);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Official,
            coordinator.State.RuntimeSurface.Kind);
        Assert.Equal(WallpaperWorkspacePhase.Idle, coordinator.State.Phase);
    }

    [Fact]
    public async Task CanceledRuntimeSelfCleanupIsReflectedInResultAndWorkspace()
    {
        var initial = CreateMediaSettings("active", 1);
        var repository = new ControllableSettingsRepository(initial);
        var runtime = new ControllableRuntime(initial, initialGeneration: 12)
        {
            SelfCleanBeforeCanceledActivationThrow = true,
        };
        var provider = new ControllableSourceProvider();
        var runtimeCheckpoint = new AsyncCheckpoint();
        runtime.BeforeActivateAsync = (_, _, _) =>
            runtimeCheckpoint.WaitAsync(CancellationToken.None);
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);
        using var cancellation = new CancellationTokenSource();

        coordinator.ReplaceDraft(CreateMediaSettings("replacement", 2));
        var apply = coordinator.ApplyAsync(
            cancellationToken: cancellation.Token);
        await runtimeCheckpoint.Entered.Task.WaitAsync(TestTimeout);

        cancellation.Cancel();
        runtimeCheckpoint.Release.TrySetResult();
        var result = await apply.WaitAsync(TestTimeout);

        Assert.Equal(RuntimeActivationOutcome.Canceled, result.Outcome);
        Assert.Null(result.ActiveSnapshot);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Official,
            result.Surface.Kind);
        Assert.Null(runtime.ActiveSnapshot);
        Assert.Null(coordinator.State.ActiveSnapshot);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Official,
            coordinator.State.RuntimeSurface.Kind);
        Assert.Equal(WallpaperWorkspacePhase.Idle, coordinator.State.Phase);
    }

    [Fact]
    public async Task RuntimeExceptionAfterSelfCleanupUsesActualRuntimeTruth()
    {
        var initial = CreateMediaSettings("active", 1);
        var repository = new ControllableSettingsRepository(initial);
        var runtime = new ControllableRuntime(initial, initialGeneration: 13)
        {
            ExceptionAfterActivationSelfCleanup =
                new InvalidOperationException("runtime failed after cleanup"),
        };
        var provider = new ControllableSourceProvider();
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);

        coordinator.ReplaceDraft(CreateMediaSettings("replacement", 2));
        var result = await coordinator.ApplyAsync().WaitAsync(TestTimeout);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Null(result.ActiveSnapshot);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Official,
            result.Surface.Kind);
        Assert.Null(runtime.ActiveSnapshot);
        Assert.Null(coordinator.State.ActiveSnapshot);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Official,
            coordinator.State.RuntimeSurface.Kind);
        Assert.Equal(WallpaperWorkspaceErrorStage.Runtime, coordinator.State.Error?.Stage);
        Assert.Equal(WallpaperWorkspacePhase.Idle, coordinator.State.Phase);
    }

    [Fact]
    public async Task HealthFaultConvergesWorkspaceToDisconnectedRuntimeTruth()
    {
        var initial = CreateMediaSettings("active", 1);
        var repository = new ControllableSettingsRepository(initial);
        var runtime = new ControllableRuntime(initial, initialGeneration: 7);
        var provider = new ControllableSourceProvider();
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);
        var disconnected = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, eventArgs) =>
        {
            if (eventArgs.State.RuntimeSurface.Kind ==
                WallpaperRuntimeSurfaceKind.Disconnected)
            {
                disconnected.TrySetResult();
            }
        };

        runtime.RaiseHealthFault(revision: 0);
        await disconnected.Task.WaitAsync(TestTimeout);

        Assert.Null(coordinator.State.ActiveSnapshot);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Disconnected,
            coordinator.State.RuntimeSurface.Kind);
        Assert.Equal("runtime-disconnected", coordinator.State.Error?.Code);
        Assert.Equal(WallpaperWorkspacePhase.Idle, coordinator.State.Phase);
    }

    [Fact]
    public async Task ResetFailureAfterRuntimeCleanupConvergesSurfaceAndPhase()
    {
        var initial = CreateMediaSettings("active", 1);
        var repository = new ControllableSettingsRepository(initial)
        {
            ResetException = new IOException("reset failed"),
        };
        var runtime = new ControllableRuntime(initial, initialGeneration: 9);
        var provider = new ControllableSourceProvider();
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => coordinator.ResetAsync().WaitAsync(TestTimeout));

        Assert.Equal("reset failed", exception.Message);
        Assert.Null(coordinator.State.ActiveSnapshot);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Official,
            coordinator.State.RuntimeSurface.Kind);
        Assert.Equal(WallpaperWorkspacePhase.Idle, coordinator.State.Phase);
        Assert.Equal(
            WallpaperWorkspaceErrorStage.Persistence,
            coordinator.State.Error?.Stage);
    }

    [Fact]
    public async Task HundredRapidAppliesEndAtLastSnapshotWithAtMostOneLease()
    {
        const int submissionCount = 100;
        var repository = new ControllableSettingsRepository(SettingsV2.CreateDefault());
        var runtime = new ControllableRuntime();
        var provider = new ControllableSourceProvider();
        var firstRuntime = new AsyncCheckpoint();
        runtime.BeforeActivateAsync = (call, _, _) =>
            call == 1
                ? firstRuntime.WaitAsync(CancellationToken.None)
                : Task.CompletedTask;
        await using var coordinator = await CreateCoordinatorAsync(
            repository,
            runtime,
            provider);

        var tasks = new List<Task<RuntimeActivationResult>>(submissionCount);
        coordinator.ReplaceDraft(CreateMediaSettings("profile-001", 1));
        tasks.Add(coordinator.ApplyAsync());
        await firstRuntime.Entered.Task.WaitAsync(TestTimeout);
        for (var index = 2; index <= submissionCount; index++)
        {
            coordinator.ReplaceDraft(
                CreateMediaSettings($"profile-{index:000}", index));
            tasks.Add(coordinator.ApplyAsync());
        }

        firstRuntime.Release.TrySetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TestTimeout);

        Assert.Equal(
            submissionCount - 1,
            results.Count(result => result.Outcome == RuntimeActivationOutcome.Superseded));
        Assert.Equal(RuntimeActivationOutcome.MediaActive, results[^1].Outcome);
        Assert.Equal("profile-100", ProfileName(coordinator.State.Draft));
        Assert.Equal("profile-100", ProfileName(coordinator.State.SavedDesired));
        Assert.Equal("profile-100", ProfileName(coordinator.State.ActiveSnapshot!));
        Assert.Equal(1, runtime.ActiveLeaseCount);
        Assert.Equal(1, runtime.MaximumActiveLeaseCount);
        Assert.Equal(1, runtime.MaxConcurrentActivations);
        Assert.Equal(2, runtime.ActivateCount);
        Assert.Equal(2, repository.SaveCount);
    }

    private static async Task<WallpaperWorkspaceCoordinator> CreateCoordinatorAsync(
        ControllableSettingsRepository repository,
        ControllableRuntime runtime,
        ControllableSourceProvider provider)
    {
        var coordinator = new WallpaperWorkspaceCoordinator(
            repository,
            runtime,
            provider,
            ownsSettingsRepository: false,
            ownsRuntime: false);
        await coordinator.InitializeAsync().WaitAsync(TestTimeout);
        return coordinator;
    }

    private static SettingsV2 CreateMediaSettings(string name, int pathMarker)
    {
        var media = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = $@"C:\Wallpapers\wallpaper-{pathMarker:000}.png",
            LastKnownKind = MediaKind.Image,
        };
        var profile = WallpaperProfile.CreateDefault(name) with
        {
            MediaId = media.MediaId,
        };
        return new SettingsV2
        {
            Profiles = new ReadOnlyCollection<WallpaperProfile>([profile]),
            MediaCatalog = new ReadOnlyCollection<MediaReference>([media]),
            RegionBindings = new ReadOnlyDictionary<SemanticRegion, Guid>(
                new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] = profile.ProfileId,
                }),
            AcceptedCdpRisk = true,
        }.CreateSnapshot();
    }

    private static SettingsV2 RenameGlobalProfile(SettingsV2 settings, string name)
    {
        var globalId = settings.RegionBindings[SemanticRegion.Global];
        return (settings with
        {
            Profiles = new ReadOnlyCollection<WallpaperProfile>(
                settings.Profiles
                    .Select(
                        profile => profile.ProfileId == globalId
                            ? profile with { Name = name }
                            : profile)
                    .ToArray()),
        }).CreateSnapshot();
    }

    private static string ProfileName(SettingsV2 settings) =>
        settings.ResolveProfile(SemanticRegion.Global).Name;

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class AsyncCheckpoint
    {
        internal TaskCompletionSource Entered { get; } = NewCompletionSource();

        internal TaskCompletionSource Release { get; } = NewCompletionSource();

        internal async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ControllableSettingsRepository(
        SettingsV2 initialSettings) : ISettingsRepository
    {
        private SettingsV2 _stored = initialSettings.CreateSnapshot();

        internal Func<int, SettingsV2, CancellationToken, Task>? BeforeSaveAsync
        {
            get;
            set;
        }

        internal int SaveCount { get; private set; }

        internal Exception? ResetException { get; init; }

        public bool HasVersion1Backup => false;

        public Task<SettingsLoadResult> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<SettingsLoadResult>(
                new SettingsLoadResult.Ready(_stored.CreateSnapshot(), false));
        }

        public async Task<SettingsV2> SaveAsync(
            SettingsV2 settings,
            CancellationToken cancellationToken = default)
        {
            var call = ++SaveCount;
            if (BeforeSaveAsync is { } beforeSave)
            {
                await beforeSave(call, settings, cancellationToken)
                    .ConfigureAwait(false);
            }

            _stored = settings.CreateSnapshot();
            return _stored.CreateSnapshot();
        }

        public Task<SettingsLoadResult> RestoreVersion1BackupAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<SettingsLoadResult>(
                new SettingsLoadResult.Ready(_stored.CreateSnapshot(), false));
        }

        public Task<SettingsV2> ResetAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ResetException is not null)
            {
                return Task.FromException<SettingsV2>(ResetException);
            }

            _stored = SettingsV2.CreateDefault();
            return Task.FromResult(_stored.CreateSnapshot());
        }

        public void Dispose()
        {
        }
    }

    private sealed class ControllableSourceProvider : IWallpaperSourceProvider
    {
        internal Func<int, MediaReference, CancellationToken, Task>?
            BeforeResolveAsync
        { get; set; }

        internal Func<int, MediaReference, CancellationToken, Task>?
            BeforeValidateAsync
        { get; set; }

        internal int ResolveCount { get; private set; }

        internal int ValidateCount { get; private set; }

        public MediaSourceKind SourceKind => MediaSourceKind.LocalFile;

        public async ValueTask<MediaReference> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            var call = ++ResolveCount;
            if (BeforeResolveAsync is { } beforeResolve)
            {
                await beforeResolve(call, reference, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return reference.Snapshot();
        }

        public async ValueTask<MediaSourceValidation> ValidateAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            var call = ++ValidateCount;
            if (BeforeValidateAsync is { } beforeValidate)
            {
                await beforeValidate(call, reference, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = reference.Snapshot();
            var metadata = snapshot.LastKnownKind == MediaKind.Video
                ? new MediaFileMetadata(
                    MediaFormat.Mp4,
                    MediaKind.Video,
                    "video/mp4",
                    1024)
                : new MediaFileMetadata(
                    MediaFormat.Png,
                    MediaKind.Image,
                    "image/png",
                    1024,
                    1920,
                    1080);
            return new MediaSourceValidation(snapshot, metadata);
        }

        public ValueTask<IMediaLease> AcquireLeaseAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException(
                "The workspace preflight provider must not acquire runtime leases.");
    }

    private sealed class ControllableRuntime : IWallpaperRuntime
    {
        private readonly List<RuntimeActivationRequest> _requests = [];
        private SettingsV2? _activeSnapshot;
        private WallpaperRuntimeSurface _surface;
        private WallpaperRuntimeStatusChangedEventArgs _status =
            new(WallpaperRuntimePhase.Idle, "Idle.");
        private long _generation;
        private int _concurrentActivations;

        internal ControllableRuntime(
            SettingsV2? initialActiveSnapshot = null,
            long initialGeneration = 0)
        {
            if (initialActiveSnapshot is null)
            {
                _surface = WallpaperRuntimeSurface.Disconnected();
                return;
            }

            _activeSnapshot = initialActiveSnapshot.CreateSnapshot();
            var mediaId = _activeSnapshot
                .ResolveProfile(SemanticRegion.Global)
                .MediaId;
            if (mediaId is null)
            {
                _surface = WallpaperRuntimeSurface.Official();
                return;
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialGeneration);

            _generation = initialGeneration;
            ActiveLeaseCount = 1;
            MaximumActiveLeaseCount = 1;
            _surface = WallpaperRuntimeSurface.MediaActive(
                initialGeneration,
                mediaId.Value,
                PlaybackOwnershipToken.Create());
        }

        internal Func<int, RuntimeActivationRequest, CancellationToken, Task>?
            BeforeActivateAsync
        { get; set; }

        internal List<RuntimeActivationRequest> Requests => _requests;

        internal int ActivateCount { get; private set; }

        internal int PromoteCount { get; private set; }

        internal int RestoreOfficialCount { get; private set; }

        internal long ActiveRevision { get; private set; }

        internal int MaxConcurrentActivations { get; private set; }

        internal int ActiveLeaseCount { get; private set; }

        internal int MaximumActiveLeaseCount { get; private set; }

        internal bool SelfCleanBeforeCanceledActivationThrow { get; set; }

        internal Exception? ExceptionAfterActivationSelfCleanup { get; set; }

        public event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? StatusChanged;

        public WallpaperRuntimeStatusChangedEventArgs Status => _status;

        public bool IsActive =>
            _surface.Kind == WallpaperRuntimeSurfaceKind.MediaActive;

        public bool IsPaused { get; private set; }

        public WallpaperRuntimeSurface Surface => _surface;

        public SettingsV2? ActiveSnapshot => _activeSnapshot?.CreateSnapshot();

        public async Task<RuntimeActivationResult> ActivateAsync(
            RuntimeActivationRequest request,
            CancellationToken cancellationToken = default)
        {
            var call = ++ActivateCount;
            _requests.Add(request);
            var concurrent = Interlocked.Increment(ref _concurrentActivations);
            MaxConcurrentActivations = Math.Max(
                MaxConcurrentActivations,
                concurrent);
            try
            {
                if (BeforeActivateAsync is { } beforeActivate)
                {
                    await beforeActivate(call, request, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (SelfCleanBeforeCanceledActivationThrow &&
                    cancellationToken.IsCancellationRequested)
                {
                    ActiveLeaseCount = 0;
                    _activeSnapshot = null;
                    _surface = WallpaperRuntimeSurface.Official();
                    _status = new WallpaperRuntimeStatusChangedEventArgs(
                        WallpaperRuntimePhase.Idle,
                        "The canceled activation cleaned its runtime resources.",
                        request.Revision);
                    throw new OperationCanceledException(cancellationToken);
                }

                if (ExceptionAfterActivationSelfCleanup is { } activationException)
                {
                    ActiveLeaseCount = 0;
                    _activeSnapshot = null;
                    _surface = WallpaperRuntimeSurface.Official();
                    _status = new WallpaperRuntimeStatusChangedEventArgs(
                        WallpaperRuntimePhase.Idle,
                        "The failed activation cleaned its runtime resources.",
                        request.Revision);
                    throw activationException;
                }

                var snapshot = request.SettingsSnapshot.CreateSnapshot();
                ActiveRevision = request.Revision;
                if (request.IsOfficial)
                {
                    ActiveLeaseCount = 0;
                    _activeSnapshot = snapshot;
                    _surface = WallpaperRuntimeSurface.Official();
                    return RuntimeActivationResult.Official(
                        request.Revision,
                        snapshot,
                        _surface);
                }

                ActiveLeaseCount = 1;
                MaximumActiveLeaseCount = Math.Max(
                    MaximumActiveLeaseCount,
                    ActiveLeaseCount);
                _activeSnapshot = snapshot;
                _surface = WallpaperRuntimeSurface.MediaActive(
                    Interlocked.Increment(ref _generation),
                    request.Media!.MediaId,
                    PlaybackOwnershipToken.Create());
                return RuntimeActivationResult.MediaActive(
                    request.Revision,
                    snapshot,
                    _surface);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentActivations);
            }
        }

        public Task<RuntimeActivationResult?> TryPromoteActiveSnapshotAsync(
            RuntimeActivationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_activeSnapshot is null ||
                !SettingsV2Comparer.RuntimeEquivalent(
                    _activeSnapshot,
                    request.SettingsSnapshot))
            {
                return Task.FromResult<RuntimeActivationResult?>(null);
            }

            PromoteCount++;
            ActiveRevision = request.Revision;
            _activeSnapshot = request.SettingsSnapshot.CreateSnapshot();
            if (request.IsOfficial)
            {
                _surface = WallpaperRuntimeSurface.Official();
                return Task.FromResult<RuntimeActivationResult?>(
                    RuntimeActivationResult.Official(
                        request.Revision,
                        _activeSnapshot,
                        _surface));
            }

            _surface = WallpaperRuntimeSurface.MediaActive(
                _surface.Generation ??
                    throw new InvalidOperationException("No active generation is available."),
                request.Media!.MediaId,
                _surface.PlaybackOwnership ??
                    throw new InvalidOperationException("No active ownership is available."));
            return Task.FromResult<RuntimeActivationResult?>(
                RuntimeActivationResult.MediaActive(
                    request.Revision,
                    _activeSnapshot,
                    _surface));
        }

        public Task SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsPaused = paused;
            _status = new WallpaperRuntimeStatusChangedEventArgs(
                paused ? WallpaperRuntimePhase.Paused : WallpaperRuntimePhase.Active,
                paused ? "Paused." : "Active.");
            return Task.CompletedTask;
        }

        public Task<RuntimeActivationResult> RestoreOfficialAsync(
            long revision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreOfficialCount++;
            ActiveLeaseCount = 0;
            _activeSnapshot = null;
            _surface = WallpaperRuntimeSurface.Official();
            ActiveRevision = revision;
            return Task.FromResult(
                RuntimeActivationResult.Canceled(
                    revision,
                    _surface));
        }

        internal void RaiseHealthFault(long revision)
        {
            ActiveLeaseCount = 0;
            _activeSnapshot = null;
            _surface = WallpaperRuntimeSurface.Disconnected(
                new WallpaperRuntimeError(
                    "runtime-disconnected",
                    "The active runtime disconnected."));
            _status = new WallpaperRuntimeStatusChangedEventArgs(
                WallpaperRuntimePhase.Faulted,
                "Disconnected.",
                revision);
            StatusChanged?.Invoke(this, _status);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
