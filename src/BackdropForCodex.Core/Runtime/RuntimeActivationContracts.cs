using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.Core.Runtime;

/// <summary>
/// The durable result of an application-level activation attempt.
/// </summary>
public enum RuntimeActivationOutcome
{
    MediaActive = 0,
    Official,
    SavedButNotActivated,
    Superseded,
    Canceled,
    Failed,
}

/// <summary>
/// A transient launch intent. It is deliberately excluded from durable settings.
/// </summary>
public enum RuntimeLaunchMode
{
    ManualApply = 0,
    EnhancedShortcut,
}

/// <summary>
/// The runtime surface that is actually visible, independent from the desired settings.
/// </summary>
public enum WallpaperRuntimeSurfaceKind
{
    Official = 0,
    MediaActive,
    Faulted,
    Disconnected,
}

/// <summary>
/// A structured, presentation-safe description of a runtime failure.
/// </summary>
public sealed record WallpaperRuntimeError
{
    public WallpaperRuntimeError(
        string code,
        string message,
        string? exceptionType = null,
        DynamicWallpaperCapabilityReasonCode? dynamicReasonCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (dynamicReasonCode is { } reasonCode &&
            (!Enum.IsDefined(reasonCode) ||
             reasonCode == DynamicWallpaperCapabilityReasonCode.None))
        {
            throw new ArgumentOutOfRangeException(nameof(dynamicReasonCode));
        }

        Code = code.Trim();
        Message = message.Trim();
        ExceptionType = string.IsNullOrWhiteSpace(exceptionType)
            ? null
            : exceptionType.Trim();
        DynamicReasonCode = dynamicReasonCode;
    }

    public string Code { get; }

    public string Message { get; }

    public string? ExceptionType { get; }

    public DynamicWallpaperCapabilityReasonCode? DynamicReasonCode { get; }

    public static WallpaperRuntimeError FromException(string code, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var dynamicFailure = FindDynamicWallpaperFailure(exception);
        if (dynamicFailure is not null)
        {
            return new WallpaperRuntimeError(
                code,
                dynamicFailure.Message,
                dynamicFailure.GetType().FullName,
                dynamicFailure.ReasonCode);
        }

        return new WallpaperRuntimeError(
            code,
            exception.Message,
            exception.GetType().FullName);
    }

    private static DynamicWallpaperUnavailableException? FindDynamicWallpaperFailure(
        Exception exception)
    {
        const int maximumVisitedExceptions = 64;
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.Count > 0 && visited.Count < maximumVisitedExceptions)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is DynamicWallpaperUnavailableException dynamicFailure)
            {
                return dynamicFailure;
            }

            if (current is AggregateException aggregate)
            {
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else if (current.InnerException is { } innerException)
            {
                pending.Push(innerException);
            }
        }

        return null;
    }
}

/// <summary>
/// A truthful snapshot of the resources currently owned by the wallpaper runtime.
/// </summary>
public sealed record WallpaperRuntimeSurface
{
    private WallpaperRuntimeSurface(
        WallpaperRuntimeSurfaceKind kind,
        long? generation,
        Guid? mediaId,
        PlaybackOwnershipToken? playbackOwnership,
        bool ownsInjection,
        WallpaperRuntimeError? error)
    {
        Kind = kind;
        Generation = generation;
        MediaId = mediaId;
        PlaybackOwnership = playbackOwnership;
        OwnsInjection = ownsInjection;
        Error = error;
    }

    public WallpaperRuntimeSurfaceKind Kind { get; }

    /// <summary>
    /// The injection generation represented by this surface. It is never an activation revision.
    /// </summary>
    public long? Generation { get; }

    public Guid? MediaId { get; }

    public PlaybackOwnershipToken? PlaybackOwnership { get; }

    public bool OwnsInjection { get; }

    public WallpaperRuntimeError? Error { get; }

    public bool OwnsPlayback =>
        PlaybackOwnership is { } ownership && !ownership.IsEmpty;

    public static WallpaperRuntimeSurface Official() =>
        new(
            WallpaperRuntimeSurfaceKind.Official,
            generation: null,
            mediaId: null,
            playbackOwnership: null,
            ownsInjection: false,
            error: null);

