using System.Collections.ObjectModel;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;

namespace BackdropForCodex.Core.Settings;

public enum WallpaperWorkspacePhase
{
    Idle = 0,
    Preflighting,
    Saving,
    Activating,
    RestoringOfficial,
    Recovering,
    Resetting,
    Disposing,
}

public enum WallpaperWorkspaceErrorStage
{
    Validation = 0,
    Preflight,
    Persistence,
    Runtime,
    Cleanup,
}

/// <summary>
/// A structured, presentation-safe failure raised while coordinating the workspace.
/// </summary>
public sealed record WallpaperWorkspaceError
{
    public WallpaperWorkspaceError(
        WallpaperWorkspaceErrorStage stage,
        string code,
        string message,
        string? exceptionType = null)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Stage = stage;
        Code = code.Trim();
        Message = message.Trim();
        ExceptionType = string.IsNullOrWhiteSpace(exceptionType)
            ? null
            : exceptionType.Trim();
    }

    public WallpaperWorkspaceErrorStage Stage { get; }

    public string Code { get; }

    public string Message { get; }

    public string? ExceptionType { get; }

    public static WallpaperWorkspaceError FromException(
        WallpaperWorkspaceErrorStage stage,
        string code,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new WallpaperWorkspaceError(
            stage,
            code,
            exception.Message,
            exception.GetType().FullName);
    }
}

/// <summary>
/// One coherent view of the editor draft, durable intent, and truthful runtime state.
/// Every settings value exposed here is a deeply isolated V2 snapshot.
/// </summary>
public sealed class WallpaperWorkspaceState
{
    internal WallpaperWorkspaceState(
        SettingsV2 draft,
        SettingsV2 savedDesired,
        SettingsV2? activeSnapshot,
        WallpaperRuntimeSurface runtimeSurface,
        long latestRevision,
        WallpaperWorkspacePhase phase,
        WallpaperWorkspaceError? error)
    {
        Draft = draft;
        SavedDesired = savedDesired;
        ActiveSnapshot = activeSnapshot;
        RuntimeSurface = runtimeSurface;
        LatestRevision = latestRevision;
        Phase = phase;
        Error = error;
    }

    public SettingsV2 Draft { get; }

    public SettingsV2 SavedDesired { get; }

    public SettingsV2? ActiveSnapshot { get; }

    public WallpaperRuntimeSurface RuntimeSurface { get; }

    public long LatestRevision { get; }

    public WallpaperWorkspacePhase Phase { get; }

    public WallpaperWorkspaceError? Error { get; }

    public bool IsDraftDirty =>
        !SettingsV2Comparer.UiDirtyEquals(Draft, SavedDesired);

    public bool IsSavedDesiredActive =>
        ActiveSnapshot is not null &&
        SettingsV2Comparer.RuntimeEquivalent(SavedDesired, ActiveSnapshot);
}

/// <summary>
/// Owns V2 editor mutations and the three distinct settings snapshots. Runtime and
/// persistence work remains in the application actor; this type only commits completed
/// transitions and refuses stale runtime publications.
/// </summary>
public sealed class WallpaperWorkspace
{
    private readonly object _stateLock = new();
    private WallpaperWorkspaceState _state;

    public WallpaperWorkspace(
        SettingsV2 loadedSettings,
        WallpaperRuntimeSurface? initialSurface = null,
        SettingsV2? activeSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(loadedSettings);
        var saved = loadedSettings.CreateSnapshot();
        var draft = saved.CreateSnapshot();
        var active = activeSnapshot?.CreateSnapshot();
        var surface = initialSurface ?? WallpaperRuntimeSurface.Disconnected();

        ValidateActiveSurface(active, surface);
        _state = new WallpaperWorkspaceState(
            draft,
            saved,
            active,
            surface,
            latestRevision: 0,
            WallpaperWorkspacePhase.Idle,
            error: null);
    }

    public WallpaperWorkspaceState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    /// <summary>
    /// Captures an additional deep snapshot suitable for an asynchronous Apply request.
    /// </summary>
    public SettingsV2 CaptureDraft()
    {
        lock (_stateLock)
        {
            return _state.Draft.CreateSnapshot();
        }
    }

