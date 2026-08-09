using System.Collections.ObjectModel;
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

    private readonly IWallpaperSourceProviderRegistry _sourceRegistry;
    private readonly SynchronizationContext? _notificationContext;
    private readonly object _refreshGate = new();
    private IReadOnlyList<WallpaperSourceDescriptor> _sources = EmptySources;
    private IReadOnlyList<MediaSourceKind> _failedSourceKinds = EmptyFailures;
    private RefreshCancellation? _activeRefresh;
    private long _refreshGeneration;
    private bool _isRefreshing;
    private bool _isDisposed;

    public WallpaperSourceLibraryViewModel(
        IWallpaperSourceProviderRegistry sourceRegistry)
    {
        _sourceRegistry = sourceRegistry ??
            throw new ArgumentNullException(nameof(sourceRegistry));
        _notificationContext = SynchronizationContext.Current;
        RefreshCommand = new AsyncRelayCommand(RefreshFromCommandAsync);
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IReadOnlyList<WallpaperSourceDescriptor> Sources => _sources;

    /// <summary>
    /// Provider namespaces whose latest discovery failed. Exception details deliberately remain
    /// behind the provider boundary; the UI only needs enough state to offer a retry.
    /// </summary>
    public IReadOnlyList<MediaSourceKind> FailedSourceKinds => _failedSourceKinds;

    public bool HasDiscoveryFailures => FailedSourceKinds.Count != 0;

    public bool IsRefreshing => _isRefreshing;

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
            var discoveredSources = new List<WallpaperSourceDescriptor>();
            var failedSourceKinds = new List<MediaSourceKind>();
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
                        providerSources);
                    discoveredSources.AddRange(
                        providerSnapshot.Where(IsSupportedLibrarySource));
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
                }
            }

            refreshCancellation.Token.ThrowIfCancellationRequested();
            PublishSnapshot(
                generation,
                discoveredSources,
                failedSourceKinds);
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

    public void Dispose()
    {
        RefreshCancellation? activeRefresh;
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
            if (_isRefreshing)
            {
                _isRefreshing = false;
                notifyRefreshing = true;
            }
        }

        CancelIfPending(activeRefresh);
        if (notifyRefreshing)
        {
            NotifyPropertiesChanged(nameof(IsRefreshing));
        }

        GC.SuppressFinalize(this);
    }

    private static bool IsSupportedLibrarySource(
        WallpaperSourceDescriptor descriptor) =>
        descriptor.DeliveryKind != WallpaperDeliveryKind.Unsupported &&
        descriptor.ContentKind is
            WallpaperContentKind.Image or
            WallpaperContentKind.Video or
            WallpaperContentKind.Scene or
            WallpaperContentKind.Web;

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
        IReadOnlyCollection<WallpaperSourceDescriptor> sources,
        IReadOnlyCollection<MediaSourceKind> failedSourceKinds)
    {
        var sourceSnapshot = new ReadOnlyCollection<WallpaperSourceDescriptor>(
            sources.ToArray());
        var failureSnapshot = new ReadOnlyCollection<MediaSourceKind>(
            failedSourceKinds.ToArray());
        lock (_refreshGate)
        {
            if (_isDisposed ||
                generation != _refreshGeneration ||
                _activeRefresh is null)
            {
                return;
            }

            _sources = sourceSnapshot;
            _failedSourceKinds = failureSnapshot;
        }

        NotifyPropertiesChanged(
            nameof(Sources),
            nameof(FailedSourceKinds),
            nameof(HasDiscoveryFailures));
    }

    private void SetRefreshing(long generation, bool value)
    {
        var changed = false;
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
        }

        if (changed)
        {
            NotifyPropertiesChanged(nameof(IsRefreshing));
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