    public static WallpaperRuntimeSurface MediaActive(
        long generation,
        Guid mediaId,
        PlaybackOwnershipToken playbackOwnership)
    {
        ValidateGeneration(generation);
        ValidateMediaId(mediaId);
        playbackOwnership.ThrowIfEmpty(nameof(playbackOwnership));

        return new WallpaperRuntimeSurface(
            WallpaperRuntimeSurfaceKind.MediaActive,
            generation,
            mediaId,
            playbackOwnership,
            ownsInjection: true,
            error: null);
    }

    public static WallpaperRuntimeSurface Faulted(
        WallpaperRuntimeError error,
        long? generation = null,
        Guid? mediaId = null,
        PlaybackOwnershipToken? playbackOwnership = null,
        bool ownsInjection = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        ValidateOptionalResources(
            generation,
            mediaId,
            playbackOwnership,
            ownsInjection);

        return new WallpaperRuntimeSurface(
            WallpaperRuntimeSurfaceKind.Faulted,
            generation,
            mediaId,
            playbackOwnership,
            ownsInjection,
            error);
    }

    public static WallpaperRuntimeSurface Disconnected(
        WallpaperRuntimeError? error = null) =>
        new(
            WallpaperRuntimeSurfaceKind.Disconnected,
            generation: null,
            mediaId: null,
            playbackOwnership: null,
            ownsInjection: false,
            error);

    private static void ValidateOptionalResources(
        long? generation,
        Guid? mediaId,
        PlaybackOwnershipToken? playbackOwnership,
        bool ownsInjection)
    {
        if (generation is { } actualGeneration)
        {
            ValidateGeneration(actualGeneration);
        }

        if (mediaId is { } actualMediaId)
        {
            ValidateMediaId(actualMediaId);
        }

        if (playbackOwnership is { } actualOwnership)
        {
            actualOwnership.ThrowIfEmpty(nameof(playbackOwnership));
            if (mediaId is null)
            {
                throw new ArgumentException(
                    "Playback ownership requires the owned media identifier.",
                    nameof(mediaId));
            }
        }

        if (ownsInjection && generation is null)
        {
            throw new ArgumentException(
                "Injection ownership requires the owned generation.",
                nameof(generation));
        }
    }

    private static void ValidateGeneration(long generation)
    {
        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "The injection generation must be positive.");
        }
    }

    private static void ValidateMediaId(Guid mediaId)
    {
        if (mediaId == Guid.Empty || mediaId.Version != 7)
        {
            throw new ArgumentException(
                "The runtime media identifier must be a UUIDv7 value.",
                nameof(mediaId));
        }
    }
}

/// <summary>
/// An internally consistent activation request derived from one canonical settings snapshot.
/// </summary>
public sealed class RuntimeActivationRequest
{
    private RuntimeActivationRequest(
        long revision,
        RuntimeLaunchMode launchMode,
        SettingsV3 settingsSnapshot,
        WallpaperProfile globalProfile,
        MediaReference? media)
    {
        Revision = revision;
        LaunchMode = launchMode;
        SettingsSnapshot = settingsSnapshot;
        GlobalProfile = globalProfile;
        Media = media;
    }

    public long Revision { get; }

    public RuntimeLaunchMode LaunchMode { get; }

    public SettingsV3 SettingsSnapshot { get; }

    public WallpaperProfile GlobalProfile { get; }

    public MediaReference? Media { get; }

    public bool IsOfficial => Media is null;

    public static RuntimeActivationRequest Create(
        long revision,
        SettingsV3 settings,
        RuntimeLaunchMode launchMode = RuntimeLaunchMode.ManualApply)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "The activation revision must be positive.");
        }

        if (!Enum.IsDefined(launchMode))
        {
            throw new ArgumentOutOfRangeException(nameof(launchMode));
        }

        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.CreateSnapshot();
        var globalProfile = snapshot.ResolveProfile(SemanticRegion.Global);
        var media = globalProfile.MediaId is { } mediaId
            ? snapshot.FindMedia(mediaId)
            : null;

        if (globalProfile.MediaId is not null && media is null)
        {
            throw new InvalidOperationException(
                "The Global profile media must be present in the canonical media catalog.");
        }

        return new RuntimeActivationRequest(
            revision,
            launchMode,
            snapshot,
            globalProfile,
            media);
    }
}

