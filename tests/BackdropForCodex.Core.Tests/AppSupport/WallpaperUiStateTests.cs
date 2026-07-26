using BackdropForCodex.App.Models;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class WallpaperUiStateTests
{
    [Fact]
    public void ConfigurationStateKeepsDraftSavedAndActiveSnapshotsDistinct()
    {
        var persisted = CreateSettings(
            Path.GetFullPath("wallpaper.png"),
            MediaKind.Image,
            acceptedCdpRisk: true,
            includeRecent: true);
        var initial = WallpaperConfigurationState.FromPersisted(persisted);

        Assert.False(initial.HasUnsavedChanges);
        Assert.True(initial.HasPendingApply);
        Assert.True(initial.IsSavedButNotActive);

        var edited = initial.WithDraft(
            UpdateGlobal(
                persisted,
                profile => profile with
                {
                    Fit = WallpaperFit.Contain,
                    BlurPx = 8,
                }));

        Assert.True(edited.HasUnsavedChanges);
        Assert.True(edited.HasPendingApply);
        Assert.Equal(
            WallpaperFit.Cover,
            edited.SavedDesired.ResolveProfile(SemanticRegion.Global).Fit);

        var saved = edited.WithPersisted(edited.Draft);
        Assert.False(saved.HasUnsavedChanges);
        Assert.True(saved.IsSavedButNotActive);

        var active = saved.WithActive(saved.SavedDesired);
        Assert.False(active.HasPendingApply);
        Assert.False(active.IsSavedButNotActive);
        Assert.True(active.IsRuntimeActive);

        var stopped = active.WithoutActive();
        Assert.True(stopped.IsSavedButNotActive);
        Assert.False(stopped.IsRuntimeActive);
    }

    [Fact]
    public void DurableComparisonPreservesExactPathsWhileRuntimeUsesWindowsSemantics()
    {
        var mediaPath = Path.GetFullPath("wallpaper.png");
        var recentPath = Path.GetFullPath("recent.png");
        var first = CreateSettings(
            mediaPath,
            MediaKind.Image,
            additionalRecentPath: recentPath);
        var upperCatalog = first.MediaCatalog
            .Select(
                media => media with
                {
                    SourceIdentifier = media.SourceIdentifier.ToUpperInvariant(),
                })
            .ToArray();
        var second = (first with { MediaCatalog = upperCatalog }).CreateSnapshot();

        Assert.False(WallpaperConfigurationState.AreEquivalent(first, second));
        Assert.True(
            WallpaperConfigurationState.AreRuntimeEquivalent(first, second));

        var changed = UpdateGlobal(
            second,
            profile => profile with { PanelOpacity = 0.80 });
        Assert.False(WallpaperConfigurationState.AreEquivalent(first, changed));
    }

    [Fact]
    public void RuntimeComparisonIgnoresNonRuntimeMetadata()
    {
        var mediaPath = Path.GetFullPath("wallpaper.png");
        var active = CreateSettings(
            mediaPath,
            MediaKind.Image,
            acceptedCdpRisk: true);
        var mediaId = active.ResolveProfile(SemanticRegion.Global).MediaId!.Value;
        var saved = (active with
        {
            AcceptedCdpRisk = false,
            RecentMediaIds = [mediaId],
        }).CreateSnapshot();

        Assert.False(WallpaperConfigurationState.AreEquivalent(active, saved));
        Assert.True(WallpaperConfigurationState.AreRuntimeEquivalent(active, saved));
        Assert.False(
            WallpaperConfigurationState
                .FromPersisted(saved)
                .WithActive(active)
                .IsSavedButNotActive);
    }

    [Fact]
    public void RuntimeComparisonTreatsAllEmptyProfilesAsOfficial()
    {
        var official = SettingsV2.CreateDefault();
        var restyled = UpdateGlobal(
            official,
            profile => profile with
            {
                Fit = WallpaperFit.Contain,
                FocusX = 0.1,
                FocusY = 0.9,
                PanelOpacity = 0.93,
                BlurPx = 2,
                DarkOverlay = 0.5,
            });

        Assert.False(WallpaperConfigurationState.AreEquivalent(official, restyled));
        Assert.True(
            WallpaperConfigurationState.AreRuntimeEquivalent(official, restyled));
    }

    [Fact]
    public void ConfigurationComparisonIgnoresDeprecatedCompatibilityProfileMetadata()
    {
        var persisted = SettingsV2.CreateDefault();
#pragma warning disable CS0618 // Exercise the deprecated persistence field's UI semantics.
        var legacyMetadataChanged = persisted with
        {
            LastCompatibilityProfileId = "legacy-profile",
        };
#pragma warning restore CS0618

        Assert.False(
            WallpaperConfigurationState.AreEquivalent(
                persisted,
                legacyMetadataChanged));
        Assert.False(
            WallpaperConfigurationState
                .FromPersisted(persisted)
                .WithDraft(legacyMetadataChanged)
                .HasUnsavedChanges);
    }

    [Fact]
    public void OperationProgressAdvancesMonotonicallyAndMakesCancellationIdempotent()
    {
        var progress = WallpaperOperationProgress.Begin();

        Assert.Equal(WallpaperOperationStage.Validating, progress.Stage);
        Assert.True(progress.IsBusy);
        Assert.True(progress.CanCancel);

        progress = progress
            .AdvanceTo(WallpaperOperationStage.Launching)
            .AdvanceTo(WallpaperOperationStage.Discovering)
            .AdvanceTo(WallpaperOperationStage.Applying);
        var cancellationRequested = progress.RequestCancellation();

        Assert.True(cancellationRequested.IsCancellationRequested);
        Assert.False(cancellationRequested.CanCancel);
        Assert.Same(
            cancellationRequested,
            cancellationRequested.RequestCancellation());
        Assert.Same(
            WallpaperOperationProgress.Idle,
            cancellationRequested.Complete());
    }

    [Fact]
    public void OperationProgressRejectsBackwardOrIdleTransitions()
    {
        var launching = WallpaperOperationProgress.Begin()
            .AdvanceTo(WallpaperOperationStage.Launching);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => launching.AdvanceTo(WallpaperOperationStage.Validating));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => launching.AdvanceTo(WallpaperOperationStage.Idle));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => launching.AdvanceTo((WallpaperOperationStage)int.MaxValue));
        Assert.Throws<InvalidOperationException>(
            () => WallpaperOperationProgress.Idle.AdvanceTo(
                WallpaperOperationStage.Validating));
    }

    private static SettingsV2 CreateSettings(
        string mediaPath,
        MediaKind mediaKind,
        bool acceptedCdpRisk = false,
        bool includeRecent = false,
        string? additionalRecentPath = null)
    {
        var baseline = SettingsV2.CreateDefault();
        var selected = CreateMedia(mediaPath, mediaKind);
        var catalog = new List<MediaReference> { selected };
        var recents = new List<Guid>();
        if (includeRecent)
        {
            recents.Add(selected.MediaId);
        }

        if (additionalRecentPath is not null)
        {
            var additional = CreateMedia(additionalRecentPath, MediaKind.Image);
            catalog.Add(additional);
            recents.Add(additional.MediaId);
        }

        var global = baseline.ResolveProfile(SemanticRegion.Global) with
        {
            MediaId = selected.MediaId,
        };
        return (baseline with
        {
            Profiles = [global],
            MediaCatalog = catalog,
            RecentMediaIds = recents,
            AcceptedCdpRisk = acceptedCdpRisk,
        }).CreateSnapshot();
    }

    private static SettingsV2 UpdateGlobal(
        SettingsV2 settings,
        Func<WallpaperProfile, WallpaperProfile> update)
    {
        var global = settings.ResolveProfile(SemanticRegion.Global);
        return (settings with
        {
            Profiles = settings.Profiles
                .Select(
                    profile => profile.ProfileId == global.ProfileId
                        ? update(profile)
                        : profile)
                .ToArray(),
        }).CreateSnapshot();
    }

    private static MediaReference CreateMedia(string path, MediaKind kind) =>
        new()
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = Path.GetFullPath(path),
            LastKnownKind = kind,
        };
}
