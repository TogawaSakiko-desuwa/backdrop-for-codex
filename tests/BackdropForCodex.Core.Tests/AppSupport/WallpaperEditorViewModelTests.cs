using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class WallpaperEditorViewModelTests
{
    [Fact]
    public void ApplySettingsHydratesOneDraftAndProjectsOnlyEditableFields()
    {
        var editor = new WallpaperEditorViewModel(new FallbackTextProvider());
        var settings = SettingsV1.CreateDefault() with
        {
            MediaPath = @"C:\wallpapers\sky.png",
            MediaKind = MediaKind.Image,
            Fit = WallpaperFit.Contain,
            FocusX = 0.2,
            FocusY = 0.8,
            PanelOpacity = 0.9,
            BlurPx = 6,
            DarkOverlay = 0.9,
            LightOverlay = 0.7,
            AcceptedCdpRisk = true,
        };
        var draftChangedCount = 0;
        editor.DraftChanged += (_, _) => draftChangedCount++;

        editor.ApplySettings(settings);

        Assert.Equal(1, draftChangedCount);
        Assert.Equal(WallpaperEditorViewModel.MaximumOverlay, editor.DarkOverlay);
        Assert.Equal(WallpaperEditorViewModel.MaximumOverlay, editor.LightOverlay);

        var baseline = SettingsV1.CreateDefault() with
        {
            RecentMediaPaths = [@"C:\wallpapers\recent.webp"],
        };
        var projected = editor.ProjectOnto(baseline);

        Assert.Equal(settings.MediaPath, projected.MediaPath);
        Assert.Equal(settings.MediaKind, projected.MediaKind);
        Assert.Equal(settings.Fit, projected.Fit);
        Assert.Equal(settings.FocusX, projected.FocusX);
        Assert.Equal(settings.FocusY, projected.FocusY);
        Assert.Equal(settings.PanelOpacity, projected.PanelOpacity);
        Assert.Equal(settings.BlurPx, projected.BlurPx);
        Assert.Equal(WallpaperEditorViewModel.MaximumOverlay, projected.DarkOverlay);
        Assert.Equal(WallpaperEditorViewModel.MaximumOverlay, projected.LightOverlay);
        Assert.True(projected.AcceptedCdpRisk);
        Assert.Equal(baseline.RecentMediaPaths, projected.RecentMediaPaths);
    }

    [Fact]
    public void SelectMediaNormalizesPathInfersKindAndPublishesOneDraft()
    {
        var mediaPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}.WEBM");
        File.WriteAllBytes(
            mediaPath,
            [
                0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x82, 0x84,
                0x77, 0x65, 0x62, 0x6D, 0x42, 0x87, 0x81, 0x04,
            ]);
        try
        {
            var editor = new WallpaperEditorViewModel(new FallbackTextProvider());
            var draftChangedCount = 0;
            editor.DraftChanged += (_, _) => draftChangedCount++;

            editor.SelectMedia(mediaPath);

            Assert.Equal(Path.GetFullPath(mediaPath), editor.SelectedMediaPath);
            Assert.Equal(MediaKind.Video, editor.SelectedMediaKind);
            Assert.True(editor.IsVideoSelected);
            Assert.False(editor.IsMediaMissing);
            Assert.Equal(1, draftChangedCount);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }

    [Fact]
    public void PersistedAndSelectedPathsAreProbedOnlyThroughSafePreviewBoundary()
    {
        const string networkPath = @"\\untrusted.invalid\share\wallpaper.png";
        var previewMedia = new RecordingPreviewMediaService(isAvailable: false);
        var editor = new WallpaperEditorViewModel(
            new FallbackTextProvider(),
            previewMedia);
        var settings = SettingsV1.CreateDefault() with
        {
            MediaPath = networkPath,
            MediaKind = MediaKind.Image,
        };

        editor.ApplySettings(settings);
        editor.SelectMedia(networkPath);

        Assert.True(editor.IsMediaMissing);
        Assert.Equal([networkPath, networkPath], previewMedia.ProbedPaths);
    }

    [Fact]
    public void FocusEditsClampAndProjectOntoTheDraft()
    {
        var editor = new WallpaperEditorViewModel(new FallbackTextProvider());

        editor.SetFocus(0.95, 0.05);
        editor.NudgeFocus(0.1, -0.1);

        Assert.Equal(1, editor.FocusX);
        Assert.Equal(0, editor.FocusY);

        editor.ResetFocus();

        Assert.Equal(0.5, editor.FocusX);
        Assert.Equal(0.5, editor.FocusY);
    }

    private sealed class FallbackTextProvider : IAppTextProvider
    {
        public string GetString(string key) => key;
    }

    private sealed class RecordingPreviewMediaService(bool isAvailable) :
        ISafeMediaPreviewService
    {
        public List<string> ProbedPaths { get; } = [];

        public ISafeMediaPreviewLease Acquire(string mediaPath) =>
            throw new InvalidOperationException("This test only exercises availability probes.");

        public bool IsAvailable(string mediaPath)
        {
            ProbedPaths.Add(mediaPath);
            return isAvailable;
        }
    }
}
