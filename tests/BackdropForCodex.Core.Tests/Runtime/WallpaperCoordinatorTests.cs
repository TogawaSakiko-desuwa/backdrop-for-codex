using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Runtime;

public sealed class WallpaperCoordinatorTests
{
    [Fact]
    public async Task ActivateAsync_ActivatesOnlyAfterValidationAndAppliesVerifiedEndpoint()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.MediaActive, result.Outcome);
        Assert.True(coordinator.IsActive);
        Assert.True(SettingsV2Comparer.DurableEquals(
            fixture.ValidSettings,
            result.ActiveSnapshot));
        Assert.True(SettingsV2Comparer.DurableEquals(
            fixture.ValidSettings,
            coordinator.ActiveSnapshot));
        Assert.Equal(WallpaperRuntimeSurfaceKind.MediaActive, result.Surface.Kind);
        Assert.Equal(fixture.ValidMedia.MediaId, result.Surface.MediaId);
        Assert.Equal(fixture.PlaybackPool.ActiveOwnership, result.Surface.PlaybackOwnership);
        Assert.Equal(
            BackdropForCodex.Core.Tests.Codex.CodexSecurityValidatorTests
                .ReferencePackageVersion,
            fixture.Package.Descriptor.Version);
        Assert.Equal(1, fixture.Activation.CallCount);
        Assert.Equal(WallpaperCoordinator.RemoteDebuggingArguments, fixture.Activation.Arguments);
        Assert.Equal(1, fixture.SourceProvider.AcquireCount);
        Assert.Equal(1, fixture.Injection.ApplyCount);
        Assert.Equal(fixture.Endpoint, fixture.Injection.LastEndpoint);
        Assert.True(fixture.Injection.LastOptions?.Source.IsFile);
        Assert.Equal(
            fixture.ValidMedia.SourceIdentifier,
            fixture.Injection.LastOptions?.LocalMediaPath);
        Assert.Equal(
            fixture.SourceProvider.ContentLength,
            fixture.Injection.LastOptions?.ExpectedContentLength);
        Assert.Equal(WallpaperRuntimePhase.Active, coordinator.Status.Phase);
        Assert.Equal(result.Revision, coordinator.Status.Revision);
    }

    [Fact]
    public async Task ActivateAsync_PreservesLegacyMarkerAndActivatesInstalled3996Identity()
    {
        var fixture = new CoordinatorFixture(new Version(26, 721, 3996, 0));
#pragma warning disable CS0618 // Explicit backward-compatible round-trip coverage.
        var requested = fixture.ValidSettings with
        {
            LastCompatibilityProfileId = "opaque-legacy-marker",
        };
#pragma warning restore CS0618
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator, requested);

#pragma warning disable CS0618 // Explicit backward-compatible round-trip coverage.
        Assert.Equal(
            "opaque-legacy-marker",
            result.ActiveSnapshot?.LastCompatibilityProfileId);
