using System.Collections.ObjectModel;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.Core.Runtime;

public sealed class WallpaperWorkspaceStateChangedEventArgs(
    WallpaperWorkspaceState state) : EventArgs
{
    public WallpaperWorkspaceState State { get; } =
        state ?? throw new ArgumentNullException(nameof(state));
}

/// <summary>
/// The single external owner of settings publication and wallpaper runtime mutations.
/// Apply requests are latest-wins; all other commands remain ordered barriers in the same actor.
/// </summary>
public sealed class WallpaperWorkspaceCoordinator : IAsyncDisposable
{
    private readonly ISettingsRepository _settingsRepository;
    private readonly IWallpaperRuntime _runtime;
    private readonly IWallpaperSourceProvider _preflightProvider;
    private readonly bool _ownsSettingsRepository;
    private readonly bool _ownsRuntime;
    private readonly object _mailboxLock = new();
    private readonly LinkedList<ActorCommand> _mailbox = [];
    private readonly SemaphoreSlim _mailboxSignal = new(0);
    private readonly Task _workerTask;
    private WallpaperWorkspace _workspace =
        new(SettingsV2.CreateDefault(), WallpaperRuntimeSurface.Disconnected());
    private LinkedListNode<ActorCommand>? _pendingApplyNode;
    private ApplyCommand? _runningApply;
    private CancellationTokenSource? _runningApplyCancellation;
    private long _nextRevision;
    private bool _initialized;
    private bool _acceptingCommands = true;
    private int _disposed;

    public WallpaperWorkspaceCoordinator(
        ISettingsRepository settingsRepository,
        IWallpaperRuntime runtime,
        IWallpaperSourceProvider preflightProvider,
        bool ownsSettingsRepository = true,
        bool ownsRuntime = true)
    {
        _settingsRepository = settingsRepository ??
            throw new ArgumentNullException(nameof(settingsRepository));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _preflightProvider = preflightProvider ??
            throw new ArgumentNullException(nameof(preflightProvider));
        if (_preflightProvider.SourceKind != MediaSourceKind.LocalFile)
        {
            throw new ArgumentException(
                "The 1.4 workspace requires the directly registered LocalFile provider.",
                nameof(preflightProvider));
        }

        _ownsSettingsRepository = ownsSettingsRepository;
        _ownsRuntime = ownsRuntime;
        _runtime.StatusChanged += Runtime_StatusChanged;
        _workerTask = Task.Run(RunActorAsync);
    }

    public event EventHandler<WallpaperWorkspaceStateChangedEventArgs>? StateChanged;

    public event EventHandler<WallpaperRuntimeStatusChangedEventArgs>? RuntimeStatusChanged;

    public WallpaperWorkspaceState State => _workspace.State;

    public bool HasVersion1Backup => _settingsRepository.HasVersion1Backup;

    public bool IsPaused => _runtime.IsPaused;

    public bool IsRuntimeActive => _runtime.IsActive;