/// <summary>
/// The typed completion of one application-level activation revision.
/// </summary>
public sealed record RuntimeActivationResult
{
    private RuntimeActivationResult(
        long revision,
        RuntimeActivationOutcome outcome,
        WallpaperRuntimeSurface surface,
        SettingsV3? activeSnapshot,
        WallpaperRuntimeError? error)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "The activation revision must be positive.");
        }

        Revision = revision;
        Outcome = outcome;
        Surface = surface ?? throw new ArgumentNullException(nameof(surface));
        ActiveSnapshot = activeSnapshot?.CreateSnapshot();
        Error = error;
    }

    public long Revision { get; }

    public RuntimeActivationOutcome Outcome { get; }

    public WallpaperRuntimeSurface Surface { get; }

    public SettingsV3? ActiveSnapshot { get; }

    public WallpaperRuntimeError? Error { get; }

    public static RuntimeActivationResult MediaActive(
        long revision,
        SettingsV3 activeSnapshot,
        WallpaperRuntimeSurface surface)
    {
        ArgumentNullException.ThrowIfNull(activeSnapshot);
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Kind != WallpaperRuntimeSurfaceKind.MediaActive ||
            surface.MediaId is not { } mediaId)
        {
            throw new ArgumentException(
                "A media-active result requires a media-active runtime surface.",
                nameof(surface));
        }

        var snapshot = activeSnapshot.CreateSnapshot();
        var globalProfile = snapshot.ResolveProfile(SemanticRegion.Global);
        if (globalProfile.MediaId != mediaId)
        {
            throw new ArgumentException(
                "The active snapshot Global media must match the runtime surface.",
                nameof(activeSnapshot));
        }

        return new RuntimeActivationResult(
            revision,
            RuntimeActivationOutcome.MediaActive,
            surface,
            snapshot,
            error: null);
    }

    public static RuntimeActivationResult Official(
        long revision,
        SettingsV3 activeSnapshot,
        WallpaperRuntimeSurface surface)
    {
        ArgumentNullException.ThrowIfNull(activeSnapshot);
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Kind != WallpaperRuntimeSurfaceKind.Official)
        {
            throw new ArgumentException(
                "An official result requires an official runtime surface.",
                nameof(surface));
        }

        var snapshot = activeSnapshot.CreateSnapshot();
        if (snapshot.ResolveProfile(SemanticRegion.Global).MediaId is not null)
        {
            throw new ArgumentException(
                "An official active snapshot cannot select Global media.",
                nameof(activeSnapshot));
        }

        return new RuntimeActivationResult(
            revision,
            RuntimeActivationOutcome.Official,
            surface,
            snapshot,
            error: null);
    }

    public static RuntimeActivationResult SavedButNotActivated(
        long revision,
        WallpaperRuntimeSurface surface,
        SettingsV3? activeSnapshot,
        WallpaperRuntimeError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new RuntimeActivationResult(
            revision,
            RuntimeActivationOutcome.SavedButNotActivated,
            surface,
            activeSnapshot,
            error);
    }

    public static RuntimeActivationResult Superseded(
        long revision,
        WallpaperRuntimeSurface surface,
        SettingsV3? activeSnapshot = null) =>
        new(
            revision,
            RuntimeActivationOutcome.Superseded,
            surface,
            activeSnapshot,
            error: null);

    public static RuntimeActivationResult Canceled(
        long revision,
        WallpaperRuntimeSurface surface,
        SettingsV3? activeSnapshot = null) =>
        new(
            revision,
            RuntimeActivationOutcome.Canceled,
            surface,
            activeSnapshot,
            error: null);

    public static RuntimeActivationResult Failed(
        long revision,
        WallpaperRuntimeSurface surface,
        SettingsV3? activeSnapshot,
        WallpaperRuntimeError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new RuntimeActivationResult(
            revision,
            RuntimeActivationOutcome.Failed,
            surface,
            activeSnapshot,
            error);
    }
}