#pragma warning restore CS0618
        Assert.Equal(
            "OpenAI.Codex_26.721.3996.0_x64__2p2nqsd0c76g0",
            fixture.Activation.Identity?.PackageFullName);
        Assert.Equal(
            PresentationContractCatalog.CodexShellId,
            coordinator.Compatibility.Presentation.ActiveContractId);
        Assert.True(coordinator.Compatibility.Capabilities.Glass.IsAvailable);
        Assert.True(coordinator.Compatibility.Capabilities.Advanced.IsAvailable);
        Assert.Equal(fixture.Endpoint, fixture.Injection.LastEndpoint);
    }

    [Fact]
    public async Task ActivateAsync_FutureVersionUsesTheSamePresentationContract()
    {
        var fixture = new CoordinatorFixture(new Version(999, 1, 2, 3));
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.MediaActive, result.Outcome);
        Assert.Equal(
            new Version(999, 1, 2, 3),
            fixture.Activation.Identity?.PackageVersion);
        Assert.Equal(
            PresentationContractCatalog.CodexShellId,
            coordinator.Compatibility.Presentation.ActiveContractId);
        Assert.Equal(
            ContractMatchState.Matched,
            coordinator.Compatibility.Presentation.MatchState);
        Assert.True(coordinator.Compatibility.Capabilities.Glass.IsAvailable);
        Assert.True(coordinator.Compatibility.Capabilities.Advanced.IsAvailable);
        Assert.Equal(fixture.Endpoint, fixture.Injection.LastEndpoint);
    }

    [Theory]
    [InlineData(WallpaperFit.Cover, WallpaperObjectFit.Cover)]
    [InlineData(WallpaperFit.Contain, WallpaperObjectFit.Contain)]
    [InlineData(WallpaperFit.Stretch, WallpaperObjectFit.Fill)]
    public async Task ActivateAsync_MapsCompositionFromCanonicalV2Snapshot(
        WallpaperFit fit,
        WallpaperObjectFit expectedObjectFit)
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var requested = fixture.UpdateGlobalProfile(profile => profile with
        {
            Fit = fit,
            FocusX = 0.2,
            FocusY = 0.8,
            DarkOverlay = 0.9,
            LightOverlay = 0.75,
        });

        var result = await fixture.ActivateAsync(coordinator, requested);

        Assert.Equal(RuntimeActivationOutcome.MediaActive, result.Outcome);
        var options = Assert.IsType<WallpaperInjectionOptions>(fixture.Injection.LastOptions);
        Assert.Equal(expectedObjectFit, options.ObjectFit);
        Assert.Equal(0.2, options.Composition.FocusX);
        Assert.Equal(0.8, options.Composition.FocusY);
        Assert.Equal(
            WallpaperCompositionOptions.MaximumOverlayOpacity,
            options.Composition.DarkOverlay);
        Assert.Equal(
            WallpaperCompositionOptions.MaximumOverlayOpacity,
            options.Composition.LightOverlay);
        var activeProfile = Assert.IsType<SettingsV2>(result.ActiveSnapshot)
            .ResolveProfile(SemanticRegion.Global);
        Assert.Equal(0.9, activeProfile.DarkOverlay);
        Assert.Equal(0.75, activeProfile.LightOverlay);
    }

    [Fact]
    public async Task TryPromoteActiveSnapshotAsync_AdvancesRevisionWithoutNewGeneration()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var active = await fixture.ActivateAsync(coordinator);
        var promotedSettings = fixture.UpdateGlobalProfile(
            profile => profile with { Name = "Promoted name" });
        var revision = fixture.NextRevision();

        var promoted = await coordinator.TryPromoteActiveSnapshotAsync(
            RuntimeActivationRequest.Create(revision, promotedSettings));

        var result = Assert.IsType<RuntimeActivationResult>(promoted);
        Assert.Equal(RuntimeActivationOutcome.MediaActive, result.Outcome);
        Assert.Equal(active.Surface.Generation, result.Surface.Generation);
        Assert.Equal(1, fixture.Injection.ApplyCount);
        Assert.Equal(1, fixture.PlaybackPool.ActivateCount);
        Assert.Equal(revision, coordinator.Status.Revision);
        Assert.Equal(
            "Promoted name",
            coordinator.ActiveSnapshot?
                .ResolveProfile(SemanticRegion.Global)
                .Name);
    }

    [Fact]
    public async Task ActivateAsync_RefusesCodexThatWasAlreadyRunningWithoutDomMutation()
    {
        var fixture = new CoordinatorFixture();
        fixture.ProcessSource.Processes = [fixture.ReviewedProcess];
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(
            typeof(CodexAlreadyRunningException).FullName,
            result.Error?.ExceptionType);
        Assert.Equal(0, fixture.Activation.CallCount);
        Assert.Equal(1, fixture.SourceProvider.AcquireCount);
        Assert.Equal(1, fixture.SourceProvider.DisposeCount);
        Assert.Equal(0, fixture.Injection.ApplyCount);
        Assert.Equal(WallpaperRuntimePhase.Faulted, coordinator.Status.Phase);
    }

    [Fact]
    public async Task ActivateAsync_RequiresExplicitRiskAcknowledgementBeforeMediaOrDomWork()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(
            coordinator,
            fixture.ValidSettings with { AcceptedCdpRisk = false });

        Assert.Equal(RuntimeActivationOutcome.SavedButNotActivated, result.Outcome);
        Assert.Equal("cdp-risk-not-accepted", result.Error?.Code);
        Assert.Equal(0, fixture.SourceProvider.AcquireCount);
        Assert.Equal(0, fixture.Activation.CallCount);
        Assert.Equal(0, fixture.Injection.ApplyCount);
    }

    [Fact]
    public async Task ActivateAsync_LeaseFailureBeforeRuntimeReportsDisconnected()
    {
        var fixture = new CoordinatorFixture();
        fixture.SourceProvider.AcquireException =
            new FileNotFoundException("media disappeared");
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(
            RuntimeActivationOutcome.SavedButNotActivated,
            result.Outcome);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Disconnected,
            result.Surface.Kind);
        Assert.Null(result.ActiveSnapshot);
        Assert.Equal("media-lease-unavailable", result.Error?.Code);
        Assert.Equal(0, fixture.Activation.CallCount);
        Assert.Equal(0, fixture.Injection.ApplyCount);
        Assert.Equal(0, fixture.PlaybackPool.ActivateCount);
    }

    [Fact]
    public async Task ActivateAsync_LeaseFailurePreservesPreviousMediaSurface()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var active = await fixture.ActivateAsync(coordinator);
        fixture.SourceProvider.AcquireException =
            new FileNotFoundException("media disappeared");
        var changed = fixture.UpdateGlobalProfile(
            profile => profile with { BlurPx = profile.BlurPx + 1 });

        var result = await fixture.ActivateAsync(coordinator, changed);

        Assert.Equal(
            RuntimeActivationOutcome.SavedButNotActivated,
            result.Outcome);
        Assert.Equal(active.Surface, result.Surface);
        Assert.True(
            SettingsV2Comparer.DurableEquals(
                active.ActiveSnapshot!,
                result.ActiveSnapshot!));
        Assert.Equal(1, fixture.Injection.ApplyCount);
        Assert.Equal(1, fixture.PlaybackPool.ActivateCount);
    }

    [Fact]
    public async Task ActivateAsync_CancellationDuringSecurityValidationReportsCleanupTruth()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        fixture.ProcessSource.Processes = [fixture.ReviewedProcess];
        var processCheckpoint = new AsyncCheckpoint();
        fixture.ProcessSource.BeforeGetProcessesAsync = (call, _) =>
            call == 2
                ? processCheckpoint.WaitAsync(CancellationToken.None)
                : Task.CompletedTask;
        using var cancellation = new CancellationTokenSource();
        var changed = fixture.UpdateGlobalProfile(
            profile => profile with { BlurPx = profile.BlurPx + 1 });

        var activation = coordinator.ActivateAsync(
            RuntimeActivationRequest.Create(
                fixture.NextRevision(),
                changed),
            cancellation.Token);
        await processCheckpoint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        processCheckpoint.Release.TrySetResult();
        var result = await activation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RuntimeActivationOutcome.Canceled, result.Outcome);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Official, result.Surface.Kind);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Official, coordinator.Surface.Kind);
        Assert.Null(result.ActiveSnapshot);
        Assert.Null(coordinator.ActiveSnapshot);
        Assert.Equal(WallpaperRuntimePhase.Idle, coordinator.Status.Phase);
        Assert.False(fixture.Injection.IsActive);
        Assert.Null(fixture.PlaybackPool.ActiveLease);
    }

    [Fact]
    public async Task ActivateAsync_CancellationAtInjectionCheckpointCleansPendingGeneration()
    {
        var fixture = new CoordinatorFixture();
        var injectionCheckpoint = new AsyncCheckpoint();
        fixture.Injection.BeforeApplyAsync = (_, _) =>
            injectionCheckpoint.WaitAsync(CancellationToken.None);
        await using var coordinator = fixture.CreateCoordinator();
        using var cancellation = new CancellationTokenSource();

        var activation = coordinator.ActivateAsync(
            RuntimeActivationRequest.Create(
                fixture.NextRevision(),
                fixture.ValidSettings),
            cancellation.Token);
        await injectionCheckpoint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        injectionCheckpoint.Release.TrySetResult();
        var result = await activation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RuntimeActivationOutcome.Canceled, result.Outcome);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Official, result.Surface.Kind);
        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Null(fixture.PlaybackPool.ActiveLease);
        Assert.Equal(1, fixture.SourceProvider.DisposeCount);
    }

    [Fact]
    public async Task ActivateAsync_CancellationAtPoolTransferCheckpointCleansInjectionAndLease()
    {
        var fixture = new CoordinatorFixture();
        var poolCheckpoint = new AsyncCheckpoint();
        fixture.PlaybackPool.BeforeActivateAsync = (_, _) =>
            poolCheckpoint.WaitAsync(CancellationToken.None);
        await using var coordinator = fixture.CreateCoordinator();
        using var cancellation = new CancellationTokenSource();

        var activation = coordinator.ActivateAsync(
            RuntimeActivationRequest.Create(
                fixture.NextRevision(),
                fixture.ValidSettings),
            cancellation.Token);
        await poolCheckpoint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        poolCheckpoint.Release.TrySetResult();
        var result = await activation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RuntimeActivationOutcome.Canceled, result.Outcome);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Official, result.Surface.Kind);
        Assert.False(fixture.Injection.IsActive);
        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Null(fixture.PlaybackPool.ActiveLease);
        Assert.Equal(1, fixture.SourceProvider.DisposeCount);
    }

    [Fact]
    public async Task RestoreOfficialAsync_CancellationAtCleanupCheckpointReportsOwnedInjection()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        var cleanupCheckpoint = new AsyncCheckpoint();
        fixture.Injection.BeforeStopAsync = (_, _) =>
            cleanupCheckpoint.WaitAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var restore = coordinator.RestoreOfficialAsync(
            fixture.NextRevision(),
            cancellation.Token);
        await cleanupCheckpoint.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        cleanupCheckpoint.Release.TrySetResult();
        var result = await restore.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Faulted, result.Surface.Kind);
        Assert.True(result.Surface.OwnsInjection);
        Assert.True(fixture.Injection.IsActive);
        Assert.Null(fixture.PlaybackPool.ActiveLease);
        Assert.NotNull(result.ActiveSnapshot);
    }

    [Fact]
    public async Task ActivateAsync_CleansMediaWhenInjectionFails()
    {
        var fixture = new CoordinatorFixture();
        fixture.Injection.ApplyException = new WallpaperInjectionException("test failure");
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(
            typeof(WallpaperInjectionException).FullName,
            result.Error?.ExceptionType);
        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.Equal(1, fixture.SourceProvider.DisposeCount);
        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Null(fixture.PlaybackPool.ActiveLease);
        Assert.Null(fixture.PlaybackPool.ActiveOwnership);
        Assert.False(coordinator.IsActive);
        Assert.Equal(
            CodexSecurityFailureCode.NoVerifiedTarget,
            coordinator.Compatibility.Security.FailureCode);
    }

    [Fact]
    public async Task ActivateAsync_PlaybackTransferFailureRetainsVerifiedSecurity()
    {
        var fixture = new CoordinatorFixture();
        fixture.PlaybackPool.ActivateException =
            new InvalidOperationException("playback transfer failed");
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(
            CodexSecurityStatus.Verified,
            coordinator.Compatibility.Security.Status);
        Assert.Equal(
            CodexSecurityStage.TargetValidation,
            coordinator.Compatibility.Security.Stage);
        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.Equal(1, fixture.SourceProvider.DisposeCount);
        Assert.Null(fixture.PlaybackPool.ActiveLease);
        Assert.Null(fixture.PlaybackPool.ActiveOwnership);
        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public async Task ActivateAsync_CleanupFailureReportsPlaybackPoolTruth()
    {
        var fixture = new CoordinatorFixture();
        fixture.PlaybackPool.ActivateException =
            new InvalidOperationException("playback transfer failed");
        fixture.PlaybackPool.ReleaseException =
            new InvalidOperationException("media release failed");
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        var activeLease = Assert.IsAssignableFrom<IMediaLease>(
            fixture.PlaybackPool.ActiveLease);
        var activeOwnership = Assert.IsType<PlaybackOwnershipToken>(
            fixture.PlaybackPool.ActiveOwnership);
        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Faulted, result.Surface.Kind);
        Assert.Equal(activeLease.Reference.MediaId, result.Surface.MediaId);
        Assert.Equal(activeOwnership, result.Surface.PlaybackOwnership);
        Assert.True(result.Surface.OwnsPlayback);
        Assert.False(result.Surface.OwnsInjection);
        Assert.Equal(result.Surface, coordinator.Surface);

        fixture.PlaybackPool.ReleaseException = null;
    }

    [Fact]
    public async Task ActivateAsync_BrowserHandshakeFailureHasTypedSecurityStage()
    {
        var fixture = new CoordinatorFixture();
        fixture.Injection.ApplyException = new WallpaperBrowserHandshakeException(
            "browser handshake failed",
            new InvalidOperationException("socket closed"));
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(
            CodexSecurityStatus.Rejected,
            coordinator.Compatibility.Security.Status);
        Assert.Equal(
            CodexSecurityStage.BrowserHandshake,
            coordinator.Compatibility.Security.Stage);
        Assert.Equal(
            CodexSecurityFailureCode.EndpointUnreachable,
            coordinator.Compatibility.Security.FailureCode);
    }

    [Fact]
    public async Task ActivateAsync_FinalPageApplyDeadlineRetainsVerifiedTargetSecurity()
    {
        var fixture = new CoordinatorFixture();
        fixture.Injection.ApplyException = new FinalPageApplyTimeoutException(
            "final page apply timed out",
            new TimeoutException());
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(
            CodexSecurityStatus.Verified,
            coordinator.Compatibility.Security.Status);
        Assert.Equal(
            CodexSecurityStage.TargetValidation,
            coordinator.Compatibility.Security.Stage);
        Assert.Equal(
            CodexSecurityFailureCode.None,
            coordinator.Compatibility.Security.FailureCode);
    }

    [Fact]
    public async Task RestoreOfficialAsync_RemovesInjectionBeforeOwnedMedia()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);

        var result = await coordinator.RestoreOfficialAsync(fixture.NextRevision());

        Assert.Equal(RuntimeActivationOutcome.Canceled, result.Outcome);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Official, result.Surface.Kind);
        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.Equal(["injection-stop", "pool-release"], fixture.CleanupEvents);
        Assert.False(coordinator.IsActive);
        Assert.Equal(WallpaperRuntimePhase.Idle, coordinator.Status.Phase);
    }

    [Fact]
    public async Task RestoreOfficialAsync_CleanupFailureReportsPlaybackPoolTruth()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        var activeLease = Assert.IsAssignableFrom<IMediaLease>(
            fixture.PlaybackPool.ActiveLease);
        var activeOwnership = Assert.IsType<PlaybackOwnershipToken>(
            fixture.PlaybackPool.ActiveOwnership);
        fixture.PlaybackPool.ReleaseException =
            new InvalidOperationException("media release failed");

        var result = await coordinator.RestoreOfficialAsync(fixture.NextRevision());

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Faulted, result.Surface.Kind);
        Assert.Equal(activeLease.Reference.MediaId, result.Surface.MediaId);
        Assert.Equal(activeOwnership, result.Surface.PlaybackOwnership);
        Assert.True(result.Surface.OwnsPlayback);
        Assert.False(result.Surface.OwnsInjection);
        Assert.Same(activeLease, fixture.PlaybackPool.ActiveLease);
        Assert.Equal(activeOwnership, fixture.PlaybackPool.ActiveOwnership);
        Assert.Equal(result.Surface, coordinator.Surface);

        fixture.PlaybackPool.ReleaseException = null;
    }

    [Fact]
    public async Task ActivateAsync_RefusesReplacementForPreviouslyOwnedProcess()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        _ = await coordinator.RestoreOfficialAsync(fixture.NextRevision());
        fixture.ProcessSource.Processes =
        [
            fixture.ReviewedProcess with
            {
                ProcessId = 84,
                StartTimeUtc = fixture.ReviewedProcess.StartTimeUtc.AddMinutes(1),
            },
        ];

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(1, fixture.Activation.CallCount);
        Assert.Equal(2, fixture.SourceProvider.AcquireCount);
        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public async Task ActivateAsync_DoesNotAttachEndpointOwnedByDifferentProcess()
    {
        var fixture = new CoordinatorFixture();
        var foreignEndpoint = new VerifiedCdpEndpoint(
            fixture.Endpoint.Candidate with { ProcessId = 84 },
            fixture.Endpoint.Browser,
            fixture.Endpoint.BrowserWebSocketUri,
            fixture.Endpoint.Targets,
            fixture.Endpoint.Identity);
        fixture.Discovery.Results.Clear();
        fixture.Discovery.Results.Enqueue(new CdpDiscoveryResult([foreignEndpoint], []));
        await using var coordinator = fixture.CreateCoordinator(
            new WallpaperCoordinatorOptions
            {
                DiscoveryTimeout = TimeSpan.FromMilliseconds(25),
                DiscoveryInterval = TimeSpan.FromMilliseconds(1),
            });

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(0, fixture.Injection.ApplyCount);
        Assert.Equal(1, fixture.Activation.CallCount);
        Assert.Equal(
            CodexSecurityFailureCode.EndpointDiscoveryTimedOut,
            coordinator.Compatibility.Security.FailureCode);
    }

    [Theory]
    [InlineData(
        CdpEndpointRejection.NoCodexTarget,
        CodexSecurityStage.TargetValidation,
        CodexSecurityFailureCode.NoCodexTarget)]
    [InlineData(
        CdpEndpointRejection.TargetSocketMismatch,
        CodexSecurityStage.TargetValidation,
        CodexSecurityFailureCode.TargetSocketMismatch)]
    public async Task ActivateAsync_PreservesDeterministicDiscoveryRejectionAtTimeout(
        CdpEndpointRejection endpointRejection,
        CodexSecurityStage expectedStage,
        CodexSecurityFailureCode expectedFailureCode)
    {
        var fixture = new CoordinatorFixture();
        fixture.Discovery.Results.Clear();
        fixture.Discovery.Results.Enqueue(new CdpDiscoveryResult(
            [],
            [
                new CdpEndpointProbe(
                    fixture.Endpoint.Candidate,
                    endpointRejection,
                    @"sensitive C:\Users\person\wallpaper.png https://example.invalid/private"),
            ]));
        await using var coordinator = fixture.CreateCoordinator(
            new WallpaperCoordinatorOptions
            {
                DiscoveryTimeout = TimeSpan.FromMilliseconds(25),
                DiscoveryInterval = TimeSpan.FromMilliseconds(1),
            });

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(CodexSecurityStatus.Rejected, coordinator.Compatibility.Security.Status);
        Assert.Equal(expectedStage, coordinator.Compatibility.Security.Stage);
        Assert.Equal(expectedFailureCode, coordinator.Compatibility.Security.FailureCode);
        Assert.DoesNotContain(
            "sensitive",
            coordinator.Compatibility.Security.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Injection.ApplyCount);
    }

    [Fact]
    public async Task SetPausedAsync_CommitsStateOnlyAfterPageConfirmsIt()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        fixture.Injection.PauseException = new WallpaperInjectionException("pause failed");

        await Assert.ThrowsAsync<WallpaperInjectionException>(
            () => coordinator.SetPausedAsync(true));

        Assert.False(coordinator.IsPaused);
        Assert.Equal(WallpaperRuntimePhase.Faulted, coordinator.Status.Phase);
    }

    [Fact]
    public async Task ActivateAsync_NewImageGenerationDoesNotInheritVideoPause()
    {
        var fixture = new CoordinatorFixture();
        fixture.SourceProvider.Format = MediaFormat.WebM;
        await using var coordinator = fixture.CreateCoordinator();
        var videoSettings = CoordinatorFixture.CreateSettings(
            "C:\\Wallpapers\\wallpaper.webm",
            MediaKind.Video);
        _ = await fixture.ActivateAsync(coordinator, videoSettings);
        await coordinator.SetPausedAsync(true);
        fixture.ProcessSource.Processes = [fixture.ReviewedProcess];
        fixture.SourceProvider.Format = MediaFormat.Png;

        _ = await fixture.ActivateAsync(coordinator);

        Assert.False(coordinator.IsPaused);
        Assert.Equal(WallpaperRuntimePhase.Active, coordinator.Status.Phase);
        Assert.Equal(2, fixture.Injection.ApplyCount);
        Assert.Equal(1, fixture.Injection.SetPausedCount);
    }

    [Fact]
    public async Task DisposeAsync_AttemptsEveryResourceAfterCleanupFailures()
    {
        var fixture = new CoordinatorFixture();
        fixture.Injection.StopException = new InvalidOperationException("injection stop failed");
        fixture.Injection.DisposeException = new InvalidOperationException("injection dispose failed");
        fixture.PlaybackPool.ReleaseException = new InvalidOperationException("media release failed");
        fixture.PlaybackPool.DisposeException = new InvalidOperationException("pool dispose failed");
        var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<AggregateException>(() => coordinator.DisposeAsync().AsTask());

        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Equal(1, fixture.Injection.DisposeCount);
        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.Equal(1, fixture.PlaybackPool.DisposeCount);
        Assert.Equal(WallpaperRuntimePhase.Disposed, coordinator.Status.Phase);
    }

    [Fact]
    public async Task InjectionHealthFault_StopsMediaAndTransitionsRuntimeToFaulted()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        var faulted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.Phase == WallpaperRuntimePhase.Faulted)
            {
                faulted.TrySetResult();
            }
        };

        fixture.Injection.RaiseHealthFault();
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(coordinator.IsActive);
        Assert.Null(fixture.PlaybackPool.ActiveLease);
        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.Equal(WallpaperRuntimePhase.Faulted, coordinator.Status.Phase);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Disconnected, coordinator.Surface.Kind);
        Assert.Null(coordinator.ActiveSnapshot);
        Assert.Equal(
            CodexSecurityFailureCode.TargetRevalidationFailed,
            coordinator.Compatibility.Security.FailureCode);
        Assert.All(
            GetCapabilities(coordinator.Capabilities),
            capability => Assert.Equal(
                CompatibilityCapabilityReasonCode.SecurityRejected,
                capability.ReasonCode));
    }

    [Fact]
    public async Task InjectionHealthFault_CleanupFailureReportsPlaybackPoolTruth()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        var activeLease = Assert.IsAssignableFrom<IMediaLease>(
            fixture.PlaybackPool.ActiveLease);
        var activeOwnership = Assert.IsType<PlaybackOwnershipToken>(
            fixture.PlaybackPool.ActiveOwnership);
        fixture.PlaybackPool.ReleaseException =
            new InvalidOperationException("media release failed");
        var faulted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.Phase == WallpaperRuntimePhase.Faulted)
            {
                faulted.TrySetResult();
            }
        };

        fixture.Injection.RaiseHealthFault();
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WallpaperRuntimeSurfaceKind.Faulted, coordinator.Surface.Kind);
        Assert.Equal(activeLease.Reference.MediaId, coordinator.Surface.MediaId);
        Assert.Equal(activeOwnership, coordinator.Surface.PlaybackOwnership);
        Assert.True(coordinator.Surface.OwnsPlayback);
        Assert.False(coordinator.Surface.OwnsInjection);
        Assert.Same(activeLease, fixture.PlaybackPool.ActiveLease);
        Assert.Equal(activeOwnership, fixture.PlaybackPool.ActiveOwnership);
        Assert.Null(coordinator.ActiveSnapshot);

        fixture.PlaybackPool.ReleaseException = null;
    }

    [Fact]
    public async Task InjectionAmbiguityFault_ReleasesMediaAfterSessionSelfTeardown()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        var faulted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StatusChanged += (_, status) =>
        {
            if (status.Phase == WallpaperRuntimePhase.Faulted)
            {
                faulted.TrySetResult();
            }
        };

        fixture.Injection.RaiseHealthFaultAfterSelfTeardown();
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(coordinator.IsActive);
        Assert.Equal(0, fixture.Injection.Generation);
        Assert.Null(fixture.PlaybackPool.ActiveLease);
        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.Equal(WallpaperRuntimePhase.Faulted, coordinator.Status.Phase);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Disconnected, coordinator.Surface.Kind);
    }

    [Fact]
    public async Task CapabilityDegradation_IsForwardedAsTypedRuntimeSignal()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        var changed = new TaskCompletionSource<WallpaperInjectionCapabilitiesChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.CapabilitiesChanged += (_, eventArgs) => changed.TrySetResult(eventArgs);

        fixture.Injection.RaiseGlassCapabilityDegradation();
        var observed = await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(fixture.Injection.Generation, observed.Generation);
        Assert.True(observed.Previous.Glass.IsAvailable);
        Assert.False(observed.Current.Glass.IsAvailable);
        Assert.Equal(observed.Current, coordinator.Capabilities);
    }

    [Fact]
    public async Task SecurityRejection_RemainsAvailableToProductionDiagnostics()
    {
        var fixture = new CoordinatorFixture();
        fixture.Package = fixture.Package with
        {
            Descriptor = new CodexPackageDescriptor(
                "Contoso.Codex",
                "Contoso.Codex_unreviewed",
                fixture.Package.Descriptor.Version,
                CodexPackageArchitecture.X64,
                CodexSecurityValidator.OfficialApplicationId),
        };
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.All(
            GetCapabilities(coordinator.Capabilities),
            capability =>
            {
                Assert.False(capability.IsAvailable);
                Assert.Equal(
                    CompatibilityCapabilityReasonCode.SecurityRejected,
                    capability.ReasonCode);
            });
        Assert.Equal(
            fixture.Package.Descriptor.Version,
            coordinator.Compatibility.CodexVersion);
        Assert.Equal(
            CodexSecurityFailureCode.UnofficialPackageIdentity,
            coordinator.Compatibility.Security.FailureCode);
        Assert.Equal(0, fixture.SourceProvider.AcquireCount);
        Assert.Equal(0, fixture.Injection.ApplyCount);
        Assert.Equal(WallpaperRuntimeSurfaceKind.Faulted, result.Surface.Kind);
    }

    [Fact]
    public async Task SecurityRejectionDuringUpdateStopsThePreviouslyActiveInjection()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        fixture.Package = fixture.Package with
        {
            Descriptor = new CodexPackageDescriptor(
                "Contoso.Codex",
                "Contoso.Codex_unreviewed",
                fixture.Package.Descriptor.Version,
                CodexPackageArchitecture.X64,
                CodexSecurityValidator.OfficialApplicationId),
        };

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.False(coordinator.IsActive);
        Assert.Equal(1, fixture.Injection.ApplyCount);
        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.Equal(
            CodexSecurityStatus.Rejected,
            coordinator.Compatibility.Security.Status);
    }

    [Fact]
    public async Task StructuralProbeFailure_SurvivesFailedApplyAndSessionCleanup()
    {
        var fixture = new CoordinatorFixture();
        var baseline = PresentationContractCatalog.CreateFullySupportedCapabilities();
        fixture.Injection.ApplyCapabilities = baseline.DowngradeWith(
            new CompatibilityCapabilities(
                new CompatibilityCapability(
                    false,
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed),
                baseline.Regions,
                baseline.Glass,
                baseline.Audio,
                baseline.Advanced));
        fixture.Injection.ApplyException =
            new WallpaperPresentationContractException(
                "global structural probe failed");
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.StructuralProbeFailed,
            coordinator.Capabilities.Global.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.AvailableFromPresentationContract,
            coordinator.Capabilities.Glass.ReasonCode);
        Assert.All(
            GetCapabilities(fixture.Injection.Capabilities),
            capability => Assert.Equal(
                CompatibilityCapabilityReasonCode.DisabledForGeneration,
                capability.ReasonCode));
        Assert.Equal(
            CodexSecurityStatus.Verified,
            coordinator.Compatibility.Security.Status);
        Assert.Equal(
            CodexSecurityStage.TargetValidation,
            coordinator.Compatibility.Security.Stage);
    }

    [Fact]
    public async Task FutureVersionPresentationEvidence_SurvivesFailedApply()
    {
        var fixture = new CoordinatorFixture(new Version(27, 1, 0, 0));
        fixture.Injection.ApplyException =
            new WallpaperMediaLoadException("test failure");
        await using var coordinator = fixture.CreateCoordinator();

        var result = await fixture.ActivateAsync(coordinator);

        Assert.Equal(RuntimeActivationOutcome.Failed, result.Outcome);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.AvailableFromGlobalBaseline,
            coordinator.Capabilities.Global.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.NotImplementedInCurrentRelease,
            coordinator.Capabilities.Regions.ReasonCode);
        Assert.Equal(
            CompatibilityCapabilityReasonCode.AvailableFromPresentationContract,
            coordinator.Capabilities.Glass.ReasonCode);
        Assert.Equal(
            PresentationContractCatalog.CodexShellId,
            coordinator.Compatibility.Presentation.ActiveContractId);
        Assert.Equal(new Version(27, 1, 0, 0), coordinator.Compatibility.CodexVersion);
        Assert.Equal(
            CodexSecurityStatus.Verified,
            coordinator.Compatibility.Security.Status);
    }

    [Fact]
    public async Task RestoreOfficialAsync_RetainsLastCompatibilitySnapshotForDiagnostics()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        var beforeDisable = coordinator.Compatibility;

        _ = await coordinator.RestoreOfficialAsync(fixture.NextRevision());

        Assert.Equal(beforeDisable, coordinator.Compatibility);
    }

    [Fact]
    public async Task CapabilityChange_FromPreviousGenerationCannotOverwriteCurrentSnapshot()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await fixture.ActivateAsync(coordinator);
        var previousGeneration = fixture.Injection.Generation;
        fixture.ProcessSource.Processes = [fixture.ReviewedProcess];

        _ = await fixture.ActivateAsync(coordinator);

        var current = coordinator.Capabilities;
        var stale = current.DowngradeWith(
            new CompatibilityCapabilities(
                new CompatibilityCapability(
                    false,
                    CompatibilityCapabilityReasonCode.StructuralProbeFailed),
                current.Regions,
                current.Glass,
                current.Audio,
                current.Advanced));
        fixture.Injection.RaiseCapabilityChange(previousGeneration, stale);

        Assert.NotEqual(previousGeneration, fixture.Injection.Generation);
        Assert.Equal(current, coordinator.Capabilities);
    }

    private static CompatibilityCapability[] GetCapabilities(
        CompatibilityCapabilities capabilities) =>
        [
            capabilities.Global,
            capabilities.Regions,
            capabilities.Glass,
            capabilities.Audio,
            capabilities.Advanced,
        ];

    private sealed class CoordinatorFixture
    {
        private readonly VerifiedCodexIdentity _identity;
        private long _nextRevision;

        public CoordinatorFixture(Version? packageVersion = null)
        {
            packageVersion ??=
                BackdropForCodex.Core.Tests.Codex.CodexSecurityValidatorTests
                    .ReferencePackageVersion;
            var descriptor =
                BackdropForCodex.Core.Tests.Codex.CodexSecurityValidatorTests
                    .CreateOfficialPackage(packageVersion);
            _identity = BackdropForCodex.Core.Tests.Codex.CodexSecurityValidatorTests
                .GetIdentity(packageVersion);
            Package = new InstalledCodexPackage(
                descriptor,
                _identity.PackageFullName,
                _identity.PackageRoot!,
                "app/ChatGPT.exe");
            ReviewedProcess = new CodexProcessSnapshot(
                42,
                "ChatGPT.exe",
                _identity.PackageFamilyName,
                _identity.PackageFullName,
                new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
                WindowsCodexProcessSnapshotSource.CurrentSessionId,
                null);
            Endpoint = new VerifiedCdpEndpoint(
                new CdpEndpointCandidate(
                    ReviewedProcess.ProcessId,
                    ReviewedProcess.ExecutableName,
                    ReviewedProcess.PackageFamilyName,
                    ReviewedProcess.PackageFullName,
                    ReviewedProcess.StartTimeUtc,
                    ReviewedProcess.SessionId,
                    new Uri("http://127.0.0.1:49152/")),
                new CdpBrowserVersion(
                    "Chrome/140.0.0.0",
                    "1.3",
                    null,
                    null,
                    "ws://127.0.0.1:49152/devtools/browser/test"),
                new Uri("ws://127.0.0.1:49152/devtools/browser/test"),
                [new ClassifiedCdpTarget(
                    new CdpTargetDescriptor(
                        "page",
                        "page",
                        "Codex",
                        "app://codex/index.html",
                        "ws://127.0.0.1:49152/devtools/page/page"),
                    CdpTargetClassification.CodexPage)],
                _identity);
            Discovery.Results.Enqueue(new CdpDiscoveryResult([Endpoint], []));
            Injection.Events = CleanupEvents;
            PlaybackPool.Events = CleanupEvents;
            ValidSettings = CreateSettings(
                "C:\\Wallpapers\\wallpaper.png",
                MediaKind.Image);
            ValidMedia = Assert.Single(ValidSettings.MediaCatalog);
        }

        public InstalledCodexPackage Package { get; set; }

        public CodexProcessSnapshot ReviewedProcess { get; }

        public VerifiedCdpEndpoint Endpoint { get; }

        public FakeProcessSource ProcessSource { get; } = new();

        public FakeActivationManager Activation { get; } = new();

        public FakeDiscovery Discovery { get; } = new();

        public FakeSourceProvider SourceProvider { get; } = new();

        public FakePlaybackPool PlaybackPool { get; } = new();

        public FakeInjectionSession Injection { get; } = new();

        public List<string> CleanupEvents { get; } = [];

        public SettingsV2 ValidSettings { get; }

        public MediaReference ValidMedia { get; }

        public long NextRevision() => Interlocked.Increment(ref _nextRevision);

        public Task<RuntimeActivationResult> ActivateAsync(
            WallpaperCoordinator coordinator,
            SettingsV2? settings = null,
            RuntimeLaunchMode launchMode = RuntimeLaunchMode.ManualApply) =>
            coordinator.ActivateAsync(
                RuntimeActivationRequest.Create(
                    NextRevision(),
                    settings ?? ValidSettings,
                    launchMode));

        public static SettingsV2 CreateSettings(string mediaPath, MediaKind mediaKind)
        {
            var media = new MediaReference
            {
                MediaId = Guid.CreateVersion7(),
                SourceKind = MediaSourceKind.LocalFile,
                SourceIdentifier = mediaPath,
                LastKnownKind = mediaKind,
            };
            var profile = WallpaperProfile.CreateDefault() with
            {
                MediaId = media.MediaId,
            };
            return new SettingsV2
            {
                Profiles = [profile],
                MediaCatalog = [media],
                RegionBindings = new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] = profile.ProfileId,
                },
                AcceptedCdpRisk = true,
            }.CreateSnapshot();
        }

        public SettingsV2 UpdateGlobalProfile(
            Func<WallpaperProfile, WallpaperProfile> update)
        {
            ArgumentNullException.ThrowIfNull(update);
            var current = ValidSettings.ResolveProfile(SemanticRegion.Global);
            var updated = update(current);
            return (ValidSettings with
            {
                Profiles = ValidSettings.Profiles
                    .Select(profile =>
                        profile.ProfileId == current.ProfileId ? updated : profile)
                    .ToArray(),
            }).CreateSnapshot();
        }

        public WallpaperCoordinator CreateCoordinator(WallpaperCoordinatorOptions? options = null) => new(
            new FakePackageLocator(() => Package),
            ProcessSource,
            Activation,
            Discovery,
            SourceProvider,
            PlaybackPool,
            Injection,
            options ?? new WallpaperCoordinatorOptions
            {
                DiscoveryTimeout = TimeSpan.FromSeconds(1),
                DiscoveryInterval = TimeSpan.FromMilliseconds(1),
            });

        private sealed class FakePackageLocator(Func<InstalledCodexPackage> locate)
            : IInstalledCodexPackageLocator
        {
            public InstalledCodexPackage Locate() => locate();
        }
    }

    private sealed class AsyncCheckpoint
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task WaitAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FakeProcessSource : ICodexProcessSnapshotSource
    {
        private int _callCount;

        public IReadOnlyList<CodexProcessSnapshot> Processes { get; set; } = [];

        public Func<int, CancellationToken, Task>? BeforeGetProcessesAsync { get; set; }

        public async ValueTask<IReadOnlyList<CodexProcessSnapshot>> GetProcessesAsync(
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (BeforeGetProcessesAsync is { } before)
            {
                await before(call, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Processes;
        }
    }

    private sealed class FakeActivationManager : IApplicationActivationManager
    {
        public int CallCount { get; private set; }

        public string? Arguments { get; private set; }

        public VerifiedCodexIdentity? Identity { get; private set; }

        public ApplicationActivationResult Activate(
            VerifiedCodexIdentity identity,
            string? arguments = null,
            ApplicationActivationOptions options = ApplicationActivationOptions.NoErrorUi)
        {
            CallCount++;
            Arguments = arguments;
            Identity = identity;
            return new ApplicationActivationResult(42);
        }
    }

    private sealed class FakeDiscovery : ICdpEndpointDiscoveryService
    {
        public Queue<CdpDiscoveryResult> Results { get; } = new();

        public ValueTask<CdpDiscoveryResult> DiscoverAsync(
            VerifiedCodexIdentity identity,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                Results.Count == 0 ? new CdpDiscoveryResult([], []) : Results.Dequeue());
    }

    private sealed class FakeSourceProvider : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKind => MediaSourceKind.LocalFile;

        public int AcquireCount { get; private set; }

        public int DisposeCount { get; private set; }

        public MediaFormat Format { get; set; } = MediaFormat.Png;

        public long ContentLength { get; set; } = 128;

        public Exception? AcquireException { get; set; }

        public ValueTask<IMediaLease> AcquireLeaseAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            if (AcquireException is not null)
            {
                throw AcquireException;
            }

            return ValueTask.FromResult<IMediaLease>(
                new FakeMediaLease(
                    reference,
                    MediaFileInspector.CreateMetadata(Format, ContentLength),
                    () => DisposeCount++));
        }

        private sealed class FakeMediaLease(
            MediaReference reference,
            MediaFileMetadata metadata,
            Action onDispose) : IMediaLease
        {
            private int _disposed;

            public MediaReference Reference { get; } =
                reference with { LastKnownKind = metadata.Kind };

            public string ResolvedPath { get; } = reference.SourceIdentifier;

            public LocalFileIdentity FileIdentity { get; } = new(1, 1);

            public MediaFileMetadata Metadata { get; } = metadata;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    onDispose();
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakePlaybackPool : IPlaybackPool
    {
        public IMediaLease? ActiveLease { get; private set; }

        public PlaybackOwnershipToken? ActiveOwnership { get; private set; }

        public int ActivateCount { get; private set; }

        public int ReleaseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Exception? ReleaseException { get; set; }

        public Exception? ActivateException { get; set; }

        public Func<int, CancellationToken, Task>? BeforeActivateAsync { get; set; }

        public Exception? DisposeException { get; set; }

        public List<string> Events { get; set; } = [];

        public async ValueTask ActivateAsync(
            IMediaLease lease,
            CancellationToken cancellationToken = default) =>
            await ActivateOwnedAsync(
                lease,
                PlaybackOwnershipToken.Create(),
                cancellationToken);

        public async ValueTask ActivateOwnedAsync(
            IMediaLease lease,
            PlaybackOwnershipToken ownership,
            CancellationToken cancellationToken = default)
        {
            ActivateCount++;
            if (BeforeActivateAsync is { } beforeActivate)
            {
                await beforeActivate(ActivateCount, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var previous = ActiveLease;
            ActiveLease = lease;
            ActiveOwnership = ownership;
            if (previous is not null && !ReferenceEquals(previous, lease))
            {
                await previous.DisposeAsync();
            }

            if (ActivateException is not null)
            {
                throw ActivateException;
            }
        }

        public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            Events.Add("pool-release");
            if (ReleaseException is not null)
            {
                throw ReleaseException;
            }

            var lease = ActiveLease;
            ActiveLease = null;
            ActiveOwnership = null;
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
        }

        public async ValueTask<bool> ReleaseOwnedAsync(
            PlaybackOwnershipToken ownership,
            CancellationToken cancellationToken = default)
        {
            if (ActiveOwnership != ownership)
            {
                return false;
            }

            await ReleaseAsync(cancellationToken);
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            var lease = ActiveLease;
            ActiveLease = null;
            ActiveOwnership = null;
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            if (DisposeException is not null)
            {
                throw DisposeException;
            }
        }
    }

    private sealed class FakeInjectionSession :
        IWallpaperInjectionSession,
        IWallpaperInjectionHealthSource,
        IWallpaperInjectionCapabilitySource
    {
        public event EventHandler<WallpaperInjectionHealthFaultedEventArgs>? HealthFaulted;

        public event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>? CapabilitiesChanged;

        public CompatibilityCapabilities Capabilities { get; private set; } =
            CompatibilityCapabilities.AllUnavailable(
                CompatibilityCapabilityReasonCode.DisabledForGeneration);

        public PresentationContractSnapshot PresentationContract { get; private set; } =
            PresentationContractSnapshot.NotEvaluated;

        public bool IsActive { get; private set; }

        public long Generation => LastOptions?.Generation ?? 0;

        public int ApplyCount { get; private set; }

        public int StopCount { get; private set; }

        public int SetPausedCount { get; private set; }

        public Exception? ApplyException { get; set; }

        public CompatibilityCapabilities? ApplyCapabilities { get; set; }

        public Exception? PauseException { get; set; }

        public Exception? StopException { get; set; }

        public Exception? DisposeException { get; set; }

        public Func<int, CancellationToken, Task>? BeforeApplyAsync { get; set; }

        public Func<int, CancellationToken, Task>? BeforeStopAsync { get; set; }

        public int DisposeCount { get; private set; }

        public List<string> Events { get; set; } = [];

        public VerifiedCdpEndpoint? LastEndpoint { get; private set; }

        public WallpaperInjectionOptions? LastOptions { get; private set; }

        public async Task ApplyAsync(
            VerifiedCdpEndpoint endpoint,
            WallpaperInjectionOptions options,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            if (BeforeApplyAsync is { } beforeApply)
            {
                await beforeApply(ApplyCount, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            LastEndpoint = endpoint;
            LastOptions = options;
            var previous = Capabilities;
            var baseline = PresentationContractCatalog.CreateFullySupportedCapabilities();
            Capabilities = ApplyCapabilities is null
                ? baseline
                : baseline.DowngradeWith(ApplyCapabilities);
            PresentationContract = new PresentationContractSnapshot(
                PresentationContractCatalog.CodexShellId,
                ContractMatchState.Matched);
            CapabilitiesChanged?.Invoke(
                this,
                new WallpaperInjectionCapabilitiesChangedEventArgs(
                    options.Generation,
                    previous,
                    Capabilities,
                    PresentationContract));

            if (ApplyException is not null)
            {
                throw ApplyException;
            }

            IsActive = true;
        }

        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
        {
            SetPausedCount++;
            return PauseException is null ? Task.CompletedTask : Task.FromException(PauseException);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (BeforeStopAsync is { } beforeStop)
            {
                await beforeStop(StopCount, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("injection-stop");
            IsActive = false;
            LastOptions = null;
            Capabilities = CompatibilityCapabilities.AllUnavailable(
                CompatibilityCapabilityReasonCode.DisabledForGeneration);
            PresentationContract = PresentationContractSnapshot.NotEvaluated;
            if (StopException is not null)
            {
                throw StopException;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsActive = false;
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }

        public void RaiseHealthFault()
        {
            var generation = Generation;
            if (generation == 0)
            {
                throw new InvalidOperationException("Apply a wallpaper before raising a health fault.");
            }

            HealthFaulted?.Invoke(
                this,
                new WallpaperInjectionHealthFaultedEventArgs(generation, "test health fault"));
        }

        public void RaiseHealthFaultAfterSelfTeardown()
        {
            var generation = Generation;
            if (generation == 0)
            {
                throw new InvalidOperationException("Apply a wallpaper before raising a health fault.");
            }

            IsActive = false;
            LastOptions = null;
            HealthFaulted?.Invoke(
                this,
                new WallpaperInjectionHealthFaultedEventArgs(
                    generation,
                    "test target ambiguity fault"));
        }

        public void RaiseGlassCapabilityDegradation()
        {
            var generation = Generation;
            if (generation == 0)
            {
                throw new InvalidOperationException(
                    "Apply a wallpaper before degrading capabilities.");
            }

            var previous = Capabilities;
            Capabilities = previous.DowngradeWith(
                new CompatibilityCapabilities(
                    previous.Global,
                    previous.Regions,
                    new CompatibilityCapability(
                        false,
                        CompatibilityCapabilityReasonCode.StructuralProbeFailed),
                    previous.Audio,
                    previous.Advanced));
            CapabilitiesChanged?.Invoke(
                this,
                new WallpaperInjectionCapabilitiesChangedEventArgs(
                    generation,
                    previous,
                    Capabilities,
                    PresentationContract));
        }

        public void RaiseCapabilityChange(
            long generation,
            CompatibilityCapabilities capabilities)
        {
            CapabilitiesChanged?.Invoke(
                this,
                new WallpaperInjectionCapabilitiesChangedEventArgs(
                    generation,
                    Capabilities,
                    capabilities,
                    PresentationContract));
        }
    }

}
