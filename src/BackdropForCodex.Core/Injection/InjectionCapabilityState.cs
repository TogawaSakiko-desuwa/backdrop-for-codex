using BackdropForCodex.Core.Codex;

namespace BackdropForCodex.Core.Injection;

/// <summary>
/// Keeps compatibility observations monotonic within one wallpaper generation.
/// </summary>
internal sealed class InjectionCapabilityState
{
    public CompatibilityCapabilities Current { get; private set; } =
        CompatibilityCapabilities.AllUnavailable(
            CompatibilityCapabilityReasonCode.DisabledForGeneration);

    public void Begin(CompatibilityCapabilities declared, bool continuesCurrentGeneration)
    {
        ArgumentNullException.ThrowIfNull(declared);
        Current = continuesCurrentGeneration
            ? Current.DowngradeWith(declared)
            : declared;
    }

    public CapabilityTransition Observe(CompatibilityCapabilities observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var previous = Current;
        Current = previous.DowngradeWith(observation);
        return new CapabilityTransition(previous, Current);
    }

    public void Reset()
    {
        Current = CompatibilityCapabilities.AllUnavailable(
            CompatibilityCapabilityReasonCode.DisabledForGeneration);
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
