using BackdropForCodex.Core.Codex;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Keeps compatibility observations monotonic within one wallpaper generation while requiring
/// transient structural failures to repeat before they become an irreversible downgrade.
/// </summary>
internal sealed class InjectionCapabilityState
{
    private const int RequiredStructuralFailureObservations = 3;
    private int _globalStructuralFailures;
    private int _regionStructuralFailures;
    private int _glassStructuralFailures;
    private int _audioStructuralFailures;
    private int _advancedStructuralFailures;
    private readonly InjectionCapabilityState? _stagingOwner;
    private readonly CompatibilityCapabilities? _stagingBaseCurrent;
    private bool _stagingSettled;

    internal InjectionCapabilityState()
    {
    }

    private InjectionCapabilityState(InjectionCapabilityState stagingOwner)
    {
        _stagingOwner = stagingOwner;
        _stagingBaseCurrent = stagingOwner.Current;
        Current = stagingOwner.Current;
        CopyStructuralFailureStreaksFrom(stagingOwner);
    }

    public CompatibilityCapabilities Current { get; private set; } =
        CompatibilityCapabilities.AllUnavailable(
            CompatibilityCapabilityReasonCode.DisabledForGeneration);

    public void Begin(CompatibilityCapabilities declared, bool continuesCurrentGeneration)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (continuesCurrentGeneration)
        {
            _ = Observe(declared);
            return;
        }

        Current = declared;
        ResetStructuralFailureStreaks();
    }

    public CapabilityTransition Observe(CompatibilityCapabilities observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var previous = Current;
        var confirmedObservation = new CompatibilityCapabilities(
            ConfirmStructuralFailure(
                previous.GlobalBackground,
                observation.GlobalBackground,
                ref _globalStructuralFailures),
            ConfirmStructuralFailure(
                previous.RegionRecognition,
                observation.RegionRecognition,
                ref _regionStructuralFailures),
            ConfirmStructuralFailure(
                previous.GlassStyle,
                observation.GlassStyle,
                ref _glassStructuralFailures),
            ConfirmStructuralFailure(
                previous.Audio,
                observation.Audio,
                ref _audioStructuralFailures),
            ConfirmStructuralFailure(
                previous.AdvancedSurfaces,
                observation.AdvancedSurfaces,
                ref _advancedStructuralFailures));
        Current = previous.DowngradeWith(confirmedObservation);
        return new CapabilityTransition(previous, Current);
    }

    internal InjectionCapabilityState CreateStagedCopy()
    {
        return new InjectionCapabilityState(this);
    }

    internal CapabilityTransition Commit(InjectionCapabilityState staged)
    {
        ValidateStagedCopy(staged);
        if (Current.DowngradeWith(staged.Current) != staged.Current)
        {
            throw new InvalidOperationException(
                "A staged compatibility state attempted to re-enable a committed capability.");
        }

        var previous = Current;
        Current = staged.Current;
        CopyStructuralFailureStreaksFrom(staged);
        staged._stagingSettled = true;
        return new CapabilityTransition(previous, Current);
    }

    internal void Abandon(InjectionCapabilityState staged)
    {
        ValidateStagedCopy(staged);
        CopyStructuralFailureStreaksFrom(staged);
        staged._stagingSettled = true;
    }

    public void Reset()
    {
        Current = CompatibilityCapabilities.AllUnavailable(
            CompatibilityCapabilityReasonCode.DisabledForGeneration);
        ResetStructuralFailureStreaks();
    }

    public static bool RequiresOwnedStyleDowngrade(
        CompatibilityCapabilities previous,
        CompatibilityCapabilities current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        return (previous.Glass.IsAvailable && !current.Glass.IsAvailable) ||
               (previous.Advanced.IsAvailable && !current.Advanced.IsAvailable);
    }

    private static CompatibilityCapability ConfirmStructuralFailure(
        CompatibilityCapability current,
        CompatibilityCapability observation,
        ref int consecutiveFailures)
    {
        if (!current.IsAvailable)
        {
            consecutiveFailures = 0;
            return current;
        }

        if (observation.IsAvailable)
        {
            consecutiveFailures = 0;
            return observation;
        }

        if (observation.ReasonCode !=
            CompatibilityCapabilityReasonCode.StructuralProbeFailed)
        {
            consecutiveFailures = 0;
            return observation;
        }

        consecutiveFailures = Math.Min(
            RequiredStructuralFailureObservations,
            consecutiveFailures + 1);
        if (consecutiveFailures < RequiredStructuralFailureObservations)
        {
            return current;
        }

        return observation;
    }

    private void ValidateStagedCopy(InjectionCapabilityState staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (!ReferenceEquals(staged._stagingOwner, this) || staged._stagingSettled)
        {
            throw new InvalidOperationException(
                "The compatibility transaction does not belong to this state or is already settled.");
        }

        if (staged._stagingBaseCurrent != Current)
        {
            throw new InvalidOperationException(
                "The committed compatibility state changed during a recovery transaction.");
        }
    }

    private void CopyStructuralFailureStreaksFrom(InjectionCapabilityState source)
    {
        _globalStructuralFailures = source._globalStructuralFailures;
        _regionStructuralFailures = source._regionStructuralFailures;
        _glassStructuralFailures = source._glassStructuralFailures;
        _audioStructuralFailures = source._audioStructuralFailures;
        _advancedStructuralFailures = source._advancedStructuralFailures;
    }

    private void ResetStructuralFailureStreaks()
    {
        _globalStructuralFailures = 0;
        _regionStructuralFailures = 0;
        _glassStructuralFailures = 0;
        _audioStructuralFailures = 0;
        _advancedStructuralFailures = 0;
    }
}

internal readonly record struct CapabilityTransition(
    CompatibilityCapabilities Previous,
    CompatibilityCapabilities Current);

/// <summary>
/// Selects one presentation contract at most once per injection generation.
/// Later observations can validate the selected contract but can never select another one.
/// </summary>
internal sealed class PresentationContractState
{
    public PresentationContractSnapshot Current { get; private set; } =
        PresentationContractSnapshot.NotEvaluated;

    public bool IsFinalized { get; private set; }

    public PresentationContractDecision Select(
        PresentationEvidence evidence,
        bool finalizeBaselineFallback)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (IsFinalized)
        {
            return new PresentationContractDecision(
                Current,
                PresentationContractCatalog.Observe(Current, evidence),
                IsFinalized: true);
        }

        var decision = PresentationContractCatalog.Match(
            evidence,
            finalizeBaselineFallback);
        if (decision.IsFinalized)
        {
            Current = decision.Snapshot;
            IsFinalized = true;
        }

        return decision;
    }

    public CompatibilityCapabilities Observe(PresentationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!IsFinalized)
        {
            throw new InvalidOperationException(
                "A presentation contract must be finalized before it can be observed.");
        }

        return PresentationContractCatalog.Observe(Current, evidence);
    }

    public void Reset()
    {
        Current = PresentationContractSnapshot.NotEvaluated;
        IsFinalized = false;
    }
}
