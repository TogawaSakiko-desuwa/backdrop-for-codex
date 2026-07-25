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
            timeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(1));

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
        Assert.Equal(1, result.EligibleCount);
        Assert.Equal(1, result.AppliedCount);
        Assert.False(result.IsAmbiguous);
        Assert.True(result.AmbiguousTargetsObserved);
    }

    [Fact]
    public async Task ReadinessGate_DeadlineUsesTheLastResolvedTargetCount()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(1));
        var attempts = 0;

        var result = await gate.WaitAsync(
            _ =>
            {
                attempts++;
                return Task.FromResult(
                    new PageApplyResult(
                        EligibleCount: attempts == 1 ? 2 : 1,
                        AppliedCount: 0,
                        IsAmbiguous: attempts == 1,
                        AmbiguousTargetsObserved: attempts == 1));
            },
            CancellationToken.None);

        Assert.True(attempts > 1);
        Assert.Equal(1, result.EligibleCount);
        Assert.Equal(0, result.AppliedCount);
        Assert.False(result.IsAmbiguous);
        Assert.True(result.AmbiguousTargetsObserved);
    }

    [Fact]
    public async Task ReadinessGate_TwoToOneThenFinalAttemptAppliesGlobalBaseline()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromMilliseconds(1));
        var contractState = new PresentationContractState();
        var evidence = PresentationEvidence.FullySupported with
        {
            ShellStructure = false,
        };
        var attempts = 0;

        var readiness = await gate.WaitAsync(
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return Task.FromResult(
                        new PageApplyResult(
                            EligibleCount: 2,
                            AppliedCount: 0,
                            IsAmbiguous: true,
                            AmbiguousTargetsObserved: true));
                }

                var pending = contractState.Select(
                    evidence,
                    finalizeBaselineFallback: false);
                Assert.False(pending.IsFinalized);
                return Task.FromResult(
                    new PageApplyResult(
                        EligibleCount: 1,
                        AppliedCount: 0,
                        IsAmbiguous: false,
                        AmbiguousTargetsObserved: false));
            },
            CancellationToken.None);

        Assert.True(PuppeteerWallpaperSession.ShouldRunFinalBaselineAttempt(
            readiness,
            contractState.IsFinalized));
        var applied = await gate.RunFinalAttemptAsync(
            _ =>
            {
                var global = contractState.Select(
                    evidence,
                    finalizeBaselineFallback: true);
                Assert.True(global.IsFinalized);
                Assert.Equal(
                    PresentationContractCatalog.GlobalBaselineId,
                    global.Snapshot.ActiveContractId);
                return Task.FromResult(
                    new PageApplyResult(
                        EligibleCount: 1,
                        AppliedCount: global.Capabilities.Global.IsAvailable ? 1 : 0,
                        IsAmbiguous: false,
                        AmbiguousTargetsObserved: false));
            },
            CancellationToken.None);

        Assert.Equal(1, applied.AppliedCount);
        Assert.Equal(
            ContractMatchState.NoMatchUsingGlobalBaseline,
            contractState.Current.MatchState);
    }

    [Fact]
    public async Task ReadinessGate_DeadlineCancelsAnInFlightAttemptAndReturnsLatestResult()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(1));
        using var callerCancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var attempts = 0;

        var result = await gate.WaitAsync(
            async token =>
            {
                attempts++;
                if (attempts == 1)
                {
                    return new PageApplyResult(
                        EligibleCount: 1,
                        AppliedCount: 0,
                        IsAmbiguous: false,
                        AmbiguousTargetsObserved: false);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return default;
            },
            callerCancellation.Token);

        Assert.Equal(2, attempts);
        Assert.Equal(1, result.EligibleCount);
        Assert.False(result.IsAmbiguous);
        Assert.False(result.AmbiguousTargetsObserved);
        Assert.False(callerCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task ReadinessGate_IgnoresAnAttemptThatReturnsAfterTheDeadline()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromMilliseconds(30),
            pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await gate.WaitAsync(
            async _ =>
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    CancellationToken.None);
                return new PageApplyResult(
                    EligibleCount: 1,
                    AppliedCount: 1,
                    IsAmbiguous: false,
                    AmbiguousTargetsObserved: false);
            },
            CancellationToken.None);

        Assert.Equal(default, result);
    }

    [Fact]
    public async Task ReadinessGate_DeadlineUsesZeroWhenAllTargetsDisappear()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromMilliseconds(50),
            pollInterval: TimeSpan.FromMilliseconds(1));
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
                            EligibleCount: 0,
                            AppliedCount: 0,
                            IsAmbiguous: false,
                            AmbiguousTargetsObserved: false));
            },
            CancellationToken.None);

        Assert.True(attempts > 1);
        Assert.Equal(0, result.EligibleCount);
        Assert.Equal(0, result.AppliedCount);
        Assert.False(result.IsAmbiguous);
        Assert.True(result.AmbiguousTargetsObserved);
    }

    [Fact]
    public async Task ReadinessGate_CallerCancellationPropagates()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(1));
        using var callerCancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.WaitAsync(
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return default;
                },
                callerCancellation.Token));

        Assert.True(callerCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task ReadinessGate_PreCanceledCallerNeverRunsAnAttempt()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(1));
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        var attempted = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.WaitAsync(
                _ =>
                {
                    attempted = true;
                    return Task.FromResult(
                        new PageApplyResult(
                            EligibleCount: 1,
                            AppliedCount: 1,
                            IsAmbiguous: false,
                            AmbiguousTargetsObserved: false));
                },
                callerCancellation.Token));

        Assert.False(attempted);
    }

    [Fact]
    public async Task ReadinessGate_CallerCancellationWinsOverAnImmediateSuccess()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(1));
        using var callerCancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.WaitAsync(
                _ =>
                {
                    callerCancellation.Cancel();
                    return Task.FromResult(
                        new PageApplyResult(
                            EligibleCount: 1,
                            AppliedCount: 1,
                            IsAmbiguous: false,
                            AmbiguousTargetsObserved: false));
                },
                callerCancellation.Token));
    }

    [Fact]
    public async Task ReadinessGate_FinalAttemptHasItsOwnOperationDeadline()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromMilliseconds(100),
            pollInterval: TimeSpan.FromMilliseconds(1));
        using var callerCancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var attempted = false;

        var exception = await Assert.ThrowsAsync<FinalPageApplyTimeoutException>(
            () => gate.RunFinalAttemptAsync(
                async token =>
                {
                    attempted = true;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return default;
                },
                callerCancellation.Token));

        Assert.True(attempted);
        Assert.False(callerCancellation.IsCancellationRequested);
        Assert.IsAssignableFrom<WallpaperInjectionException>(exception);
        Assert.IsNotType<WallpaperBrowserHandshakeException>(exception);
    }

    [Fact]
    public async Task ReadinessGate_FinalAttemptRejectsSuccessReturnedAfterItsDeadline()
    {
        var gate = new InitialPageReadinessGate(
            timeout: TimeSpan.FromMilliseconds(30),
            pollInterval: TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<FinalPageApplyTimeoutException>(
            () => gate.RunFinalAttemptAsync(
                async _ =>
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        CancellationToken.None);
                    return new PageApplyResult(
                        EligibleCount: 1,
                        AppliedCount: 1,
                        IsAmbiguous: false,
                        AmbiguousTargetsObserved: false);
                },
                CancellationToken.None));
    }

    [Fact]
    public void CapabilityState_CannotReenableADegradedCapabilityWithinGeneration()
    {
        var declared = PresentationContractCatalog.CreateFullySupportedCapabilities();
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
        var declared = PresentationContractCatalog.CreateFullySupportedCapabilities();
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
        var identity = BackdropForCodex.Core.Tests.Codex.CodexSecurityValidatorTests
            .GetIdentity();
        var candidate = new CdpEndpointCandidate(
            1234,
            "ChatGPT.exe",
            identity.PackageFamilyName,
            identity.PackageFullName,
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
            identity,
            browser,
            [target]);
        return Assert.IsType<VerifiedCdpEndpoint>(result.Endpoint);
    }
}
