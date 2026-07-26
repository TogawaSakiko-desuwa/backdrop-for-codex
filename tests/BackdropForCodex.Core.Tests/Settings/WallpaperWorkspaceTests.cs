using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.Settings;

public sealed class WallpaperWorkspaceTests
{
    [Fact]
    public void EditingDraftDoesNotChangeSavedOrActiveState()
    {
        var loaded = SettingsV2.CreateDefault();
        var workspace = new WallpaperWorkspace(loaded);
        var saved = workspace.State.SavedDesired;

        var created = workspace.CreateProfile();

        Assert.Equal("New profile 1", created.Name);
        Assert.Equal(7, created.ProfileId.Version);
        Assert.Null(created.MediaId);
        Assert.Equal(
            created.ProfileId,
            workspace.State.Draft.RegionBindings[SemanticRegion.Global]);
        Assert.Same(saved, workspace.State.SavedDesired);
        Assert.Null(workspace.State.ActiveSnapshot);
        Assert.True(workspace.State.IsDraftDirty);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Disconnected,
            workspace.State.RuntimeSurface.Kind);
    }

    [Fact]
    public void DuplicateCopiesEveryProfileFieldAndReusesMedia()
    {
        var media = CreateMedia(Guid.CreateVersion7(), "source.png");
        var source = WallpaperProfile.CreateDefault("Scene") with
        {
            MediaId = media.MediaId,
            Fit = WallpaperFit.Contain,
            FocusX = 0.1,
            FocusY = 0.9,
            PanelOpacity = 0.91,
            BlurPx = 4,
            DarkOverlay = 0.6,
            LightOverlay = 0.2,
            SoundEnabled = true,
            Volume = 0.77,
            PerformancePolicy = PerformancePolicy.PreferEfficiency,
        };
        var workspace = new WallpaperWorkspace(
            CreateSettings([source], [media], globalProfileId: source.ProfileId));

        var duplicate = workspace.DuplicateProfile(source.ProfileId);

        Assert.Equal("Scene Copy 1", duplicate.Name);
        Assert.NotEqual(source.ProfileId, duplicate.ProfileId);
        Assert.Equal(source.MediaId, duplicate.MediaId);
        Assert.Equal(source.Fit, duplicate.Fit);
        Assert.Equal(source.FocusX, duplicate.FocusX);
        Assert.Equal(source.FocusY, duplicate.FocusY);
        Assert.Equal(source.PanelOpacity, duplicate.PanelOpacity);
        Assert.Equal(source.BlurPx, duplicate.BlurPx);
        Assert.Equal(source.DarkOverlay, duplicate.DarkOverlay);
        Assert.Equal(source.LightOverlay, duplicate.LightOverlay);
        Assert.Equal(source.SoundEnabled, duplicate.SoundEnabled);
        Assert.Equal(source.Volume, duplicate.Volume);
        Assert.Equal(source.PerformancePolicy, duplicate.PerformancePolicy);
        Assert.Single(workspace.State.Draft.MediaCatalog);
        Assert.Equal(
            duplicate.ProfileId,
            workspace.State.Draft.RegionBindings[SemanticRegion.Global]);
    }

    [Fact]
    public void RenameTrimsButDoesNotImposeUniqueNames()
    {
        var first = WallpaperProfile.CreateDefault("Same");
        var second = WallpaperProfile.CreateDefault("Other");
        var workspace = new WallpaperWorkspace(
            CreateSettings([first, second], globalProfileId: first.ProfileId));

        var renamed = workspace.RenameProfile(second.ProfileId, "  Same  ");

        Assert.Equal("Same", renamed.Name);
        Assert.Equal(
            2,
            workspace.State.Draft.Profiles.Count(profile => profile.Name == "Same"));
        Assert.Throws<ArgumentException>(
            () => workspace.RenameProfile(second.ProfileId, "   "));
        Assert.Throws<ArgumentException>(
            () => workspace.RenameProfile(
                second.ProfileId,
                new string('x', WallpaperProfile.MaximumNameLength + 1)));
    }

    [Fact]
    public void DeleteRequiresReplacementAndAtomicallyRebindsAllRegions()
    {
        var media = CreateMedia(Guid.CreateVersion7(), "orphan-after-delete.png");
        var deleted = WallpaperProfile.CreateDefault("Delete me") with
        {
            MediaId = media.MediaId,
        };
        var replacement = WallpaperProfile.CreateDefault("Keep me");
        var settings = CreateSettings(
            [deleted, replacement],
            [media],
            deleted.ProfileId,
            new Dictionary<SemanticRegion, Guid>
            {
                [SemanticRegion.Global] = deleted.ProfileId,
                [SemanticRegion.Home] = deleted.ProfileId,
                [SemanticRegion.Conversation] = replacement.ProfileId,
                [SemanticRegion.CodeAndDiff] = deleted.ProfileId,
            });
        var workspace = new WallpaperWorkspace(settings);

        var exception = Assert.Throws<WallpaperProfileReplacementRequiredException>(
            () => workspace.DeleteProfile(deleted.ProfileId));

        Assert.Equal(deleted.ProfileId, exception.ProfileId);
        Assert.Equal(
            [
                SemanticRegion.Global,
                SemanticRegion.Home,
                SemanticRegion.CodeAndDiff,
            ],
            exception.BoundRegions);

        workspace.DeleteProfile(deleted.ProfileId, replacement.ProfileId);

        Assert.Equal(replacement, Assert.Single(workspace.State.Draft.Profiles));
        Assert.All(
            workspace.State.Draft.RegionBindings,
            binding => Assert.Equal(replacement.ProfileId, binding.Value));
        Assert.Equal(media, Assert.Single(workspace.State.Draft.MediaCatalog));
    }

    [Fact]
    public void DeleteRejectsFinalProfile()
    {
        var settings = SettingsV2.CreateDefault();
        var workspace = new WallpaperWorkspace(settings);

        Assert.Throws<InvalidOperationException>(
            () => workspace.DeleteProfile(settings.ResolveProfile(SemanticRegion.Global).ProfileId));
    }

    [Fact]
    public void SelectProfilePreservesHiddenBindings()
    {
        var first = WallpaperProfile.CreateDefault("First");
        var second = WallpaperProfile.CreateDefault("Second");
        var workspace = new WallpaperWorkspace(
            CreateSettings(
                [first, second],
                globalProfileId: first.ProfileId,
                bindings: new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] = first.ProfileId,
                    [SemanticRegion.Home] = first.ProfileId,
                    [SemanticRegion.SettingsAndOther] = second.ProfileId,
                }));

        workspace.SelectProfile(second.ProfileId);

        Assert.Equal(
            second.ProfileId,
            workspace.State.Draft.RegionBindings[SemanticRegion.Global]);
        Assert.Equal(
            first.ProfileId,
            workspace.State.Draft.RegionBindings[SemanticRegion.Home]);
        Assert.Equal(
            second.ProfileId,
            workspace.State.Draft.RegionBindings[SemanticRegion.SettingsAndOther]);
    }

    [Fact]
    public void SelectAndClearLocalMediaReusesIdentityAndRetainsOrphans()
    {
        var first = WallpaperProfile.CreateDefault("First");
        var second = WallpaperProfile.CreateDefault("Second");
        var workspace = new WallpaperWorkspace(
            CreateSettings([first, second], globalProfileId: first.ProfileId));
        var canonicalPath = Path.GetFullPath(Path.Combine("wallpapers", "same.png"));
        var aliasPath = Path.Combine(
            Path.GetDirectoryName(canonicalPath)!,
            "unused",
            "..",
            Path.GetFileName(canonicalPath));

        var selected = workspace.SelectLocalMedia(
            first.ProfileId,
            aliasPath,
            MediaKind.Image);
        var reused = workspace.SelectLocalMedia(
            second.ProfileId,
            canonicalPath,
            MediaKind.Image);

        Assert.Equal(selected.MediaId, reused.MediaId);
        Assert.Single(workspace.State.Draft.MediaCatalog);
        Assert.Equal(selected.MediaId, Assert.Single(workspace.State.Draft.RecentMediaIds));
        Assert.All(
            workspace.State.Draft.Profiles,
            profile => Assert.Equal(selected.MediaId, profile.MediaId));

        workspace.ClearMedia(first.ProfileId);
        workspace.ClearMedia(second.ProfileId);

        Assert.All(
            workspace.State.Draft.Profiles,
            profile => Assert.Null(profile.MediaId));
        Assert.Single(workspace.State.Draft.MediaCatalog);
        Assert.Single(workspace.State.Draft.RecentMediaIds);
    }

    [Fact]
    public void StaleRuntimeTransitionsCannotOverwriteLatestRevision()
    {
        var workspace = new WallpaperWorkspace(SettingsV2.CreateDefault());
        var firstSaved = workspace.CaptureDraft();
        workspace.BeginRevision(1);
        workspace.BeginRevision(2);

        var saveWasLatest = workspace.CommitSavedDesired(firstSaved, 1);
        var runtimeWasLatest = workspace.SetRuntimeSurface(
            WallpaperRuntimeSurface.Official(),
            clearActiveSnapshot: true,
            revision: 1);

        Assert.False(saveWasLatest);
        Assert.False(runtimeWasLatest);
        Assert.Equal(2, workspace.State.LatestRevision);
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Disconnected,
            workspace.State.RuntimeSurface.Kind);

        var active = workspace.CaptureDraft();
        Assert.True(
            workspace.CommitActive(
                active,
                WallpaperRuntimeSurface.Official(),
                revision: 2));

        Assert.NotSame(active, workspace.State.ActiveSnapshot);
        Assert.True(
            SettingsV2Comparer.DurableEquals(
                active,
                workspace.State.ActiveSnapshot));
        Assert.Equal(
            WallpaperRuntimeSurfaceKind.Official,
            workspace.State.RuntimeSurface.Kind);
        Assert.Equal(WallpaperWorkspacePhase.Idle, workspace.State.Phase);
    }

    [Fact]
    public void CaptureDraftIsDeeplyIsolatedFromLaterEdits()
    {
        var workspace = new WallpaperWorkspace(SettingsV2.CreateDefault());

        var captured = workspace.CaptureDraft();
        workspace.CreateProfile();

        Assert.Single(captured.Profiles);
        Assert.Equal(2, workspace.State.Draft.Profiles.Count);
        Assert.False(SettingsV2Comparer.UiDirtyEquals(captured, workspace.State.Draft));
    }

    [Fact]
    public void ActiveCommitMustUseTheCanonicalSavedSnapshot()
    {
        var workspace = new WallpaperWorkspace(SettingsV2.CreateDefault());
        workspace.CreateProfile();
        var unsavedDraft = workspace.CaptureDraft();
        workspace.BeginRevision(1);

        var exception = Assert.Throws<InvalidOperationException>(
            () => workspace.CommitActive(
                unsavedDraft,
                WallpaperRuntimeSurface.Official(),
                revision: 1));

        Assert.Contains("same canonical snapshot", exception.Message, StringComparison.Ordinal);
        Assert.Null(workspace.State.ActiveSnapshot);
    }

    [Fact]
    public void IndependentMetadataCommitDoesNotClobberWallpaperDraft()
    {
        var workspace = new WallpaperWorkspace(SettingsV2.CreateDefault());
        var created = workspace.CreateProfile();
        var saved = (workspace.State.SavedDesired with
        {
            AcceptedCdpRisk = true,
        }).CreateSnapshot();
        var draft = (workspace.State.Draft with
        {
            AcceptedCdpRisk = true,
        }).CreateSnapshot();

        workspace.CommitIndependentSettings(saved, draft);

        Assert.True(workspace.State.SavedDesired.AcceptedCdpRisk);
        Assert.True(workspace.State.Draft.AcceptedCdpRisk);
        Assert.Contains(
            workspace.State.Draft.Profiles,
            profile => profile.ProfileId == created.ProfileId);
        Assert.DoesNotContain(
            workspace.State.SavedDesired.Profiles,
            profile => profile.ProfileId == created.ProfileId);
        Assert.True(workspace.State.IsDraftDirty);
    }

    [Fact]
    public void ConditionalDraftReplacementIsAtomicAgainstAChangedDraft()
    {
        var workspace = new WallpaperWorkspace(SettingsV2.CreateDefault());
        var expected = workspace.CaptureDraft();
        var replacement = workspace.CreateProfile().ProfileId;
        var canonical = workspace.CaptureDraft();
        workspace.RenameProfile(replacement, "Edited after comparison snapshot");

        var replaced = workspace.ReplaceDraftIfUnchanged(expected, canonical);

        Assert.False(replaced);
        Assert.Equal(
            "Edited after comparison snapshot",
            workspace.State.Draft.Profiles.Single(
                profile => profile.ProfileId == replacement).Name);
    }

    private static SettingsV2 CreateSettings(
        IReadOnlyList<WallpaperProfile> profiles,
        IReadOnlyList<MediaReference>? mediaCatalog = null,
        Guid? globalProfileId = null,
        IReadOnlyDictionary<SemanticRegion, Guid>? bindings = null) =>
        new SettingsV2
        {
            Profiles = profiles,
            MediaCatalog = mediaCatalog ?? Array.Empty<MediaReference>(),
            RegionBindings = bindings ??
                new Dictionary<SemanticRegion, Guid>
                {
                    [SemanticRegion.Global] =
                        globalProfileId ?? profiles[0].ProfileId,
                },
        };

    private static MediaReference CreateMedia(Guid mediaId, string fileName) =>
        new()
        {
            MediaId = mediaId,
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = Path.GetFullPath(fileName),
            LastKnownKind = MediaKind.Image,
        };
}
