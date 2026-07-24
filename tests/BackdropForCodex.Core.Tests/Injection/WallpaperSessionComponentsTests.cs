using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using Xunit;

namespace BackdropForCodex.Core.Tests.Injection;

public sealed class WallpaperSessionComponentsTests
{
    [Fact]
    public async Task ReadinessGate_UsesTenSecondProductionDeadlineAndRejectsPersistentAmbiguity()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), InitialPageReadinessGate.DefaultTimeout);

        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromTicks(1),
            pollInterval: TimeSpan.FromTicks(1));

        var result = await gate.WaitAsync(
            _ => Task.FromResult(
                new PageApplyResult(
                    EligibleCount: 2,
                    AppliedCount: 0,
                    IsAmbiguous: true,
                    AmbiguousTargetsObserved: true)),
            CancellationToken.None);

        Assert.Equal(2, result.EligibleCount);
        Assert.Equal(0, result.AppliedCount);
        Assert.True(result.IsAmbiguous);
        Assert.True(result.AmbiguousTargetsObserved);
    }

    [Fact]
    public async Task ReadinessGate_WaitsForAmbiguityToResolveWithoutSelectingEitherCandidate()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromTicks(1));
        var attempts = 0;

        var result = await gate.WaitAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(
                    attempts == 1
                        ? new PageApplyResult(
                            EligibleCount: 2,
                            AppliedCount: 0,
                            IsAmbiguous: true,
                            AmbiguousTargetsObserved: true)
                        : new PageApplyResult(
                            EligibleCount: 1,
                            AppliedCount: 1,
                            IsAmbiguous: false,
                            AmbiguousTargetsObserved: false));
            },
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(2, result.EligibleCount);
        Assert.Equal(1, result.AppliedCount);
        Assert.False(result.IsAmbiguous);
        Assert.True(result.AmbiguousTargetsObserved);
    }

    [Fact]
    public void CapabilityState_CannotReenableADegradedCapabilityWithinGeneration()
    {
        var declared = BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests
            .GetProfile()
            .Capabilities;
        var failedGlassObservation = new CompatibilityCapabilities(
            declared.Global,
            declared.Regions,
            new CompatibilityCapability(
                false,
                CompatibilityCapabilityReasonCode.StructuralProbeFailed),
            declared.Audio,
            declared.Advanced);
        var state = new InjectionCapabilityState();
        state.Begin(declared, continuesCurrentGeneration: false);

        var downgrade = state.Observe(failedGlassObservation);
        var attemptedReenable = state.Observe(declared);

        Assert.True(downgrade.Previous.Glass.IsAvailable);
        Assert.False(downgrade.Current.Glass.IsAvailable);
        Assert.False(attemptedReenable.Current.Glass.IsAvailable);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            attemptedReenable.Current.Glass.ReasonCode);

        state.Begin(declared, continuesCurrentGeneration: false);

        Assert.True(state.Current.Glass.IsAvailable);
    }

    [Fact]
    public void CapabilityState_IdentifiesOnlyOwnedStyleDowngrades()
    {
        var declared = BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests
            .GetProfile()
            .Capabilities;
        var degradedAdvanced = new CompatibilityCapabilities(
            declared.Global,
            declared.Regions,
            declared.Glass,
            declared.Audio,
            new CompatibilityCapability(
                false,
                CompatibilityCapabilityReasonCode.StructuralProbeFailed));

        Assert.True(InjectionCapabilityState.RequiresOwnedStyleDowngrade(
            declared,
            degradedAdvanced));
        Assert.False(InjectionCapabilityState.RequiresOwnedStyleDowngrade(
            degradedAdvanced,
            declared));
    }

    [Theory]
    [InlineData("not-a-uri")]
    [InlineData("app://codex.evil/index.html")]
    [InlineData("app://codex/index.html.evil")]
    public void VerifiedTargetSelector_RejectsMalformedAndLookalikeDocuments(string pageUrl)
    {
        var endpoint = CreateVerifiedEndpoint();

        Assert.False(VerifiedCodexPageSelector.IsReviewedTargetDocument(
            "codex-page",
            pageUrl,
            endpoint));
    }

    private static VerifiedCdpEndpoint CreateVerifiedEndpoint()
    {
        var candidate = new CdpEndpointCandidate(
            1234,
            "ChatGPT.exe",
            CodexCompatibilityCatalog.OfficialPackageFamilyName,
            CodexCompatibilityCatalog.SupportedPackageFullName,
            new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
            WindowsCodexProcessSnapshotSource.CurrentSessionId,
            new Uri("http://127.0.0.1:9222/"));
        var browser = new CdpBrowserVersion(
            "Chrome/140.0.0.0",
            "1.3",
            null,
            null,
            "ws://127.0.0.1:9222/devtools/browser/browser-id");
        var target = new CdpTargetDescriptor(
            "codex-page",
            "page",
            "Codex",
            "app://codex/index.html",
            "ws://127.0.0.1:9222/devtools/page/codex-page");

        var result = CdpEndpointIdentityVerifier.Verify(
            candidate,
            BackdropForCodex.Core.Tests.Codex.CodexCompatibilityTests.GetProfile(),
            browser,
            [target]);
        return Assert.IsType<VerifiedCdpEndpoint>(result.Endpoint);
    }
}
