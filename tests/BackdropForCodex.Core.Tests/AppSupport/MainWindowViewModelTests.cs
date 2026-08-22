using System.Collections.Concurrent;
using BackdropForCodex.App.Models;
using BackdropForCodex.App.Services.Errors;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.App.Services.Preferences;
using BackdropForCodex.App.Services.Wallpaper;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using BackdropForCodex.Core.Shortcuts;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task StartupResolvesCurrentWallpaperEngineReferenceWithoutDiscoveringLibrary()
    {
        var media = CreateWorkshopMedia("123456");
        var descriptor = new WallpaperSourceDescriptor(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            media.SourceIdentifier,
            "Workshop video",
            WallpaperContentKind.Video,
            WallpaperDeliveryKind.DirectMedia,
            WallpaperDeliveryCapabilities.None);
        var provider = new CountingWorkshopProvider(descriptor);
        var wallpaper = new FakeWallpaperApplicationService(
            WithSelectedMedia(SettingsV3.CreateDefault(), media));
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: new WallpaperSourceProviderRegistry([provider]));

        await viewModel.InitializeAsync();

        Assert.Equal(
            WallpaperSourceAvailability.NotLoaded,
            viewModel.SourceLibrary.IntegrationAvailability);
        Assert.Equal(0, provider.DiscoveryCount);
        Assert.Equal(1, provider.ResolveCount);
        Assert.True(viewModel.CanSubmitApply);

        await viewModel.RefreshWallpaperEngineLibraryAsync();

        Assert.Equal(
            WallpaperSourceAvailability.Ready,
            viewModel.SourceLibrary.IntegrationAvailability);
        Assert.Equal(1, provider.DiscoveryCount);
        Assert.Equal(1, provider.ResolveCount);
    }

    [Fact]
    public async Task SourceDiscoveryFailureIsVisibleAndRetryRestoresReadyStatus()
    {
        var provider = new RecoveringDiscoveryProvider();
        var registry = new WallpaperSourceProviderRegistry([provider]);
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = new MainWindowViewModel(
            new FakeWallpaperApplicationService(SettingsV3.CreateDefault()),
            preferencesStore,
            new StubErrorMapper(),
            new FallbackTextProvider(),
            sourceRegistry: registry);

        await viewModel.InitializeAsync();

        Assert.Equal(0, provider.DiscoveryCount);
        Assert.False(viewModel.SourceLibrary.HasDiscoveryFailures);

        await viewModel.RefreshWallpaperEngineLibraryAsync();

        Assert.Equal(1, provider.DiscoveryCount);
        Assert.True(viewModel.SourceLibrary.HasDiscoveryFailures);
        Assert.True(viewModel.IsStatusOpen);
        Assert.Equal(UiStatusTone.Warning, viewModel.StatusTone);
        Assert.Equal("Some sources are unavailable", viewModel.StatusTitle);

        provider.ShouldFail = false;
        await viewModel.SourceLibrary.RefreshCommand.ExecuteAsync(parameter: null);

        Assert.False(viewModel.SourceLibrary.HasDiscoveryFailures);
        Assert.Equal(UiStatusTone.Informational, viewModel.StatusTone);
        Assert.Equal("Ready", viewModel.StatusTitle);
    }

    [Fact]
    public async Task UnregisteredRecentSourceStaysPersistedButIsHiddenAndCannotBeSelected()
    {
        var media = CreateWorkshopMedia("123456");
        var settings = AddRecent(SettingsV3.CreateDefault(), media);
        var wallpaper = new FakeWallpaperApplicationService(settings);
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);

        await viewModel.InitializeAsync();

        Assert.Empty(viewModel.Recents);
        Assert.NotNull(viewModel.SavedDesired.FindMedia(media.MediaId));
        Assert.Throws<MediaSourceNotSupportedException>(
            () => viewModel.SelectSource(media));
        Assert.False(viewModel.Editor.HasSelectedMedia);
    }

    [Fact]
    public async Task RegisteredWorkshopVideoAppearsAndCanReuseEditorSelection()
    {
        var media = CreateWorkshopMedia("123456");
        var settings = AddRecent(SettingsV3.CreateDefault(), media);
        var wallpaper = new FakeWallpaperApplicationService(settings);
        using var preferencesStore = new FakeAppPreferencesStore();
        var provider = new RegisteredWorkshopProvider();
        var registry = new WallpaperSourceProviderRegistry([provider]);
        var preview = new ReferenceOnlyPreviewService();
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            previewMedia: preview,
            sourceRegistry: registry);

        await viewModel.InitializeAsync();
        var recent = Assert.Single(viewModel.Recents);
        viewModel.SelectSource(recent.Reference);

        Assert.Null(recent.Path);
        Assert.Equal("123456", recent.Reference.SourceIdentifier);
        Assert.Equal(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            viewModel.Editor.SelectedMediaReference?.SourceKind);
        Assert.Equal("123456", viewModel.Editor.SelectedMediaIdentifier);
        Assert.NotEmpty(preview.ProbedReferences);
        Assert.Equal(0, preview.PathProbeCount);

        var unknownContent = media with
        {
            MediaId = Guid.CreateVersion7(),
            LastKnownKind = MediaKind.None,
        };
        Assert.Throws<ArgumentException>(
            () => viewModel.SelectSource(unknownContent));
        Assert.Equal("123456", viewModel.Editor.SelectedMediaIdentifier);
    }

    [Fact]
    public async Task RendererBackedSource_IsTypedUnavailableAndCannotEnterApplyPath()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore();
        var preview = new FileExistencePreviewService();
        var registry = new WallpaperSourceProviderRegistry(
            [
                new LocalFileWallpaperSourceProvider(),
                new RegisteredWorkshopProvider(),
            ]);
        using var viewModel = new MainWindowViewModel(
            wallpaper,
            preferencesStore,
            new StubErrorMapper(),
            new FallbackTextProvider(),
            preview,
            registry);
        await viewModel.InitializeAsync();
        var source = new WallpaperSourceDescriptor(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            "123456",
            "Scene project",
            WallpaperContentKind.Scene,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.DynamicFrames);

        var canSubmitChanged = false;
        viewModel.PropertyChanged += (_, eventArgs) =>
            canSubmitChanged |= eventArgs.PropertyName == nameof(viewModel.CanSubmitApply);
        viewModel.SelectSource(source);

        Assert.True(canSubmitChanged);
        Assert.True(viewModel.Editor.HasSelectedMedia);
        Assert.Equal(
            WallpaperContentKind.Scene,
            SelectedMedia(viewModel.ConfigurationState.Draft)?.LastKnownContentKind);
        Assert.True(viewModel.Editor.IsPreviewUnavailable);
        Assert.False(viewModel.CanSubmitApply);
        Assert.False(await viewModel.ApplyAsync());
        Assert.Equal(0, wallpaper.ApplyCallCount);
    }

    [Fact]
    public async Task AvailableDynamicSourceCanSubmitDespiteHavingOnlyAStaticPreview()
    {
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            CreateSettings(acceptedCdpRisk: true),
            DynamicWallpaperCapability.Available());
        using var preferencesStore = new FakeAppPreferencesStore();
        var registry = new WallpaperSourceProviderRegistry(
            [
                new LocalFileWallpaperSourceProvider(),
                new MutableWorkshopDiscoveryProvider(
                    [CreateDynamicDescriptor("123456", WallpaperContentKind.Scene)]),
            ]);
        using var viewModel = new MainWindowViewModel(
            wallpaper,
            preferencesStore,
            new StubErrorMapper(),
            new FallbackTextProvider(),
            new FileExistencePreviewService(),
            registry);
        await viewModel.InitializeAsync();
        var source = new WallpaperSourceDescriptor(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            "123456",
            "Scene project",
            WallpaperContentKind.Scene,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.DynamicFrames);

        viewModel.SelectSource(source);

        Assert.Equal(1, wallpaper.ProbeCount);
        Assert.True(viewModel.Editor.IsPreviewUnavailable);
        Assert.True(viewModel.CanSubmitApply);
        Assert.True(await viewModel.ApplyAsync());
        Assert.Equal(1, wallpaper.ApplyCallCount);
    }

    [Theory]
    [InlineData(WallpaperContentKind.Scene)]
    [InlineData(WallpaperContentKind.Web)]
    public async Task DynamicApplyWithFullCompatibilityShowsNormalSuccess(
        WallpaperContentKind contentKind)
    {
        var compatibility = CreateCompatibilitySnapshot(
            PresentationEvidence.FullySupported);
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            CreateSettings(acceptedCdpRisk: true),
            DynamicWallpaperCapability.Available(),
            compatibility);
        using var preferencesStore = new FakeAppPreferencesStore(
            AppPreferencesV1.CreateDefault() with
            {
                HasAcknowledgedWebWallpaperPrivacyNotice = true,
            });
        var source = CreateDynamicDescriptor("123456", contentKind);
        var registry = new WallpaperSourceProviderRegistry(
            [new MutableWorkshopDiscoveryProvider([source])]);
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: registry);
        await viewModel.InitializeAsync();
        viewModel.SelectSource(source);

        Assert.True(await viewModel.ApplyAsync());

        Assert.Equal(ContractMatchState.Matched, compatibility.Presentation.MatchState);
        Assert.True(compatibility.Capabilities.GlassStyle.IsAvailable);
        Assert.True(compatibility.Capabilities.AdvancedSurfaces.IsAvailable);
        Assert.Equal("Wallpaper is active", viewModel.StatusTitle);
        Assert.Equal(UiStatusTone.Success, viewModel.StatusTone);
        Assert.DoesNotContain(
            "reduced effects",
            viewModel.StatusTitle,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(WallpaperContentKind.Scene)]
    [InlineData(WallpaperContentKind.Web)]
    public async Task DynamicApplyWithGlobalOnlyCompatibilityShowsReducedEffectsWarning(
        WallpaperContentKind contentKind)
    {
        var compatibility = CreateCompatibilitySnapshot(
            PresentationEvidence.FullySupported with { ShellStructure = false });
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            CreateSettings(acceptedCdpRisk: true),
            DynamicWallpaperCapability.Available(),
            compatibility);
        using var preferencesStore = new FakeAppPreferencesStore(
            AppPreferencesV1.CreateDefault() with
            {
                HasAcknowledgedWebWallpaperPrivacyNotice = true,
            });
        var source = CreateDynamicDescriptor("123456", contentKind);
        var registry = new WallpaperSourceProviderRegistry(
            [new MutableWorkshopDiscoveryProvider([source])]);
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: registry);
        await viewModel.InitializeAsync();
        viewModel.SelectSource(source);

        Assert.True(await viewModel.ApplyAsync());

        Assert.Equal(
            ContractMatchState.NoMatchUsingGlobalBaseline,
            compatibility.Presentation.MatchState);
        Assert.True(compatibility.Capabilities.GlobalBackground.IsAvailable);
        Assert.False(compatibility.Capabilities.GlassStyle.IsAvailable);
        Assert.False(compatibility.Capabilities.AdvancedSurfaces.IsAvailable);
        Assert.Equal("Wallpaper active with reduced effects", viewModel.StatusTitle);
        Assert.Equal(UiStatusTone.Warning, viewModel.StatusTone);
    }

    [Fact]
    public async Task WebWallpaperApplyRequiresExplicitPrivacyAcknowledgementAndPersistsItOnce()
    {
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            CreateSettings(acceptedCdpRisk: true),
            DynamicWallpaperCapability.Available());
        using var preferencesStore = new FakeAppPreferencesStore();
        var source = CreateDynamicDescriptor("123456", WallpaperContentKind.Web);
        var registry = new WallpaperSourceProviderRegistry(
            [new MutableWorkshopDiscoveryProvider([source])]);
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: registry);
        var promptCount = 0;
        var shouldAcknowledge = false;
        viewModel.WebWallpaperPrivacyPromptAsync = _ =>
        {
            promptCount++;
            return Task.FromResult(shouldAcknowledge);
        };
        await viewModel.InitializeAsync();
        viewModel.SelectSource(source);

        Assert.False(await viewModel.ApplyAsync());
        Assert.Equal(1, promptCount);
        Assert.Equal(0, wallpaper.ApplyCallCount);
        Assert.False(preferencesStore.Current.HasAcknowledgedWebWallpaperPrivacyNotice);

        shouldAcknowledge = true;
        Assert.True(await viewModel.ApplyAsync());
        Assert.True(preferencesStore.Current.HasAcknowledgedWebWallpaperPrivacyNotice);
        Assert.Equal(2, promptCount);
        Assert.Equal(1, wallpaper.ApplyCallCount);
        Assert.Equal(
            WallpaperContentKind.Web,
            wallpaper.LastExpectedSourceResolution?.Descriptor.ContentKind);
        Assert.Equal(
            source.SourceIdentifier,
            wallpaper.LastExpectedSourceResolution?.CanonicalReference.SourceIdentifier);

        Assert.True(await viewModel.ApplyAsync());
        Assert.Equal(2, promptCount);
        Assert.Equal(2, wallpaper.ApplyCallCount);
    }

    [Fact]
    public async Task ConcurrentWebWallpaperAppliesEachCarryTheAuthorizedResolution()
    {
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            CreateSettings(acceptedCdpRisk: true),
            DynamicWallpaperCapability.Available());
        using var preferencesStore = new FakeAppPreferencesStore();
        var source = CreateDynamicDescriptor("123456", WallpaperContentKind.Web);
        var registry = new WallpaperSourceProviderRegistry(
            [new MutableWorkshopDiscoveryProvider([source])]);
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: registry);
        var promptEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrompt = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.WebWallpaperPrivacyPromptAsync = async _ =>
        {
            promptEntered.TrySetResult();
            return await releasePrompt.Task.ConfigureAwait(false);
        };
        await viewModel.InitializeAsync();
        viewModel.SelectSource(source);

        var firstApply = viewModel.ApplyAsync();
        await promptEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondApply = viewModel.ApplyAsync();
        releasePrompt.TrySetResult(true);
        _ = await Task.WhenAll(firstApply, secondApply);

        Assert.Equal(2, wallpaper.ExpectedSourceResolutions.Count);
        Assert.All(
            wallpaper.ExpectedSourceResolutions,
            resolution => Assert.Equal(
                WallpaperContentKind.Web,
                resolution?.Descriptor.ContentKind));
    }

    [Fact]
    public async Task WebPrivacyUsesCurrentProviderKindInsteadOfStaleSceneMetadata()
    {
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            CreateSettings(acceptedCdpRisk: true),
            DynamicWallpaperCapability.Available());
        using var preferencesStore = new FakeAppPreferencesStore();
        var currentWeb = CreateDynamicDescriptor("123456", WallpaperContentKind.Web);
        var registry = new WallpaperSourceProviderRegistry(
            [new MutableWorkshopDiscoveryProvider([currentWeb])]);
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: registry);
        var promptCount = 0;
        viewModel.WebWallpaperPrivacyPromptAsync = _ =>
        {
            promptCount++;
            return Task.FromResult(false);
        };
        await viewModel.InitializeAsync();
        viewModel.SelectSource(
            CreateWorkshopDynamicMedia("123456", WallpaperContentKind.Scene));

        Assert.False(await viewModel.EnsureWebWallpaperPrivacyAcknowledgedAsync());
        Assert.Equal(1, promptCount);
        Assert.False(preferencesStore.Current.HasAcknowledgedWebWallpaperPrivacyNotice);
    }

    [Fact]
    public async Task WebPrivacyDoesNotPromptWhenCurrentProviderKindChangedToScene()
    {
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            CreateSettings(acceptedCdpRisk: true),
            DynamicWallpaperCapability.Available());
        using var preferencesStore = new FakeAppPreferencesStore();
        var currentScene = CreateDynamicDescriptor("123456", WallpaperContentKind.Scene);
        var registry = new WallpaperSourceProviderRegistry(
            [new MutableWorkshopDiscoveryProvider([currentScene])]);
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: registry);
        var promptCount = 0;
        viewModel.WebWallpaperPrivacyPromptAsync = _ =>
        {
            promptCount++;
            return Task.FromResult(true);
        };
        await viewModel.InitializeAsync();
        viewModel.SelectSource(
            CreateWorkshopDynamicMedia("123456", WallpaperContentKind.Web));

        Assert.True(await viewModel.EnsureWebWallpaperPrivacyAcknowledgedAsync());
        Assert.Equal(0, promptCount);
        Assert.False(preferencesStore.Current.HasAcknowledgedWebWallpaperPrivacyNotice);
    }

    [Fact]
    public async Task WebWallpaperAutoLaunchFailsClosedWhenPrivacyPromptIsUnavailable()
    {
        var source = CreateDynamicDescriptor("123456", WallpaperContentKind.Web);
        var settings = WithSelectedMedia(
            CreateSettings(acceptedCdpRisk: true),
            CreateWorkshopDynamicMedia("123456", WallpaperContentKind.Web));
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            settings,
            DynamicWallpaperCapability.Available());
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: new WallpaperSourceProviderRegistry(
                [new MutableWorkshopDiscoveryProvider([source])]));
        await viewModel.InitializeAsync();

        var outcome = await viewModel.AutoLaunchAsync();

        Assert.Equal(AutoLaunchOutcome.NeedsPrivacyAcknowledgement, outcome);
        Assert.Equal(0, wallpaper.ApplyCallCount);
        Assert.False(preferencesStore.Current.HasAcknowledgedWebWallpaperPrivacyNotice);
    }

    [Fact]
    public async Task MissingDynamicProjectCannotApplyAndRevivesAfterRediscovery()
    {
        var media = CreateWorkshopDynamicMedia(
            "123456",
            WallpaperContentKind.Scene);
        var settings = WithSelectedMedia(
            SettingsV3.CreateDefault() with { AcceptedCdpRisk = true },
            media);
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            settings,
            DynamicWallpaperCapability.Available());
        var provider = new MutableWorkshopDiscoveryProvider([]);
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: new WallpaperSourceProviderRegistry([provider]));

        await viewModel.InitializeAsync();

        Assert.True(viewModel.Editor.HasSelectedMedia);
        Assert.False(viewModel.CanSubmitApply);
        var canSubmitChanged = false;
        viewModel.PropertyChanged += (_, eventArgs) =>
            canSubmitChanged |= eventArgs.PropertyName == nameof(viewModel.CanSubmitApply);

        provider.Sources =
            [CreateDynamicDescriptor("123456", WallpaperContentKind.Scene)];
        await viewModel.SourceLibrary.RefreshAsync();

        Assert.True(canSubmitChanged);
        Assert.True(viewModel.CanSubmitApply);
    }

    [Fact]
    public async Task DynamicCapabilityRefreshImmediatelyInvalidatesApplyState()
    {
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            CreateSettings(acceptedCdpRisk: true),
            DynamicWallpaperCapability.Available());
        using var preferencesStore = new FakeAppPreferencesStore();
        var registry = new WallpaperSourceProviderRegistry(
            [
                new MutableWorkshopDiscoveryProvider(
                    [CreateDynamicDescriptor("123456", WallpaperContentKind.Scene)]),
            ]);
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: registry);
        await viewModel.InitializeAsync();
        viewModel.SelectSource(
            new WallpaperSourceDescriptor(
                MediaSourceKind.WallpaperEngineWorkshopProject,
                "123456",
                "Scene project",
                WallpaperContentKind.Scene,
                WallpaperDeliveryKind.WallpaperEngineWindow,
                WallpaperDeliveryCapabilities.DynamicFrames));
        Assert.True(viewModel.CanSubmitApply);
        var canSubmitChanged = false;
        viewModel.PropertyChanged += (_, eventArgs) =>
            canSubmitChanged |= eventArgs.PropertyName == nameof(viewModel.CanSubmitApply);

        wallpaper.Capability = DynamicWallpaperCapability.Unavailable(
            DynamicWallpaperCapabilityReasonCode.WallpaperEnginePlacementUnavailable);
        await viewModel.SourceLibrary.RefreshAsync();

        Assert.True(canSubmitChanged);
        Assert.False(viewModel.CanSubmitApply);
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.WallpaperEnginePlacementUnavailable,
            viewModel.SourceLibrary.DynamicCapability.ReasonCode);
    }

    [Fact]
    public async Task RuntimeCapabilityChangeReprobesDynamicApplyCommands()
    {
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            CreateSettings(acceptedCdpRisk: true),
            DynamicWallpaperCapability.Available());
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: new WallpaperSourceProviderRegistry(
                [
                    new MutableWorkshopDiscoveryProvider(
                        [CreateDynamicDescriptor("123456", WallpaperContentKind.Web)]),
                ]));
        await viewModel.InitializeAsync();
        viewModel.SelectSource(
            new WallpaperSourceDescriptor(
                MediaSourceKind.WallpaperEngineWorkshopProject,
                "123456",
                "Web project",
                WallpaperContentKind.Web,
                WallpaperDeliveryKind.WallpaperEngineWindow,
                WallpaperDeliveryCapabilities.DynamicFrames));
        Assert.True(viewModel.CanSubmitApply);
        var invalidated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(viewModel.CanSubmitApply) &&
                !viewModel.CanSubmitApply)
            {
                invalidated.TrySetResult();
            }
        };

        wallpaper.Capability = DynamicWallpaperCapability.Unavailable(
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable);
        wallpaper.RaiseCapabilitiesChanged();
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, wallpaper.ProbeCount);
        Assert.False(viewModel.CanSubmitApply);
    }

    [Theory]
    [InlineData(WallpaperContentKind.Scene)]
    [InlineData(WallpaperContentKind.Web)]
    public async Task PersistedDynamicReferenceCanBeReselectedAndReprojected(
        WallpaperContentKind contentKind)
    {
        var media = CreateWorkshopDynamicMedia("123456", contentKind);
        var settings = AddRecent(
            SettingsV3.CreateDefault() with { AcceptedCdpRisk = true },
            media);
        var wallpaper = new FakeDynamicWallpaperApplicationService(
            settings,
            DynamicWallpaperCapability.Available());
        using var preferencesStore = new FakeAppPreferencesStore();
        var registry = new WallpaperSourceProviderRegistry(
            [
                new MutableWorkshopDiscoveryProvider(
                    [CreateDynamicDescriptor("123456", contentKind)]),
            ]);
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            previewMedia: new ReferenceOnlyPreviewService(),
            sourceRegistry: registry);
        await viewModel.InitializeAsync();

        var recent = Assert.Single(viewModel.Recents);
        Assert.Equal(contentKind, recent.ContentKind);
        viewModel.SelectSource(recent.Reference);

        Assert.Equal(contentKind, viewModel.Editor.SelectedMediaReference?.LastKnownContentKind);
        Assert.Equal(media.LastKnownDisplayName, viewModel.Editor.SelectedMediaName);
        Assert.False(viewModel.Editor.IsMediaMissing);
        Assert.True(viewModel.CanSubmitApply);
        var projected = SelectedMedia(viewModel.ConfigurationState.Draft);
        Assert.NotNull(projected);
        Assert.Equal(media.SourceKind, projected.SourceKind);
        Assert.Equal(media.SourceIdentifier, projected.SourceIdentifier);
        Assert.Equal(contentKind, projected.LastKnownContentKind);
        Assert.Equal(media.LastKnownDisplayName, projected.LastKnownDisplayName);
    }

    [Fact]
    public void WallpaperCompatibility_ExposesCapabilitySourceSnapshot()
    {
        var compatibility = WallpaperCompatibilitySnapshot.NotEvaluated with
        {
            CodexVersion = new Version(26, 805, 14, 3),
        };
        var wallpaper = new FakeCapabilityWallpaperApplicationService(
            SettingsV3.CreateDefault(),
            compatibility);
        using var viewModel = CreateViewModel(
            wallpaper,
            new FakeAppPreferencesStore());

        Assert.Same(compatibility, viewModel.WallpaperCompatibility);
    }

    [Fact]
    public async Task ComposedSettingsStateOwnsConfigurationPreferencesAndRecents()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var persisted = CreateSettings(
                recentMedia: [(mediaPath, MediaKind.Image)]);
            var wallpaper = new FakeWallpaperApplicationService(persisted);
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);

            await viewModel.InitializeAsync();
            viewModel.SelectMedia(mediaPath);
            await viewModel.SetThemeModeAsync(ThemeMode.Dark);

            Assert.Same(
                viewModel.Settings.ConfigurationState,
                viewModel.ConfigurationState);
            Assert.Same(viewModel.Settings.Preferences, viewModel.Preferences);
            Assert.Same(viewModel.Settings.Recents, viewModel.Recents);
            Assert.Equal(
                mediaPath,
                SelectedMedia(viewModel.Settings.ConfigurationState.Draft)
                    ?.SourceIdentifier);
            Assert.Equal(ThemeMode.Dark, viewModel.Settings.ThemeMode);
            Assert.Single(viewModel.Settings.Recents);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task InitializeAsyncHydratesPersistedUiStateWithoutStartingRuntime()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var missingRecentPath = Path.Combine(
                Path.GetTempPath(),
                $"{Guid.NewGuid():N}.webm");
            var settings = CreateSettings(
                mediaPath,
                MediaKind.Image,
                profile => profile with
                {
                    Fit = WallpaperFit.Contain,
                    FocusX = 0.25,
                    FocusY = 0.75,
                    PanelOpacity = 0.84,
                    BlurPx = 7,
                    DarkOverlay = 0.42,
                    LightOverlay = 0.21,
                },
                acceptedCdpRisk: true,
                recentMedia:
                [
                    (mediaPath, MediaKind.Image),
                    (missingRecentPath, MediaKind.Video),
                ]);
            var preferences = AppPreferencesV1.CreateDefault() with
            {
                ThemeMode = ThemeMode.Dark,
                HasShownTrayTip = true,
            };
            var wallpaper = new FakeWallpaperApplicationService(settings);
            using var preferencesStore = new FakeAppPreferencesStore(preferences);
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);

            await viewModel.InitializeAsync();

            Assert.True(
                WallpaperConfigurationState.AreEquivalent(
                    settings,
                    viewModel.SavedDesired));
            Assert.Equal(mediaPath, viewModel.Editor.SelectedMediaPath);
            Assert.Equal(MediaKind.Image, viewModel.Editor.SelectedMediaKind);
            Assert.Equal(WallpaperFit.Contain, viewModel.Editor.Fit);
            Assert.Equal(0.25, viewModel.Editor.FocusX);
            Assert.Equal(0.75, viewModel.Editor.FocusY);
            Assert.Equal(0.84, viewModel.Editor.PanelOpacity);
            Assert.Equal(7, viewModel.Editor.BlurPx);
            Assert.Equal(0.42, viewModel.Editor.DarkOverlay);
            Assert.Equal(0.21, viewModel.Editor.LightOverlay);
            Assert.True(viewModel.Editor.AcceptedCdpRisk);
            Assert.False(viewModel.Editor.IsMediaMissing);
            Assert.False(viewModel.IsDraftDirty);
            Assert.Equal(ThemeMode.Dark, viewModel.ThemeMode);
            Assert.True(viewModel.HasShownTrayTip);
            Assert.Collection(
                viewModel.Recents,
                recent =>
                {
                    Assert.Equal(mediaPath, recent.Path);
                    Assert.Equal(MediaKind.Image, recent.Kind);
                    Assert.True(recent.Exists);
                },
                recent =>
                {
                    Assert.Equal(missingRecentPath, recent.Path);
                    Assert.Equal(MediaKind.Video, recent.Kind);
                    Assert.False(recent.Exists);
                });
            Assert.Equal(0, wallpaper.ApplyCallCount);
            Assert.False(viewModel.IsActive);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task SelectMediaMarksOnlyTheDraftDirty()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var persisted = CreateSettings(acceptedCdpRisk: true);
            var wallpaper = new FakeWallpaperApplicationService(persisted);
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();

            viewModel.SelectMedia(mediaPath);

            Assert.Equal(mediaPath, viewModel.Editor.SelectedMediaPath);
            Assert.Equal(MediaKind.Image, viewModel.Editor.SelectedMediaKind);
            Assert.False(viewModel.Editor.IsMediaMissing);
            Assert.True(viewModel.IsDraftDirty);
            Assert.True(
                WallpaperConfigurationState.AreEquivalent(
                    persisted,
                    viewModel.SavedDesired));
            Assert.Equal(0, wallpaper.SaveCallCount);
            Assert.Equal(0, wallpaper.ApplyCallCount);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task EditorDraftChangesRefreshRootCrossDomainClearState()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                SettingsV3.CreateDefault());
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();
            var changes = new List<string?>();
            viewModel.PropertyChanged += (_, eventArgs) =>
                changes.Add(eventArgs.PropertyName);

            viewModel.SelectMedia(mediaPath);

            Assert.True(viewModel.CanClearSelectedMedia);
            Assert.Contains(nameof(MainWindowViewModel.CanClearSelectedMedia), changes);

            changes.Clear();
            viewModel.ClearSelectedMedia();

            Assert.False(viewModel.CanClearSelectedMedia);
            Assert.Contains(nameof(MainWindowViewModel.CanClearSelectedMedia), changes);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task ApplyFailureAfterCommitReportsSavedButInactive()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                CreateSettings(acceptedCdpRisk: true))
            {
                ApplyFailure = new IOException(
                    "Simulated runtime startup failure."),
                PersistApplyRequestBeforeFailure = true,
            };
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();
            viewModel.SelectMedia(mediaPath);
            viewModel.Editor.Fit = WallpaperFit.Contain;
            viewModel.SetFocus(0.2, 0.8);
            viewModel.Editor.PanelOpacity = 0.86;
            viewModel.Editor.BlurPx = 6;
            viewModel.Editor.DarkOverlay = 0.44;
            viewModel.Editor.LightOverlay = 0.16;

            var applied = await viewModel.ApplyAsync();

            var savedProfile = Global(viewModel.SavedDesired);
            var savedMedia = SelectedMedia(viewModel.SavedDesired);
            Assert.False(applied);
            Assert.Equal(1, wallpaper.ApplyCallCount);
            Assert.False(viewModel.IsActive);
            Assert.Null(viewModel.ActiveSnapshot);
            Assert.True(viewModel.IsSavedButInactive);
            Assert.Equal(mediaPath, savedMedia?.SourceIdentifier);
            Assert.Equal(MediaKind.Image, savedMedia?.LastKnownKind);
            Assert.Equal(WallpaperFit.Contain, savedProfile.Fit);
            Assert.Equal(0.2, savedProfile.FocusX);
            Assert.Equal(0.8, savedProfile.FocusY);
            Assert.Equal(0.86, savedProfile.PanelOpacity);
            Assert.Equal(6, savedProfile.BlurPx);
            Assert.Equal(0.44, savedProfile.DarkOverlay);
            Assert.Equal(0.16, savedProfile.LightOverlay);
            Assert.False(viewModel.IsDraftDirty);
            Assert.Equal(UiStatusTone.Error, viewModel.StatusTone);
            Assert.Equal("Wallpaper could not be applied", viewModel.StatusTitle);
            Assert.Contains("simulated wallpaper runtime", viewModel.StatusMessage);
            Assert.True(viewModel.CanRetryStatusApply);
            Assert.True(viewModel.HasStatusDetails);
            Assert.True(viewModel.IsStatusOpen);

            viewModel.IsStatusOpen = false;

            Assert.False(viewModel.CanRetryStatusApply);
            Assert.False(viewModel.HasStatusDetails);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task PreflightFailureWithoutCommitIsNotReportedAsSaved()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault())
        {
            ApplyFailure = new IOException("Simulated preflight failure."),
        };
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);
        await viewModel.InitializeAsync();

        Assert.False(await viewModel.ApplyAsync());

        Assert.Equal(
            WallpaperWorkspaceErrorStage.Preflight,
            wallpaper.Workspace.Error?.Stage);
        Assert.Equal("Activation failed", viewModel.WorkspaceStatusText);
        Assert.Equal(0, wallpaper.SaveCallCount);
    }

    [Fact]
    public async Task AcceptRiskAsyncPersistsAcknowledgementImmediately()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);

        await viewModel.AcceptRiskAsync();

        Assert.Equal(1, wallpaper.SaveCallCount);
        Assert.NotNull(wallpaper.LastSavedSettings);
        Assert.True(wallpaper.LastSavedSettings.AcceptedCdpRisk);
        Assert.True(viewModel.SavedDesired.AcceptedCdpRisk);
        Assert.True(viewModel.Editor.AcceptedCdpRisk);
        Assert.False(viewModel.IsDraftDirty);
        Assert.Equal(UiStatusTone.Success, viewModel.StatusTone);
    }

    [Fact]
    public async Task AcceptRiskDuringActivationAllowsMediaResubmission()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                SettingsV3.CreateDefault())
            {
                BlockFirstApply = true,
            };
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();

            var firstApply = viewModel.ApplyAsync();
            await wallpaper.FirstApplyEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert.True(viewModel.IsBusy);

            viewModel.SelectMedia(mediaPath);
            await viewModel.AcceptRiskAsync();

            Assert.True(viewModel.Editor.AcceptedCdpRisk);
            Assert.True(await viewModel.ApplyAsync());
            wallpaper.ReleaseFirstApply.TrySetResult();
            Assert.False(await firstApply);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task BeginningOperationHidesPreviousStatusSoProgressAndCancelCanSurface()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault())
        {
            BlockFirstApply = true,
        };
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.IsStatusOpen);

        var apply = viewModel.ApplyAsync();
        await wallpaper.FirstApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.IsStatusOpen);
        Assert.True(viewModel.CancelCommand.CanExecute(parameter: null));

        wallpaper.ReleaseFirstApply.TrySetResult();
        _ = await apply;
    }

    [Fact]
    public async Task RiskSavePreservesDirtyStyleUntilFullApplyNormalizesIt()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            CreateSettings(
                updateProfile: profile => profile with
                {
                    DarkOverlay = 0.90,
                    LightOverlay = 0.75,
                }));
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);

        await viewModel.InitializeAsync();

        Assert.Equal(
            WallpaperEditorViewModel.MaximumOverlay,
            viewModel.Editor.DarkOverlay);
        Assert.Equal(
            WallpaperEditorViewModel.MaximumOverlay,
            viewModel.Editor.LightOverlay);
        Assert.Equal(0.90, Global(viewModel.SavedDesired).DarkOverlay);
        Assert.Equal(0.75, Global(viewModel.SavedDesired).LightOverlay);
        Assert.True(viewModel.IsDraftDirty);

        await viewModel.AcceptRiskAsync();

        Assert.Equal(0.90, Global(viewModel.SavedDesired).DarkOverlay);
        Assert.Equal(0.75, Global(viewModel.SavedDesired).LightOverlay);
        Assert.True(viewModel.IsDraftDirty);

        Assert.True(await viewModel.ApplyAsync());

        Assert.Equal(
            WallpaperEditorViewModel.MaximumOverlay,
            Global(wallpaper.LastSavedSettings!).DarkOverlay);
        Assert.Equal(
            WallpaperEditorViewModel.MaximumOverlay,
            Global(wallpaper.LastSavedSettings!).LightOverlay);
        Assert.False(viewModel.IsDraftDirty);
    }

    [Fact]
    public async Task CropFocusEditingIsCoverOnlyAndClampsKeyboardStyleNudges()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                SettingsV3.CreateDefault());
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();
            viewModel.SelectMedia(mediaPath);

            Assert.True(viewModel.Editor.CanAdjustFocus);

            viewModel.SetFocus(0.98, 0.02);
            viewModel.NudgeFocus(0.10, -0.10);

            Assert.Equal(1, viewModel.Editor.FocusX);
            Assert.Equal(0, viewModel.Editor.FocusY);

            viewModel.ResetFocus();

            Assert.Equal(0.5, viewModel.Editor.FocusX);
            Assert.Equal(0.5, viewModel.Editor.FocusY);

            viewModel.Editor.Fit = WallpaperFit.Contain;

            Assert.False(viewModel.Editor.CanAdjustFocus);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task VideoDraftDoesNotEnablePauseForAnActiveImage()
    {
        var imagePath = CreateTemporaryMediaFile(".png");
        var videoPath = CreateTemporaryMediaFile(".mp4");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                CreateSettings(acceptedCdpRisk: true));
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();
            viewModel.SelectMedia(imagePath);

            Assert.True(await viewModel.ApplyAsync());

            viewModel.SelectMedia(videoPath);

            Assert.True(viewModel.IsActive);
            Assert.Equal(
                MediaKind.Image,
                SelectedMedia(viewModel.ActiveSnapshot!)?.LastKnownKind);
            Assert.False(viewModel.TogglePauseCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(imagePath);
            File.Delete(videoPath);
        }
    }

    [Theory]
    [InlineData(WallpaperContentKind.Scene)]
    [InlineData(WallpaperContentKind.Web)]
    public async Task ActiveDynamicWallpaperCanBePaused(
        WallpaperContentKind contentKind)
    {
        var settings = WithSelectedMedia(
            SettingsV3.CreateDefault() with { AcceptedCdpRisk = true },
            CreateWorkshopDynamicMedia("123456", contentKind));
        var wallpaper = new FakeWallpaperApplicationService(settings);
        wallpaper.SeedActive(settings);
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(
            wallpaper,
            preferencesStore,
            sourceRegistry: new WallpaperSourceProviderRegistry(
                [new RegisteredWorkshopProvider()]));
        await viewModel.InitializeAsync();

        Assert.True(viewModel.TogglePauseCommand.CanExecute(null));
        await viewModel.TogglePauseCommand.ExecuteAsync(null);

        Assert.True(wallpaper.IsPaused);
        Assert.True(viewModel.IsPaused);
    }

    [Fact]
    public async Task RestoreFailureKeepsTheRuntimeActive()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var settings = CreateSettings(
                mediaPath,
                MediaKind.Image,
                acceptedCdpRisk: true);
            var wallpaper = new FakeWallpaperApplicationService(settings)
            {
                DisableFailure = new IOException(
                    "Simulated restore failure."),
            };
            wallpaper.SeedActive(settings);
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();

            await viewModel.DisableAsync();

            Assert.True(viewModel.IsActive);
            Assert.Equal(UiStatusTone.Error, viewModel.StatusTone);
            Assert.DoesNotContain(
                "Simulated restore failure",
                viewModel.StatusMessage,
                StringComparison.Ordinal);
            Assert.True(viewModel.DisableCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task TypedRestoreFailureIsNotReportedAsSuccess()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var settings = CreateSettings(
                mediaPath,
                MediaKind.Image,
                acceptedCdpRisk: true);
            var wallpaper = new FakeWallpaperApplicationService(settings)
            {
                ReturnTypedDisableFailure = true,
            };
            wallpaper.SeedActive(settings);
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();

            await viewModel.DisableAsync();

            Assert.Equal(
                WallpaperRuntimeSurfaceKind.Faulted,
                wallpaper.Workspace.RuntimeSurface.Kind);
            Assert.Equal(UiStatusTone.Error, viewModel.StatusTone);
            Assert.Equal(
                "Official background could not be restored",
                viewModel.StatusTitle);
            Assert.DoesNotContain(
                "Simulated typed restore failure",
                viewModel.StatusMessage,
                StringComparison.Ordinal);
            Assert.Contains(
                "Retry after resolving the runtime issue",
                viewModel.StatusMessage,
                StringComparison.Ordinal);
            Assert.True(viewModel.HasStatusDetails);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task ConcurrentPreferenceUpdatesPreserveThemeAndTrayTip()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore
        {
            BlockFirstSave = true,
        };
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);
        await viewModel.InitializeAsync();

        var themeTask = viewModel.SetThemeModeAsync(ThemeMode.Dark);
        await preferencesStore.FirstSaveEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        var trayTipTask = viewModel.MarkTrayTipShownAsync();
        preferencesStore.ReleaseFirstSave.TrySetResult();

        await Task.WhenAll(themeTask, trayTipTask);

        Assert.Equal(ThemeMode.Dark, preferencesStore.Current.ThemeMode);
        Assert.True(preferencesStore.Current.HasShownTrayTip);
        Assert.Equal(ThemeMode.Dark, viewModel.ThemeMode);
        Assert.True(viewModel.HasShownTrayTip);
    }

    [Fact]
    public async Task RecoveryRequiredSettingsAreReadOnlyUntilExplicitBackupRestore()
    {
        var mediaPath = CreateTemporaryMediaFile(".png");
        try
        {
            var backupSettings = CreateSettings(
                mediaPath,
                MediaKind.Image,
                acceptedCdpRisk: true);
            var wallpaper = new FakeWallpaperApplicationService(
                SettingsV3.CreateDefault())
            {
                LoadFailure = new SettingsRecoveryRequiredException(
                    SettingsRecoveryReason.InvalidDocument,
                    hasVersion1Backup: true),
                BackupSettings = backupSettings,
            };
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);

            await viewModel.InitializeAsync();

            Assert.True(viewModel.HasProtectedSettings);
            Assert.True(viewModel.HasVersion1Backup);
            Assert.False(viewModel.CanEdit);
            Assert.True(viewModel.CanOpenSettings);
            Assert.True(viewModel.CanRestoreVersion1Backup);
            viewModel.SelectMedia(mediaPath);
            await viewModel.AcceptRiskAsync();
            Assert.Null(viewModel.Editor.SelectedMediaPath);
            Assert.Equal(0, wallpaper.SaveCallCount);

            await viewModel.RestoreVersion1BackupAsync();

            Assert.Equal(1, wallpaper.RestoreBackupCallCount);
            Assert.False(viewModel.HasProtectedSettings);
            Assert.True(viewModel.CanEdit);
            Assert.Equal(mediaPath, viewModel.Editor.SelectedMediaPath);
            Assert.True(viewModel.Editor.AcceptedCdpRisk);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public async Task LaterReloadFailureTransitionsEditorIntoProtectedRecoveryState()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);
        await viewModel.InitializeAsync();
        Assert.False(viewModel.HasProtectedSettings);

        wallpaper.LoadFailure = new FutureSettingsVersionException(
            schemaVersion: 99,
            hasVersion1Backup: true);

        await Assert.ThrowsAsync<FutureSettingsVersionException>(
            () => viewModel.Settings.LoadWallpaperSettingsAsync(
                CancellationToken.None));

        Assert.True(viewModel.HasProtectedSettings);
        Assert.True(viewModel.HasVersion1Backup);
        Assert.False(viewModel.CanEdit);
        Assert.True(viewModel.CanRestoreVersion1Backup);
    }

    [Fact]
    public async Task EmptyProfileAppliesOfficialWithoutRiskAcceptance()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);
        await viewModel.InitializeAsync();

        var applied = await viewModel.ApplyAsync();

        Assert.True(applied);
        Assert.False(viewModel.Editor.AcceptedCdpRisk);
        Assert.Equal(1, wallpaper.ApplyCallCount);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Official,
            wallpaper.Workspace.RuntimeSurface.Kind);
        Assert.NotNull(viewModel.ActiveSnapshot);
        Assert.Null(Global(viewModel.ActiveSnapshot!).MediaId);
        Assert.False(viewModel.ShortcutNeedsRetry);
        Assert.False(viewModel.RetryShortcutCommand.CanExecute(null));
    }

    [Fact]
    public async Task ApplyingDoesNotBlockDraftEditingOrLatestWinsResubmission()
    {
        var firstPath = CreateTemporaryMediaFile(".png");
        var secondPath = CreateTemporaryMediaFile(".png");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                CreateSettings(acceptedCdpRisk: true))
            {
                BlockFirstApply = true,
            };
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();
            viewModel.SelectMedia(firstPath);

            var firstApply = viewModel.ApplyAsync();
            await wallpaper.FirstApplyEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            Assert.True(viewModel.IsBusy);
            Assert.True(viewModel.CanEditDraft);
            Assert.True(viewModel.CanSubmitApply);

            viewModel.SelectMedia(secondPath);
            viewModel.Editor.BlurPx = 5;
            var secondApply = viewModel.ApplyAsync();

            Assert.True(await secondApply);
            wallpaper.ReleaseFirstApply.TrySetResult();
            Assert.False(await firstApply);

            Assert.Equal(2, wallpaper.ApplyCallCount);
            Assert.Equal(
                secondPath,
                SelectedMedia(viewModel.SavedDesired)?.SourceIdentifier);
            Assert.Equal(5, Global(viewModel.SavedDesired).BlurPx);
            Assert.True(
                WallpaperConfigurationState.AreRuntimeEquivalent(
                    viewModel.SavedDesired,
                    viewModel.ActiveSnapshot!));
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public async Task SavedButNotActivatedHasDistinctWorkspaceStatus()
    {
        var firstPath = CreateTemporaryMediaFile(".png");
        var secondPath = CreateTemporaryMediaFile(".png");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                CreateSettings(acceptedCdpRisk: true));
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();
            viewModel.SelectMedia(firstPath);
            Assert.True(await viewModel.ApplyAsync());

            viewModel.SelectMedia(secondPath);
            wallpaper.NextApplyOutcome =
                RuntimeActivationOutcome.SavedButNotActivated;

            Assert.False(await viewModel.ApplyAsync());
            Assert.Equal("Saved, not activated", viewModel.WorkspaceStatusText);
            Assert.Equal("Saved, not activated", viewModel.FooterStatusText);
            Assert.Equal("Wallpaper could not be applied", viewModel.StatusTitle);
            Assert.DoesNotContain(
                "saved media could not be acquired",
                viewModel.StatusMessage,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(UiStatusTone.Warning, viewModel.StatusTone);
            Assert.True(viewModel.CanRetryStatusApply);
            Assert.True(viewModel.HasStatusDetails);
            Assert.False(viewModel.ShortcutNeedsRetry);
            Assert.False(viewModel.RetryShortcutCommand.CanExecute(null));
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [Fact]
    public async Task ProfileCrudRequestsFocusForTheSelectedCard()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);
        await viewModel.InitializeAsync();
        var focusRequests = 0;
        viewModel.RestoreProfileFocus = () => focusRequests++;

        viewModel.CreateProfileCommand.Execute(null);
        Assert.Equal(1, focusRequests);

        viewModel.DuplicateProfileCommand.Execute(viewModel.SelectedProfileCard);
        Assert.Equal(2, focusRequests);

        viewModel.RenameProfilePromptAsync =
            _ => Task.FromResult<string?>("Renamed profile");
        await viewModel.RenameProfileCommand.ExecuteAsync(
            viewModel.SelectedProfileCard);
        Assert.Equal(3, focusRequests);

        viewModel.DeleteProfilePromptAsync = _ => Task.FromResult(true);
        await viewModel.DeleteProfileCommand.ExecuteAsync(
            viewModel.SelectedProfileCard);
        Assert.Equal(4, focusRequests);
    }

    [Fact]
    public async Task OlderRevisionStatusCannotOverwriteLatestUiState()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore();
        using var viewModel = CreateViewModel(wallpaper, preferencesStore);
        await viewModel.InitializeAsync();
        Assert.True(await viewModel.ApplyAsync());
        Assert.True(await viewModel.ApplyAsync());

        wallpaper.RaiseStatus(
            WallpaperRuntimePhase.Active,
            revision: wallpaper.Workspace.LatestRevision);
        Assert.Equal(WallpaperRuntimePhase.Active, viewModel.RuntimePhase);

        wallpaper.RaiseStatus(
            WallpaperRuntimePhase.Stopping,
            revision: wallpaper.Workspace.LatestRevision - 1);

        Assert.Equal(WallpaperRuntimePhase.Active, viewModel.RuntimePhase);
        Assert.NotEqual("Restoring the official Codex background…", viewModel.OperationStage);
    }

    [Fact]
    public async Task QueuedOlderRevisionStatusIsRecheckedOnUiDispatch()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore();
        var uiContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        MainWindowViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            viewModel = CreateViewModel(
                wallpaper,
                preferencesStore,
                captureCurrentSynchronizationContext: true);
            await viewModel.InitializeAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using (viewModel)
        {
            Assert.True(await viewModel.ApplyAsync());
            var olderRevision = wallpaper.Workspace.LatestRevision;
            uiContext.HoldPosts = true;

            await Task.Run(
                () => wallpaper.RaiseStatus(
                    WallpaperRuntimePhase.Stopping,
                    olderRevision));
            Assert.Equal(1, uiContext.PendingCount);

            Assert.True(await viewModel.ApplyAsync());
            Assert.True(wallpaper.Workspace.LatestRevision > olderRevision);

            uiContext.Drain();

            Assert.NotEqual(WallpaperRuntimePhase.Stopping, viewModel.RuntimePhase);
            Assert.NotEqual(
                "Restoring the official Codex background…",
                viewModel.OperationStage);
        }
    }

    [Fact]
    public async Task QueuedOlderWorkspaceIsRecheckedOnUiDispatch()
    {
        var wallpaper = new FakeWallpaperApplicationService(
            SettingsV3.CreateDefault());
        using var preferencesStore = new FakeAppPreferencesStore();
        var uiContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        MainWindowViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            viewModel = CreateViewModel(
                wallpaper,
                preferencesStore,
                captureCurrentSynchronizationContext: true);
            await viewModel.InitializeAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using (viewModel)
        {
            var olderWorkspace = wallpaper.Workspace;
            uiContext.HoldPosts = true;
            await Task.Run(() => wallpaper.RaiseWorkspace(olderWorkspace));
            Assert.Equal(1, uiContext.PendingCount);

            var currentDraft = (wallpaper.Workspace.Draft with
            {
                AcceptedCdpRisk = true,
            }).CreateSnapshot();
            try
            {
                SynchronizationContext.SetSynchronizationContext(uiContext);
                wallpaper.ReplaceDraft(currentDraft);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(
                    previousContext);
            }

            Assert.True(
                viewModel.Settings.ConfigurationState.Draft.AcceptedCdpRisk);
            uiContext.Drain();
            Assert.True(
                viewModel.Settings.ConfigurationState.Draft.AcceptedCdpRisk);
        }
    }

    [Fact]
    public async Task SavedButNotActivatedPreservesOlderActiveSnapshot()
    {
        var firstPath = CreateTemporaryMediaFile(".png");
        var secondPath = CreateTemporaryMediaFile(".png");
        try
        {
            var wallpaper = new FakeWallpaperApplicationService(
                CreateSettings(acceptedCdpRisk: true));
            using var preferencesStore = new FakeAppPreferencesStore();
            using var viewModel = CreateViewModel(wallpaper, preferencesStore);
            await viewModel.InitializeAsync();
            viewModel.SelectMedia(firstPath);
            Assert.True(await viewModel.ApplyAsync());
            var firstActive = viewModel.ActiveSnapshot;

            wallpaper.NextApplyOutcome =
                RuntimeActivationOutcome.SavedButNotActivated;
            viewModel.SelectMedia(secondPath);
            Assert.False(await viewModel.ApplyAsync());

            Assert.True(viewModel.IsActive);
            Assert.True(viewModel.IsSavedButInactive);
            Assert.Equal(
                firstPath,
                SelectedMedia(viewModel.ActiveSnapshot!)?.SourceIdentifier);
            Assert.Equal(
                secondPath,
                SelectedMedia(viewModel.SavedDesired)?.SourceIdentifier);
            Assert.True(
                WallpaperConfigurationState.AreEquivalent(
                    firstActive!,
                    viewModel.ActiveSnapshot!));
            Assert.False(
                WallpaperConfigurationState.AreRuntimeEquivalent(
                    viewModel.SavedDesired,
                    viewModel.ActiveSnapshot!));
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    private static MainWindowViewModel CreateViewModel(
        IWallpaperApplicationService wallpaper,
        IAppPreferencesStore preferencesStore,
        bool captureCurrentSynchronizationContext = false,
        ISafeMediaPreviewService? previewMedia = null,
        IWallpaperSourceProviderRegistry? sourceRegistry = null)
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            if (!captureCurrentSynchronizationContext)
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }

            return new MainWindowViewModel(
                wallpaper,
                preferencesStore,
                new StubErrorMapper(),
                new FallbackTextProvider(),
                previewMedia ?? new FileExistencePreviewService(),
                sourceRegistry ?? new WallpaperSourceProviderRegistry(
                    [new LocalFileWallpaperSourceProvider()]));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    internal static (MainWindowViewModel ViewModel, IAppTextProvider Text)
        CreateLayoutFixture()
    {
        var text = new FallbackTextProvider();
        var viewModel = new MainWindowViewModel(
            new FakeWallpaperApplicationService(SettingsV3.CreateDefault()),
            new FakeAppPreferencesStore(),
            new StubErrorMapper(),
            text);
        return (viewModel, text);
    }

    private static SettingsV3 CreateSettings(
        string? selectedMediaPath = null,
        MediaKind selectedMediaKind = MediaKind.None,
        Func<WallpaperProfile, WallpaperProfile>? updateProfile = null,
        bool acceptedCdpRisk = false,
        IReadOnlyList<(string Path, MediaKind Kind)>? recentMedia = null)
    {
        var baseline = SettingsV3.CreateDefault();
        var catalog = new List<MediaReference>();
        var byPath = new Dictionary<string, MediaReference>(
            StringComparer.OrdinalIgnoreCase);

        MediaReference AddMedia(string path, MediaKind kind)
        {
            var normalized = Path.GetFullPath(path);
            if (byPath.TryGetValue(normalized, out var existing))
            {
                return existing;
            }

            var media = new MediaReference
            {
                MediaId = Guid.CreateVersion7(),
                SourceKind = MediaSourceKind.LocalFile,
                SourceIdentifier = normalized,
                LastKnownKind = kind,
            };
            catalog.Add(media);
            byPath.Add(normalized, media);
            return media;
        }

        var global = baseline.ResolveProfile(SemanticRegion.Global);
        if (selectedMediaPath is not null)
        {
            var selected = AddMedia(selectedMediaPath, selectedMediaKind);
            global = global with { MediaId = selected.MediaId };
        }

        global = updateProfile?.Invoke(global) ?? global;
        var recentIds = recentMedia?
            .Select(item => AddMedia(item.Path, item.Kind).MediaId)
            .Distinct()
            .ToArray() ?? [];
        return (baseline with
        {
            Profiles = [global],
            MediaCatalog = catalog,
            RecentMediaIds = recentIds,
            AcceptedCdpRisk = acceptedCdpRisk,
        }).CreateSnapshot();
    }

    private static MediaReference CreateWorkshopMedia(string publishedFileId) =>
        new()
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = publishedFileId,
            LastKnownKind = MediaKind.Video,
        };

    private static MediaReference CreateWorkshopDynamicMedia(
        string publishedFileId,
        WallpaperContentKind contentKind) =>
        new()
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = publishedFileId,
            LastKnownKind = MediaKind.None,
            LastKnownContentKind = contentKind,
            LastKnownDisplayName = $"{contentKind} project",
        };

    private static WallpaperSourceDescriptor CreateDynamicDescriptor(
        string publishedFileId,
        WallpaperContentKind contentKind) =>
        new(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            publishedFileId,
            $"{contentKind} project",
            contentKind,
            WallpaperDeliveryKind.WallpaperEngineWindow,
            WallpaperDeliveryCapabilities.DynamicFrames);

    private static WallpaperCompatibilitySnapshot CreateCompatibilitySnapshot(
        PresentationEvidence evidence)
    {
        var decision = PresentationContractCatalog.Match(
            evidence,
            finalizeBaselineFallback: true);
        return WallpaperCompatibilitySnapshot.NotEvaluated with
        {
            Presentation = decision.Snapshot,
            Capabilities = decision.Capabilities,
        };
    }

    private static SettingsV3 WithSelectedMedia(
        SettingsV3 baseline,
        MediaReference media)
    {
        var global = baseline.ResolveProfile(SemanticRegion.Global);
        return (baseline with
        {
            MediaCatalog = [.. baseline.MediaCatalog, media],
            Profiles = baseline.Profiles
                .Select(
                    profile => profile.ProfileId == global.ProfileId
                        ? profile with { MediaId = media.MediaId }
                        : profile)
                .ToArray(),
        }).CreateSnapshot();
    }

    private static SettingsV3 AddRecent(
        SettingsV3 baseline,
        MediaReference media) =>
        (baseline with
        {
            MediaCatalog = [.. baseline.MediaCatalog, media],
            RecentMediaIds = [media.MediaId],
        }).CreateSnapshot();

    private static WallpaperProfile Global(SettingsV3 settings) =>
        settings.ResolveProfile(SemanticRegion.Global);

    private static MediaReference? SelectedMedia(SettingsV3 settings)
    {
        var mediaId = Global(settings).MediaId;
        return mediaId is { } actualMediaId
            ? settings.FindMedia(actualMediaId)
            : null;
    }

    private static string CreateTemporaryMediaFile(string extension)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"backdrop-view-model-{Guid.NewGuid():N}{extension}");
        byte[] bytes = extension.ToLowerInvariant() switch
        {
            ".png" =>
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            ],
            ".mp4" =>
            [
                0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70,
                0x69, 0x73, 0x6F, 0x6D,
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(extension),
                "The test helper only creates reviewed media fixtures."),
        };
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private class FakeWallpaperApplicationService : IWallpaperApplicationService
    {
        private WallpaperWorkspace _workspace;
        private SettingsV3 _persistedSettings;
        private long _nextRevision;
        private int _applyCallCount;

        public FakeWallpaperApplicationService(SettingsV3 persistedSettings)
        {
            _persistedSettings = persistedSettings.CreateSnapshot();
            _workspace = new WallpaperWorkspace(
                _persistedSettings,
                WallpaperRuntimeSurface.Disconnected());
        }

        public event EventHandler<WallpaperRuntimeStatusChangedEventArgs>?
            StatusChanged;

        public event EventHandler<WallpaperWorkspaceStateChangedEventArgs>?
            WorkspaceChanged;

        public WallpaperWorkspaceState Workspace => _workspace.State;

        public bool IsActive =>
            Workspace.RuntimeSurface.Kind ==
            WallpaperRuntimeSurfaceKind.MediaActive;

        public bool IsPaused { get; private set; }

        public bool HasVersion1Backup =>
            BackupSettings is not null ||
            LoadFailure is SettingsRecoveryRequiredException
            {
                HasVersion1Backup: true,
            } ||
            LoadFailure is FutureSettingsVersionException
            {
                HasVersion1Backup: true,
            };

        public Exception? ApplyFailure { get; set; }

        public Exception? DisableFailure { get; set; }

        public bool ReturnTypedDisableFailure { get; set; }

        public Exception? LoadFailure { get; set; }

        public SettingsV3? BackupSettings { get; set; }

        public bool PersistApplyRequestBeforeFailure { get; set; }

        public bool BlockFirstApply { get; set; }

        public RuntimeActivationOutcome? NextApplyOutcome { get; set; }

        public TaskCompletionSource FirstApplyEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstApply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SaveCallCount { get; private set; }

        public int ApplyCallCount => Volatile.Read(ref _applyCallCount);

        public WallpaperSourceResolution? LastExpectedSourceResolution
        {
            get;
            private set;
        }

        public ConcurrentQueue<WallpaperSourceResolution?> ExpectedSourceResolutions
        {
            get;
        } = new();

        public int RestoreBackupCallCount { get; private set; }

        public SettingsV3? LastSavedSettings { get; private set; }

        public Task<WallpaperWorkspaceState> InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LoadFailure is not null)
            {
                return Task.FromException<WallpaperWorkspaceState>(LoadFailure);
            }

            PublishWorkspace();
            return Task.FromResult(Workspace);
        }

        public void ReplaceDraft(SettingsV3 draft)
        {
            _workspace.ReplaceDraft(draft);
            PublishWorkspace();
        }

        public WallpaperProfile CreateProfile(string baseName = "New profile")
        {
            var profile = _workspace.CreateProfile(baseName);
            PublishWorkspace();
            return profile;
        }

        public WallpaperProfile DuplicateProfile(
            Guid profileId,
            string suffix = "Copy")
        {
            var profile = _workspace.DuplicateProfile(profileId, suffix);
            PublishWorkspace();
            return profile;
        }

        public WallpaperProfile RenameProfile(Guid profileId, string name)
        {
            var profile = _workspace.RenameProfile(profileId, name);
            PublishWorkspace();
            return profile;
        }

        public void DeleteProfile(
            Guid profileId,
            Guid? replacementProfileId = null)
        {
            _workspace.DeleteProfile(profileId, replacementProfileId);
            PublishWorkspace();
        }

        public void SelectProfile(Guid profileId)
        {
            _workspace.SelectProfile(profileId);
            PublishWorkspace();
        }

        public MediaReference SelectLocalMedia(
            Guid profileId,
            string path,
            MediaKind mediaKind)
        {
            var media = _workspace.SelectLocalMedia(profileId, path, mediaKind);
            PublishWorkspace();
            return media;
        }

        public void ClearMedia(Guid profileId)
        {
            _workspace.ClearMedia(profileId);
            PublishWorkspace();
        }

        public Task<WallpaperApplyResult> ApplyAsync(
            RuntimeLaunchMode launchMode = RuntimeLaunchMode.ManualApply,
            CancellationToken cancellationToken = default) =>
            ApplyAsync(launchMode, expectedSourceResolution: null, cancellationToken);

        public async Task<WallpaperApplyResult> ApplyAsync(
            RuntimeLaunchMode launchMode,
            WallpaperSourceResolution? expectedSourceResolution,
            CancellationToken cancellationToken = default)
        {
            LastExpectedSourceResolution = expectedSourceResolution;
            ExpectedSourceResolutions.Enqueue(expectedSourceResolution);
            _ = launchMode;
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _applyCallCount);
            var revision = Interlocked.Increment(ref _nextRevision);
            var requested = _workspace.CaptureDraft();
            _workspace.BeginRevision(revision);
            PublishWorkspace();

            if (call == 1 && BlockFirstApply)
            {
                FirstApplyEntered.TrySetResult();
                await ReleaseFirstApply.Task.ConfigureAwait(false);
                var superseded = RuntimeActivationResult.Superseded(
                    revision,
                    Workspace.RuntimeSurface,
                    Workspace.ActiveSnapshot);
                return new WallpaperApplyResult(
                    superseded,
                    Workspace.SavedDesired,
                    ShortcutReady: false);
            }

            var saved = PromoteSelectedRecent(requested);
            if (ApplyFailure is null || PersistApplyRequestBeforeFailure)
            {
                CommitSaved(saved, requested, revision);
            }

            if (ApplyFailure is not null)
            {
                _ = _workspace.SetProgress(
                    revision,
                    WallpaperWorkspacePhase.Idle,
                    WallpaperWorkspaceError.FromException(
                        PersistApplyRequestBeforeFailure
                            ? WallpaperWorkspaceErrorStage.Runtime
                            : WallpaperWorkspaceErrorStage.Preflight,
                        "apply-failed",
                        ApplyFailure));
                PublishWorkspace();
                return await Task.FromException<WallpaperApplyResult>(
                    ApplyFailure);
            }

            var requestedOutcome = NextApplyOutcome;
            NextApplyOutcome = null;
            if (requestedOutcome == RuntimeActivationOutcome.SavedButNotActivated)
            {
                var error = new WallpaperRuntimeError(
                    "lease-unavailable",
                    "The saved media could not be acquired for activation.");
                _ = _workspace.SetRuntimeSurface(
                    Workspace.RuntimeSurface,
                    clearActiveSnapshot: false,
                    revision,
                    new WallpaperWorkspaceError(
                        WallpaperWorkspaceErrorStage.Runtime,
                        error.Code,
                        error.Message));
                PublishWorkspace();
                var inactive = RuntimeActivationResult.SavedButNotActivated(
                    revision,
                    Workspace.RuntimeSurface,
                    Workspace.ActiveSnapshot,
                    error);
                return new WallpaperApplyResult(
                    inactive,
                    Workspace.SavedDesired,
                    ShortcutReady: false);
            }

            if (requestedOutcome == RuntimeActivationOutcome.Failed)
            {
                var error = new WallpaperRuntimeError(
                    "activation-failed",
                    "The simulated activation failed after injection began.");
                var faulted = WallpaperRuntimeSurface.Faulted(error);
                _ = _workspace.SetRuntimeSurface(
                    faulted,
                    clearActiveSnapshot: true,
                    revision,
                    new WallpaperWorkspaceError(
                        WallpaperWorkspaceErrorStage.Runtime,
                        error.Code,
                        error.Message));
                PublishWorkspace();
                var failed = RuntimeActivationResult.Failed(
                    revision,
                    faulted,
                    activeSnapshot: null,
                    error);
                return new WallpaperApplyResult(
                    failed,
                    Workspace.SavedDesired,
                    ShortcutReady: false);
            }

            var selectedMedia = SelectedMedia(saved);
            RuntimeActivationResult activation;
            if (selectedMedia is null)
            {
                var official = WallpaperRuntimeSurface.Official();
                _ = _workspace.CommitActive(saved, official, revision);
                activation = RuntimeActivationResult.Official(
                    revision,
                    saved,
                    official);
            }
            else
            {
                var active = WallpaperRuntimeSurface.MediaActive(
                    generation: revision,
                    selectedMedia.MediaId,
                    PlaybackOwnershipToken.Create());
                _ = _workspace.CommitActive(saved, active, revision);
                activation = RuntimeActivationResult.MediaActive(
                    revision,
                    saved,
                    active);
            }

            IsPaused = false;
            PublishWorkspace();
            return new WallpaperApplyResult(
                activation,
                Workspace.SavedDesired,
                ShortcutReady:
                    activation.Outcome == RuntimeActivationOutcome.MediaActive);
        }

        public void CancelLatestApply() => ReleaseFirstApply.TrySetResult();

        public Task<SettingsV3> SetRiskAcceptanceAsync(
            bool accepted,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCallCount++;
            var saved = (_persistedSettings with
            {
                AcceptedCdpRisk = accepted,
            }).CreateSnapshot();
            var draft = (Workspace.Draft with
            {
                AcceptedCdpRisk = accepted,
            }).CreateSnapshot();
            _persistedSettings = saved;
            LastSavedSettings = saved;
            _workspace.CommitIndependentSettings(saved, draft);
            PublishWorkspace();
            return Task.FromResult(saved);
        }

        public Task<SettingsV3> RemoveRecentMediaAsync(
            Guid mediaId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SaveRecentsAsync(
                Workspace.SavedDesired.RecentMediaIds
                    .Where(id => id != mediaId)
                    .ToArray());
        }

        public Task<SettingsV3> ClearRecentMediaAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SaveRecentsAsync([]);
        }

        public Task SetPausedAsync(
            bool paused,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsPaused = IsActive && paused;
            return Task.CompletedTask;
        }

        public Task<RuntimeActivationResult> RestoreOfficialAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DisableFailure is not null)
            {
                return Task.FromException<RuntimeActivationResult>(
                    DisableFailure);
            }

            var revision = Interlocked.Increment(ref _nextRevision);
            _workspace.BeginRevision(
                revision,
                WallpaperWorkspacePhase.RestoringOfficial);
            if (ReturnTypedDisableFailure)
            {
                var error = new WallpaperRuntimeError(
                    "restore-official-failed",
                    "Simulated typed restore failure.");
                var faulted = WallpaperRuntimeSurface.Faulted(error);
                _ = _workspace.SetRuntimeSurface(
                    faulted,
                    clearActiveSnapshot: true,
                    revision,
                    new WallpaperWorkspaceError(
                        WallpaperWorkspaceErrorStage.Cleanup,
                        error.Code,
                        error.Message));
                PublishWorkspace();
                return Task.FromResult(
                    RuntimeActivationResult.Failed(
                        revision,
                        faulted,
                        activeSnapshot: null,
                        error));
            }

            var official = WallpaperRuntimeSurface.Official();
            _ = _workspace.SetRuntimeSurface(
                official,
                clearActiveSnapshot: true,
                revision);
            IsPaused = false;
            PublishWorkspace();
            return Task.FromResult(
                RuntimeActivationResult.Canceled(
                    revision,
                    official,
                    activeSnapshot: null));
        }

        public Task<SettingsV3> RestoreVersion1BackupAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreBackupCallCount++;
            _persistedSettings =
                (BackupSettings ?? SettingsV3.CreateDefault()).CreateSnapshot();
            _workspace = new WallpaperWorkspace(
                _persistedSettings,
                WallpaperRuntimeSurface.Official());
            LoadFailure = null;
            PublishWorkspace();
            return Task.FromResult(_persistedSettings);
        }

        public Task<SettingsV3> ResetWallpaperSettingsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _persistedSettings = SettingsV3.CreateDefault();
            _workspace = new WallpaperWorkspace(
                _persistedSettings,
                WallpaperRuntimeSurface.Official(),
                _persistedSettings);
            LoadFailure = null;
            PublishWorkspace();
            return Task.FromResult(_persistedSettings);
        }

        public DesktopShortcutWriteResult CreateOrUpdateShortcut() =>
            throw new NotSupportedException();

        public DesktopShortcutDeleteResult DeleteOwnedShortcut() =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void SeedActive(SettingsV3 settings)
        {
            var selected = SelectedMedia(settings) ??
                throw new InvalidOperationException(
                    "An active test fixture must select media.");
            var surface = WallpaperRuntimeSurface.MediaActive(
                generation: 1,
                selected.MediaId,
                PlaybackOwnershipToken.Create());
            _persistedSettings = settings.CreateSnapshot();
            _workspace = new WallpaperWorkspace(
                _persistedSettings,
                surface,
                _persistedSettings);
        }

        public void RaiseStatus(WallpaperRuntimePhase phase, long revision) =>
            StatusChanged?.Invoke(
                this,
                new WallpaperRuntimeStatusChangedEventArgs(
                    phase,
                    phase.ToString(),
                    revision));

        public void RaiseWorkspace(WallpaperWorkspaceState workspace) =>
            WorkspaceChanged?.Invoke(
                this,
                new WallpaperWorkspaceStateChangedEventArgs(workspace));

        private void CommitSaved(
            SettingsV3 saved,
            SettingsV3 requested,
            long revision)
        {
            SaveCallCount++;
            _persistedSettings = saved.CreateSnapshot();
            LastSavedSettings = _persistedSettings;
            _ = _workspace.CommitSavedDesired(_persistedSettings, revision);
            if (SettingsV3Comparer.UiDirtyEquals(
                    Workspace.Draft,
                    requested))
            {
                _workspace.ReplaceDraft(_persistedSettings);
            }

            PublishWorkspace();
        }

        private Task<SettingsV3> SaveRecentsAsync(IReadOnlyList<Guid> recents)
        {
            SaveCallCount++;
            var saved = (_persistedSettings with
            {
                RecentMediaIds = recents,
            }).CreateSnapshot();
            var draft = (Workspace.Draft with
            {
                RecentMediaIds = recents,
            }).CreateSnapshot();
            _persistedSettings = saved;
            LastSavedSettings = saved;
            _workspace.CommitIndependentSettings(saved, draft);
            PublishWorkspace();
            return Task.FromResult(saved);
        }

        private static SettingsV3 PromoteSelectedRecent(SettingsV3 settings)
        {
            var mediaId = Global(settings).MediaId;
            if (mediaId is null)
            {
                return settings.CreateSnapshot();
            }

            return (settings with
            {
                RecentMediaIds = new[] { mediaId.Value }
                    .Concat(
                        settings.RecentMediaIds.Where(id => id != mediaId.Value))
                    .Take(SettingsV3.MaximumRecentMediaIds)
                    .ToArray(),
            }).CreateSnapshot();
        }

        private void PublishWorkspace() =>
            WorkspaceChanged?.Invoke(
                this,
                new WallpaperWorkspaceStateChangedEventArgs(Workspace));
    }

    private sealed class FakeCapabilityWallpaperApplicationService :
        FakeWallpaperApplicationService,
        IWallpaperApplicationCapabilitySource
    {
        public FakeCapabilityWallpaperApplicationService(
            SettingsV3 persistedSettings,
            WallpaperCompatibilitySnapshot compatibility)
            : base(persistedSettings)
        {
            Compatibility = compatibility;
        }

        public event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>?
            CapabilitiesChanged
        {
            add { }
            remove { }
        }

        public CompatibilityCapabilities Capabilities =>
            Compatibility.Capabilities;

        public WallpaperCompatibilitySnapshot Compatibility { get; }
    }

    private sealed class FakeDynamicWallpaperApplicationService :
        FakeWallpaperApplicationService,
        IDynamicWallpaperCapabilitySource,
        IWallpaperApplicationCapabilitySource
    {
        private int _probeCount;

        public FakeDynamicWallpaperApplicationService(
            SettingsV3 persistedSettings,
            DynamicWallpaperCapability capability,
            WallpaperCompatibilitySnapshot? compatibility = null)
            : base(persistedSettings)
        {
            Capability = capability;
            Compatibility = compatibility ?? WallpaperCompatibilitySnapshot.NotEvaluated;
        }

        public DynamicWallpaperCapability Capability { get; set; }

        public int ProbeCount => Volatile.Read(ref _probeCount);

        public event EventHandler<WallpaperInjectionCapabilitiesChangedEventArgs>?
            CapabilitiesChanged;

        public CompatibilityCapabilities Capabilities =>
            Compatibility.Capabilities;

        public WallpaperCompatibilitySnapshot Compatibility { get; }

        public ValueTask<DynamicWallpaperCapability> ProbeDynamicWallpaperAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _probeCount);
            return ValueTask.FromResult(Capability);
        }

        public void RaiseCapabilitiesChanged() =>
            CapabilitiesChanged?.Invoke(
                this,
                new WallpaperInjectionCapabilitiesChangedEventArgs(
                    generation: 1,
                    Capabilities,
                    Capabilities,
                    Compatibility.Presentation));
    }

    private sealed class FakeAppPreferencesStore : IAppPreferencesStore
    {
        private AppPreferencesV1 _preferences;
        private int _saveCallCount;

        public FakeAppPreferencesStore(AppPreferencesV1? preferences = null)
        {
            _preferences = preferences ?? AppPreferencesV1.CreateDefault();
        }

        public bool BlockFirstSave { get; init; }

        public TaskCompletionSource FirstSaveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSave { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AppPreferencesV1 Current => _preferences;

        public Task<AppPreferencesV1> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_preferences);
        }

        public async Task SaveAsync(
            AppPreferencesV1 preferences,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _saveCallCount) == 1 &&
                BlockFirstSave)
            {
                FirstSaveEntered.TrySetResult();
                await ReleaseFirstSave.Task.WaitAsync(cancellationToken);
            }

            _preferences = preferences;
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _preferences = AppPreferencesV1.CreateDefault();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RegisteredWorkshopProvider : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKind =>
            MediaSourceKind.WallpaperEngineWorkshopProject;

        public ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<WallpaperSourceDescriptor>>([]);
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = reference.Snapshot();
            var descriptor = new WallpaperSourceDescriptor(
                SourceKind,
                snapshot.SourceIdentifier,
                "Workshop video",
                WallpaperContentKind.Video,
                WallpaperDeliveryKind.DirectMedia,
                WallpaperDeliveryCapabilities.None);
            return ValueTask.FromResult(
                new WallpaperSourceResolution(
                    snapshot,
                    descriptor,
                    new MediaFileMetadata(
                        MediaFormat.Mp4,
                        MediaKind.Video,
                        "video/mp4",
                        128)));
        }
    }

    private sealed class CountingWorkshopProvider(WallpaperSourceDescriptor descriptor)
        : IWallpaperSourceProvider
    {
        private int _discoveryCount;
        private int _resolveCount;

        public MediaSourceKind SourceKind => descriptor.SourceKind;

        public int DiscoveryCount => Volatile.Read(ref _discoveryCount);

        public int ResolveCount => Volatile.Read(ref _resolveCount);

        public ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _discoveryCount);
            return ValueTask.FromResult<IReadOnlyList<WallpaperSourceDescriptor>>([descriptor]);
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _resolveCount);
            return ValueTask.FromResult(
                new WallpaperSourceResolution(
                    reference,
                    descriptor,
                    new MediaFileMetadata(
                        MediaFormat.Mp4,
                        MediaKind.Video,
                        "video/mp4",
                        128)));
        }
    }

    private sealed class MutableWorkshopDiscoveryProvider(
        IReadOnlyList<WallpaperSourceDescriptor> sources) : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKind =>
            MediaSourceKind.WallpaperEngineWorkshopProject;

        public IReadOnlyList<WallpaperSourceDescriptor> Sources { get; set; } =
            sources;

        public ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Sources);
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptor = Sources.FirstOrDefault(
                source => string.Equals(
                    source.SourceIdentifier,
                    reference.SourceIdentifier,
                    StringComparison.Ordinal));
            if (descriptor is null)
            {
                throw new WallpaperEngineProjectUnavailableException(
                    SourceKind,
                    WallpaperEngineProjectUnavailableReason.NotFound);
            }

            var metadata = descriptor.ContentKind switch
            {
                WallpaperContentKind.Image => new MediaFileMetadata(
                    MediaFormat.Png,
                    MediaKind.Image,
                    "image/png",
                    128),
                WallpaperContentKind.Video => new MediaFileMetadata(
                    MediaFormat.Mp4,
                    MediaKind.Video,
                    "video/mp4",
                    128),
                _ => null,
            };
            return ValueTask.FromResult(
                new WallpaperSourceResolution(reference, descriptor, metadata));
        }
    }

    private sealed class RecoveringDiscoveryProvider : IWallpaperSourceProvider
    {
        private int _discoveryCount;

        public MediaSourceKind SourceKind =>
            MediaSourceKind.WallpaperEngineWorkshopProject;

        public bool ShouldFail { get; set; } = true;

        public int DiscoveryCount => Volatile.Read(ref _discoveryCount);

        public ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _discoveryCount);
            if (ShouldFail)
            {
                throw new IOException("Wallpaper Engine library is unavailable.");
            }

            return ValueTask.FromResult<IReadOnlyList<WallpaperSourceDescriptor>>([]);
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ReferenceOnlyPreviewService : ISafeMediaPreviewService
    {
        public List<MediaReference> ProbedReferences { get; } = [];

        public int PathProbeCount { get; private set; }

        public ISafeMediaPreviewLease Acquire(MediaReference reference) =>
            throw new NotSupportedException(reference.SourceIdentifier);

        public ISafeMediaPreviewLease Acquire(string mediaPath) =>
            throw new NotSupportedException(mediaPath);

        public bool IsAvailable(MediaReference reference)
        {
            ProbedReferences.Add(reference.Snapshot());
            return true;
        }

        public bool IsAvailable(string mediaPath)
        {
            _ = mediaPath;
            PathProbeCount++;
            return false;
        }
    }

    private sealed class FileExistencePreviewService : ISafeMediaPreviewService
    {
        public ISafeMediaPreviewLease Acquire(MediaReference reference) =>
            throw new NotSupportedException(reference.SourceIdentifier);

        public ISafeMediaPreviewLease Acquire(string mediaPath) =>
            throw new NotSupportedException(mediaPath);

        public bool IsAvailable(MediaReference reference) =>
            reference.SourceKind != MediaSourceKind.LocalFile ||
            File.Exists(reference.SourceIdentifier);

        public bool IsAvailable(string mediaPath) => File.Exists(mediaPath);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending =
            new();

        public bool HoldPosts { get; set; }

        public int PendingCount
        {
            get
            {
                lock (_pending)
                {
                    return _pending.Count;
                }
            }
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            lock (_pending)
            {
                if (HoldPosts)
                {
                    _pending.Enqueue((callback, state));
                    return;
                }
            }

            callback(state);
        }

        public void Drain()
        {
            HoldPosts = false;
            while (true)
            {
                (SendOrPostCallback Callback, object? State) work;
                lock (_pending)
                {
                    if (!_pending.TryDequeue(out work))
                    {
                        return;
                    }
                }

                work.Callback(work.State);
            }
        }
    }

    private sealed class FallbackTextProvider : IAppTextProvider
    {
        public string GetString(string key) => key;
    }

    private sealed class StubErrorMapper : IUserFacingErrorMapper
    {
        public UserFacingError Map(
            Exception exception,
            UserFacingOperation operation = UserFacingOperation.General)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return new UserFacingError(
                UserFacingErrorCode.WallpaperApplyFailed,
                "Wallpaper could not be applied",
                "The simulated wallpaper runtime failed.",
                "Retry after resolving the runtime issue.",
                CanRetry: true);
        }

        public UserFacingError Map(
            WallpaperRuntimeError runtimeError,
            UserFacingOperation operation = UserFacingOperation.General)
        {
            ArgumentNullException.ThrowIfNull(runtimeError);
            return new UserFacingError(
                UserFacingErrorCode.WallpaperApplyFailed,
                "Wallpaper could not be applied",
                "The simulated wallpaper runtime failed.",
                "Retry after resolving the runtime issue.",
                CanRetry: true);
        }
    }
}
