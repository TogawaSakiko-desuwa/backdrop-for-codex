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
    public void ApplySettingsHydratesGlobalDraftAndProjectsOnlyEditableFields()
    {
        var settings = CreateSettings(
            @"C:\wallpapers\sky.png",
            MediaKind.Image,
            profile => profile with
            {
                Fit = WallpaperFit.Contain,
                FocusX = 0.2,
                FocusY = 0.8,
                PanelOpacity = 0.9,
                BlurPx = 6,
                DarkOverlay = 0.9,
                LightOverlay = 0.7,
            },
            acceptedCdpRisk: true);
        var editor = new WallpaperEditorViewModel(new FallbackTextProvider());
        var draftChangedCount = 0;
        editor.DraftChanged += (_, _) => draftChangedCount++;

        editor.ApplySettings(settings);

        Assert.Equal(1, draftChangedCount);
        Assert.Equal(WallpaperEditorViewModel.MaximumOverlay, editor.DarkOverlay);
        Assert.Equal(WallpaperEditorViewModel.MaximumOverlay, editor.LightOverlay);

        var recentMedia = CreateMedia(
            @"C:\wallpapers\recent.webp",
            MediaKind.Image);
        var baseline = SettingsV2.CreateDefault() with
        {
            MediaCatalog = [recentMedia],
            RecentMediaIds = [recentMedia.MediaId],
        };
        var projected = editor.ProjectOnto(baseline);
        var projectedProfile = projected.ResolveProfile(SemanticRegion.Global);
        var projectedMedia = projected.FindMedia(projectedProfile.MediaId!.Value);

        Assert.Equal(
            Path.GetFullPath(@"C:\wallpapers\sky.png"),
            projectedMedia?.SourceIdentifier);
        Assert.Equal(MediaKind.Image, projectedMedia?.LastKnownKind);
        Assert.Equal(WallpaperFit.Contain, projectedProfile.Fit);
        Assert.Equal(0.2, projectedProfile.FocusX);
        Assert.Equal(0.8, projectedProfile.FocusY);
        Assert.Equal(0.9, projectedProfile.PanelOpacity);
        Assert.Equal(6, projectedProfile.BlurPx);
        Assert.Equal(
            WallpaperEditorViewModel.MaximumOverlay,
            projectedProfile.DarkOverlay);
        Assert.Equal(
            WallpaperEditorViewModel.MaximumOverlay,
            projectedProfile.LightOverlay);
        Assert.True(projected.AcceptedCdpRisk);
        Assert.Contains(recentMedia.MediaId, projected.RecentMediaIds);
        Assert.NotNull(projected.FindMedia(recentMedia.MediaId));
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
    public void PersistedAndSelectedSourcesAreProbedOnlyThroughReferenceBoundary()
    {
        const string networkPath = @"\\untrusted.invalid\share\wallpaper.png";
        var previewMedia = new RecordingPreviewMediaService(isAvailable: false);
        var editor = new WallpaperEditorViewModel(
            new FallbackTextProvider(),
            previewMedia);
        var settings = CreateSettings(networkPath, MediaKind.Image);

        editor.ApplySettings(settings);
        editor.SelectMedia(networkPath);

        Assert.True(editor.IsMediaMissing);
        Assert.Equal(
            [networkPath, networkPath],
            previewMedia.ProbedReferences
                .Select(reference => reference.SourceIdentifier));
        Assert.Empty(previewMedia.ProbedPaths);
    }

    [Fact]
    public void SelectSourcePreservesWorkshopIdentityAndProjectsWithoutPathNormalization()
    {
        var previewMedia = new RecordingPreviewMediaService(isAvailable: true);
        var editor = new WallpaperEditorViewModel(
            new FallbackTextProvider(),
            previewMedia);
        var descriptor = new WallpaperSourceDescriptor(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            "76561198000000001",
            "Workshop video",
            WallpaperContentKind.Video,
            WallpaperDeliveryKind.DirectMedia,
            WallpaperDeliveryCapabilities.None);

        editor.SelectSource(descriptor);
        var projected = editor.ProjectOnto(SettingsV2.CreateDefault());
        var profile = projected.ResolveProfile(SemanticRegion.Global);
        var media = projected.FindMedia(Assert.IsType<Guid>(profile.MediaId));

        Assert.Null(editor.SelectedMediaPath);
        Assert.Equal("76561198000000001", editor.SelectedMediaIdentifier);
        Assert.Equal(MediaKind.Video, editor.SelectedMediaKind);
        Assert.NotNull(media);
        Assert.Equal(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            media.SourceKind);
        Assert.Equal("76561198000000001", media.SourceIdentifier);
        var probed = Assert.Single(previewMedia.ProbedReferences);
        Assert.Equal(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            probed.SourceKind);
        Assert.Empty(previewMedia.ProbedPaths);
    }

    [Fact]
    public void SelectMediaReferenceRetainsCanonicalDefensiveSnapshot()
    {
        var editor = new WallpaperEditorViewModel(
            new FallbackTextProvider(),
            new RecordingPreviewMediaService(isAvailable: true));
        var input = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.WallpaperEngineWorkshopProject,
            SourceIdentifier = "000123",
            LastKnownKind = MediaKind.Image,
        };

        editor.SelectMediaReference(input);
        var firstRead = Assert.IsType<MediaReference>(editor.SelectedMediaReference);
        var secondRead = Assert.IsType<MediaReference>(editor.SelectedMediaReference);

        Assert.Equal("000123", input.SourceIdentifier);
        Assert.Equal("123", firstRead.SourceIdentifier);
        Assert.NotSame(input, firstRead);
        Assert.NotSame(firstRead, secondRead);
    }

    [Theory]
    [InlineData(WallpaperContentKind.Scene)]
    [InlineData(WallpaperContentKind.Web)]
    public void NonDirectSourceIsUnavailableWithoutBeingProbedAsAPath(
        WallpaperContentKind contentKind)
    {
        var previewMedia = new RecordingPreviewMediaService(isAvailable: false);
        var editor = new WallpaperEditorViewModel(
            new FallbackTextProvider(),
            previewMedia);
        editor.SelectSource(
            new WallpaperSourceDescriptor(
                MediaSourceKind.WallpaperEngineWorkshopProject,
                "123",
                contentKind.ToString(),
                contentKind,
                WallpaperDeliveryKind.WallpaperEngineWindow,
                WallpaperDeliveryCapabilities.DynamicFrames));

        Assert.True(editor.HasSelectedMedia);
        Assert.Equal(MediaKind.None, editor.SelectedMediaKind);
        Assert.True(editor.IsPreviewUnavailable);
        Assert.False(editor.IsMediaMissing);
        Assert.Empty(previewMedia.ProbedReferences);
        Assert.Empty(previewMedia.ProbedPaths);
    }

    [Theory]
    [InlineData(WallpaperContentKind.Unknown)]
    [InlineData(WallpaperContentKind.Application)]
    public void UnsupportedSourceIsRejectedBeforeItCanEnterTheDraft(
        WallpaperContentKind contentKind)
    {
        var editor = new WallpaperEditorViewModel(
            new FallbackTextProvider(),
            new RecordingPreviewMediaService(isAvailable: false));
        var descriptor = new WallpaperSourceDescriptor(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            "123",
            contentKind.ToString(),
            contentKind,
            WallpaperDeliveryKind.Unsupported,
            WallpaperDeliveryCapabilities.None);

        Assert.Throws<WallpaperContentNotSupportedException>(
            () => editor.SelectSource(descriptor));
        Assert.False(editor.HasSelectedMedia);
        Assert.Null(
            editor.ProjectOnto(SettingsV2.CreateDefault())
                .ResolveProfile(SemanticRegion.Global)
                .MediaId);
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

    private static SettingsV2 CreateSettings(
        string? mediaPath,
        MediaKind mediaKind,
        Func<WallpaperProfile, WallpaperProfile>? updateProfile = null,
        bool acceptedCdpRisk = false)
    {
        var baseline = SettingsV2.CreateDefault();
        var profile = baseline.ResolveProfile(SemanticRegion.Global);
        MediaReference[] catalog = [];
        if (mediaPath is not null)
        {
            var media = CreateMedia(mediaPath, mediaKind);
            catalog = [media];
            profile = profile with { MediaId = media.MediaId };
        }

        profile = updateProfile?.Invoke(profile) ?? profile;
        return (baseline with
        {
            Profiles = [profile],
            MediaCatalog = catalog,
            AcceptedCdpRisk = acceptedCdpRisk,
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

    private sealed class FallbackTextProvider : IAppTextProvider
    {
        public string GetString(string key) => key;
    }

    private sealed class RecordingPreviewMediaService(bool isAvailable) :
        ISafeMediaPreviewService
    {
        public List<string> ProbedPaths { get; } = [];

        public List<MediaReference> ProbedReferences { get; } = [];

        public ISafeMediaPreviewLease Acquire(MediaReference reference) =>
            Acquire(reference.SourceIdentifier);

        public ISafeMediaPreviewLease Acquire(string mediaPath) =>
            throw new InvalidOperationException(
                "This test only exercises availability probes.");

        public bool IsAvailable(string mediaPath)
        {
            ProbedPaths.Add(mediaPath);
            return isAvailable;
        }

        public bool IsAvailable(MediaReference reference)
        {
            ProbedReferences.Add(reference.Snapshot());
            return isAvailable;
        }
    }
}
