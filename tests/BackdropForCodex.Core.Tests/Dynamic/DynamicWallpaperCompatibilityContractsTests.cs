using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Runtime;
using Xunit;

namespace BackdropForCodex.Core.Tests.Dynamic;

public sealed class DynamicWallpaperCompatibilityContractsTests
{
    [Fact]
    public void SuccessfulDynamicContractsRejectAnUnknownMatchState()
    {
        var presentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            (ContractMatchState)int.MaxValue);

        AssertRejectedByPublicConstructors(presentation, FullySupportedCapabilities());
    }

    [Fact]
    public void SuccessfulDynamicContractsRejectAMatchedSelectionWithoutAContractIdentifier()
    {
        var presentation = new PresentationContractSnapshot(
            ActiveContractId: null,
            ContractMatchState.Matched);

        AssertRejectedByPublicConstructors(presentation, FullySupportedCapabilities());
    }

    [Theory]
    [InlineData(ContractMatchState.NoMatchUsingGlobalBaseline)]
    [InlineData(ContractMatchState.AmbiguousUsingGlobalBaseline)]
    public void SuccessfulDynamicContractsRejectAGlobalFallbackWithANonBaselineIdentifier(
        ContractMatchState matchState)
    {
        var presentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            matchState);

        AssertRejectedByPublicConstructors(
            presentation,
            GlobalOnlyCapabilities(matchState));
    }

    [Theory]
    [InlineData(ContractMatchState.NoMatchUsingGlobalBaseline, true, false)]
    [InlineData(ContractMatchState.NoMatchUsingGlobalBaseline, false, true)]
    [InlineData(ContractMatchState.AmbiguousUsingGlobalBaseline, true, false)]
    [InlineData(ContractMatchState.AmbiguousUsingGlobalBaseline, false, true)]
    public void SuccessfulDynamicContractsRejectOptionalCapabilitiesEnabledByAGlobalFallback(
        ContractMatchState matchState,
        bool glassAvailable,
        bool advancedAvailable)
    {
        var presentation = new PresentationContractSnapshot(
            PresentationContractCatalog.GlobalBaselineId,
            matchState);
        var capabilities = GlobalOnlyCapabilities(
            matchState,
            glassAvailable,
            advancedAvailable);

        AssertRejectedByPublicConstructors(presentation, capabilities);
    }

    [Fact]
    public void SuccessfulDynamicContractsAllowAMatchedSelectionWithDegradedOptionalCapabilities()
    {
        var presentation = new PresentationContractSnapshot(
            PresentationContractCatalog.CodexShellId,
            ContractMatchState.Matched);
        var capabilities = MatchedCapabilitiesWithOptionalDowngrade();
        var lease = new FakeDynamicLease();

        var result = new DynamicWallpaperActivationResult(
            lease,
            presentation,
            capabilities);
        var options = new DynamicWallpaperInjectionOptions(
            generation: lease.Generation,
            lockedPresentationContract: presentation,
            capabilityCeiling: capabilities);

        Assert.Equal(presentation, result.Presentation);
        Assert.Equal(capabilities, result.Capabilities);
        Assert.Equal(presentation, options.LockedPresentationContract);
        Assert.Equal(capabilities, options.CapabilityCeiling);
        Assert.Null(options.CapabilityState);
    }

    private static void AssertRejectedByPublicConstructors(
        PresentationContractSnapshot presentation,
        CompatibilityCapabilities capabilities)
    {
        var lease = new FakeDynamicLease();

        Assert.ThrowsAny<ArgumentException>(() =>
            new DynamicWallpaperActivationResult(
                lease,
                presentation,
                capabilities));
        Assert.ThrowsAny<ArgumentException>(() =>
            new DynamicWallpaperInjectionOptions(
                generation: lease.Generation,
                lockedPresentationContract: presentation,
                capabilityCeiling: capabilities));
    }

    private static CompatibilityCapabilities FullySupportedCapabilities() => new(
        AvailableFromGlobalBaseline(),
        Disabled(CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
        AvailableFromPresentationContract(),
        Disabled(CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
        AvailableFromPresentationContract());

    private static CompatibilityCapabilities MatchedCapabilitiesWithOptionalDowngrade() => new(
        AvailableFromGlobalBaseline(),
        Disabled(CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
        Disabled(CompatibilityCapabilityReasonCode.StructuralProbeFailed),
        Disabled(CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
        Disabled(CompatibilityCapabilityReasonCode.StructuralProbeFailed));

    private static CompatibilityCapabilities GlobalOnlyCapabilities(
        ContractMatchState matchState,
        bool glassAvailable = false,
        bool advancedAvailable = false)
    {
        var fallbackReason = matchState == ContractMatchState.NoMatchUsingGlobalBaseline
            ? CompatibilityCapabilityReasonCode.NoMatchingPresentationContract
            : CompatibilityCapabilityReasonCode.AmbiguousPresentationContract;

        return new CompatibilityCapabilities(
            AvailableFromGlobalBaseline(),
            Disabled(CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
            glassAvailable
                ? AvailableFromPresentationContract()
                : Disabled(fallbackReason),
            Disabled(CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease),
            advancedAvailable
                ? AvailableFromPresentationContract()
                : Disabled(fallbackReason));
    }

    private static CompatibilityCapability AvailableFromGlobalBaseline() => new(
        isAvailable: true,
        CompatibilityCapabilityReasonCode.AvailableFromGlobalBaseline);

    private static CompatibilityCapability AvailableFromPresentationContract() => new(
        isAvailable: true,
        CompatibilityCapabilityReasonCode.AvailableFromPresentationContract);

    private static CompatibilityCapability Disabled(
        CompatibilityCapabilityReasonCode reasonCode) => new(
            isAvailable: false,
            reasonCode);

    private sealed class FakeDynamicLease : IActiveWallpaperLease
    {
        public long Generation => 17;

        public ActiveWallpaperDeliveryKind DeliveryKind =>
            ActiveWallpaperDeliveryKind.DynamicStream;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
