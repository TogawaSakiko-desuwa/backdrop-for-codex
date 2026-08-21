using System.IO;
using System.Windows.Media.Imaging;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Dynamic;
using BackdropForCodex.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackdropForCodex.App.ViewModels;

public enum WallpaperSourceAvailability
{
    NotLoaded = 0,
    Ready,
    Refreshing,
    NotInstalled,
    InstallationSelectionRequired,
    InstallationSelectionInvalid,
    InstallationSelectionFailed,
    ProjectMissing,
    MetadataInvalid,
    Unsupported,
    RendererUnavailable,
    Stale,
}

public enum WallpaperSourceContentFilter
{
    All = 0,
    Image,
    Video,
    Scene,
    Web,
}

public enum WallpaperSourceOriginFilter
{
    All = 0,
    Workshop,
    Local,
}

public enum WallpaperSourceOrigin
{
    Workshop = 0,
    Local,
}

public interface IWallpaperSourceActivationAvailability
{
    event EventHandler? AvailabilityChanged;

    bool CanActivate(WallpaperSourceDescriptor descriptor);
}

internal sealed class DirectWallpaperSourceActivationAvailability
    : IWallpaperSourceActivationAvailability
{
    public static DirectWallpaperSourceActivationAvailability Instance { get; } = new();

    public event EventHandler? AvailabilityChanged
    {
        add { }
        remove { }
    }

    public bool CanActivate(WallpaperSourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.DeliveryKind == WallpaperDeliveryKind.DirectMedia;
    }
}

