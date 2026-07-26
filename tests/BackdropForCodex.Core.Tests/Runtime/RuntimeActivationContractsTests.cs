using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Runtime;

public sealed class RuntimeActivationContractsTests
{
    [Fact]
    public void RequestCreatesDeepSnapshotAndDerivesGlobalMembersFromIt()
    {
        var profile = WallpaperProfile.CreateDefault("Chosen");
        var media = CreateMedia();
        profile = profile with
        {
            MediaId = media.MediaId,
        };
        var profiles = new List<WallpaperProfile> { profile };
        var catalog = new List<MediaReference> { media };
        var bindings = new Dictionary<SemanticRegion, Guid>
        {
            [SemanticRegion.Global] = profile.ProfileId,
        };
        var settings = new SettingsV2
        {
            Profiles = profiles,
            MediaCatalog = catalog,
            RegionBindings = bindings,
        };

        var request = RuntimeActivationRequest.Create(
            41,
            settings,
            RuntimeLaunchMode.EnhancedShortcut);
        profiles.Clear();
        catalog.Clear();
        bindings.Clear();

        Assert.Equal(41, request.Revision);
        Assert.Equal(RuntimeLaunchMode.EnhancedShortcut, request.LaunchMode);
        Assert.Single(request.SettingsSnapshot.Profiles);
        Assert.Single(request.SettingsSnapshot.MediaCatalog);
        Assert.Single(request.SettingsSnapshot.RegionBindings);
        Assert.NotSame(profile, request.GlobalProfile);
        Assert.NotSame(media, request.Media);
        Assert.Same(request.GlobalProfile, request.SettingsSnapshot.Profiles[0]);
        Assert.Same(request.Media, request.SettingsSnapshot.MediaCatalog[0]);
        Assert.Equal(request.GlobalProfile.MediaId, request.Media?.MediaId);
        Assert.False(request.IsOfficial);
    }

    [Fact]
    public void RequestDefaultsToManualOfficialActivation()
    {
        var request = RuntimeActivationRequest.Create(1, SettingsV2.CreateDefault());

        Assert.Equal(RuntimeLaunchMode.ManualApply, request.LaunchMode);
        Assert.Null(request.Media);
        Assert.True(request.IsOfficial);
    }

    [Fact]
    public void RequestRejectsInvalidRevisionAndLaunchMode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RuntimeActivationRequest.Create(0, SettingsV2.CreateDefault()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RuntimeActivationRequest.Create(
                1,
                SettingsV2.CreateDefault(),
                (RuntimeLaunchMode)99));
    }

    [Fact]
    public void MediaActiveSurfaceReportsActualResourceOwnership()
    {
        var mediaId = Guid.CreateVersion7();
        var ownership = PlaybackOwnershipToken.Create();

        var surface = WallpaperRuntimeSurface.MediaActive(
            generation: 7,
            mediaId,
            ownership);

        Assert.Equal(WallpaperRuntimeSurfaceKind.MediaActive, surface.Kind);
        Assert.Equal(7, surface.Generation);
        Assert.Equal(mediaId, surface.MediaId);
        Assert.Equal(ownership, surface.PlaybackOwnership);
        Assert.True(surface.OwnsPlayback);
        Assert.True(surface.OwnsInjection);
        Assert.Null(surface.Error);
    }

    [Fact]
    public void FaultedSurfaceCanReportPartiallyRetainedResources()
    {
        var error = new WallpaperRuntimeError("injection.stop", "Cleanup failed.");
        var mediaId = Guid.CreateVersion7();
        var ownership = PlaybackOwnershipToken.Create();

        var surface = WallpaperRuntimeSurface.Faulted(
            error,
            generation: 12,
            mediaId,
            ownership,
            ownsInjection: false);

        Assert.Equal(WallpaperRuntimeSurfaceKind.Faulted, surface.Kind);
        Assert.Equal(12, surface.Generation);
        Assert.Equal(mediaId, surface.MediaId);
        Assert.Equal(ownership, surface.PlaybackOwnership);
        Assert.True(surface.OwnsPlayback);
        Assert.False(surface.OwnsInjection);
        Assert.Same(error, surface.Error);
    }

    [Fact]
    public void SurfaceFactoriesRejectInconsistentResourceEvidence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => WallpaperRuntimeSurface.MediaActive(
                0,
                Guid.CreateVersion7(),
                PlaybackOwnershipToken.Create()));
        Assert.Throws<ArgumentException>(
            () => WallpaperRuntimeSurface.MediaActive(
                1,
                Guid.Empty,
                PlaybackOwnershipToken.Create()));
        Assert.Throws<ArgumentException>(
            () => WallpaperRuntimeSurface.Faulted(
                new WallpaperRuntimeError("cleanup", "Failed."),
                playbackOwnership: PlaybackOwnershipToken.Create()));
        Assert.Throws<ArgumentException>(
            () => WallpaperRuntimeSurface.Faulted(
                new WallpaperRuntimeError("cleanup", "Failed."),
                ownsInjection: true));
    }

    [Fact]
    public void SuccessfulResultRequiresSurfaceAndActiveSnapshotToAgree()
    {
        var settings = CreateMediaSettings();
        var mediaId = settings.ResolveProfile(SemanticRegion.Global).MediaId!.Value;
        var surface = WallpaperRuntimeSurface.MediaActive(
            3,
            mediaId,
            PlaybackOwnershipToken.Create());

        var result = RuntimeActivationResult.MediaActive(9, settings, surface);

        Assert.Equal(9, result.Revision);
        Assert.Equal(RuntimeActivationOutcome.MediaActive, result.Outcome);
        Assert.Equal(mediaId, result.ActiveSnapshot?
            .ResolveProfile(SemanticRegion.Global)
            .MediaId);
        Assert.Same(surface, result.Surface);
        Assert.Null(result.Error);
        Assert.NotSame(settings, result.ActiveSnapshot);
    }

    [Fact]
    public void OfficialResultRejectsSnapshotThatStillSelectsMedia()
    {
        Assert.Throws<ArgumentException>(
            () => RuntimeActivationResult.Official(
                1,
                CreateMediaSettings(),
                WallpaperRuntimeSurface.Official()));
    }

    [Theory]
    [InlineData(RuntimeActivationOutcome.SavedButNotActivated)]
    [InlineData(RuntimeActivationOutcome.Failed)]
    public void FailureResultsRetainStructuredError(
        RuntimeActivationOutcome outcome)
    {
        var error = new WallpaperRuntimeError(
            "media.changed",
            "The media changed before activation.");
        var surface = WallpaperRuntimeSurface.Disconnected();

        var result = outcome == RuntimeActivationOutcome.SavedButNotActivated
            ? RuntimeActivationResult.SavedButNotActivated(
                4,
                surface,
                activeSnapshot: null,
                error)
            : RuntimeActivationResult.Failed(
                4,
                surface,
                activeSnapshot: null,
                error);

        Assert.Equal(outcome, result.Outcome);
        Assert.Same(error, result.Error);
        Assert.Null(result.ActiveSnapshot);
    }

    private static SettingsV2 CreateMediaSettings()
    {
        var media = CreateMedia();
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
        };
    }

    private static MediaReference CreateMedia() =>
        new()
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = @"C:\Wallpapers\stable.png",
            LastKnownKind = MediaKind.Image,
        };
}
