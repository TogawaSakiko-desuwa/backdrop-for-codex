using System.Collections.ObjectModel;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BackdropForCodex.App.ViewModels;

/// <summary>
/// Discovers registered wallpaper sources into atomic, provider-ordered UI snapshots. Discovery
/// remains provider-backed: this model never synthesizes entries for integrations that are not
/// registered or sources that a provider did not return.
/// </summary>
public sealed class WallpaperSourceLibraryViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<WallpaperSourceDescriptor> EmptySources =
        Array.AsReadOnly(Array.Empty<WallpaperSourceDescriptor>());
    private static readonly IReadOnlyList<MediaSourceKind> EmptyFailures =
        Array.AsReadOnly(Array.Empty<MediaSourceKind>());
    private static readonly IReadOnlyList<WallpaperSourceItemViewModel> EmptyItems =
        Array.AsReadOnly(Array.Empty<WallpaperSourceItemViewModel>());

    private readonly IWallpaperSourceProviderRegistry _sourceRegistry;
    private readonly IWallpaperThumbnailPreviewService _thumbnailPreview;
    private readonly IWallpaperSourceActivationAvailability _activationAvailability;
    private readonly IWallpaperEngineInstallationSelectionService?
        _installationSelectionService;
    private readonly SynchronizationContext? _notificationContext;
    private readonly object _refreshGate = new();
    private readonly Dictionary<MediaSourceKind, WallpaperSourceDescriptor[]>
        _stableProviderSnapshots = [];
    private IReadOnlyList<WallpaperSourceDescriptor> _sources = EmptySources;
    private IReadOnlyList<MediaSourceKind> _failedSourceKinds = EmptyFailures;
    private IReadOnlyList<WallpaperSourceItemViewModel> _items = EmptyItems;
    private IReadOnlyList<WallpaperSourceItemViewModel> _visibleItems = EmptyItems;
    private WallpaperSourceItemViewModel? _resolvedReferenceItem;
    private WallpaperSourceAvailability? _resolvedReferenceAvailability;
    private WallpaperSourceItemViewModel? _selectedItem;
    private WallpaperSourceAvailability _integrationAvailability =
        WallpaperSourceAvailability.NotLoaded;
    private WallpaperEngineAvailabilityReason? _installationAvailabilityReason;
    private string _searchText = string.Empty;
    private WallpaperSourceContentFilter _contentFilter;
    private WallpaperSourceOriginFilter _originFilter;
    private RefreshCancellation? _activeRefresh;
    private RefreshCancellation? _activeReferenceRefresh;
    private long _refreshGeneration;
    private long _referenceRefreshGeneration;
    private bool _isRefreshing;
    private bool _isDisposed;

    public WallpaperSourceLibraryViewModel(
        IWallpaperSourceProviderRegistry sourceRegistry,
        IWallpaperThumbnailPreviewService? thumbnailPreview = null,
        IWallpaperSourceActivationAvailability? activationAvailability = null,
        IDynamicWallpaperCapabilitySource? dynamicCapabilitySource = null,
        IWallpaperEngineInstallationSelectionService? installationSelectionService = null)
    {
        _sourceRegistry = sourceRegistry ??
            throw new ArgumentNullException(nameof(sourceRegistry));
        _thumbnailPreview = thumbnailPreview ?? AppWallpaperSources.Thumbnails;
        if (activationAvailability is not null && dynamicCapabilitySource is not null)
        {
            throw new ArgumentException(
                "Specify either explicit activation availability or a dynamic capability source, not both.",
                nameof(dynamicCapabilitySource));
        }

        _activationAvailability = activationAvailability ??
            (dynamicCapabilitySource is null
                ? DirectWallpaperSourceActivationAvailability.Instance
                : new ProbedWallpaperSourceActivationAvailability(dynamicCapabilitySource));
        _installationSelectionService = installationSelectionService;
        _notificationContext = SynchronizationContext.Current;
        _activationAvailability.AvailabilityChanged +=
            ActivationAvailability_AvailabilityChanged;
        RefreshCommand = new AsyncRelayCommand(RefreshFromCommandAsync);
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IReadOnlyList<WallpaperSourceDescriptor> Sources => _sources;

    public IReadOnlyList<WallpaperSourceItemViewModel> Items => _items;

    public IReadOnlyList<WallpaperSourceItemViewModel> VisibleItems => _visibleItems;

    public int InstalledCount => Items.Count;

    public WallpaperSourceAvailability IntegrationAvailability =>
        _integrationAvailability;

    public WallpaperEngineAvailabilityReason? InstallationAvailabilityReason =>
        _installationAvailabilityReason;

    public bool CanManageWallpaperEngineInstallation =>
        _installationSelectionService is not null;

    public bool HasPreferredWallpaperEngineInstallation =>
        _installationSelectionService?.HasPreferredWallpaperEngineInstallation == true;

    public WallpaperSourceItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            var normalized = value ?? string.Empty;
            if (SetProperty(ref _searchText, normalized))
            {
                RefreshVisibleItems();
            }
        }
    }

    public WallpaperSourceContentFilter ContentFilter
    {
        get => _contentFilter;
        set
        {
            if (SetProperty(ref _contentFilter, value))
            {
                RefreshVisibleItems();
            }
        }
    }

    public WallpaperSourceOriginFilter OriginFilter
    {
        get => _originFilter;
        set
        {
            if (SetProperty(ref _originFilter, value))
            {
                RefreshVisibleItems();
            }
        }
    }

    /// <summary>
    /// Provider namespaces whose latest discovery failed. Exception details deliberately remain
    /// behind the provider boundary; the UI only needs enough state to offer a retry.
    /// </summary>
    public IReadOnlyList<MediaSourceKind> FailedSourceKinds => _failedSourceKinds;

    public bool HasDiscoveryFailures => FailedSourceKinds.Count != 0;

    public bool IsRefreshing => _isRefreshing;

    /// <summary>
    /// Availability of the one durable reference resolved outside full library discovery.
    /// This keeps startup bounded while still validating the currently selected wallpaper.
    /// </summary>
    public WallpaperSourceAvailability? ResolvedReferenceAvailability =>
        _resolvedReferenceAvailability;

    public DynamicWallpaperCapability DynamicCapability =>
        _activationAvailability is ProbedWallpaperSourceActivationAvailability probed
            ? probed.Capability
            : DynamicWallpaperCapability.Unavailable(
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineUnavailable);

    internal bool CanActivate(MediaReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.SourceKind is not (
                MediaSourceKind.WallpaperEngineLocalProject or
                MediaSourceKind.WallpaperEngineWorkshopProject))
        {
            return reference.LastKnownKind is MediaKind.Image or MediaKind.Video;
        }

        var contentKind = ResolveContentKind(reference);
        if (contentKind == WallpaperContentKind.Unknown)
        {
            return false;
        }

        var resolvedReferenceItem = Volatile.Read(ref _resolvedReferenceItem);
        return Items.Any(item => CanActivateItem(item, reference, contentKind)) ||
            (resolvedReferenceItem is not null &&
             CanActivateItem(resolvedReferenceItem, reference, contentKind));
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RefreshCancellation refreshCancellation;
        RefreshCancellation? previousRefresh;
        long generation;
        lock (_refreshGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            refreshCancellation = new RefreshCancellation(cancellationToken);
            previousRefresh = _activeRefresh;
            _activeRefresh = refreshCancellation;
            generation = ++_refreshGeneration;
        }

        try
        {
            CancelIfPending(previousRefresh);
            SetRefreshing(generation, value: true);
            if (_activationAvailability is ProbedWallpaperSourceActivationAvailability probed)
            {
                await probed
                    .RefreshAsync(refreshCancellation.Token)
                    .ConfigureAwait(true);
            }

            var discoveredSources = new List<WallpaperSourceDescriptor>();
            var failedSourceKinds = new List<MediaSourceKind>();
            var successfulProviderSnapshots =
                new Dictionary<MediaSourceKind, WallpaperSourceDescriptor[]>();
            var failureAvailability = WallpaperSourceAvailability.Ready;
            WallpaperEngineAvailabilityReason? installationAvailabilityReason = null;
            foreach (var provider in _sourceRegistry.Providers)
            {
                refreshCancellation.Token.ThrowIfCancellationRequested();
                try
                {
                    var providerSources = await provider
                        .DiscoverAsync(refreshCancellation.Token)
                        .ConfigureAwait(true);
                    var providerSnapshot = SnapshotProviderSources(
                        provider,
                        providerSources)
                        .Where(IsSupportedLibrarySource)
                        .ToArray();
                    successfulProviderSnapshots[provider.SourceKind] = providerSnapshot;
                    discoveredSources.AddRange(
                        providerSnapshot);
                }
                catch (OperationCanceledException)
                    when (refreshCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                    when (IsRecoverableProviderFailure(
                        exception,
                        refreshCancellation.Token))
                {
                    failedSourceKinds.Add(provider.SourceKind);
                    if (exception is WallpaperEngineUnavailableException unavailable)
                    {
                        installationAvailabilityReason ??= unavailable.Reason;
                    }
                    failureAvailability = SelectFailureAvailability(
                        failureAvailability,
                        MapFailureAvailability(exception));
                    lock (_refreshGate)
                    {
                        if (_stableProviderSnapshots.TryGetValue(
                                provider.SourceKind,
                                out var stableSnapshot))
                        {
                            discoveredSources.AddRange(stableSnapshot);
                        }
                    }
                }
            }

            refreshCancellation.Token.ThrowIfCancellationRequested();
            PublishSnapshot(
                generation,
                discoveredSources,
                failedSourceKinds,
                successfulProviderSnapshots,
                failureAvailability,
                installationAvailabilityReason);
        }
        finally
        {
            SetRefreshing(generation, value: false);
            lock (_refreshGate)
            {
                if (ReferenceEquals(_activeRefresh, refreshCancellation))
                {
                    _activeRefresh = null;
                }
            }

            refreshCancellation.Dispose();
        }
    }

    public async Task RefreshActivationAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_refreshGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
        }

        if (_activationAvailability is ProbedWallpaperSourceActivationAvailability probed)
        {
            await probed.RefreshAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Resolves only the supplied durable reference. Unlike <see cref="RefreshAsync"/>, this does
    /// not enumerate Workshop or local project roots and is therefore safe on the startup path.
    /// </summary>
    public async Task RefreshReferenceAvailabilityAsync(
        MediaReference? reference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        RefreshCancellation refreshCancellation;
        RefreshCancellation? previousRefresh;
        long generation;
        lock (_refreshGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            refreshCancellation = new RefreshCancellation(cancellationToken);
            previousRefresh = _activeReferenceRefresh;
            _activeReferenceRefresh = refreshCancellation;
            generation = ++_referenceRefreshGeneration;
        }

        try
        {
            CancelIfPending(previousRefresh);
            var snapshot = reference?.Snapshot();
            if (snapshot is null ||
                snapshot.SourceKind is not (
                    MediaSourceKind.WallpaperEngineLocalProject or
                    MediaSourceKind.WallpaperEngineWorkshopProject))
            {
                PublishResolvedReference(
                    generation,
                    item: null,
                    availability: null);
                return;
            }

            try
            {
                var resolution = await _sourceRegistry
                    .ResolveRequiredAsync(snapshot, refreshCancellation.Token)
                    .ConfigureAwait(true);
                if (!IsSupportedLibrarySource(resolution.Descriptor))
                {
                    throw new WallpaperSourceCapabilityException(
                        "The resolved wallpaper source is not supported by the library.");
                }

                var item = new WallpaperSourceItemViewModel(
                    resolution.Descriptor,
                    WallpaperSourceAvailability.Ready,
                    _thumbnailPreview,
                    _activationAvailability);
                PublishResolvedReference(
                    generation,
                    item,
                    item.Availability);
            }
            catch (OperationCanceledException)
                when (refreshCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
                when (IsRecoverableProviderFailure(
                    exception,
                    refreshCancellation.Token))
            {
                PublishResolvedReference(
                    generation,
                    item: null,
                    MapFailureAvailability(exception));
            }
        }
        finally
        {
            lock (_refreshGate)
            {
                if (ReferenceEquals(_activeReferenceRefresh, refreshCancellation))
                {
                    _activeReferenceRefresh = null;
                }
            }

            refreshCancellation.Dispose();
        }
    }

    internal void UseResolvedDescriptor(WallpaperSourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!IsSupportedLibrarySource(descriptor))
        {
            throw new WallpaperSourceCapabilityException(
                "The selected wallpaper source is not supported by the library.");
        }

        var item = new WallpaperSourceItemViewModel(
            descriptor,
            WallpaperSourceAvailability.Ready,
            _thumbnailPreview,
            _activationAvailability);
        RefreshCancellation? previousRefresh;
        long generation;
        lock (_refreshGate)
        {
            if (_isDisposed)
            {
                item.Dispose();
                throw new ObjectDisposedException(nameof(WallpaperSourceLibraryViewModel));
            }

            previousRefresh = _activeReferenceRefresh;
            _activeReferenceRefresh = null;
            generation = ++_referenceRefreshGeneration;
        }

        CancelIfPending(previousRefresh);
        PublishResolvedReference(generation, item, item.Availability);
    }

    public async Task<bool> SelectInstallationAsync(
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        if (_installationSelectionService is null)
        {
            return false;
        }

        try
        {
            await _installationSelectionService
                .SelectWallpaperEngineInstallationAsync(selectedPath, cancellationToken)
                .ConfigureAwait(true);
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
            NotifyPropertiesChanged(nameof(HasPreferredWallpaperEngineInstallation));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WallpaperEngineUnavailableException unavailable)
        {
            PublishInstallationSelectionFailure(unavailable.Reason);
            return false;
        }
        catch (Exception exception) when (IsRecoverableProviderFailure(exception, cancellationToken))
        {
            PublishInstallationSelectionFailure(reason: null);
            return false;
        }
    }

    public async Task<bool> ClearInstallationSelectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_installationSelectionService is null)
        {
            return false;
        }

        try
        {
            await _installationSelectionService
                .ClearWallpaperEngineInstallationAsync(cancellationToken)
                .ConfigureAwait(true);
            await RefreshAsync(cancellationToken).ConfigureAwait(true);
            NotifyPropertiesChanged(nameof(HasPreferredWallpaperEngineInstallation));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableProviderFailure(exception, cancellationToken))
        {
            PublishInstallationSelectionFailure(
                (exception as WallpaperEngineUnavailableException)?.Reason);
            return false;
        }
    }

    public void Dispose()
    {
        RefreshCancellation? activeRefresh;
        RefreshCancellation? activeReferenceRefresh;
        WallpaperSourceItemViewModel? resolvedReferenceItem;
        var notifyRefreshing = false;
        lock (_refreshGate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            activeRefresh = _activeRefresh;
            _activeRefresh = null;
            activeReferenceRefresh = _activeReferenceRefresh;
            _activeReferenceRefresh = null;
            resolvedReferenceItem = _resolvedReferenceItem;
            _resolvedReferenceItem = null;
            if (_isRefreshing)
            {
                _isRefreshing = false;
                notifyRefreshing = true;
            }
        }

        CancelIfPending(activeRefresh);
        CancelIfPending(activeReferenceRefresh);
        _activationAvailability.AvailabilityChanged -=
            ActivationAvailability_AvailabilityChanged;
        DisposeItems(_items);
        resolvedReferenceItem?.Dispose();
        if (notifyRefreshing)
        {
            NotifyPropertiesChanged(nameof(IsRefreshing));
        }

        GC.SuppressFinalize(this);
    }

    private static bool IsSupportedLibrarySource(
        WallpaperSourceDescriptor descriptor) =>
        (descriptor.SourceKind is
            MediaSourceKind.WallpaperEngineLocalProject or
            MediaSourceKind.WallpaperEngineWorkshopProject) &&
        descriptor.DeliveryKind != WallpaperDeliveryKind.Unsupported &&
        descriptor.ContentKind is
            WallpaperContentKind.Image or
            WallpaperContentKind.Video or
            WallpaperContentKind.Scene or
            WallpaperContentKind.Web;

    private static bool CanActivateItem(
        WallpaperSourceItemViewModel item,
        MediaReference reference,
        WallpaperContentKind contentKind) =>
        item.ContentKind == contentKind &&
        item.Descriptor.SourceKind == reference.SourceKind &&
        SourceIdentifiersEqual(
            item.Descriptor.SourceIdentifier,
            reference.SourceIdentifier,
            reference.SourceKind) &&
        item.CanApply;

    private async Task RefreshFromCommandAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseding refreshes and window disposal are normal command termination paths.
        }
        catch (ObjectDisposedException)
        {
            // A queued command can be delivered after its owning window has begun disposal.
        }
    }

    private static void CancelIfPending(RefreshCancellation? cancellation) =>
        cancellation?.Cancel();

    private static WallpaperSourceDescriptor[] SnapshotProviderSources(
        IWallpaperSourceProvider provider,
        IReadOnlyList<WallpaperSourceDescriptor>? providerSources)
    {
        if (providerSources is null)
        {
            throw new WallpaperSourceCapabilityException(
                "A wallpaper source provider returned no discovery collection.");
        }

        var snapshot = providerSources.ToArray();
        if (snapshot.Any(
                descriptor =>
                    descriptor is null ||
                    descriptor.SourceKind != provider.SourceKind))
        {
            throw new WallpaperSourceCapabilityException(
                "A wallpaper source provider returned a source outside its namespace.");
        }

        return snapshot;
    }

    private static bool IsRecoverableProviderFailure(
        Exception exception,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is not (OutOfMemoryException or AccessViolationException);

    private void PublishSnapshot(
        long generation,
        List<WallpaperSourceDescriptor> sources,
        List<MediaSourceKind> failedSourceKinds,
        IReadOnlyDictionary<MediaSourceKind, WallpaperSourceDescriptor[]>
            successfulProviderSnapshots,
        WallpaperSourceAvailability failureAvailability,
        WallpaperEngineAvailabilityReason? installationAvailabilityReason)
    {
        var sourceSnapshot = new ReadOnlyCollection<WallpaperSourceDescriptor>(
            sources.ToArray());
        var failureSnapshot = new ReadOnlyCollection<MediaSourceKind>(
            failedSourceKinds.ToArray());
        IReadOnlyList<WallpaperSourceItemViewModel> previousItems;
        lock (_refreshGate)
        {
            if (_isDisposed ||
                generation != _refreshGeneration ||
                _activeRefresh is null)
            {
                return;
            }

            if (_thumbnailPreview is IWallpaperThumbnailCacheInvalidation invalidation)
            {
                invalidation.AdvanceGenerations(
                    successfulProviderSnapshots.Keys.ToArray());
            }

            foreach (var pair in successfulProviderSnapshots)
            {
                _stableProviderSnapshots[pair.Key] = pair.Value;
            }

            previousItems = _items;
            _sources = sourceSnapshot;
            _failedSourceKinds = failureSnapshot;
            _items = new ReadOnlyCollection<WallpaperSourceItemViewModel>(
                sourceSnapshot
                    .Select(
                        descriptor => new WallpaperSourceItemViewModel(
                            descriptor,
                            failureSnapshot.Contains(descriptor.SourceKind)
                                ? WallpaperSourceAvailability.Stale
                                : WallpaperSourceAvailability.Ready,
                            _thumbnailPreview,
                            _activationAvailability))
                    .ToArray());
            _integrationAvailability = failedSourceKinds.Count == 0
                ? ResolveActivationIntegrationAvailability()
                : failureAvailability;
            _installationAvailabilityReason = failedSourceKinds.Count == 0
                ? null
                : installationAvailabilityReason;
        }

        DisposeItems(previousItems);
        RetainSelectionAndRefreshVisibleItems();
        NotifyPropertiesChanged(
            nameof(Sources),
            nameof(Items),
            nameof(InstalledCount),
            nameof(IntegrationAvailability),
            nameof(InstallationAvailabilityReason),
            nameof(FailedSourceKinds),
            nameof(HasDiscoveryFailures));
    }

    private void SetRefreshing(long generation, bool value)
    {
        var changed = false;
        var availabilityChanged = false;
        lock (_refreshGate)
        {
            if (_isDisposed || generation != _refreshGeneration)
            {
                return;
            }

            if (_isRefreshing != value)
            {
                _isRefreshing = value;
                changed = true;
            }

            if (value &&
                _integrationAvailability != WallpaperSourceAvailability.Refreshing)
            {
                _integrationAvailability = WallpaperSourceAvailability.Refreshing;
                availabilityChanged = true;
            }
        }

        if (changed)
        {
            NotifyPropertiesChanged(nameof(IsRefreshing));
        }

        if (availabilityChanged)
        {
            NotifyPropertiesChanged(nameof(IntegrationAvailability));
        }
    }

    private void ActivationAvailability_AvailabilityChanged(
        object? sender,
        EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;

        void Update()
        {
            lock (_refreshGate)
            {
                if (_isDisposed)
                {
                    return;
                }
            }

            foreach (var item in Items)
            {
                item.RefreshActivationAvailability();
            }

            var resolvedReferenceItem = Volatile.Read(ref _resolvedReferenceItem);
            resolvedReferenceItem?.RefreshActivationAvailability();
            if (resolvedReferenceItem is not null)
            {
                _resolvedReferenceAvailability = resolvedReferenceItem.Availability;
            }

            var integrationChanged = false;
            lock (_refreshGate)
            {
                if (!_isDisposed &&
                    !_isRefreshing &&
                    _integrationAvailability != WallpaperSourceAvailability.NotLoaded &&
                    _failedSourceKinds.Count == 0)
                {
                    var availability = ResolveActivationIntegrationAvailability();
                    if (_integrationAvailability != availability)
                    {
                        _integrationAvailability = availability;
                        integrationChanged = true;
                    }
                }
            }

            OnPropertyChanged(nameof(DynamicCapability));
            OnPropertyChanged(nameof(ResolvedReferenceAvailability));
            if (integrationChanged)
            {
                OnPropertyChanged(nameof(IntegrationAvailability));
            }
        }

        if (_notificationContext is null ||
            ReferenceEquals(SynchronizationContext.Current, _notificationContext))
        {
            Update();
            return;
        }

        _notificationContext.Post(_ => Update(), state: null);
    }

    private WallpaperSourceAvailability ResolveActivationIntegrationAvailability() =>
        _activationAvailability is ProbedWallpaperSourceActivationAvailability &&
        !DynamicCapability.IsAvailable
            ? WallpaperSourceAvailability.RendererUnavailable
            : WallpaperSourceAvailability.Ready;

    private void RetainSelectionAndRefreshVisibleItems()
    {
        var selected = SelectedItem;
        if (selected is not null)
        {
            SelectedItem = Items.FirstOrDefault(
                item => HasSameIdentity(item.Descriptor, selected.Descriptor));
        }

        RefreshVisibleItems();
    }

    private void RefreshVisibleItems()
    {
        var search = SearchText.Trim();
        _visibleItems = new ReadOnlyCollection<WallpaperSourceItemViewModel>(
            Items.Where(
                    item =>
                        MatchesSearch(item, search) &&
                        MatchesContentFilter(item) &&
                        MatchesOriginFilter(item))
                .ToArray());
        if (SelectedItem is not null && !VisibleItems.Contains(SelectedItem))
        {
            SelectedItem = null;
        }

        NotifyPropertiesChanged(nameof(VisibleItems));
    }

    private static bool MatchesSearch(
        WallpaperSourceItemViewModel item,
        string search) =>
        search.Length == 0 ||
        item.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private bool MatchesContentFilter(WallpaperSourceItemViewModel item) =>
        ContentFilter == WallpaperSourceContentFilter.All ||
        (int)ContentFilter == (int)item.ContentKind;

    private bool MatchesOriginFilter(WallpaperSourceItemViewModel item) =>
        OriginFilter == WallpaperSourceOriginFilter.All ||
        (OriginFilter == WallpaperSourceOriginFilter.Workshop &&
         item.Origin == WallpaperSourceOrigin.Workshop) ||
        (OriginFilter == WallpaperSourceOriginFilter.Local &&
         item.Origin == WallpaperSourceOrigin.Local);

    private static bool HasSameIdentity(
        WallpaperSourceDescriptor left,
        WallpaperSourceDescriptor right) =>
        left.SourceKind == right.SourceKind &&
        SourceIdentifiersEqual(
            left.SourceIdentifier,
            right.SourceIdentifier,
            left.SourceKind);

    private static bool SourceIdentifiersEqual(
        string left,
        string right,
        MediaSourceKind sourceKind) =>
        string.Equals(
            left,
            right,
            sourceKind == MediaSourceKind.WallpaperEngineLocalProject
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static WallpaperContentKind ResolveContentKind(
        MediaReference reference) =>
        reference.LastKnownContentKind switch
        {
            WallpaperContentKind.Image or
            WallpaperContentKind.Video or
            WallpaperContentKind.Scene or
            WallpaperContentKind.Web => reference.LastKnownContentKind,
            _ => reference.LastKnownKind switch
            {
                MediaKind.Image => WallpaperContentKind.Image,
                MediaKind.Video => WallpaperContentKind.Video,
                _ => WallpaperContentKind.Unknown,
            },
        };

    private static WallpaperSourceAvailability MapFailureAvailability(
        Exception exception) => exception switch
        {
            WallpaperEngineUnavailableException
            {
                Reason: WallpaperEngineAvailabilityReason.MultipleInstallations,
            } => WallpaperSourceAvailability.InstallationSelectionRequired,
            WallpaperEngineUnavailableException
            {
                Reason: WallpaperEngineAvailabilityReason.PreferredInstallationInvalid,
            } => WallpaperSourceAvailability.InstallationSelectionInvalid,
            WallpaperEngineUnavailableException =>
                WallpaperSourceAvailability.NotInstalled,
            WallpaperEngineProjectUnavailableException
            {
                Reason: WallpaperEngineProjectUnavailableReason.NotFound,
            } => WallpaperSourceAvailability.ProjectMissing,
            WallpaperEngineProjectUnavailableException
            {
                Reason: WallpaperEngineProjectUnavailableReason.InvalidManifest,
            } => WallpaperSourceAvailability.MetadataInvalid,
            WallpaperEngineProjectUnavailableException =>
                WallpaperSourceAvailability.Unsupported,
            _ => WallpaperSourceAvailability.Stale,
        };

    private static WallpaperSourceAvailability SelectFailureAvailability(
        WallpaperSourceAvailability current,
        WallpaperSourceAvailability candidate) =>
        current == WallpaperSourceAvailability.Ready ? candidate : current;

    private void PublishInstallationSelectionFailure(
        WallpaperEngineAvailabilityReason? reason)
    {
        lock (_refreshGate)
        {
            if (_isDisposed)
            {
                return;
            }

            _installationAvailabilityReason = reason;
            _integrationAvailability = reason switch
            {
                WallpaperEngineAvailabilityReason.MultipleInstallations =>
                    WallpaperSourceAvailability.InstallationSelectionRequired,
                WallpaperEngineAvailabilityReason.PreferredInstallationInvalid =>
                    WallpaperSourceAvailability.InstallationSelectionInvalid,
                null => WallpaperSourceAvailability.InstallationSelectionFailed,
                _ => WallpaperSourceAvailability.NotInstalled,
            };
        }

        NotifyPropertiesChanged(
            nameof(IntegrationAvailability),
            nameof(InstallationAvailabilityReason),
            nameof(HasPreferredWallpaperEngineInstallation));
    }

    private void PublishResolvedReference(
        long generation,
        WallpaperSourceItemViewModel? item,
        WallpaperSourceAvailability? availability)
    {
        WallpaperSourceItemViewModel? previousItem;
        lock (_refreshGate)
        {
            if (_isDisposed || generation != _referenceRefreshGeneration)
            {
                item?.Dispose();
                return;
            }

            previousItem = _resolvedReferenceItem;
            _resolvedReferenceItem = item;
            _resolvedReferenceAvailability = availability;
        }

        if (!ReferenceEquals(previousItem, item))
        {
            previousItem?.Dispose();
        }

        NotifyPropertiesChanged(nameof(ResolvedReferenceAvailability));
    }

    private static void DisposeItems(
        IEnumerable<WallpaperSourceItemViewModel> items)
    {
        foreach (var item in items)
        {
            item.Dispose();
        }
    }

    private void NotifyPropertiesChanged(params string[] propertyNames)
    {
        void Raise()
        {
            foreach (var propertyName in propertyNames)
            {
                OnPropertyChanged(propertyName);
            }
        }

        if (_notificationContext is null ||
            ReferenceEquals(SynchronizationContext.Current, _notificationContext))
        {
            Raise();
            return;
        }

        _notificationContext.Post(_ => Raise(), state: null);
    }

    /// <summary>
    /// Serializes cancellation and disposal for a refresh-owned source. Callers may request
    /// cancellation after releasing the generation gate without racing the refresh's finally.
    /// </summary>
    private sealed class RefreshCancellation : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source;
        private readonly CancellationTokenRegistration _externalCancellation;
        private bool _cancellationStarted;
        private bool _cancellationInProgress;
        private bool _disposeRequested;
        private bool _isDisposed;

        public RefreshCancellation(CancellationToken cancellationToken)
        {
            _source = new CancellationTokenSource();
            Token = _source.Token;
            _externalCancellation = cancellationToken.UnsafeRegister(
                static state => ((RefreshCancellation)state!).Cancel(),
                this);
        }

        public CancellationToken Token { get; }

        public bool IsCancellationRequested => Token.IsCancellationRequested;

        public void Cancel()
        {
            lock (_gate)
            {
                if (_disposeRequested || _cancellationStarted)
                {
                    return;
                }

                _cancellationStarted = true;
                _cancellationInProgress = true;
            }

            try
            {
                try
                {
                    _source.Cancel(throwOnFirstException: false);
                }
                catch (AggregateException)
                {
                    // A provider-owned cancellation callback cannot break refresh cleanup or
                    // surface an expected cancellation as an unhandled ICommand exception.
                }
            }
            finally
            {
                var disposeNow = false;
                lock (_gate)
                {
                    _cancellationInProgress = false;
                    if (_disposeRequested && !_isDisposed)
                    {
                        _isDisposed = true;
                        disposeNow = true;
                    }
                }

                if (disposeNow)
                {
                    DisposeSource();
                }
            }
        }

        public void Dispose()
        {
            var disposeNow = false;
            lock (_gate)
            {
                if (_disposeRequested)
                {
                    return;
                }

                _disposeRequested = true;
                if (!_cancellationInProgress)
                {
                    _isDisposed = true;
                    disposeNow = true;
                }
            }

            if (disposeNow)
            {
                DisposeSource();
            }
        }

        private void DisposeSource()
        {
            _ = _externalCancellation.Unregister();
            _source.Dispose();
        }
    }
}
