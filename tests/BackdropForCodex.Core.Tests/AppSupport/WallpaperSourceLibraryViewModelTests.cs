using BackdropForCodex.App.ViewModels;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class WallpaperSourceLibraryViewModelTests
{
    [Fact]
    public async Task ExistingLocalProviderDiscoversNoSyntheticSources()
    {
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry(
                [new LocalFileWallpaperSourceProvider()]));

        Assert.Equal(
            WallpaperSourceAvailability.NotLoaded,
            viewModel.IntegrationAvailability);
        Assert.Equal(0, viewModel.InstalledCount);

        await viewModel.RefreshAsync();

        Assert.Empty(viewModel.Sources);
        Assert.Equal(0, viewModel.InstalledCount);
        Assert.Equal(
            WallpaperSourceAvailability.Ready,
            viewModel.IntegrationAvailability);
        Assert.Empty(viewModel.FailedSourceKinds);
        Assert.False(viewModel.HasDiscoveryFailures);
        Assert.False(viewModel.IsRefreshing);
    }

    [Fact]
    public async Task BackgroundRefreshPublishesNotificationsOnCapturedContext()
    {
        var notificationContext = new QueuedSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        WallpaperSourceLibraryViewModel viewModel;
        try
        {
            SynchronizationContext.SetSynchronizationContext(notificationContext);
            viewModel = new WallpaperSourceLibraryViewModel(
                new WallpaperSourceProviderRegistry(
                    [new LocalFileWallpaperSourceProvider()]));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        using (viewModel)
        {
            var notificationsOnCapturedContext = new List<bool>();
            viewModel.PropertyChanged += (_, _) =>
                notificationsOnCapturedContext.Add(
                    ReferenceEquals(
                        SynchronizationContext.Current,
                        notificationContext));

            await Task.Run(() => viewModel.RefreshAsync());

            Assert.Empty(notificationsOnCapturedContext);
            notificationContext.Drain();
            Assert.NotEmpty(notificationsOnCapturedContext);
            Assert.All(notificationsOnCapturedContext, Assert.True);
        }
    }

    [Fact]
    public async Task DiscoveryPreservesProviderOrderAndFiltersUnsupportedContent()
    {
        var provider = new MutableSourceProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [
                CreateWorkshopDescriptor("1", WallpaperContentKind.Video),
                CreateWorkshopDescriptor("2", WallpaperContentKind.Scene),
                CreateWorkshopDescriptor("3", WallpaperContentKind.Image),
                CreateWorkshopDescriptor("4", WallpaperContentKind.Web),
                CreateWorkshopDescriptor("5", WallpaperContentKind.Unknown),
                CreateWorkshopDescriptor("6", WallpaperContentKind.Application),
            ]);
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry(
                [new LocalFileWallpaperSourceProvider(), provider]));

        await viewModel.RefreshAsync();

        Assert.Equal(
            [
                WallpaperContentKind.Video,
                WallpaperContentKind.Scene,
                WallpaperContentKind.Image,
                WallpaperContentKind.Web,
            ],
            viewModel.Sources.Select(source => source.ContentKind));
        Assert.Equal(
            ["1", "2", "3", "4"],
            viewModel.Sources.Select(source => source.SourceIdentifier));
        Assert.All(
            viewModel.Sources,
            source => Assert.Equal(provider.SourceKind, source.SourceKind));
        Assert.False(viewModel.HasDiscoveryFailures);
    }

    [Fact]
    public async Task DynamicItemsFollowTheRealCapabilityProbeWhileDirectItemsStayAvailable()
    {
        var provider = new MutableSourceProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [
                CreateWorkshopDescriptor("21", WallpaperContentKind.Video),
                CreateWorkshopDescriptor("22", WallpaperContentKind.Scene),
            ]);
        var capabilitySource = new MutableDynamicCapabilitySource(
            DynamicWallpaperCapability.Unavailable(
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable));
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            dynamicCapabilitySource: capabilitySource);

        await viewModel.RefreshAsync();

        Assert.Equal(1, capabilitySource.ProbeCount);
        var video = Assert.Single(
            viewModel.Items,
            item => item.ContentKind == WallpaperContentKind.Video);
        var scene = Assert.Single(
            viewModel.Items,
            item => item.ContentKind == WallpaperContentKind.Scene);
        Assert.True(video.CanApply);
        Assert.Equal(WallpaperSourceAvailability.Ready, video.Availability);
        Assert.False(scene.CanApply);
        Assert.Equal(
            WallpaperSourceAvailability.RendererUnavailable,
            scene.Availability);
        Assert.Equal(
            WallpaperSourceAvailability.RendererUnavailable,
            viewModel.IntegrationAvailability);

        capabilitySource.Capability = DynamicWallpaperCapability.Available();
        await viewModel.RefreshAsync();

        Assert.Equal(2, capabilitySource.ProbeCount);
        scene = Assert.Single(
            viewModel.Items,
            item => item.ContentKind == WallpaperContentKind.Scene);
        Assert.True(scene.CanApply);
        Assert.Equal(WallpaperSourceAvailability.Ready, scene.Availability);
        Assert.Equal(
            WallpaperSourceAvailability.Ready,
            viewModel.IntegrationAvailability);
    }

    [Fact]
    public async Task TypedSafetyProbeFailureBecomesExplicitUnavailableState()
    {
        var provider = new MutableSourceProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [
                CreateWorkshopDescriptor("31", WallpaperContentKind.Video),
                CreateWorkshopDescriptor("32", WallpaperContentKind.Scene),
            ]);
        var capabilitySource = new MutableDynamicCapabilitySource(
            DynamicWallpaperCapability.Available())
        {
            ProbeFailure = new DynamicWallpaperUnavailableException(
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable),
        };
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]),
            dynamicCapabilitySource: capabilitySource);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.DynamicCapability.IsAvailable);
        Assert.Equal(
            DynamicWallpaperCapabilityReasonCode.WallpaperEngineAudioIsolationUnavailable,
            viewModel.DynamicCapability.ReasonCode);
        Assert.False(viewModel.HasDiscoveryFailures);
        Assert.Equal(
            WallpaperSourceAvailability.RendererUnavailable,
            viewModel.IntegrationAvailability);
        Assert.True(
            Assert.Single(
                viewModel.Items,
                item => item.ContentKind == WallpaperContentKind.Video).CanApply);
        var scene = Assert.Single(
            viewModel.Items,
            item => item.ContentKind == WallpaperContentKind.Scene);
        Assert.False(scene.CanApply);
        Assert.Equal(
            WallpaperSourceAvailability.RendererUnavailable,
            scene.Availability);
    }

    [Fact]
    public async Task OneProviderFailureDoesNotRemoveOtherProviderSources()
    {
        var workshop = new MutableSourceProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [CreateWorkshopDescriptor("7", WallpaperContentKind.Video)]);
        var failing = new ThrowingSourceProvider(
            MediaSourceKind.WallpaperEngineLocalProject);
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([failing, workshop]));

        await viewModel.RefreshAsync();

        var source = Assert.Single(viewModel.Sources);
        Assert.Equal("7", source.SourceIdentifier);
        Assert.Equal(
            [MediaSourceKind.WallpaperEngineLocalProject],
            viewModel.FailedSourceKinds);
        Assert.True(viewModel.HasDiscoveryFailures);
    }

    [Fact]
    public async Task RefreshCommandRetriesFailedProvidersAndClearsFailureState()
    {
        var provider = new RecoveringSourceProvider();
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]));

        await viewModel.RefreshAsync();
        Assert.True(viewModel.HasDiscoveryFailures);

        provider.ShouldFail = false;
        viewModel.RefreshCommand.Execute(parameter: null);
        await Assert.IsAssignableFrom<Task>(viewModel.RefreshCommand.ExecutionTask);

        Assert.Equal(2, provider.DiscoveryCount);
        Assert.False(viewModel.HasDiscoveryFailures);
        Assert.Equal("14", Assert.Single(viewModel.Sources).SourceIdentifier);
    }

    [Fact]
    public async Task SourcesFollowRegistryOrderInsteadOfSourceKindOrder()
    {
        var workshop = new MutableSourceProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [CreateWorkshopDescriptor("13", WallpaperContentKind.Video)]);
        var localProject = new MutableSourceProvider(
            MediaSourceKind.WallpaperEngineLocalProject,
            [
                CreateDescriptor(
                    MediaSourceKind.WallpaperEngineLocalProject,
                    @"C:\wallpaper-engine\projects\scene",
                    WallpaperContentKind.Scene),
            ]);
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([workshop, localProject]));

        await viewModel.RefreshAsync();

        Assert.Equal(
            [
                MediaSourceKind.WallpaperEngineWorkshopProject,
                MediaSourceKind.WallpaperEngineLocalProject,
            ],
            viewModel.Sources.Select(source => source.SourceKind));
    }

    [Fact]
    public async Task RefreshPublishesDefensiveReplacementSnapshots()
    {
        var provider = new MutableSourceProvider(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            [CreateWorkshopDescriptor("8", WallpaperContentKind.Image)]);
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]));

        await viewModel.RefreshAsync();
        var firstSnapshot = viewModel.Sources;
        provider.Sources =
            [CreateWorkshopDescriptor("9", WallpaperContentKind.Video)];

        await viewModel.RefreshAsync();

        Assert.Equal("8", Assert.Single(firstSnapshot).SourceIdentifier);
        Assert.Equal("9", Assert.Single(viewModel.Sources).SourceIdentifier);
        Assert.NotSame(firstSnapshot, viewModel.Sources);
        Assert.Throws<NotSupportedException>(
            () => ((IList<WallpaperSourceDescriptor>)viewModel.Sources).Add(
                CreateWorkshopDescriptor("10", WallpaperContentKind.Image)));
        Assert.Throws<NotSupportedException>(
            () => ((IList<MediaSourceKind>)viewModel.FailedSourceKinds).Add(
                MediaSourceKind.LocalFile));
    }

    [Fact]
    public async Task NewRefreshCancelsOlderRefreshAndOnlyLatestCanPublish()
    {
        var provider = new SupersedingSourceProvider();
        using var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]));

        var firstRefresh = viewModel.RefreshAsync();
        await provider.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRefresh = viewModel.RefreshAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => firstRefresh);
        await secondRefresh;

        var source = Assert.Single(viewModel.Sources);
        Assert.Equal("12", source.SourceIdentifier);
        Assert.Equal(2, provider.CallCount);
        Assert.False(viewModel.IsRefreshing);
    }

    [Fact]
    public async Task DisposeDuringPendingRefreshCancelsWithoutADisposalRace()
    {
        var provider = new SupersedingSourceProvider();
        var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]));

        var refresh = viewModel.RefreshAsync();
        await provider.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeException = Record.Exception(viewModel.Dispose);

        Assert.Null(disposeException);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.False(viewModel.IsRefreshing);
    }

    [Fact]
    public async Task DisposeCancelsRunningRefreshCommandWithoutCommandException()
    {
        var provider = new ThrowingCancellationCallbackProvider();
        var viewModel = new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([provider]));

        viewModel.RefreshCommand.Execute(parameter: null);
        var commandTask = Assert.IsAssignableFrom<Task>(
            viewModel.RefreshCommand.ExecutionTask);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeException = Record.Exception(viewModel.Dispose);

        Assert.Null(disposeException);
        await commandTask;
        Assert.True(commandTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExternalCancellationAndDisposeUseOneSerializedLifetime()
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            using var cancellation = new CancellationTokenSource();
            var provider = new SupersedingSourceProvider();
            var viewModel = new WallpaperSourceLibraryViewModel(
                new WallpaperSourceProviderRegistry([provider]));
            var refresh = viewModel.RefreshAsync(cancellation.Token);
            await provider.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Task.WhenAll(
                Task.Run(cancellation.Cancel),
                Task.Run(viewModel.Dispose));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        }
    }

    private static WallpaperSourceDescriptor CreateWorkshopDescriptor(
        string sourceIdentifier,
        WallpaperContentKind contentKind) =>
        CreateDescriptor(
            MediaSourceKind.WallpaperEngineWorkshopProject,
            sourceIdentifier,
            contentKind);

    private static WallpaperSourceDescriptor CreateDescriptor(
        MediaSourceKind sourceKind,
        string sourceIdentifier,
        WallpaperContentKind contentKind)
    {
        var deliveryKind = contentKind switch
        {
            WallpaperContentKind.Image or WallpaperContentKind.Video =>
                WallpaperDeliveryKind.DirectMedia,
            WallpaperContentKind.Scene or WallpaperContentKind.Web =>
                WallpaperDeliveryKind.WallpaperEngineWindow,
            _ => WallpaperDeliveryKind.Unsupported,
        };
        var capabilities = deliveryKind == WallpaperDeliveryKind.WallpaperEngineWindow
            ? WallpaperDeliveryCapabilities.DynamicFrames
            : WallpaperDeliveryCapabilities.None;
        return new WallpaperSourceDescriptor(
            sourceKind,
            sourceIdentifier,
            $"Source {sourceIdentifier}",
            contentKind,
            deliveryKind,
            capabilities);
    }

    private sealed class MutableSourceProvider(
        MediaSourceKind sourceKind,
        IReadOnlyList<WallpaperSourceDescriptor> sources)
        : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKind { get; } = sourceKind;

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
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingSourceProvider(MediaSourceKind sourceKind)
        : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKind { get; } = sourceKind;

        public ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Simulated provider discovery failure.");
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SupersedingSourceProvider : IWallpaperSourceProvider
    {
        private int _callCount;

        public MediaSourceKind SourceKind =>
            MediaSourceKind.WallpaperEngineWorkshopProject;

        public int CallCount => Volatile.Read(ref _callCount);

        public TaskCompletionSource FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                FirstCallEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }

            cancellationToken.ThrowIfCancellationRequested();
            return [CreateWorkshopDescriptor("12", WallpaperContentKind.Video)];
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecoveringSourceProvider : IWallpaperSourceProvider
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
                throw new InvalidOperationException("Provider unavailable.");
            }

            return ValueTask.FromResult<IReadOnlyList<WallpaperSourceDescriptor>>(
                [CreateWorkshopDescriptor("14", WallpaperContentKind.Video)]);
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingCancellationCallbackProvider : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKind =>
            MediaSourceKind.WallpaperEngineWorkshopProject;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(
                static () => throw new InvalidOperationException(
                    "Simulated provider cancellation callback failure."));
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MutableDynamicCapabilitySource(
        DynamicWallpaperCapability capability) : IDynamicWallpaperCapabilitySource
    {
        private int _probeCount;

        public DynamicWallpaperCapability Capability { get; set; } = capability;

        public int ProbeCount => Volatile.Read(ref _probeCount);

        public Exception? ProbeFailure { get; init; }

        public ValueTask<DynamicWallpaperCapability> ProbeDynamicWallpaperAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _probeCount);
            if (ProbeFailure is not null)
            {
                return ValueTask.FromException<DynamicWallpaperCapability>(ProbeFailure);
            }

            return ValueTask.FromResult(Capability);
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = [];

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_callbacks)
            {
                _callbacks.Enqueue((callback, state));
            }
        }

        public void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) work;
                lock (_callbacks)
                {
                    if (!_callbacks.TryDequeue(out work))
                    {
                        return;
                    }
                }

                var previousContext = Current;
                try
                {
                    SetSynchronizationContext(this);
                    work.Callback(work.State);
                }
                finally
                {
                    SetSynchronizationContext(previousContext);
                }
            }
        }
    }
}
