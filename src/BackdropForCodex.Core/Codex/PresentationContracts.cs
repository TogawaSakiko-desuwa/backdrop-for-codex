using System.Collections.ObjectModel;

namespace BackdropForCodex.Core.Codex;

public enum ContractMatchState
{
    NotEvaluated = 0,
    Matched,
    NoMatchUsingGlobalBaseline,
    AmbiguousUsingGlobalBaseline,
    GlobalBaselineFailed,
}

/// <summary>
/// Non-sensitive structural and platform evidence collected from an already verified Codex page.
/// Package versions and page data are deliberately absent.
/// </summary>
internal sealed record PresentationEvidence(
    bool GlobalStructure,
    bool ShellStructure,
    bool BackdropFilterSupported,
    bool SelectorHasSupported)
{
    internal static PresentationEvidence FullySupported { get; } = new(
        GlobalStructure: true,
        ShellStructure: true,
        BackdropFilterSupported: true,
        SelectorHasSupported: true);

    internal static PresentationEvidence Unavailable { get; } = new(
        GlobalStructure: false,
        ShellStructure: false,
        BackdropFilterSupported: false,
        SelectorHasSupported: false);
}

/// <summary>
/// Immutable, built-in presentation knowledge. Contracts contain only matching requirements;
/// actual injected CSS remains in the reviewed injection modules.
/// </summary>
internal sealed class PresentationContract
{
    internal PresentationContract(string id, bool requiresShellStructure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        RequiresShellStructure = requiresShellStructure;
    }

    internal string Id { get; }

    internal bool RequiresShellStructure { get; }

    internal bool Matches(PresentationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence.GlobalStructure &&
               (!RequiresShellStructure || evidence.ShellStructure);
    }
}

public sealed record PresentationContractSnapshot(
    string? ActiveContractId,
    ContractMatchState MatchState)
{
    public static PresentationContractSnapshot NotEvaluated { get; } = new(
        ActiveContractId: null,
        ContractMatchState.NotEvaluated);
}

internal sealed record PresentationContractDecision(
    PresentationContractSnapshot Snapshot,
    CompatibilityCapabilities Capabilities,
    bool IsFinalized);

public static class PresentationContractCatalog
{
    public const string GlobalBaselineId = "global-baseline-v1";
    public const string CodexShellId = "codex-shell-v1";

    internal static PresentationContract GlobalBaseline { get; } = new(
        GlobalBaselineId,
        requiresShellStructure: false);

    internal static PresentationContract CodexShell { get; } = new(
        CodexShellId,
        requiresShellStructure: true);

    internal static IReadOnlyList<PresentationContract> AdvancedContracts { get; } =
        new ReadOnlyCollection<PresentationContract>([CodexShell]);

    internal static PresentationContractDecision Match(
        PresentationEvidence evidence,
        bool finalizeBaselineFallback,
        IReadOnlyList<PresentationContract>? advancedContracts = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        advancedContracts ??= AdvancedContracts;
        ArgumentNullException.ThrowIfNull(advancedContracts);

        if (!GlobalBaseline.Matches(evidence))
        {
            return new PresentationContractDecision(
                new PresentationContractSnapshot(
                    ActiveContractId: null,
                    ContractMatchState.GlobalBaselineFailed),
                CreateBaselineFailed(),
                IsFinalized: finalizeBaselineFallback);
        }

        var matches = advancedContracts
            .Where(contract => contract is not null && contract.Matches(evidence))
            .ToArray();
        if (matches.Length == 1)
        {
            return new PresentationContractDecision(
                new PresentationContractSnapshot(
                    matches[0].Id,
                    ContractMatchState.Matched),
                ObserveMatchedShell(evidence),
                IsFinalized: true);
        }

        var matchState = matches.Length == 0
            ? ContractMatchState.NoMatchUsingGlobalBaseline
            : ContractMatchState.AmbiguousUsingGlobalBaseline;
        var unavailableReason = matches.Length == 0
            ? CompatibilityCapabilityReasonCode.NoMatchingPresentationContract
            : CompatibilityCapabilityReasonCode.AmbiguousPresentationContract;
        return new PresentationContractDecision(
            new PresentationContractSnapshot(GlobalBaseline.Id, matchState),
            CreateGlobalOnly(unavailableReason, evidence.GlobalStructure),
            IsFinalized: finalizeBaselineFallback);
    }

    internal static CompatibilityCapabilities Observe(
        PresentationContractSnapshot selection,
        PresentationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(evidence);

        if (selection.MatchState == ContractMatchState.Matched &&
            string.Equals(
                selection.ActiveContractId,
                CodexShell.Id,
                StringComparison.Ordinal))
        {
            return ObserveMatchedShell(evidence);
        }

        if (selection.MatchState is
            ContractMatchState.NoMatchUsingGlobalBaseline or
            ContractMatchState.AmbiguousUsingGlobalBaseline)
        {
            var reason = selection.MatchState ==
                ContractMatchState.NoMatchUsingGlobalBaseline
                ? CompatibilityCapabilityReasonCode.NoMatchingPresentationContract
                : CompatibilityCapabilityReasonCode.AmbiguousPresentationContract;
            return CreateGlobalOnly(reason, evidence.GlobalStructure);
        }

        return CreateBaselineFailed();
    }

    internal static CompatibilityCapabilities CreateFullySupportedCapabilities() =>
        ObserveMatchedShell(PresentationEvidence.FullySupported);

    private static CompatibilityCapabilities ObserveMatchedShell(
        PresentationEvidence evidence)
    {
        var global = evidence.GlobalStructure
            ? CompatibilityCapability.AvailableFromGlobalBaseline()
            : CompatibilityCapability.Disabled(
                CompatibilityCapabilityReasonCode.StructuralProbeFailed);
        var shellAvailable = evidence.GlobalStructure && evidence.ShellStructure;
        var glassAvailable = shellAvailable &&
            evidence.BackdropFilterSupported &&
            evidence.SelectorHasSupported;
        var advancedAvailable = shellAvailable && evidence.SelectorHasSupported;
        var notImplemented = CompatibilityCapability.Disabled(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease);
        return new CompatibilityCapabilities(
            global,
            notImplemented,
            glassAvailable
                ? CompatibilityCapability.AvailableFromPresentationContract()
                : CompatibilityCapability.Disabled(
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed),
            notImplemented,
            advancedAvailable
                ? CompatibilityCapability.AvailableFromPresentationContract()
                : CompatibilityCapability.Disabled(
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed));
    }

    private static CompatibilityCapabilities CreateGlobalOnly(
        CompatibilityCapabilityReasonCode optionalReason,
        bool globalAvailable)
    {
        var notImplemented = CompatibilityCapability.Disabled(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease);
        var optionalUnavailable = CompatibilityCapability.Disabled(optionalReason);
        return new CompatibilityCapabilities(
            globalAvailable
                ? CompatibilityCapability.AvailableFromGlobalBaseline()
                : CompatibilityCapability.Disabled(
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed),
            notImplemented,
            optionalUnavailable,
            notImplemented,
            optionalUnavailable);
    }

    private static CompatibilityCapabilities CreateBaselineFailed()
    {
        var structuralFailure = CompatibilityCapability.Disabled(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed);
        var notImplemented = CompatibilityCapability.Disabled(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease);
        return new CompatibilityCapabilities(
            structuralFailure,
            notImplemented,
            structuralFailure,
            notImplemented,
            structuralFailure);
    }
}