    public async Task<WallpaperWorkspaceState> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return State;
        }

        return await EnqueueControlAsync(
                async token =>
                {
                    if (_initialized)
                    {
                        return State;
                    }

                    var result = await _settingsRepository
                        .LoadAsync(token)
                        .ConfigureAwait(false);
                    var settings = result switch
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
                            "The settings repository returned an unknown state."),
                    };

                    _workspace = new WallpaperWorkspace(
                        settings,
                        _runtime.Surface,
                        _runtime.ActiveSnapshot);
                    _initialized = true;
                    PublishState();
                    return State;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void ReplaceDraft(SettingsV2 draft)
    {
        EnsureInitialized();
        _workspace.ReplaceDraft(draft);
        PublishState();
    }

    public WallpaperProfile CreateProfile(string baseName = "New profile")
    {
        EnsureInitialized();
        var profile = _workspace.CreateProfile(baseName);
        PublishState();
        return profile;
    }

    public WallpaperProfile DuplicateProfile(
        Guid profileId,
        string suffix = "Copy")
    {
        EnsureInitialized();
        var profile = _workspace.DuplicateProfile(profileId, suffix);
        PublishState();
        return profile;
    }

    public WallpaperProfile RenameProfile(Guid profileId, string name)
    {
        EnsureInitialized();
        var profile = _workspace.RenameProfile(profileId, name);
        PublishState();
        return profile;
    }

    public void DeleteProfile(Guid profileId, Guid? replacementProfileId = null)
    {
        EnsureInitialized();
        _workspace.DeleteProfile(profileId, replacementProfileId);
        PublishState();
    }

    public void SelectProfile(Guid profileId)
    {
        EnsureInitialized();
        _workspace.SelectProfile(profileId);
        PublishState();
    }

    public MediaReference SelectLocalMedia(
        Guid profileId,
        string path,
        MediaKind mediaKind)
    {
        EnsureInitialized();
        var media = _workspace.SelectLocalMedia(profileId, path, mediaKind);
        PublishState();
        return media;
    }

    public void ClearMedia(Guid profileId)
    {
        EnsureInitialized();
        _workspace.ClearMedia(profileId);
        PublishState();
    }

    public Task<RuntimeActivationResult> ApplyAsync(
        RuntimeLaunchMode launchMode = RuntimeLaunchMode.ManualApply,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (!Enum.IsDefined(launchMode))
        {
            throw new ArgumentOutOfRangeException(nameof(launchMode));
        }

        var revision = Interlocked.Increment(ref _nextRevision);
        var command = new ApplyCommand(
            revision,
            _workspace.CaptureDraft(),
            launchMode,
            cancellationToken);
        _workspace.BeginRevision(revision);
        PublishState();
        EnqueueApply(command);
        return command.Completion.Task;
    }

    public void CancelLatestApply()
    {
        ApplyCommand? pending;
        CancellationTokenSource? runningCancellation;
        lock (_mailboxLock)
        {
            pending = _pendingApplyNode?.Value as ApplyCommand;
            if (pending is not null)
            {
                pending.UserCanceled = true;
                _mailbox.Remove(_pendingApplyNode!);
                _pendingApplyNode = null;
                _ = _mailboxSignal.Wait(0);
            }

            if (_runningApply is not null)
            {
                _runningApply.UserCanceled = true;
            }

            runningCancellation = _runningApplyCancellation;
        }

        if (pending is not null)
        {
            pending.Completion.TrySetResult(
                RuntimeActivationResult.Canceled(
                    pending.Revision,
                    State.RuntimeSurface,
                    State.ActiveSnapshot));
            _ = _workspace.SetProgress(
                pending.Revision,
                WallpaperWorkspacePhase.Idle);
            PublishState();
        }

        runningCancellation?.Cancel();
    }

    public Task<SettingsV2> SetRiskAcceptanceAsync(
        bool accepted,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return EnqueueControlAsync(
            async token =>
            {
                var state = State;
                var savedCandidate = (state.SavedDesired with
                {
                    AcceptedCdpRisk = accepted,
                }).CreateSnapshot();
                var saved = await _settingsRepository
                    .SaveAsync(savedCandidate, token)
                    .ConfigureAwait(false);
                _workspace.CommitIndependentSettings(
                    saved,
                    draft => draft with
                    {
                        AcceptedCdpRisk = accepted,
                    });
                PublishState();
                return saved;
            },
            cancellationToken);
    }

    public Task<SettingsV2> RemoveRecentMediaAsync(
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return MutateRecentMediaAsync(
            recent => recent.Where(id => id != mediaId).ToArray(),
            cancellationToken);
    }

    public Task<SettingsV2> ClearRecentMediaAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return MutateRecentMediaAsync(_ => Array.Empty<Guid>(), cancellationToken);
    }

    public Task SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return EnqueueControlAsync(
            async token =>
            {
                await _runtime.SetPausedAsync(paused, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task<RuntimeActivationResult> RestoreOfficialAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        CancelLatestApply();
        var revision = Interlocked.Increment(ref _nextRevision);
        _workspace.BeginRevision(
            revision,
            WallpaperWorkspacePhase.RestoringOfficial);
        PublishState();
        return EnqueueControlAsync(
            async token =>
            {
                try
                {
                    var result = await _runtime
                        .RestoreOfficialAsync(revision, token)
                        .ConfigureAwait(false);
                    var error = result.Error is null
                        ? ToWorkspaceError(
                            result.Surface.Error,
                            WallpaperWorkspaceErrorStage.Cleanup)
                        : new WallpaperWorkspaceError(
                            WallpaperWorkspaceErrorStage.Cleanup,
                            result.Error.Code,
                            result.Error.Message,
                            result.Error.ExceptionType);
                    ReconcileRuntimeTruth(
                        revision,
                        WallpaperWorkspacePhase.Idle,
                        error);
                    return result;
                }
                catch (Exception exception)
                {
                    ReconcileRuntimeTruth(
                        revision,
                        WallpaperWorkspacePhase.Idle,
                        exception is OperationCanceledException
                            ? null
                            : WallpaperWorkspaceError.FromException(
                                WallpaperWorkspaceErrorStage.Cleanup,
                                "restore-official-failed",
                                exception));
                    throw;
                }
            },
            cancellationToken);
    }

    public Task<SettingsV2> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        CancelLatestApply();
        var revision = Interlocked.Increment(ref _nextRevision);
        _workspace.BeginRevision(revision, WallpaperWorkspacePhase.Resetting);
        PublishState();
        return EnqueueControlAsync(
            async token =>
            {
                var errorStage = WallpaperWorkspaceErrorStage.Cleanup;
                try
                {
                    var runtimeResult = await _runtime
                        .RestoreOfficialAsync(revision, token)
                        .ConfigureAwait(false);
                    ReconcileRuntimeTruth(
                        revision,
                        WallpaperWorkspacePhase.Resetting,
                        ToWorkspaceError(
                            runtimeResult.Error ??
                            runtimeResult.Surface.Error,
                            WallpaperWorkspaceErrorStage.Cleanup));
                    if (runtimeResult.Surface.Kind ==
                        WallpaperRuntimeSurfaceKind.Faulted)
                    {
                        throw new InvalidOperationException(
                            runtimeResult.Error?.Message ??
                            "The official background could not be restored.");
                    }

                    errorStage = WallpaperWorkspaceErrorStage.Persistence;
                    var reset = await _settingsRepository
                        .ResetAsync(token)
                        .ConfigureAwait(false);
                    _workspace = new WallpaperWorkspace(
                        reset,
                        WallpaperRuntimeSurface.Official(),
                        reset);
                    _initialized = true;
                    PublishState();
                    return reset;
                }
                catch (Exception exception)
                {
                    ReconcileRuntimeTruth(
                        revision,
                        WallpaperWorkspacePhase.Idle,
                        exception is OperationCanceledException
                            ? null
                            : WallpaperWorkspaceError.FromException(
                                errorStage,
                                "reset-failed",
                                exception));
                    throw;
                }
            },
            cancellationToken);
    }

    public Task<SettingsV2> RestoreVersion1BackupAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        CancelLatestApply();
        var revision = Interlocked.Increment(ref _nextRevision);
        _workspace.BeginRevision(revision, WallpaperWorkspacePhase.Recovering);
        PublishState();
        return EnqueueControlAsync(
            async token =>
            {
                var errorStage = WallpaperWorkspaceErrorStage.Cleanup;
                try
                {
                    var runtimeResult = await _runtime
                        .RestoreOfficialAsync(revision, token)
                        .ConfigureAwait(false);
                    ReconcileRuntimeTruth(
                        revision,
                        WallpaperWorkspacePhase.Recovering,
                        ToWorkspaceError(
                            runtimeResult.Error ??
                            runtimeResult.Surface.Error,
                            WallpaperWorkspaceErrorStage.Cleanup));
                    if (runtimeResult.Surface.Kind ==
                        WallpaperRuntimeSurfaceKind.Faulted)
                    {
                        throw new InvalidOperationException(
                            runtimeResult.Error?.Message ??
                            "The official background could not be restored.");
                    }

                    errorStage = WallpaperWorkspaceErrorStage.Persistence;
                    var result = await _settingsRepository
                        .RestoreVersion1BackupAsync(token)
                        .ConfigureAwait(false);
                    var restored = result switch
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
                    _workspace = new WallpaperWorkspace(
                        restored,
                        WallpaperRuntimeSurface.Official());
                    _initialized = true;
                    PublishState();
                    return restored;
                }
                catch (Exception exception)
                {
                    ReconcileRuntimeTruth(
                        revision,
                        WallpaperWorkspacePhase.Idle,
                        exception is OperationCanceledException
                            ? null
                            : WallpaperWorkspaceError.FromException(
                                errorStage,
                                "restore-backup-failed",
                                exception));
                    throw;
                }
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelLatestApply();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_mailboxLock)
        {
            _acceptingCommands = false;
            _mailbox.AddLast(new StopActorCommand(completion));
            _mailboxSignal.Release();
        }

        await completion.Task.ConfigureAwait(false);
        await _workerTask.ConfigureAwait(false);
        _runtime.StatusChanged -= Runtime_StatusChanged;

        var failures = new List<Exception>();
        if (_ownsRuntime)
        {
            try
            {
                await _runtime.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (_ownsSettingsRepository)
        {
            try
            {
                _settingsRepository.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        _mailboxSignal.Dispose();
        GC.SuppressFinalize(this);
        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "One or more workspace resources could not be disposed.",
                failures);
        }
    }

    private void EnqueueApply(ApplyCommand command)
    {
        ApplyCommand? supersededPending = null;
        CancellationTokenSource? runningCancellation;
        lock (_mailboxLock)
        {
            ThrowIfNotAccepting();
            var replacedPending = _pendingApplyNode is not null;
            if (_pendingApplyNode is not null)
            {
                supersededPending = (ApplyCommand)_pendingApplyNode.Value;
                supersededPending.Superseded = true;
                _mailbox.Remove(_pendingApplyNode);
                _pendingApplyNode = null;
            }

            if (_runningApply is not null)
            {
                _runningApply.Superseded = true;
            }

            _pendingApplyNode = _mailbox.AddLast(command);
            runningCancellation = _runningApplyCancellation;
            if (!replacedPending)
            {
                _mailboxSignal.Release();
            }
        }

        if (supersededPending is not null)
        {
            supersededPending.Completion.TrySetResult(
                RuntimeActivationResult.Superseded(
                    supersededPending.Revision,
                    State.RuntimeSurface,
                    State.ActiveSnapshot));
        }

        runningCancellation?.Cancel();
    }

    private Task<T> EnqueueControlAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var command = new ControlCommand<T>(action, cancellationToken);
        lock (_mailboxLock)
        {
            ThrowIfNotAccepting();
            _mailbox.AddLast(command);
            _mailboxSignal.Release();
        }

        return command.Completion.Task;
    }

    private async Task RunActorAsync()
    {
        var stop = false;
        while (!stop)
        {
            await _mailboxSignal.WaitAsync().ConfigureAwait(false);
            ActorCommand command;
            lock (_mailboxLock)
            {
                var node = _mailbox.First;
                if (node is null)
                {
                    // A pending Apply can be removed after the worker consumed its
                    // semaphore permit but before it acquired the mailbox lock.
                    continue;
                }

                _mailbox.RemoveFirst();
                command = node.Value;
                if (ReferenceEquals(node, _pendingApplyNode))
                {
                    _pendingApplyNode = null;
                }
            }

            if (command is StopActorCommand stopCommand)
            {
                stopCommand.Completion.TrySetResult(true);
                stop = true;
                continue;
            }

            if (command is RuntimeStateSyncCommand runtimeSync)
            {
                ReconcileRuntimeTruth(
                    runtimeSync.ObservedRevision,
                    WallpaperWorkspacePhase.Idle,
                    ToWorkspaceError(_runtime.Surface.Error));
                continue;
            }

            if (command is ApplyCommand apply)
            {
                CancellationTokenSource linkedCancellation;
                lock (_mailboxLock)
                {
                    _runningApply = apply;
                    linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        apply.CancellationToken);
                    _runningApplyCancellation = linkedCancellation;
                }

                try
                {
                    await ExecuteApplyAsync(apply, linkedCancellation.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    lock (_mailboxLock)
                    {
                        if (ReferenceEquals(_runningApply, apply))
                        {
                            _runningApply = null;
                            _runningApplyCancellation = null;
                        }
                    }

                    linkedCancellation.Dispose();
                }

                continue;
            }

            await command.ExecuteAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteApplyAsync(
        ApplyCommand command,
        CancellationToken cancellationToken)
    {
        RuntimeActivationResult result;
        try
        {
            ThrowIfApplyStopped(command, cancellationToken);
            _ = _workspace.SetProgress(
                command.Revision,
                WallpaperWorkspacePhase.Preflighting);
            PublishState();

            var canonical = await CanonicalizeAsync(
                    command.DraftSnapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            ThrowIfApplyStopped(command, cancellationToken);

            _ = _workspace.SetProgress(
                command.Revision,
                WallpaperWorkspacePhase.Saving);
            PublishState();
            var saved = await _settingsRepository
                .SaveAsync(canonical, cancellationToken)
                .ConfigureAwait(false);

            // SaveAsync returning is the durable commit point even when a newer revision
            // arrived during the non-cancellable atomic replacement.
            var isLatest = _workspace.CommitSavedDesired(saved, command.Revision);
            if (isLatest)
            {
                _ = _workspace.ReplaceDraftIfUnchanged(
                    command.DraftSnapshot,
                    saved);
            }

            PublishState();
            ThrowIfApplyStopped(command, cancellationToken);

            _ = _workspace.SetProgress(
                command.Revision,
                WallpaperWorkspacePhase.Activating);
            PublishState();

            var currentState = State;
            var activationRequest = RuntimeActivationRequest.Create(
                command.Revision,
                saved,
                command.LaunchMode);
            if (currentState.ActiveSnapshot is not null &&
                SettingsV2Comparer.RuntimeEquivalent(
                    saved,
                    currentState.ActiveSnapshot))
            {
                result = await _runtime
                    .TryPromoteActiveSnapshotAsync(
                        activationRequest,
                        cancellationToken)
                    .ConfigureAwait(false) ??
                    await _runtime
                        .ActivateAsync(
                            activationRequest,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            else
            {
                result = await _runtime
                    .ActivateAsync(
                        activationRequest,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (command.Superseded || command.UserCanceled)
            {
                result = await CompleteStoppedApplyAsync(command, result)
                    .ConfigureAwait(false);
            }

            CommitRuntimeResult(result);
        }
        catch (OperationCanceledException)
        {
            ReconcileRuntimeTruth(
                command.Revision,
                WallpaperWorkspacePhase.Idle);
            result = command.Superseded
                ? RuntimeActivationResult.Superseded(
                    command.Revision,
                    _runtime.Surface,
                    _runtime.ActiveSnapshot)
                : RuntimeActivationResult.Canceled(
                    command.Revision,
                    _runtime.Surface,
                    _runtime.ActiveSnapshot);
        }
        catch (Exception exception)
        {
            var stage = State.Phase switch
            {
                WallpaperWorkspacePhase.Preflighting =>
                    WallpaperWorkspaceErrorStage.Preflight,
                WallpaperWorkspacePhase.Saving =>
                    WallpaperWorkspaceErrorStage.Persistence,
                WallpaperWorkspacePhase.Activating =>
                    WallpaperWorkspaceErrorStage.Runtime,
                _ => WallpaperWorkspaceErrorStage.Validation,
            };
            var workspaceError = WallpaperWorkspaceError.FromException(
                stage,
                "apply-failed",
                exception);
            if (stage == WallpaperWorkspaceErrorStage.Runtime)
            {
                ReconcileRuntimeTruth(
                    command.Revision,
                    WallpaperWorkspacePhase.Idle,
                    workspaceError);
            }
            else
            {
                _ = _workspace.SetProgress(
                    command.Revision,
                    WallpaperWorkspacePhase.Idle,
                    workspaceError);
                PublishState();
            }

            if (command.Superseded || command.UserCanceled)
            {
                ReconcileRuntimeTruth(
                    command.Revision,
                    WallpaperWorkspacePhase.Idle);
                result = command.Superseded
                    ? RuntimeActivationResult.Superseded(
                        command.Revision,
                        _runtime.Surface,
                        _runtime.ActiveSnapshot)
                    : RuntimeActivationResult.Canceled(
                        command.Revision,
                        _runtime.Surface,
                        _runtime.ActiveSnapshot);
            }
            else
            {
                var surface = stage == WallpaperWorkspaceErrorStage.Runtime
                    ? _runtime.Surface
                    : State.RuntimeSurface;
                var activeSnapshot = stage == WallpaperWorkspaceErrorStage.Runtime
                    ? _runtime.ActiveSnapshot
                    : State.ActiveSnapshot;
                result = RuntimeActivationResult.Failed(
                    command.Revision,
                    surface,
                    activeSnapshot,
                    WallpaperRuntimeError.FromException("apply-failed", exception));
            }
        }

        command.Completion.TrySetResult(result);
    }

    private void CommitRuntimeResult(RuntimeActivationResult result)
    {
        switch (result.Outcome)
        {
            case RuntimeActivationOutcome.MediaActive:
            case RuntimeActivationOutcome.Official:
                _ = _workspace.CommitActive(
                    result.ActiveSnapshot ??
                        throw new InvalidOperationException(
                            "A successful activation requires an active snapshot."),
                    result.Surface,
                    result.Revision);
                break;

            case RuntimeActivationOutcome.SavedButNotActivated:
                _workspace.ReconcileRuntimeState(
                    result.ActiveSnapshot,
                    result.Surface,
                    result.Revision,
                    WallpaperWorkspacePhase.Idle,
                    ToWorkspaceError(result.Error));
                break;

            case RuntimeActivationOutcome.Failed:
                _workspace.ReconcileRuntimeState(
                    result.ActiveSnapshot,
                    result.Surface,
                    result.Revision,
                    WallpaperWorkspacePhase.Idle,
                    ToWorkspaceError(result.Error));
                break;

            case RuntimeActivationOutcome.Superseded:
            case RuntimeActivationOutcome.Canceled:
                _workspace.ReconcileRuntimeState(
                    _runtime.ActiveSnapshot,
                    _runtime.Surface,
                    result.Revision,
                    WallpaperWorkspacePhase.Idle,
                    ToWorkspaceError(
                        result.Surface.Error ??
                        _runtime.Surface.Error));
                break;

            default:
                throw new InvalidOperationException(
                    "The runtime returned an unknown activation outcome.");
        }

        PublishState();
    }

    private async Task<SettingsV2> CanonicalizeAsync(
        SettingsV2 draft,
        CancellationToken cancellationToken)
    {
        var snapshot = draft.CreateSnapshot();
        var global = snapshot.ResolveProfile(SemanticRegion.Global);
        if (global.MediaId is not { } mediaId)
        {
            return snapshot;
        }

        if (!snapshot.AcceptedCdpRisk)
        {
            throw new CdpRiskNotAcceptedException();
        }

        var media = snapshot.FindMedia(mediaId) ??
            throw new SettingsValidationException(
                ["The Global profile media is missing from MediaCatalog."]);
        if (media.SourceKind != MediaSourceKind.LocalFile)
        {
            throw new MediaSourceNotSupportedException(media.SourceKind);
        }

        var resolved = await _preflightProvider
            .ResolveAsync(media, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = await _preflightProvider
            .ValidateAsync(resolved, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var canonicalMedia = validation.Reference with
        {
            MediaId = media.MediaId,
            LastKnownKind = validation.Metadata.Kind,
        };
        var catalog = snapshot.MediaCatalog
            .Select(item => item.MediaId == mediaId ? canonicalMedia : item)
            .ToArray();
        var recent = PromoteRecentMedia(snapshot, mediaId);
        return (snapshot with
        {
            MediaCatalog = new ReadOnlyCollection<MediaReference>(catalog),
            RecentMediaIds = new ReadOnlyCollection<Guid>(recent),
        }).CreateSnapshot();
    }

    private static Guid[] PromoteRecentMedia(SettingsV2 settings, Guid mediaId)
    {
        var hiddenIds = settings.RecentMediaIds
            .Where(id => settings.FindMedia(id)?.SourceKind != MediaSourceKind.LocalFile)
            .ToArray();
        var hiddenSet = hiddenIds.ToHashSet();
        var localIds = new[] { mediaId }
            .Concat(
                settings.RecentMediaIds.Where(
                    id => id != mediaId && !hiddenSet.Contains(id)))
            .Distinct()
            .Take(SettingsV2.MaximumRecentMediaIds - hiddenIds.Length)
            .ToArray();

        var result = new List<Guid>(SettingsV2.MaximumRecentMediaIds);
        var localIndex = 0;
        foreach (var existing in settings.RecentMediaIds)
        {
            if (hiddenSet.Contains(existing))
            {
                result.Add(existing);
            }
            else if (localIndex < localIds.Length)
            {
                result.Add(localIds[localIndex++]);
            }
        }

        while (localIndex < localIds.Length)
        {
            result.Add(localIds[localIndex++]);
        }

        return result.Take(SettingsV2.MaximumRecentMediaIds).ToArray();
    }

    private Task<SettingsV2> MutateRecentMediaAsync(
        Func<IReadOnlyList<Guid>, IReadOnlyList<Guid>> update,
        CancellationToken cancellationToken) =>
        EnqueueControlAsync(
            async token =>
            {
                var state = State;
                var recent = update(state.SavedDesired.RecentMediaIds).ToArray();
                var savedCandidate = (state.SavedDesired with
                {
                    RecentMediaIds = new ReadOnlyCollection<Guid>(recent),
                }).CreateSnapshot();
                var saved = await _settingsRepository
                    .SaveAsync(savedCandidate, token)
                    .ConfigureAwait(false);
                _workspace.CommitIndependentSettings(
                    saved,
                    draft => draft with
                    {
                        RecentMediaIds = new ReadOnlyCollection<Guid>(recent),
                    });
                PublishState();
                return saved;
            },
            cancellationToken);

    private static WallpaperWorkspaceError? ToWorkspaceError(
        WallpaperRuntimeError? error,
        WallpaperWorkspaceErrorStage stage =
            WallpaperWorkspaceErrorStage.Runtime) =>
        error is null
            ? null
            : new WallpaperWorkspaceError(
                stage,
                error.Code,
                error.Message,
                error.ExceptionType);

    private async Task<RuntimeActivationResult> CompleteStoppedApplyAsync(
        ApplyCommand command,
        RuntimeActivationResult result)
    {
        WallpaperRuntimeError? cleanupError = null;
        if (result.Outcome is
            RuntimeActivationOutcome.MediaActive or
            RuntimeActivationOutcome.Official)
        {
            try
            {
                result = await _runtime
                    .RestoreOfficialAsync(
                        command.Revision,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                cleanupError = result.Error ?? result.Surface.Error;
            }
            catch (Exception exception)
            {
                cleanupError = WallpaperRuntimeError.FromException(
                    "superseded-cleanup-failed",
                    exception);
            }
        }

        var actualSurface = _runtime.Surface;
        var actualActive = _runtime.ActiveSnapshot;
        ReconcileRuntimeTruth(
            command.Revision,
            WallpaperWorkspacePhase.Idle,
            ToWorkspaceError(cleanupError ?? actualSurface.Error));
        return command.Superseded
            ? RuntimeActivationResult.Superseded(
                command.Revision,
                actualSurface,
                actualActive)
            : RuntimeActivationResult.Canceled(
                command.Revision,
                actualSurface,
                actualActive);
    }

    private void ReconcileRuntimeTruth(
        long observedRevision,
        WallpaperWorkspacePhase phaseWhenLatest,
        WallpaperWorkspaceError? error = null)
    {
        _workspace.ReconcileRuntimeState(
            _runtime.ActiveSnapshot,
            _runtime.Surface,
            observedRevision,
            phaseWhenLatest,
            error);
        PublishState();
    }

    private static void ThrowIfApplyStopped(
        ApplyCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Superseded || command.UserCanceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private void PublishState()
    {
        var eventArgs = new WallpaperWorkspaceStateChangedEventArgs(State);
        var handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<WallpaperWorkspaceStateChangedEventArgs> handler in
                 handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception)
            {
                // Workspace observers cannot interrupt persistence or runtime ownership.
            }
        }
    }

    private void Runtime_StatusChanged(
        object? sender,
        WallpaperRuntimeStatusChangedEventArgs eventArgs)
    {
        var requiresRuntimeSync = eventArgs.Phase is (
                WallpaperRuntimePhase.Faulted or
                WallpaperRuntimePhase.Disposed) &&
            _runtime.Surface.Kind is (
                WallpaperRuntimeSurfaceKind.Faulted or
                WallpaperRuntimeSurfaceKind.Disconnected);
        if (requiresRuntimeSync)
        {
            lock (_mailboxLock)
            {
                if (_acceptingCommands && Volatile.Read(ref _disposed) == 0)
                {
                    _mailbox.AddLast(
                        new RuntimeStateSyncCommand(eventArgs.Revision));
                    _mailboxSignal.Release();
                }
            }
        }

        RuntimeStatusChanged?.Invoke(this, eventArgs);
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "The wallpaper workspace must be initialized before use.");
        }
    }

    private void ThrowIfNotAccepting()
    {
        ObjectDisposedException.ThrowIf(
            !_acceptingCommands || Volatile.Read(ref _disposed) != 0,
            this);
    }

    private abstract class ActorCommand
    {
        internal abstract Task ExecuteAsync();
    }

    private sealed class ApplyCommand(
        long revision,
        SettingsV2 draftSnapshot,
        RuntimeLaunchMode launchMode,
        CancellationToken cancellationToken) : ActorCommand
    {
        internal long Revision { get; } = revision;

        internal SettingsV2 DraftSnapshot { get; } = draftSnapshot;

        internal RuntimeLaunchMode LaunchMode { get; } = launchMode;

        internal CancellationToken CancellationToken { get; } = cancellationToken;

        internal TaskCompletionSource<RuntimeActivationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Superseded { get; set; }

        internal bool UserCanceled { get; set; }

        internal override Task ExecuteAsync() =>
            throw new InvalidOperationException(
                "Apply commands are executed by the typed actor path.");
    }

    private sealed class ControlCommand<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) : ActorCommand
    {
        internal TaskCompletionSource<T> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal override async Task ExecuteAsync()
        {
            try
            {
                var result = await action(cancellationToken).ConfigureAwait(false);
                Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                Completion.TrySetException(exception);
            }
        }
    }

    private sealed class StopActorCommand(
        TaskCompletionSource<bool> completion) : ActorCommand
    {
        internal TaskCompletionSource<bool> Completion { get; } = completion;

        internal override Task ExecuteAsync() => Task.CompletedTask;
    }

    private sealed class RuntimeStateSyncCommand(long observedRevision) : ActorCommand
    {
        internal long ObservedRevision { get; } = observedRevision;

        internal override Task ExecuteAsync() => Task.CompletedTask;
    }
}