internal sealed class ProbedWallpaperSourceActivationAvailability(
    IDynamicWallpaperCapabilitySource capabilitySource)
    : IWallpaperSourceActivationAvailability
{
    private readonly IDynamicWallpaperCapabilitySource _capabilitySource =
        capabilitySource ?? throw new ArgumentNullException(nameof(capabilitySource));
    private DynamicWallpaperCapability _capability = DynamicWallpaperCapability.Unavailable(
        DynamicWallpaperCapabilityReasonCode.WallpaperEngineUnavailable);
    private long _probeGeneration;

    public event EventHandler? AvailabilityChanged;

    internal DynamicWallpaperCapability Capability => Volatile.Read(ref _capability);

    public bool CanActivate(WallpaperSourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.DeliveryKind == WallpaperDeliveryKind.DirectMedia ||
            (descriptor.DeliveryKind == WallpaperDeliveryKind.WallpaperEngineWindow &&
             Capability.IsAvailable);
    }

    internal async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _probeGeneration);
        DynamicWallpaperCapability capability;
        try
        {
            capability = await _capabilitySource
                .ProbeDynamicWallpaperAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DynamicWallpaperUnavailableException exception)
        {
            capability = DynamicWallpaperCapability.Unavailable(exception.ReasonCode);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or AccessViolationException))
        {
            capability = DynamicWallpaperCapability.Unavailable(
                DynamicWallpaperCapabilityReasonCode.WallpaperEngineUnavailable);
        }

        if (generation == Volatile.Read(ref _probeGeneration))
        {
            UpdateCapability(capability);
        }
    }

    private void UpdateCapability(DynamicWallpaperCapability capability)
    {
        var previous = Interlocked.Exchange(ref _capability, capability);
        if (previous != capability)
        {
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class WallpaperSourceItemViewModel : ObservableObject, IDisposable
{
    private readonly IWallpaperThumbnailPreviewService _thumbnailPreview;
    private readonly IWallpaperSourceActivationAvailability _activationAvailability;
    private CancellationTokenSource? _thumbnailCancellation;
    private WallpaperSourceAvailability _baseAvailability;
    private WallpaperSourceAvailability _availability;
    private BitmapSource? _thumbnail;
    private bool _isThumbnailLoading;
    private bool _isThumbnailUnavailable;
    private bool _isDisposed;

    internal WallpaperSourceItemViewModel(
        WallpaperSourceDescriptor descriptor,
        WallpaperSourceAvailability availability,
        IWallpaperThumbnailPreviewService thumbnailPreview,
        IWallpaperSourceActivationAvailability activationAvailability)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _baseAvailability = availability;
        _thumbnailPreview = thumbnailPreview ??
            throw new ArgumentNullException(nameof(thumbnailPreview));
        _activationAvailability = activationAvailability ??
            throw new ArgumentNullException(nameof(activationAvailability));
        Origin = descriptor.SourceKind switch
        {
            MediaSourceKind.WallpaperEngineWorkshopProject =>
                WallpaperSourceOrigin.Workshop,
            MediaSourceKind.WallpaperEngineLocalProject =>
                WallpaperSourceOrigin.Local,
            _ => throw new ArgumentOutOfRangeException(nameof(descriptor)),
        };
        Reference = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = descriptor.SourceKind,
            SourceIdentifier = descriptor.SourceIdentifier,
            LastKnownKind = descriptor.ContentKind switch
            {
                WallpaperContentKind.Image => MediaKind.Image,
                WallpaperContentKind.Video => MediaKind.Video,
                _ => MediaKind.None,
            },
            LastKnownContentKind = descriptor.ContentKind,
            LastKnownDisplayName = descriptor.DisplayName,
        }.Snapshot();
        _availability = ResolveAvailability();
    }

    public WallpaperSourceDescriptor Descriptor { get; }

    public MediaReference Reference { get; }

    public string DisplayName => Descriptor.DisplayName;

    public WallpaperContentKind ContentKind => Descriptor.ContentKind;

    public WallpaperSourceOrigin Origin { get; }

    public bool IsStaticPreview =>
        ContentKind is WallpaperContentKind.Scene or WallpaperContentKind.Web;

    public WallpaperSourceAvailability Availability
    {
        get => _availability;
        private set
        {
            if (SetProperty(ref _availability, value))
            {
                OnPropertyChanged(nameof(CanAssign));
                OnPropertyChanged(nameof(CanApply));
            }
        }
    }

    public bool CanAssign =>
        Availability is WallpaperSourceAvailability.Ready or
            WallpaperSourceAvailability.RendererUnavailable;

    public bool CanApply =>
        Availability == WallpaperSourceAvailability.Ready &&
        _activationAvailability.CanActivate(Descriptor);

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }

    public bool IsThumbnailLoading
    {
        get => _isThumbnailLoading;
        private set => SetProperty(ref _isThumbnailLoading, value);
    }

    public bool IsThumbnailUnavailable
    {
        get => _isThumbnailUnavailable;
        private set => SetProperty(ref _isThumbnailUnavailable, value);
    }

    public async Task EnsureThumbnailAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || Thumbnail is not null || IsThumbnailLoading)
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _thumbnailCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        IsThumbnailLoading = true;
        IsThumbnailUnavailable = false;
        try
        {
            var thumbnail = await _thumbnailPreview
                .LoadAsync(Reference, decodePixelWidth: 240, cancellation.Token)
                .ConfigureAwait(true);
            if (!ReferenceEquals(
                    Volatile.Read(ref _thumbnailCancellation),
                    cancellation) ||
                cancellation.IsCancellationRequested)
            {
                return;
            }

            Thumbnail = thumbnail;
            IsThumbnailUnavailable = thumbnail is null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (IsExpectedThumbnailFailure(exception))
        {
            IsThumbnailUnavailable = true;
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _thumbnailCancellation,
                        null,
                        cancellation),
                    cancellation))
            {
                IsThumbnailLoading = false;
                cancellation.Dispose();
            }
        }
    }

    public void CancelThumbnailLoad()
    {
        var cancellation = Interlocked.Exchange(ref _thumbnailCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        IsThumbnailLoading = false;
    }

    internal void RefreshActivationAvailability()
    {
        Availability = ResolveAvailability();
        OnPropertyChanged(nameof(CanApply));
    }

    internal void MarkStale()
    {
        _baseAvailability = WallpaperSourceAvailability.Stale;
        Availability = WallpaperSourceAvailability.Stale;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelThumbnailLoad();
        Thumbnail = null;
        GC.SuppressFinalize(this);
    }

    private WallpaperSourceAvailability ResolveAvailability()
    {
        if (_baseAvailability != WallpaperSourceAvailability.Ready)
        {
            return _baseAvailability;
        }

        return Descriptor.DeliveryKind == WallpaperDeliveryKind.WallpaperEngineWindow &&
            !_activationAvailability.CanActivate(Descriptor)
                ? WallpaperSourceAvailability.RendererUnavailable
                : WallpaperSourceAvailability.Ready;
    }

    private static bool IsExpectedThumbnailFailure(Exception exception) => exception is
        WallpaperEngineUnavailableException or
        WallpaperEngineProjectUnavailableException or
        WallpaperSourceCapabilityException or
        IOException or
        FormatException or
        UnauthorizedAccessException or
        NotSupportedException or
        ArgumentException;
}