    public void ReplaceDraft(SettingsV2 draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var snapshot = draft.CreateSnapshot();
        lock (_stateLock)
        {
            _state = CopyState(_state, draft: snapshot);
        }
    }

    /// <summary>
    /// Replaces the editor draft only when it still durably matches the caller's captured
    /// snapshot. The comparison and replacement share one lock so an edit cannot be lost
    /// between those two operations.
    /// </summary>
    public bool ReplaceDraftIfUnchanged(
        SettingsV2 expectedDraft,
        SettingsV2 replacementDraft)
    {
        ArgumentNullException.ThrowIfNull(expectedDraft);
        ArgumentNullException.ThrowIfNull(replacementDraft);
        var expectedSnapshot = expectedDraft.CreateSnapshot();
        var replacementSnapshot = replacementDraft.CreateSnapshot();
        lock (_stateLock)
        {
            if (!SettingsV2Comparer.DurableEquals(
                    _state.Draft,
                    expectedSnapshot))
            {
                return false;
            }

            _state = CopyState(_state, draft: replacementSnapshot);
            return true;
        }
    }

    /// <summary>
    /// Marks a newly submitted revision. Revisions are process-lifetime monotonic.
    /// </summary>
    public void BeginRevision(
        long revision,
        WallpaperWorkspacePhase phase = WallpaperWorkspacePhase.Preflighting)
    {
        ValidateRevision(revision);
        ValidatePhase(phase);
        lock (_stateLock)
        {
            if (revision <= _state.LatestRevision)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(revision),
                    "A new workspace revision must be greater than the latest revision.");
            }

