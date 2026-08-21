using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Runtime;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Visual inputs for a generation whose media bytes arrive through an encoded stream rather than
/// a page file input.
/// </summary>
public sealed record DynamicWallpaperInjectionOptions
{
    public DynamicWallpaperInjectionOptions(
        long generation,
        WallpaperObjectFit objectFit = WallpaperObjectFit.Cover,
        double mediaOpacity = 1,
        GlassEffectOptions? glass = null,
        WallpaperCompositionOptions? composition = null,
        PresentationContractSnapshot? lockedPresentationContract = null,
        CompatibilityCapabilities? capabilityCeiling = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        if (!Enum.IsDefined(objectFit))
        {
            throw new ArgumentOutOfRangeException(nameof(objectFit));
        }

        if (!double.IsFinite(mediaOpacity) || mediaOpacity is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(mediaOpacity));
        }

        if ((lockedPresentationContract is null) != (capabilityCeiling is null))
        {
            throw new ArgumentException(
                "A locked presentation contract and its capability ceiling must be supplied together.",
                nameof(lockedPresentationContract));
        }

        if (lockedPresentationContract is not null)
        {
            PresentationContractCatalog.ValidateDynamicCompatibility(
                lockedPresentationContract,
                capabilityCeiling!,
                nameof(lockedPresentationContract),
                nameof(capabilityCeiling));
        }

        Generation = generation;
        ObjectFit = objectFit;
        MediaOpacity = mediaOpacity;
        Glass = glass ?? new GlassEffectOptions();
        Composition = composition ?? new WallpaperCompositionOptions();
        LockedPresentationContract = lockedPresentationContract;
        CapabilityCeiling = capabilityCeiling;
    }

    internal DynamicWallpaperInjectionOptions(
        long generation,
        WallpaperObjectFit objectFit,
        double mediaOpacity,
        GlassEffectOptions? glass,
        WallpaperCompositionOptions? composition,
        PresentationContractSnapshot? lockedPresentationContract,
        CompatibilityCapabilities? capabilityCeiling,
        RuntimeMutationSignal? mutationSignal)
        : this(
            generation,
            objectFit,
            mediaOpacity,
            glass,
            composition,
            lockedPresentationContract,
            capabilityCeiling)
    {
        if (mutationSignal is not null && mutationSignal.Generation != generation)
        {
            throw new ArgumentException(
                "The mutation signal must belong to the injection generation.",
                nameof(mutationSignal));
        }

        MutationSignal = mutationSignal;
    }

    internal DynamicWallpaperInjectionOptions(
        long generation,
        WallpaperObjectFit objectFit,
        double mediaOpacity,
        GlassEffectOptions? glass,
        WallpaperCompositionOptions? composition,
        PresentationContractSnapshot lockedPresentationContract,
        CompatibilityCapabilities capabilityCeiling,
        InjectionCapabilityState capabilityState,
        bool requireCurrentGlobalBaseline,
        string? expectedPageIdentity = null,
        RuntimeMutationSignal? mutationSignal = null)
        : this(
            generation,
            objectFit,
            mediaOpacity,
            glass,
            composition,
            lockedPresentationContract,
            capabilityCeiling,
            mutationSignal)
    {
        CapabilityState = capabilityState ?? throw new ArgumentNullException(nameof(capabilityState));
        RequireCurrentGlobalBaseline = requireCurrentGlobalBaseline;
        if (expectedPageIdentity is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedPageIdentity);
        }

        ExpectedPageIdentity = expectedPageIdentity;
    }

    public long Generation { get; }

    public WallpaperObjectFit ObjectFit { get; }

    public double MediaOpacity { get; }

    public GlassEffectOptions Glass { get; }

    public WallpaperCompositionOptions Composition { get; }

    /// <summary>
    /// Gets the presentation contract selected by an earlier pipeline in this generation. A
    /// recovery pipeline validates this contract but can never select a different one.
    /// </summary>
    public PresentationContractSnapshot? LockedPresentationContract { get; }

    /// <summary>
    /// Gets the maximum capabilities still available to this generation. Recovery observations
    /// may only intersect this ceiling; they cannot re-enable a previously disabled capability.
    /// </summary>
    public CompatibilityCapabilities? CapabilityCeiling { get; }

    /// <summary>
    /// Carries the generation-scoped compatibility confirmation state across internal recovery
    /// attempts. It is intentionally unavailable to public callers.
    /// </summary>
    internal InjectionCapabilityState? CapabilityState { get; }

    /// <summary>
    /// Requires the first post-readiness observation to prove the Global baseline immediately.
    /// Runtime recovery leaves this false so transient observations use generation confirmation.
    /// </summary>
    internal bool RequireCurrentGlobalBaseline { get; }

    /// <summary>
    /// Carries the opaque target identity proven by the read-only initial preflight. It is never
    /// exposed to public callers and is revalidated before any page mutation.
    /// </summary>
    internal string? ExpectedPageIdentity { get; }

    /// <summary>
    /// Reports that page publication may have happened even when its acknowledgement is lost.
    /// Public callers do not participate in the coordinator transaction.
    /// </summary>
    internal RuntimeMutationSignal? MutationSignal { get; }
}
