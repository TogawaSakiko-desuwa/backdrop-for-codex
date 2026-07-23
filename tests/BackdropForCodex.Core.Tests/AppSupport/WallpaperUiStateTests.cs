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
        var mediaPath = Path.GetFullPath("wallpaper.png");
        var persisted = SettingsV1.CreateDefault() with
        {
            MediaPath = mediaPath,
            MediaKind = MediaKind.Image,
            AcceptedCdpRisk = true,
            RecentMediaPaths = [mediaPath],
        };
        var initial = WallpaperConfigurationState.FromPersisted(persisted);

        Assert.False(initial.HasUnsavedChanges);
        Assert.True(initial.HasPendingApply);
        Assert.True(initial.IsSavedButNotActive);

        var edited = initial.WithDraft(
            persisted with
            {
                Fit = WallpaperFit.Contain,
                BlurPx = 8,
            });

        Assert.True(edited.HasUnsavedChanges);
        Assert.True(edited.HasPendingApply);
        Assert.Equal(WallpaperFit.Cover, edited.SavedDesired.Fit);

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
    public void ConfigurationComparisonUsesWindowsPathSemanticsAndSequenceValues()
    {
        var mediaPath = Path.GetFullPath("wallpaper.png");
        var recentPath = Path.GetFullPath("recent.png");
        var first = SettingsV1.CreateDefault() with
        {
            MediaPath = mediaPath,
            MediaKind = MediaKind.Image,
            RecentMediaPaths = [mediaPath, recentPath],
        };
        var second = first with
        {
            MediaPath = mediaPath.ToUpperInvariant(),
            RecentMediaPaths = [mediaPath.ToUpperInvariant(), recentPath.ToUpperInvariant()],
        };

        Assert.True(WallpaperConfigurationState.AreEquivalent(first, second));

        var changed = second with { PanelOpacity = 0.80 };
        Assert.False(WallpaperConfigurationState.AreEquivalent(first, changed));
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
}
