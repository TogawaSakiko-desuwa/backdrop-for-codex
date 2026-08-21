using BackdropForCodex.App.Services.Media;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.Core.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class WallpaperEngineLibraryViewModelTests
{
    public enum ThumbnailSourceFailureKind
    {
        InstallationUnavailable,
        ProjectMissing,
    }

    [Fact]
    public async Task SearchTypeAndOriginFiltersComposeWithoutChangingTheStableSnapshot()
    {
        var workshop = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [
                CreateDescriptor(
                    MediaSourceKind.WallpaperEngineWorkshopProject,
                    "101",
                    "Rainy loft",
                    WallpaperContentKind.Scene),
                CreateDescriptor(
                    MediaSourceKind.WallpaperEngineWorkshopProject,
                    "102",
                    "Quiet coast",
                    WallpaperContentKind.Video),
            ]);
        var local = new MutableProvider(
            MediaSourceKind.WallpaperEngineLocalProject,
            [
                CreateDescriptor(
                    MediaSourceKind.WallpaperEngineLocalProject,
                    @"C:\WE\projects\myprojects\rain-notes",
                    "Rain notes",
                    WallpaperContentKind.Web),
            ]);
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([workshop, local]),
            NullWallpaperThumbnailPreviewService.Instance);

        await viewModel.RefreshAsync();

        Assert.Equal(3, viewModel.InstalledCount);
        Assert.Equal(3, viewModel.Items.Count);

        viewModel.SearchText = "rain";
        Assert.Equal(2, viewModel.VisibleItems.Count);

        viewModel.ContentFilter = WallpaperSourceContentFilter.Scene;
        var scene = Assert.Single(viewModel.VisibleItems);
        Assert.Equal("101", scene.Descriptor.SourceIdentifier);

        viewModel.ContentFilter = WallpaperSourceContentFilter.All;
        viewModel.OriginFilter = WallpaperSourceOriginFilter.Local;
        var localResult = Assert.Single(viewModel.VisibleItems);
        Assert.Equal(WallpaperSourceOrigin.Local, localResult.Origin);

        Assert.Equal(3, viewModel.Items.Count);
        Assert.Equal(3, viewModel.Sources.Count);
    }

    [Fact]
    public async Task FailedProviderRefreshRetainsItsLastStableSnapshotAsStale()
    {
        var provider = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [
                CreateDescriptor(
                    MediaSourceKind.WallpaperEngineWorkshopProject,
                    "201",
                    "Stable source",
                    WallpaperContentKind.Video),
            ]);
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            NullWallpaperThumbnailPreviewService.Instance);

        await viewModel.RefreshAsync();
        Assert.Equal(WallpaperSourceAvailability.Ready, Assert.Single(viewModel.Items).Availability);

        provider.Failure = new WallpaperEngineUnavailableException(
            WallpaperEngineAvailabilityReason.NotInstalled);
        await viewModel.RefreshAsync();

        var retained = Assert.Single(viewModel.Items);
        Assert.Equal("201", retained.Descriptor.SourceIdentifier);
        Assert.Equal(WallpaperSourceAvailability.Stale, retained.Availability);
        Assert.False(retained.CanAssign);
        Assert.True(viewModel.HasDiscoveryFailures);
        Assert.Equal(WallpaperSourceAvailability.NotInstalled, viewModel.IntegrationAvailability);
    }

    [Theory]
    [InlineData(
        WallpaperEngineAvailabilityReason.MultipleInstallations,
        WallpaperSourceAvailability.InstallationSelectionRequired)]
    [InlineData(
        WallpaperEngineAvailabilityReason.PreferredInstallationInvalid,
        WallpaperSourceAvailability.InstallationSelectionInvalid)]
    [InlineData(
        WallpaperEngineAvailabilityReason.NotInstalled,
        WallpaperSourceAvailability.NotInstalled)]
    public async Task InstallationDiscoveryRetainsTheTypedAvailabilityReason(
        WallpaperEngineAvailabilityReason reason,
        WallpaperSourceAvailability expectedAvailability)
    {
        var provider = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [])
        {
            Failure = new WallpaperEngineUnavailableException(reason),
        };
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            NullWallpaperThumbnailPreviewService.Instance);

        await viewModel.RefreshAsync();

        Assert.Equal(expectedAvailability, viewModel.IntegrationAvailability);
        Assert.Equal(reason, viewModel.InstallationAvailabilityReason);
    }

    [Fact]
    public async Task ValidatedInstallationSelectionRefreshesTheSameProviderRegistry()
    {
        var provider = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            []);
        var selection = new RecordingInstallationSelectionService();
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            NullWallpaperThumbnailPreviewService.Instance,
            installationSelectionService: selection);
        var selectedPath = Path.Combine(
            Path.GetTempPath(),
            "Wallpaper Engine",
            "wallpaper64.exe");

        var selected = await viewModel.SelectInstallationAsync(selectedPath);

        Assert.True(selected);
        Assert.Equal(selectedPath, selection.SelectedPath);
        Assert.Equal(1, provider.DiscoveryCount);
        Assert.True(viewModel.HasPreferredWallpaperEngineInstallation);
    }

    [Fact]
    public async Task RejectedInstallationSelectionKeepsThePreviousPreferenceAndTypedReason()
    {
        var provider = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            []);
        var selection = new RecordingInstallationSelectionService
        {
            HasPreferredWallpaperEngineInstallation = true,
            SelectionFailure = new WallpaperEngineUnavailableException(
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid),
        };
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            NullWallpaperThumbnailPreviewService.Instance,
            installationSelectionService: selection);

        var selected = await viewModel.SelectInstallationAsync(@"C:\invalid\wallpaper64.exe");

        Assert.False(selected);
        Assert.True(viewModel.HasPreferredWallpaperEngineInstallation);
        Assert.Equal(
            WallpaperEngineAvailabilityReason.PreferredInstallationInvalid,
            viewModel.InstallationAvailabilityReason);
        Assert.Equal(
            WallpaperSourceAvailability.InstallationSelectionInvalid,
            viewModel.IntegrationAvailability);
        Assert.Equal(0, provider.DiscoveryCount);
    }

    [Fact]
    public async Task PreferenceWriteFailureIsVisibleAndKeepsThePreviousChoice()
    {
        var provider = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            []);
        var selection = new RecordingInstallationSelectionService
        {
            HasPreferredWallpaperEngineInstallation = true,
            SelectionFailure = new IOException("Synthetic preference write failure."),
        };
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            NullWallpaperThumbnailPreviewService.Instance,
            installationSelectionService: selection);

        var selected = await viewModel.SelectInstallationAsync(@"C:\WE\wallpaper64.exe");

        Assert.False(selected);
        Assert.True(viewModel.HasPreferredWallpaperEngineInstallation);
        Assert.Null(viewModel.InstallationAvailabilityReason);
        Assert.Equal(
            WallpaperSourceAvailability.InstallationSelectionFailed,
            viewModel.IntegrationAvailability);
        Assert.Equal(0, provider.DiscoveryCount);
    }

    [Fact]
    public async Task ClearingSelectionReturnsToAutomaticDiscoveryAndRefreshesSources()
    {
        var provider = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            []);
        var selection = new RecordingInstallationSelectionService
        {
            HasPreferredWallpaperEngineInstallation = true,
        };
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            NullWallpaperThumbnailPreviewService.Instance,
            installationSelectionService: selection);

        await viewModel.ClearInstallationSelectionAsync();

        Assert.True(selection.ClearCalled);
        Assert.False(viewModel.HasPreferredWallpaperEngineInstallation);
        Assert.Equal(1, provider.DiscoveryCount);
    }

    [Fact]
    public async Task RendererAvailabilityIsDynamicWhileSceneCanStillBeAssignedToTheDraft()
    {
        var provider = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [
                CreateDescriptor(
                    MediaSourceKind.WallpaperEngineWorkshopProject,
                    "301",
                    "Kinetic scene",
                    WallpaperContentKind.Scene),
            ]);
        var activation = new MutableActivationAvailability();
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            NullWallpaperThumbnailPreviewService.Instance,
            activation);

        await viewModel.RefreshAsync();

        var scene = Assert.Single(viewModel.Items);
        Assert.True(scene.IsStaticPreview);
        Assert.True(scene.CanAssign);
        Assert.False(scene.CanApply);
        Assert.Equal(
            WallpaperSourceAvailability.RendererUnavailable,
            scene.Availability);

        activation.CanActivateDynamicSources = true;
        activation.RaiseAvailabilityChanged();

        Assert.True(scene.CanAssign);
        Assert.True(scene.CanApply);
        Assert.Equal(WallpaperSourceAvailability.Ready, scene.Availability);
    }

    [Fact]
    public async Task ThumbnailCapabilityFailureDoesNotChangeDiscoveredProjectAvailability()
    {
        var provider = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [
                CreateDescriptor(
                    MediaSourceKind.WallpaperEngineWorkshopProject,
                    "401",
                    "Usable video",
                    WallpaperContentKind.Video),
            ]);
        var thumbnailPreview = new ThrowingThumbnailPreviewService(
            new WallpaperSourceCapabilityException("Synthetic oversized thumbnail."));
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            thumbnailPreview);
        await viewModel.RefreshAsync();
        var item = Assert.Single(viewModel.Items);

        await item.EnsureThumbnailAsync();

        Assert.True(item.IsThumbnailUnavailable);
        Assert.Equal(WallpaperSourceAvailability.Ready, item.Availability);
        Assert.True(item.CanAssign);
        Assert.True(item.CanApply);
    }

    [Fact]
    public async Task ThumbnailDecodeFailureDoesNotEscapeOrDisableAUsableProject()
    {
        var provider = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [
                CreateDescriptor(
                    MediaSourceKind.WallpaperEngineWorkshopProject,
                    "402",
                    "Usable image",
                    WallpaperContentKind.Image),
            ]);
        var thumbnailPreview = new ThrowingThumbnailPreviewService(
            new FileFormatException("Synthetic corrupt thumbnail."));
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            thumbnailPreview);
        await viewModel.RefreshAsync();
        var item = Assert.Single(viewModel.Items);

        await item.EnsureThumbnailAsync();

        Assert.True(item.IsThumbnailUnavailable);
        Assert.Equal(WallpaperSourceAvailability.Ready, item.Availability);
        Assert.True(item.CanAssign);
        Assert.True(item.CanApply);
    }

    [Theory]
    [InlineData(ThumbnailSourceFailureKind.InstallationUnavailable)]
    [InlineData(ThumbnailSourceFailureKind.ProjectMissing)]
    public async Task ThumbnailSourceFailureDoesNotOverrideTheLatestDiscoverySnapshot(
        ThumbnailSourceFailureKind failureKind)
    {
        var provider = new MutableProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [
                CreateDescriptor(
                    MediaSourceKind.WallpaperEngineWorkshopProject,
                    "403",
                    "Recently discovered video",
                    WallpaperContentKind.Video),
            ]);
        Exception failure = failureKind switch
        {
            ThumbnailSourceFailureKind.InstallationUnavailable =>
                new WallpaperEngineUnavailableException(
                    WallpaperEngineAvailabilityReason.NotInstalled),
            ThumbnailSourceFailureKind.ProjectMissing =>
                new WallpaperEngineProjectUnavailableException(
                    MediaSourceKind.WallpaperEngineWorkshopProject,
                    WallpaperEngineProjectUnavailableReason.NotFound),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
        };
        var thumbnailPreview = new ThrowingThumbnailPreviewService(failure);
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            thumbnailPreview);
        await viewModel.RefreshAsync();
        var item = Assert.Single(viewModel.Items);

        await item.EnsureThumbnailAsync();

        Assert.True(item.IsThumbnailUnavailable);
        Assert.Equal(WallpaperSourceAvailability.Ready, item.Availability);
        Assert.True(item.CanAssign);
        Assert.True(item.CanApply);
    }

    private static WallpaperSourceDescriptor CreateDescriptor(
        MediaSourceKind sourceKind,
        string identifier,
        string displayName,
        WallpaperContentKind contentKind)
    {
        var dynamic = contentKind is WallpaperContentKind.Scene or WallpaperContentKind.Web;
        return new WallpaperSourceDescriptor(
            sourceKind,
            identifier,
            displayName,
            contentKind,
            dynamic
                ? WallpaperDeliveryKind.WallpaperEngineWindow
                : WallpaperDeliveryKind.DirectMedia,
            dynamic
                ? WallpaperDeliveryCapabilities.DynamicFrames
                : WallpaperDeliveryCapabilities.None);
    }

    private sealed class MutableProvider(
        MediaSourceKind sourceKind,
        IReadOnlyList<WallpaperSourceDescriptor> sources)
        : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKind { get; } = sourceKind;

        public IReadOnlyList<WallpaperSourceDescriptor> Sources { get; set; } = sources;

        public Exception? Failure { get; set; }

        public int DiscoveryCount { get; private set; }

        public ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscoveryCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return ValueTask.FromResult(Sources);
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingInstallationSelectionService
        : IWallpaperEngineInstallationSelectionService
    {
        public bool HasPreferredWallpaperEngineInstallation { get; set; }

        public string? SelectedPath { get; private set; }

        public bool ClearCalled { get; private set; }

        public Exception? SelectionFailure { get; init; }

        public Task SelectWallpaperEngineInstallationAsync(
            string selectedPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SelectedPath = selectedPath;
            if (SelectionFailure is not null)
            {
                return Task.FromException(SelectionFailure);
            }

            HasPreferredWallpaperEngineInstallation = true;
            return Task.CompletedTask;
        }

        public Task ClearWallpaperEngineInstallationAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearCalled = true;
            HasPreferredWallpaperEngineInstallation = false;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableActivationAvailability
        : IWallpaperSourceActivationAvailability
    {
        public event EventHandler? AvailabilityChanged;

        public bool CanActivateDynamicSources { get; set; }

        public bool CanActivate(WallpaperSourceDescriptor descriptor) =>
            descriptor.DeliveryKind == WallpaperDeliveryKind.DirectMedia ||
            CanActivateDynamicSources;

        public void RaiseAvailabilityChanged() =>
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ThrowingThumbnailPreviewService(Exception failure)
        : IWallpaperThumbnailPreviewService
    {
        public Task<BitmapSource?> LoadAsync(
            MediaReference reference,
            int decodePixelWidth,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<BitmapSource?>(failure);
        }
    }
}