            _state = CopyState(
                _state,
                latestRevision: revision,
                phase: phase,
                error: null,
                replaceError: true);
        }
    }

    /// <summary>
    /// Publishes progress only when it still belongs to the latest submitted revision.
    /// </summary>
    public bool SetProgress(
        long revision,
        WallpaperWorkspacePhase phase,
        WallpaperWorkspaceError? error = null)
    {
        ValidateRevision(revision);
        ValidatePhase(phase);
        lock (_stateLock)
        {
            if (revision != _state.LatestRevision)
            {
                return false;
            }

            _state = CopyState(
                _state,
                phase: phase,
                error: error,
                replaceError: true);
            return true;
        }
    }

    /// <summary>
    /// Records the atomic persistence commit point. A completed older save is still durable
    /// and therefore updates SavedDesired, but cannot replace newer progress or error state.
    /// </summary>
    public bool CommitSavedDesired(SettingsV2 savedDesired, long revision)
    {
        ArgumentNullException.ThrowIfNull(savedDesired);
        ValidateRevision(revision);
        var snapshot = savedDesired.CreateSnapshot();

        lock (_stateLock)
        {
            var isLatest = revision == _state.LatestRevision;
            _state = CopyState(
                _state,
                savedDesired: snapshot,
                latestRevision: Math.Max(revision, _state.LatestRevision),
                phase: isLatest ? WallpaperWorkspacePhase.Saving : _state.Phase,
                error: isLatest ? null : _state.Error,
                replaceError: isLatest);
            return isLatest;
        }
    }

    /// <summary>
    /// Commits a serialized non-activation settings mutation (for example risk acceptance or
    /// recent-media management) without changing activation revision, progress, or runtime state.
    /// The caller supplies the corresponding Draft so unsaved profile edits remain isolated.
    /// </summary>
    public void CommitIndependentSettings(
        SettingsV2 savedDesired,
        SettingsV2 draft)
    {
        ArgumentNullException.ThrowIfNull(savedDesired);
        ArgumentNullException.ThrowIfNull(draft);
        var savedSnapshot = savedDesired.CreateSnapshot();
        var draftSnapshot = draft.CreateSnapshot();

        lock (_stateLock)
        {
            _state = CopyState(
                _state,
                draft: draftSnapshot,
                savedDesired: savedSnapshot);
        }
    }

    /// <summary>
    /// Commits an independently persisted settings update and applies only that update to
    /// the latest editor draft under the same lock. This preserves edits made while the
    /// persistence operation was awaiting I/O.
    /// </summary>
    internal void CommitIndependentSettings(
        SettingsV2 savedDesired,
        Func<SettingsV2, SettingsV2> updateDraft)
    {
        ArgumentNullException.ThrowIfNull(savedDesired);
        ArgumentNullException.ThrowIfNull(updateDraft);
        var savedSnapshot = savedDesired.CreateSnapshot();

        lock (_stateLock)
        {
            var updatedDraft = updateDraft(_state.Draft).CreateSnapshot();
            _state = CopyState(
                _state,
                draft: updatedDraft,
                savedDesired: savedSnapshot);
        }
    }

    /// <summary>
    /// Commits an actually active snapshot. Stale revisions are ignored.
    /// </summary>
    public bool CommitActive(
        SettingsV2 activeSnapshot,
        WallpaperRuntimeSurface runtimeSurface,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(activeSnapshot);
        ArgumentNullException.ThrowIfNull(runtimeSurface);
        ValidateRevision(revision);

        var snapshot = activeSnapshot.CreateSnapshot();
        ValidateActiveSurface(snapshot, runtimeSurface);
        lock (_stateLock)
        {
            if (revision != _state.LatestRevision)
            {
                return false;
            }

            if (!SettingsV2Comparer.DurableEquals(
                    snapshot,
                    _state.SavedDesired))
            {
                throw new InvalidOperationException(
                    "The active snapshot must be the same canonical snapshot that was saved.");
            }

            _state = CopyState(
                _state,
                activeSnapshot: snapshot,
                replaceActiveSnapshot: true,
                runtimeSurface: runtimeSurface,
                phase: WallpaperWorkspacePhase.Idle,
                error: null,
                replaceError: true);
            return true;
        }
    }

    /// <summary>
    /// Updates the actual surface after cleanup, disconnection, or failure. Stale
    /// revisions cannot clear a newer active snapshot.
    /// </summary>
    public bool SetRuntimeSurface(
        WallpaperRuntimeSurface runtimeSurface,
        bool clearActiveSnapshot,
        long revision,
        WallpaperWorkspaceError? error = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeSurface);
        ValidateRevision(revision);
        lock (_stateLock)
        {
            if (revision != _state.LatestRevision)
            {
                return false;
            }

            var active = clearActiveSnapshot ? null : _state.ActiveSnapshot;
            ValidateActiveSurface(active, runtimeSurface);
            _state = CopyState(
                _state,
                activeSnapshot: active,
                replaceActiveSnapshot: clearActiveSnapshot,
                runtimeSurface: runtimeSurface,
                phase: WallpaperWorkspacePhase.Idle,
                error: error,
                replaceError: true);
            return true;
        }
    }

    /// <summary>
    /// Converges the workspace to the runtime's current resource truth. Runtime state is
    /// always synchronized, including for an observation from an older revision; stale
    /// observations cannot replace the latest command's progress or structured error.
    /// </summary>
    public void ReconcileRuntimeState(
        SettingsV2? activeSnapshot,
        WallpaperRuntimeSurface runtimeSurface,
        long observedRevision,
        WallpaperWorkspacePhase phaseWhenLatest = WallpaperWorkspacePhase.Idle,
        WallpaperWorkspaceError? error = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeSurface);
        ArgumentOutOfRangeException.ThrowIfNegative(observedRevision);
        ValidatePhase(phaseWhenLatest);
        var active = activeSnapshot?.CreateSnapshot();
        ValidateActiveSurface(active, runtimeSurface);

        lock (_stateLock)
        {
            var observationIsLatest = observedRevision == _state.LatestRevision;
            _state = CopyState(
                _state,
                activeSnapshot: active,
                replaceActiveSnapshot: true,
                runtimeSurface: runtimeSurface,
                phase: observationIsLatest ? phaseWhenLatest : _state.Phase,
                error: observationIsLatest ? error : _state.Error,
                replaceError: observationIsLatest);
        }
    }

    public WallpaperProfile CreateProfile(string baseName = "New profile")
    {
        var normalizedBaseName = NormalizeProfileName(baseName, nameof(baseName));
        lock (_stateLock)
        {
            var profile = WallpaperProfile.CreateDefault(
                CreateAvailableName(
                    _state.Draft.Profiles,
                    normalizedBaseName));
            var profiles = _state.Draft.Profiles.Append(profile).ToArray();
            var bindings = CopyBindings(_state.Draft.RegionBindings);
            bindings[SemanticRegion.Global] = profile.ProfileId;
            ReplaceDraftInsideLock(
                _state.Draft with
                {
                    Profiles = profiles,
                    RegionBindings = bindings,
                });
            return _state.Draft.Profiles.Single(
                candidate => candidate.ProfileId == profile.ProfileId);
        }
    }

    public WallpaperProfile DuplicateProfile(
        Guid profileId,
        string suffix = "Copy")
    {
        ValidateProfileId(profileId, nameof(profileId));
        var normalizedSuffix = NormalizeProfileName(suffix, nameof(suffix));
        lock (_stateLock)
        {
            var source = FindProfile(_state.Draft, profileId);
            var profile = source with
            {
                ProfileId = Guid.CreateVersion7(),
                Name = CreateAvailableName(
                    _state.Draft.Profiles,
                    $"{source.Name} {normalizedSuffix}"),
            };
            var profiles = _state.Draft.Profiles.Append(profile).ToArray();
            var bindings = CopyBindings(_state.Draft.RegionBindings);
            bindings[SemanticRegion.Global] = profile.ProfileId;
            ReplaceDraftInsideLock(
                _state.Draft with
                {
                    Profiles = profiles,
                    RegionBindings = bindings,
                });
            return _state.Draft.Profiles.Single(
                candidate => candidate.ProfileId == profile.ProfileId);
        }
    }

    public WallpaperProfile RenameProfile(Guid profileId, string name)
    {
        ValidateProfileId(profileId, nameof(profileId));
        var normalizedName = NormalizeProfileName(name, nameof(name));
        lock (_stateLock)
        {
            var index = FindProfileIndex(_state.Draft, profileId);
            var profiles = _state.Draft.Profiles.ToArray();
            profiles[index] = profiles[index] with { Name = normalizedName };
            ReplaceDraftInsideLock(_state.Draft with { Profiles = profiles });
            return _state.Draft.Profiles[index];
        }
    }

    public void DeleteProfile(Guid profileId, Guid? replacementProfileId = null)
    {
        ValidateProfileId(profileId, nameof(profileId));
        lock (_stateLock)
        {
            _ = FindProfile(_state.Draft, profileId);
            if (_state.Draft.Profiles.Count == 1)
            {
                throw new InvalidOperationException(
                    "The final wallpaper profile cannot be deleted.");
            }

            var boundRegions = _state.Draft.RegionBindings
                .Where(binding => binding.Value == profileId)
                .Select(binding => binding.Key)
                .OrderBy(region => region)
                .ToArray();
            if (boundRegions.Length != 0 && replacementProfileId is null)
            {
                throw new WallpaperProfileReplacementRequiredException(
                    profileId,
                    boundRegions);
            }

            if (replacementProfileId == profileId)
            {
                throw new ArgumentException(
                    "The deleted profile cannot replace itself.",
                    nameof(replacementProfileId));
            }

            if (replacementProfileId is { } replacement)
            {
                _ = FindProfile(_state.Draft, replacement);
            }

            var profiles = _state.Draft.Profiles
                .Where(profile => profile.ProfileId != profileId)
                .ToArray();
            var bindings = CopyBindings(_state.Draft.RegionBindings);
            if (boundRegions.Length != 0)
            {
                var replacementId = replacementProfileId!.Value;
                foreach (var region in boundRegions)
                {
                    bindings[region] = replacementId;
                }
            }

            ReplaceDraftInsideLock(
                _state.Draft with
                {
                    Profiles = profiles,
                    RegionBindings = bindings,
                });
        }
    }

    /// <summary>
    /// Selects a profile for the currently implemented Global region while retaining every
    /// hidden/future region binding.
    /// </summary>
    public void SelectProfile(Guid profileId)
    {
        ValidateProfileId(profileId, nameof(profileId));
        lock (_stateLock)
        {
            _ = FindProfile(_state.Draft, profileId);
            var bindings = CopyBindings(_state.Draft.RegionBindings);
            bindings[SemanticRegion.Global] = profileId;
            ReplaceDraftInsideLock(_state.Draft with { RegionBindings = bindings });
        }
    }

    /// <summary>
    /// Selects a canonical local media reference, reusing its durable identifier when the
    /// normalized Windows path already exists. Orphaned catalog entries are retained.
    /// </summary>
    public MediaReference SelectLocalMedia(
        Guid profileId,
        string path,
        MediaKind mediaKind)
    {
        ValidateProfileId(profileId, nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (mediaKind is not (MediaKind.Image or MediaKind.Video))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mediaKind),
                "Selected local media must have a validated image or video kind.");
        }

        var candidate = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = path,
            LastKnownKind = mediaKind,
        }.Snapshot();

        lock (_stateLock)
        {
            var profileIndex = FindProfileIndex(_state.Draft, profileId);
            var mediaCatalog = _state.Draft.MediaCatalog.ToArray();
            var mediaIndex = Array.FindIndex(
                mediaCatalog,
                media =>
                    media.SourceKind == MediaSourceKind.LocalFile &&
                    string.Equals(
                        media.SourceIdentifier,
                        candidate.SourceIdentifier,
                        StringComparison.OrdinalIgnoreCase));

            MediaReference selected;
            if (mediaIndex >= 0)
            {
                selected = mediaCatalog[mediaIndex] with
                {
                    SourceIdentifier = candidate.SourceIdentifier,
                    LastKnownKind = mediaKind,
                };
                mediaCatalog[mediaIndex] = selected;
            }
            else
            {
                selected = candidate;
                mediaCatalog = [.. mediaCatalog, selected];
            }

            var profiles = _state.Draft.Profiles.ToArray();
            profiles[profileIndex] = profiles[profileIndex] with
            {
                MediaId = selected.MediaId,
            };
            var recentMediaIds = new[] { selected.MediaId }
                .Concat(
                    _state.Draft.RecentMediaIds.Where(
                        mediaId => mediaId != selected.MediaId))
                .Take(SettingsV2.MaximumRecentMediaIds)
                .ToArray();

            ReplaceDraftInsideLock(
                _state.Draft with
                {
                    Profiles = profiles,
                    MediaCatalog = mediaCatalog,
                    RecentMediaIds = recentMediaIds,
                });
            return _state.Draft.FindMedia(selected.MediaId)!;
        }
    }

    /// <summary>
    /// Makes the profile an explicit Official/empty profile without deleting media metadata.
    /// </summary>
    public void ClearMedia(Guid profileId)
    {
        ValidateProfileId(profileId, nameof(profileId));
        lock (_stateLock)
        {
            var profileIndex = FindProfileIndex(_state.Draft, profileId);
            var profiles = _state.Draft.Profiles.ToArray();
            profiles[profileIndex] = profiles[profileIndex] with { MediaId = null };
            ReplaceDraftInsideLock(_state.Draft with { Profiles = profiles });
        }
    }

    private static WallpaperWorkspaceState CopyState(
        WallpaperWorkspaceState source,
        SettingsV2? draft = null,
        SettingsV2? savedDesired = null,
        SettingsV2? activeSnapshot = null,
        bool replaceActiveSnapshot = false,
        WallpaperRuntimeSurface? runtimeSurface = null,
        long? latestRevision = null,
        WallpaperWorkspacePhase? phase = null,
        WallpaperWorkspaceError? error = null,
        bool replaceError = false) =>
        new(
            draft ?? source.Draft,
            savedDesired ?? source.SavedDesired,
            replaceActiveSnapshot ? activeSnapshot : source.ActiveSnapshot,
            runtimeSurface ?? source.RuntimeSurface,
            latestRevision ?? source.LatestRevision,
            phase ?? source.Phase,
            replaceError ? error : source.Error);

    private void ReplaceDraftInsideLock(SettingsV2 draft)
    {
        var snapshot = draft.CreateSnapshot();
        _state = CopyState(_state, draft: snapshot);
    }

    private static WallpaperProfile FindProfile(SettingsV2 settings, Guid profileId) =>
        settings.Profiles.FirstOrDefault(profile => profile.ProfileId == profileId)
        ?? throw new KeyNotFoundException(
            $"Wallpaper profile '{profileId}' was not found.");

    private static int FindProfileIndex(SettingsV2 settings, Guid profileId)
    {
        for (var index = 0; index < settings.Profiles.Count; index++)
        {
            if (settings.Profiles[index].ProfileId == profileId)
            {
                return index;
            }
        }

        throw new KeyNotFoundException(
            $"Wallpaper profile '{profileId}' was not found.");
    }

    private static Dictionary<SemanticRegion, Guid> CopyBindings(
        IReadOnlyDictionary<SemanticRegion, Guid> bindings) =>
        bindings.ToDictionary(binding => binding.Key, binding => binding.Value);

    private static string CreateAvailableName(
        IReadOnlyList<WallpaperProfile> profiles,
        string baseName)
    {
        var names = new HashSet<string>(
            profiles.Select(profile => profile.Name),
            StringComparer.OrdinalIgnoreCase);
        for (var suffix = 1; ; suffix++)
        {
            var suffixText = $" {suffix}";
            var maximumBaseLength =
                WallpaperProfile.MaximumNameLength - suffixText.Length;
            if (maximumBaseLength <= 0)
            {
                throw new InvalidOperationException(
                    "A generated wallpaper profile name cannot fit the name limit.");
            }

            var truncatedBaseName = baseName.Length <= maximumBaseLength
                ? baseName
                : baseName[..maximumBaseLength].TrimEnd();
            var candidate = string.Concat(truncatedBaseName, suffixText);
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string NormalizeProfileName(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        var normalized = name.Trim();
        if (normalized.Length > WallpaperProfile.MaximumNameLength)
        {
            throw new ArgumentException(
                $"The profile name cannot exceed {WallpaperProfile.MaximumNameLength} characters.",
                parameterName);
        }

        return normalized;
    }

    private static void ValidateProfileId(Guid profileId, string parameterName)
    {
        if (profileId == Guid.Empty || profileId.Version != 7)
        {
            throw new ArgumentException(
                "The profile identifier must be an opaque UUIDv7 value.",
                parameterName);
        }
    }

    private static void ValidateRevision(long revision)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "The activation revision must be positive.");
        }
    }

    private static void ValidatePhase(WallpaperWorkspacePhase phase)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }
    }

    private static void ValidateActiveSurface(
        SettingsV2? activeSnapshot,
        WallpaperRuntimeSurface runtimeSurface)
    {
        if (activeSnapshot is null)
        {
            if (runtimeSurface.Kind == WallpaperRuntimeSurfaceKind.MediaActive)
            {
                throw new ArgumentException(
                    "A media-active runtime surface requires an active settings snapshot.",
                    nameof(runtimeSurface));
            }

            return;
        }

        var global = activeSnapshot.ResolveProfile(SemanticRegion.Global);
        if (runtimeSurface.Kind == WallpaperRuntimeSurfaceKind.MediaActive &&
            global.MediaId != runtimeSurface.MediaId)
        {
            throw new ArgumentException(
                "The active snapshot media must match the runtime surface.",
                nameof(runtimeSurface));
        }

        if (runtimeSurface.Kind == WallpaperRuntimeSurfaceKind.Official &&
            global.MediaId is not null)
        {
            throw new ArgumentException(
                "An Official runtime surface requires an empty active profile.",
                nameof(runtimeSurface));
        }
    }
}

public sealed class WallpaperProfileReplacementRequiredException : InvalidOperationException
{
    public WallpaperProfileReplacementRequiredException(
        Guid profileId,
        IReadOnlyList<SemanticRegion> boundRegions)
        : base("A replacement profile is required for every bound region.")
    {
        ArgumentNullException.ThrowIfNull(boundRegions);
        ProfileId = profileId;
        BoundRegions = new ReadOnlyCollection<SemanticRegion>(
            boundRegions.ToArray());
    }

    public Guid ProfileId { get; }

    public IReadOnlyList<SemanticRegion> BoundRegions { get; }
}
