using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.Core.Dynamic;

/// <summary>
/// Immutable inputs for preparing one verified, generation-scoped dynamic wallpaper. The project
/// launch path remains inside the borrowed project lease and is never copied into this request or
/// a page payload.
/// </summary>
public sealed record DynamicWallpaperActivationRequest
{
    public DynamicWallpaperActivationRequest(
        long generation,
        VerifiedCdpEndpoint endpoint,
        WallpaperSourceResolution resolution,
        WallpaperProfile profile)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(profile);
        if (resolution.Descriptor.DeliveryKind !=
                WallpaperDeliveryKind.WallpaperEngineWindow ||
            resolution.Descriptor.ContentKind is not
                (WallpaperContentKind.Scene or WallpaperContentKind.Web))
        {
            throw new WallpaperSourceCapabilityException(
                "Dynamic activation requires a resolved Wallpaper Engine scene or web project.");
        }

        Generation = generation;
        Endpoint = endpoint;
        Resolution = resolution;
        Profile = profile;
    }

    internal DynamicWallpaperActivationRequest(
        long generation,
        VerifiedCdpEndpoint endpoint,
        WallpaperSourceResolution resolution,
        WallpaperProfile profile,
        RuntimeMutationSignal mutationSignal)
        : this(generation, endpoint, resolution, profile)
    {
        MutationSignal = mutationSignal ??
            throw new ArgumentNullException(nameof(mutationSignal));
        if (mutationSignal.Generation != generation)
        {
            throw new ArgumentException(
                "The mutation signal must belong to the activation generation.",
                nameof(mutationSignal));
        }
    }

    public long Generation { get; }

    public VerifiedCdpEndpoint Endpoint { get; }

    public WallpaperSourceResolution Resolution { get; }

    public WallpaperProfile Profile { get; }

    internal RuntimeMutationSignal? MutationSignal { get; }
}

/// <summary>
/// Successful, generation-scoped dynamic activation together with the immutable presentation
/// decision used to create its page-owned media surface. Compatibility belongs to the completed
/// activation transaction rather than to the disposable resource lifetime.
/// </summary>
public sealed record DynamicWallpaperActivationResult
{
    public DynamicWallpaperActivationResult(
        IActiveWallpaperLease lease,
        PresentationContractSnapshot presentation,
        CompatibilityCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (lease.DeliveryKind != ActiveWallpaperDeliveryKind.DynamicStream)
        {
            throw new ArgumentException(
                "A dynamic activation result requires a dynamic-stream lease.",
                nameof(lease));
        }

        PresentationContractCatalog.ValidateDynamicCompatibility(
            presentation,
            capabilities,
            nameof(presentation),
            nameof(capabilities));

        Lease = lease;
        Presentation = presentation;
        Capabilities = capabilities;
    }

    public IActiveWallpaperLease Lease { get; }

    public PresentationContractSnapshot Presentation { get; }

    public CompatibilityCapabilities Capabilities { get; }
}

/// <summary>
/// Prepares a complete dynamic lifetime and returns only after the page has acknowledged the
/// initialization segment and first key frame. The project lease remains caller-owned if the
/// operation fails; a successful returned lease assumes its ownership.
/// </summary>
public interface IDynamicWallpaperActivationFactory
{
    ValueTask<DynamicWallpaperCapability> ProbeAsync(
        CancellationToken cancellationToken = default);

    ValueTask<DynamicWallpaperActivationResult> ActivateAsync(
        DynamicWallpaperActivationRequest request,
        IWallpaperEngineProjectLease projectLease,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Exposes the current machine/runtime preflight without starting a Wallpaper Engine pop-out or
/// attaching to Codex.
/// </summary>
public interface IDynamicWallpaperCapabilitySource
{
    ValueTask<DynamicWallpaperCapability> ProbeDynamicWallpaperAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Establishes the page-owned MediaSource lifetime against one already verified endpoint. The
/// implementation assumes ownership of the buffer on entry and disconnects only its CDP client.
/// </summary>
public interface IDynamicWallpaperPageSessionFactory
{
    ValueTask<DynamicWallpaperActivationResult> StartAsync(
        VerifiedCdpEndpoint endpoint,
        EncodedWallpaperStreamBuffer buffer,
        DynamicWallpaperInjectionOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional internal preflight implemented by page-session factories that can prove the initial
/// presentation contract without mutating the page. Activation runs this before it allocates any
/// Wallpaper Engine, capture, encoder or page-stream resource.
/// </summary>
internal interface IDynamicWallpaperInitialPresentationReadinessSource
{
    ValueTask<DynamicWallpaperInitialPresentationReadiness> WaitForInitialPresentationAsync(
        VerifiedCdpEndpoint endpoint,
        CancellationToken cancellationToken = default);
}

internal sealed class DynamicWallpaperInitialPresentationReadiness
{
    internal DynamicWallpaperInitialPresentationReadiness(
        string targetIdentity,
        PresentationContractSnapshot presentation,
        CompatibilityCapabilities capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIdentity);
        PresentationContractCatalog.ValidateDynamicCompatibility(
            presentation,
            capabilities,
            nameof(presentation),
            nameof(capabilities));

        TargetIdentity = targetIdentity;
        Presentation = presentation;
        Capabilities = capabilities;
        CapabilityState = new InjectionCapabilityState();
        CapabilityState.Begin(capabilities, continuesCurrentGeneration: false);
    }

    internal string TargetIdentity { get; }

    internal PresentationContractSnapshot Presentation { get; }

    internal CompatibilityCapabilities Capabilities { get; }

    internal InjectionCapabilityState CapabilityState { get; }
}

internal interface IRetryableDynamicWallpaperPageSessionCleanup
{
    ValueTask ReleaseRetainedCleanupAsync();
}

/// <summary>
/// Internal page-playback contract used to freeze the currently presented frame and to prove that
/// a resumed capture generation has reached the page before playback is released again.
/// </summary>
internal interface IDynamicWallpaperPagePlaybackLease :
    IActiveWallpaperLease,
    IPausableActiveWallpaperLease
{
    long LastAcknowledgedKeyFrameSequence { get; }

    ValueTask WaitForBufferedSegmentsAsync(
        CancellationToken cancellationToken = default);

    ValueTask WaitForKeyFrameAfterAsync(
        long sequence,
        CancellationToken cancellationToken = default);
}
