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
    public async Task StartOrUpdateAsync_ActivatesOnlyAfterValidationAndAppliesVerifiedEndpoint()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();

        var saved = await coordinator.StartOrUpdateAsync(fixture.ValidSettings);

        Assert.True(coordinator.IsActive);
        Assert.Equal(MediaKind.Image, saved.MediaKind);
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
        Assert.Equal(fixture.ValidSettings.MediaPath, fixture.Injection.LastOptions?.LocalMediaPath);
        Assert.Equal(
            fixture.SourceProvider.ContentLength,
            fixture.Injection.LastOptions?.ExpectedContentLength);
        Assert.Equal(1, fixture.SettingsRepository.SaveCount);
        Assert.Equal(WallpaperRuntimePhase.Active, coordinator.Status.Phase);
    }

    [Fact]
    public async Task StartOrUpdateAsync_PreservesLegacyMarkerAndActivatesInstalled3996Identity()
    {
        var fixture = new CoordinatorFixture(new Version(26, 721, 3996, 0));
#pragma warning disable CS0618 // Explicit backward-compatible round-trip coverage.
        fixture.SettingsRepository.Settings = fixture.SettingsRepository.Settings with
        {
            LastCompatibilityProfileId = "opaque-legacy-marker",
        };
#pragma warning restore CS0618
        await using var coordinator = fixture.CreateCoordinator();

        var saved = await coordinator.StartOrUpdateAsync(fixture.ValidSettings);

#pragma warning disable CS0618 // Explicit backward-compatible round-trip coverage.
        Assert.Equal(
            "opaque-legacy-marker",
            saved.LastCompatibilityProfileId);
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
    public async Task StartOrUpdateAsync_FutureVersionUsesTheSamePresentationContract()
    {
        var fixture = new CoordinatorFixture(new Version(999, 1, 2, 3));
        await using var coordinator = fixture.CreateCoordinator();

        _ = await coordinator.StartOrUpdateAsync(fixture.ValidSettings);

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
    [InlineData(MediaSourceKind.WallpaperEngineWorkshopProject, MediaKind.Video)]
    [InlineData(MediaSourceKind.LocalFile, MediaKind.None)]
    public async Task V1FacadeRefusesToOverwriteProjectionIncompatibleV2GlobalMedia(
        MediaSourceKind sourceKind,
        MediaKind mediaKind)
    {
        var fixture = new CoordinatorFixture();
        var media = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = sourceKind,
            SourceIdentifier = sourceKind == MediaSourceKind.LocalFile
                ? @"C:\Wallpapers\unresolved.png"
                : "42",
            LastKnownKind = mediaKind,
        };
        var profile = WallpaperProfile.CreateDefault() with
        {
            MediaId = media.MediaId,
        };
        var original = new SettingsV2
        {
            Profiles = [profile],
            MediaCatalog = [media],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = profile.ProfileId,
            },
        }.Snapshot();
        fixture.SettingsRepository.Settings = original;
        await using var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<SettingsProjectionException>(
            () => coordinator.LoadSettingsAsync());
        await Assert.ThrowsAsync<SettingsProjectionException>(
            () => coordinator.SaveSettingsAsync(fixture.ValidSettings));

        Assert.Equal(0, fixture.SettingsRepository.SaveCount);
        Assert.Equal(
            original.ResolveProfile(SemanticRegion.Global).MediaId,
            fixture.SettingsRepository.Settings
                .ResolveProfile(SemanticRegion.Global)
                .MediaId);
    }

    [Fact]
    public async Task LoadSettingsAsyncRechecksRepositoryAfterInitialSuccess()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();

        _ = await coordinator.LoadSettingsAsync();
        fixture.SettingsRepository.LoadResultOverride =
            new SettingsLoadResult.FutureReadOnly(
                SchemaVersion: 99,
                HasVersion1Backup: true);

        var exception = await Assert.ThrowsAsync<FutureSettingsVersionException>(
            () => coordinator.LoadSettingsAsync());

        Assert.Equal(99, exception.SchemaVersion);
        Assert.True(exception.HasVersion1Backup);
        Assert.Equal(2, fixture.SettingsRepository.LoadCount);
    }

    [Fact]
    public async Task SaveSettingsAsyncRechecksProjectionAgainstCurrentRepositoryState()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        _ = await coordinator.LoadSettingsAsync();
        var workshop = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "42",
            LastKnownKind = MediaKind.Video,
        };
        var profile = WallpaperProfile.CreateDefault() with
        {
            MediaId = workshop.MediaId,
        };
        fixture.SettingsRepository.Settings = new SettingsV2
        {
            Profiles = [profile],
            MediaCatalog = [workshop],
            RegionBindings = new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = profile.ProfileId,
            },
        };
        fixture.SettingsRepository.HasVersion1Backup = true;

        var exception = await Assert.ThrowsAsync<SettingsProjectionException>(
            () => coordinator.SaveSettingsAsync(fixture.ValidSettings));

        Assert.True(exception.HasVersion1Backup);
        Assert.Equal(2, fixture.SettingsRepository.LoadCount);
        Assert.Equal(0, fixture.SettingsRepository.SaveCount);
        Assert.Equal(workshop.MediaId, fixture.SettingsRepository.Settings.Profiles[0].MediaId);
    }

    [Theory]
    [InlineData(WallpaperFit.Cover, WallpaperObjectFit.Cover)]
    [InlineData(WallpaperFit.Contain, WallpaperObjectFit.Contain)]
    [InlineData(WallpaperFit.Stretch, WallpaperObjectFit.Fill)]
    public async Task StartOrUpdateAsync_MapsCompositionAndKeepsNormalizedStateConsistent(
        WallpaperFit fit,
        WallpaperObjectFit expectedObjectFit)
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var requested = fixture.ValidSettings with
        {
            Fit = fit,
            FocusX = 0.2,
            FocusY = 0.8,
            DarkOverlay = 0.9,
            LightOverlay = 0.75,
        };

        var saved = await coordinator.StartOrUpdateAsync(requested);

        var options = Assert.IsType<WallpaperInjectionOptions>(fixture.Injection.LastOptions);
        Assert.Equal(expectedObjectFit, options.ObjectFit);
        Assert.Equal(0.2, options.Composition.FocusX);
        Assert.Equal(0.8, options.Composition.FocusY);
        Assert.Equal(SettingsV1.MaximumEffectiveOverlay, options.Composition.DarkOverlay);
        Assert.Equal(SettingsV1.MaximumEffectiveOverlay, options.Composition.LightOverlay);
        Assert.Equal(SettingsV1.MaximumEffectiveOverlay, saved.DarkOverlay);
        Assert.Equal(SettingsV1.MaximumEffectiveOverlay, saved.LightOverlay);
        AssertSettingsEquivalent(
            saved,
            SettingsV1Projection.ProjectGlobal(fixture.SettingsRepository.Settings));
    }

    [Fact]
    public async Task StartOrUpdateAsync_RefusesCodexThatWasAlreadyRunning()
    {
        var fixture = new CoordinatorFixture();
        fixture.ProcessSource.Processes = [fixture.ReviewedProcess];
        await using var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<CodexAlreadyRunningException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

        Assert.Equal(0, fixture.Activation.CallCount);
        Assert.Equal(1, fixture.SourceProvider.AcquireCount);
        Assert.Equal(1, fixture.SourceProvider.DisposeCount);
        Assert.Equal(0, fixture.Injection.ApplyCount);
        Assert.Equal(WallpaperRuntimePhase.Faulted, coordinator.Status.Phase);
    }

    [Fact]
    public async Task StartOrUpdateAsync_RequiresExplicitRiskAcknowledgement()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<CdpRiskNotAcceptedException>(
            () => coordinator.StartOrUpdateAsync(
                fixture.ValidSettings with { AcceptedCdpRisk = false }));

        Assert.Equal(0, fixture.SourceProvider.AcquireCount);
        Assert.Equal(0, fixture.Activation.CallCount);
    }

    [Fact]
    public async Task StartOrUpdateAsync_CleansMediaWhenInjectionFails()
    {
        var fixture = new CoordinatorFixture();
        fixture.Injection.ApplyException = new WallpaperInjectionException("test failure");
        await using var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<WallpaperInjectionException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.Equal(1, fixture.SourceProvider.DisposeCount);
        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.False(coordinator.IsActive);
        Assert.Equal(
            CodexSecurityFailureCode.NoVerifiedTarget,
            coordinator.Compatibility.Security.FailureCode);
    }

    [Fact]
    public async Task StartOrUpdateAsync_PlaybackTransferFailureRetainsVerifiedSecurity()
    {
        var fixture = new CoordinatorFixture();
        fixture.PlaybackPool.ActivateException =
            new InvalidOperationException("playback transfer failed");
        await using var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

        Assert.Equal(
            CodexSecurityStatus.Verified,
            coordinator.Compatibility.Security.Status);
        Assert.Equal(
            CodexSecurityStage.TargetValidation,
            coordinator.Compatibility.Security.Stage);
        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public async Task StartOrUpdateAsync_BrowserHandshakeFailureHasTypedSecurityStage()
    {
        var fixture = new CoordinatorFixture();
        fixture.Injection.ApplyException = new WallpaperBrowserHandshakeException(
            "browser handshake failed",
            new InvalidOperationException("socket closed"));
        await using var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<WallpaperBrowserHandshakeException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

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
    public async Task StartOrUpdateAsync_FinalPageApplyDeadlineRetainsVerifiedTargetSecurity()
    {
        var fixture = new CoordinatorFixture();
        fixture.Injection.ApplyException = new FinalPageApplyTimeoutException(
            "final page apply timed out",
            new TimeoutException());
        await using var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<FinalPageApplyTimeoutException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

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
    public async Task DisableAsync_RemovesInjectionBeforeStoppingMedia()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);

        await coordinator.DisableAsync();

        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.Equal(["injection-stop", "pool-release"], fixture.CleanupEvents);
        Assert.False(coordinator.IsActive);
        Assert.Equal(WallpaperRuntimePhase.Idle, coordinator.Status.Phase);
    }

    [Fact]
    public async Task StartOrUpdateAsync_RefusesReplacementForPreviouslyOwnedProcess()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
        await coordinator.DisableAsync();
        fixture.ProcessSource.Processes =
        [
            fixture.ReviewedProcess with
            {
                ProcessId = 84,
                StartTimeUtc = fixture.ReviewedProcess.StartTimeUtc.AddMinutes(1),
            },
        ];

        await Assert.ThrowsAsync<CodexAlreadyRunningException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

        Assert.Equal(1, fixture.Activation.CallCount);
        Assert.Equal(2, fixture.SourceProvider.AcquireCount);
        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public async Task StartOrUpdateAsync_DoesNotAttachEndpointOwnedByDifferentProcess()
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

        await Assert.ThrowsAsync<CdpEndpointTimeoutException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

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
    public async Task StartOrUpdateAsync_PreservesDeterministicDiscoveryRejectionAtTimeout(
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

        await Assert.ThrowsAsync<CdpEndpointTimeoutException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

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
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
        fixture.Injection.PauseException = new WallpaperInjectionException("pause failed");

        await Assert.ThrowsAsync<WallpaperInjectionException>(
            () => coordinator.SetPausedAsync(true));

        Assert.False(coordinator.IsPaused);
        Assert.Equal(WallpaperRuntimePhase.Faulted, coordinator.Status.Phase);
    }

    [Fact]
    public async Task StartOrUpdateAsync_NewImageGenerationDoesNotInheritVideoPause()
    {
        var fixture = new CoordinatorFixture();
        fixture.SourceProvider.Format = MediaFormat.WebM;
        await using var coordinator = fixture.CreateCoordinator();
        var videoSettings = fixture.ValidSettings with
        {
            MediaPath = "C:\\Wallpapers\\wallpaper.webm",
            MediaKind = MediaKind.Video,
        };
        await coordinator.StartOrUpdateAsync(videoSettings);
        await coordinator.SetPausedAsync(true);
        fixture.ProcessSource.Processes = [fixture.ReviewedProcess];
        fixture.SourceProvider.Format = MediaFormat.Png;

        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);

        Assert.False(coordinator.IsPaused);
        Assert.Equal(WallpaperRuntimePhase.Active, coordinator.Status.Phase);
        Assert.Equal(2, fixture.Injection.ApplyCount);
        Assert.Equal(1, fixture.Injection.SetPausedCount);
    }

    [Fact]
    public async Task SaveSettingsAsync_PersistsRiskRevocationWithoutLaunchingCodex()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var revoked = fixture.ValidSettings with { AcceptedCdpRisk = false };

        var saved = await coordinator.SaveSettingsAsync(revoked);

        Assert.False(saved.AcceptedCdpRisk);
        Assert.False(fixture.SettingsRepository.Settings.AcceptedCdpRisk);
        Assert.Equal(0, fixture.Activation.CallCount);
    }

    [Fact]
    public async Task SaveSettingsAsync_ReturnsTheSameOverlayValuesThatWerePersisted()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        var requested = fixture.ValidSettings with
        {
            DarkOverlay = 0.95,
            LightOverlay = 0.8,
        };

        var saved = await coordinator.SaveSettingsAsync(requested);

        Assert.Equal(SettingsV1.MaximumEffectiveOverlay, saved.DarkOverlay);
        Assert.Equal(SettingsV1.MaximumEffectiveOverlay, saved.LightOverlay);
        AssertSettingsEquivalent(
            saved,
            SettingsV1Projection.ProjectGlobal(fixture.SettingsRepository.Settings));
    }

    [Fact]
    public async Task DisposeAsync_AttemptsEveryResourceAfterCleanupFailures()
    {
        var fixture = new CoordinatorFixture();
        fixture.Injection.StopException = new InvalidOperationException("injection stop failed");
        fixture.Injection.DisposeException = new InvalidOperationException("injection dispose failed");
        fixture.PlaybackPool.ReleaseException = new InvalidOperationException("media release failed");
        fixture.PlaybackPool.DisposeException = new InvalidOperationException("pool dispose failed");
        fixture.SettingsRepository.DisposeException =
            new InvalidOperationException("settings dispose failed");
        var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<AggregateException>(() => coordinator.DisposeAsync().AsTask());

        Assert.Equal(1, fixture.Injection.StopCount);
        Assert.Equal(1, fixture.Injection.DisposeCount);
        Assert.Equal(1, fixture.PlaybackPool.ReleaseCount);
        Assert.Equal(1, fixture.PlaybackPool.DisposeCount);
        Assert.Equal(1, fixture.SettingsRepository.DisposeCount);
        Assert.Equal(WallpaperRuntimePhase.Disposed, coordinator.Status.Phase);
    }

    [Fact]
    public async Task InjectionHealthFault_StopsMediaAndTransitionsRuntimeToFaulted()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
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
    public async Task InjectionAmbiguityFault_ReleasesMediaAfterSessionSelfTeardown()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
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
    }

    [Fact]
    public async Task CapabilityDegradation_IsForwardedAsTypedRuntimeSignal()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
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

        await Assert.ThrowsAsync<CodexSecurityValidationException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

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
    }

    [Fact]
    public async Task SecurityRejectionDuringUpdateStopsThePreviouslyActiveInjection()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
        fixture.Package = fixture.Package with
        {
            Descriptor = new CodexPackageDescriptor(
                "Contoso.Codex",
                "Contoso.Codex_unreviewed",
                fixture.Package.Descriptor.Version,
                CodexPackageArchitecture.X64,
                CodexSecurityValidator.OfficialApplicationId),
        };

        await Assert.ThrowsAsync<CodexSecurityValidationException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

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

        await Assert.ThrowsAsync<WallpaperPresentationContractException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

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

        await Assert.ThrowsAsync<WallpaperMediaLoadException>(
            () => coordinator.StartOrUpdateAsync(fixture.ValidSettings));

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
    public async Task DisableAsync_RetainsLastCompatibilitySnapshotForDiagnostics()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
        var beforeDisable = coordinator.Compatibility;

        await coordinator.DisableAsync();

        Assert.Equal(beforeDisable, coordinator.Compatibility);
    }

    [Fact]
    public async Task ResetSettingsAsync_RetainsLastCompatibilitySnapshotForDiagnostics()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
        var beforeReset = coordinator.Compatibility;

        await coordinator.ResetSettingsAsync();

        Assert.Equal(beforeReset, coordinator.Compatibility);
    }

    [Fact]
    public async Task CapabilityChange_FromPreviousGenerationCannotOverwriteCurrentSnapshot()
    {
        var fixture = new CoordinatorFixture();
        await using var coordinator = fixture.CreateCoordinator();
        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);
        var previousGeneration = fixture.Injection.Generation;
        fixture.ProcessSource.Processes = [fixture.ReviewedProcess];

        await coordinator.StartOrUpdateAsync(fixture.ValidSettings);

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

    private static void AssertSettingsEquivalent(SettingsV1 expected, SettingsV1 actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.MediaPath, actual.MediaPath);
        Assert.Equal(expected.MediaKind, actual.MediaKind);
        Assert.Equal(expected.Fit, actual.Fit);
        Assert.Equal(expected.FocusX, actual.FocusX);
        Assert.Equal(expected.FocusY, actual.FocusY);
        Assert.Equal(expected.PanelOpacity, actual.PanelOpacity);
        Assert.Equal(expected.BlurPx, actual.BlurPx);
        Assert.Equal(expected.DarkOverlay, actual.DarkOverlay);
        Assert.Equal(expected.LightOverlay, actual.LightOverlay);
        Assert.Equal(expected.RecentMediaPaths.ToArray(), actual.RecentMediaPaths.ToArray());
        Assert.Equal(expected.AcceptedCdpRisk, actual.AcceptedCdpRisk);
    }

    private sealed class CoordinatorFixture
    {
        private readonly VerifiedCodexIdentity _identity;

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

        public FakeSettingsRepository SettingsRepository { get; } = new();

        public List<string> CleanupEvents { get; } = [];

        public SettingsV1 ValidSettings { get; } = SettingsV1.CreateDefault() with
        {
            MediaPath = "C:\\Wallpapers\\wallpaper.png",
            MediaKind = MediaKind.Image,
            AcceptedCdpRisk = true,
        };

        public WallpaperCoordinator CreateCoordinator(WallpaperCoordinatorOptions? options = null) => new(
            new FakePackageLocator(() => Package),
            ProcessSource,
            Activation,
            Discovery,
            SourceProvider,
            PlaybackPool,
            Injection,
            SettingsRepository,
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

    private sealed class FakeProcessSource : ICodexProcessSnapshotSource
    {
        public IReadOnlyList<CodexProcessSnapshot> Processes { get; set; } = [];

        public ValueTask<IReadOnlyList<CodexProcessSnapshot>> GetProcessesAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Processes);
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

        public ValueTask<IMediaLease> AcquireLeaseAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
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

        public int ActivateCount { get; private set; }

        public int ReleaseCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Exception? ReleaseException { get; set; }

        public Exception? ActivateException { get; set; }

        public Exception? DisposeException { get; set; }

        public List<string> Events { get; set; } = [];

        public async ValueTask ActivateAsync(
            IMediaLease lease,
            CancellationToken cancellationToken = default)
        {
            ActivateCount++;
            var previous = ActiveLease;
            ActiveLease = lease;
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
            var lease = ActiveLease;
            ActiveLease = null;
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }

            if (ReleaseException is not null)
            {
                throw ReleaseException;
            }
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            var lease = ActiveLease;
            ActiveLease = null;
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

        public int DisposeCount { get; private set; }

        public List<string> Events { get; set; } = [];

        public VerifiedCdpEndpoint? LastEndpoint { get; private set; }

        public WallpaperInjectionOptions? LastOptions { get; private set; }

        public Task ApplyAsync(
            VerifiedCdpEndpoint endpoint,
            WallpaperInjectionOptions options,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
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
                return Task.FromException(ApplyException);
            }

            IsActive = true;
            return Task.CompletedTask;
        }

        public Task SetPausedAsync(bool paused, CancellationToken cancellationToken = default)
        {
            SetPausedCount++;
            return PauseException is null ? Task.CompletedTask : Task.FromException(PauseException);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            Events.Add("injection-stop");
            IsActive = false;
            LastOptions = null;
            Capabilities = CompatibilityCapabilities.AllUnavailable(
                CompatibilityCapabilityReasonCode.DisabledForGeneration);
            PresentationContract = PresentationContractSnapshot.NotEvaluated;
            return StopException is null ? Task.CompletedTask : Task.FromException(StopException);
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

    private sealed class FakeSettingsRepository : ISettingsRepository
    {
        public bool HasVersion1Backup { get; set; }

        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public SettingsV2 Settings { get; set; } = SettingsV2.CreateDefault();

        public SettingsLoadResult? LoadResultOverride { get; set; }

        public int DisposeCount { get; private set; }

        public Exception? DisposeException { get; set; }

        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult<SettingsLoadResult>(
                LoadResultOverride ??
                new SettingsLoadResult.Ready(Settings, MigratedFromVersion1: false));
        }

        public Task<SettingsV2> SaveAsync(
            SettingsV2 settings,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Settings = settings;
            return Task.FromResult(settings);
        }

        public Task<SettingsLoadResult> RestoreVersion1BackupAsync(
            CancellationToken cancellationToken = default) =>
            LoadAsync(cancellationToken);

        public Task<SettingsV2> ResetAsync(CancellationToken cancellationToken = default)
        {
            Settings = SettingsV2.CreateDefault();
            return Task.FromResult(Settings);
        }

        public void Dispose()
        {
            DisposeCount++;
            if (DisposeException is not null)
            {
                throw DisposeException;
            }
        }
    }
}
