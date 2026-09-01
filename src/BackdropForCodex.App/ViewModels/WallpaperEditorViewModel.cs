using System.IO;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BackdropForCodex.App.ViewModels;

/// <summary>
/// Owns the editable wallpaper draft and its projection onto persisted settings.
/// Runtime, persistence, and operation state remain the responsibility of the parent view model.
/// </summary>
public sealed class WallpaperEditorViewModel : ObservableObject
{
    public const double MaximumOverlay = 0.60;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly string[] VideoExtensions = [".mp4", ".webm"];

    private readonly IAppTextProvider _text;
    private readonly ISafeMediaPreviewService _previewMedia;
    private MediaReference? _selectedMediaReference;
    private string? _selectedMediaDisplayName;
    private WallpaperFit _fit = WallpaperFit.Cover;
    private double _focusX = 0.5;
    private double _focusY = 0.5;
    private double _panelOpacity = 0.78;
    private double _blurPx = 14;
    private double _darkOverlay = 0.30;
    private double _lightOverlay = 0.18;
    private bool _acceptedCdpRisk;
    private bool _isMediaMissing;
    private bool _isEditingEnabled = true;
    private bool _isApplyingSettings;

    public WallpaperEditorViewModel(
        IAppTextProvider text,
        ISafeMediaPreviewService? previewMedia = null)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _previewMedia = previewMedia ?? AppWallpaperSources.Preview;
    }

    /// <summary>
    /// Raised once after an editable value changes, including a batched settings hydration.
    /// </summary>
    public event EventHandler? DraftChanged;

    /// <summary>
    /// Canonical source identity for the current draft. A new immutable snapshot is returned so
    /// view code never receives the editor's retained instance.
    /// </summary>
    public MediaReference? SelectedMediaReference =>
        _selectedMediaReference?.Snapshot();

    public string? SelectedMediaIdentifier =>
        _selectedMediaReference?.SourceIdentifier;

    /// <summary>
    /// Compatibility surface for local file pickers. Provider identifiers must use
    /// <see cref="SelectedMediaReference"/> instead.
    /// </summary>
    public string? SelectedMediaPath =>
        _selectedMediaReference?.SourceKind == MediaSourceKind.LocalFile
            ? _selectedMediaReference.SourceIdentifier
            : null;

    public string SelectedMediaName => _selectedMediaReference is null
        ? _text.GetStringOrFallback("Media_None", "No media selected")
        : _selectedMediaDisplayName ?? GetDefaultDisplayName(_selectedMediaReference);

    public bool HasSelectedMedia => _selectedMediaReference is not null;

    public MediaKind SelectedMediaKind =>
        _selectedMediaReference?.LastKnownKind ?? MediaKind.None;

    public bool IsVideoSelected => SelectedMediaKind == MediaKind.Video;

    public bool IsMediaMissing
    {
        get => _isMediaMissing;
        private set => SetProperty(ref _isMediaMissing, value);
    }

    public WallpaperFit Fit
    {
        get => _fit;
        set
        {
            if (SetProperty(ref _fit, value))
            {
                OnPropertyChanged(nameof(IsCoverFit));
                OnPropertyChanged(nameof(CanAdjustFocus));
                NotifyDraftChanged();
            }
        }
    }

    public bool IsCoverFit => Fit == WallpaperFit.Cover;

    public bool CanAdjustFocus =>
        _isEditingEnabled &&
        SelectedMediaKind is MediaKind.Image or MediaKind.Video &&
        IsCoverFit;

    public bool IsPreviewUnavailable =>
        HasSelectedMedia && SelectedMediaKind == MediaKind.None;

    public bool IsEditingEnabled => _isEditingEnabled;

    public double FocusX
    {
        get => _focusX;
        set
        {
            if (SetProperty(ref _focusX, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(FocusLabel));
                NotifyDraftChanged();
            }
        }
    }

    public double FocusY
    {
        get => _focusY;
        set
        {
            if (SetProperty(ref _focusY, Math.Clamp(value, 0, 1)))
            {
                OnPropertyChanged(nameof(FocusLabel));
                NotifyDraftChanged();
            }
        }
    }

    public string FocusLabel => $"{FocusX:P0}·{FocusY:P0}";

    public double PanelOpacity
    {
        get => _panelOpacity;
        set
        {
            if (SetProperty(ref _panelOpacity, value))
            {
                OnPropertyChanged(nameof(PanelOpacityPercent));
                NotifyDraftChanged();
            }
        }
    }

    public string PanelOpacityPercent => $"{PanelOpacity:P0}";

    public double BlurPx
    {
        get => _blurPx;
        set
        {
            if (SetProperty(ref _blurPx, value))
            {
                OnPropertyChanged(nameof(BlurLabel));
                NotifyDraftChanged();
            }
        }
    }

    public string BlurLabel => $"{BlurPx:N0} px";

    public double DarkOverlay
    {
        get => _darkOverlay;
        set
        {
            if (SetProperty(ref _darkOverlay, ClampOverlay(value)))
            {
                OnPropertyChanged(nameof(DarkOverlayPercent));
                NotifyDraftChanged();
            }
        }
    }

    public string DarkOverlayPercent => $"{DarkOverlay:P0}";

    public double LightOverlay
    {
        get => _lightOverlay;
        set
        {
            if (SetProperty(ref _lightOverlay, ClampOverlay(value)))
            {
                OnPropertyChanged(nameof(LightOverlayPercent));
                NotifyDraftChanged();
            }
        }
    }

    public string LightOverlayPercent => $"{LightOverlay:P0}";

    public bool AcceptedCdpRisk
    {
        get => _acceptedCdpRisk;
        private set
        {
            if (SetProperty(ref _acceptedCdpRisk, value))
            {
                OnPropertyChanged(nameof(RequiresCdpRisk));
                NotifyDraftChanged();
            }
        }
    }

    public bool RequiresCdpRisk => HasSelectedMedia && !AcceptedCdpRisk;

    public void SelectMedia(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        var normalizedPath = Path.GetFullPath(mediaPath);
        var kind = InferMediaKind(normalizedPath);
        if (kind == MediaKind.None)
        {
            throw new MediaValidationException("The selected extension is not supported.");
        }

        SelectMediaReference(
            new MediaReference
            {
                MediaId = Guid.CreateVersion7(),
                SourceKind = MediaSourceKind.LocalFile,
                SourceIdentifier = normalizedPath,
                LastKnownKind = kind,
                LastKnownContentKind = kind == MediaKind.Image
                    ? WallpaperContentKind.Image
                    : WallpaperContentKind.Video,
                LastKnownDisplayName = Path.GetFileName(normalizedPath),
            },
            Path.GetFileName(normalizedPath));
    }

    /// <summary>
    /// Selects a discovered provider source without interpreting its identifier as a file path.
    /// Scene and Web sources remain durable dynamic selections while intentionally carrying no
    /// direct-media kind; their static workbench preview is independent from runtime activation.
    /// </summary>
    public void SelectSource(WallpaperSourceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.DeliveryKind == WallpaperDeliveryKind.Unsupported)
        {
            throw new WallpaperContentNotSupportedException(descriptor);
        }

        SelectMediaReference(
            new MediaReference
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
            },
            descriptor.DisplayName);
    }

    public void SelectMediaReference(
        MediaReference reference,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var snapshot = reference.Snapshot();
        RunBatch(
            () =>
            {
                SetSelectedMediaReference(snapshot, displayName);
                IsMediaMissing = IsDirectMedia(snapshot) &&
                    !_previewMedia.IsAvailable(snapshot);
            });
    }

    public void ApplySettings(SettingsV3 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = settings.CreateSnapshot();
        var profile = snapshot.ResolveProfile(SemanticRegion.Global);
        var media = profile.MediaId is { } mediaId
            ? snapshot.FindMedia(mediaId)
            : null;

        RunBatch(
            () =>
            {
                SetSelectedMediaReference(media, displayName: null);
                Fit = profile.Fit;
                FocusX = profile.FocusX;
                FocusY = profile.FocusY;
                PanelOpacity = profile.PanelOpacity;
                BlurPx = profile.BlurPx;
                DarkOverlay = profile.DarkOverlay;
                LightOverlay = profile.LightOverlay;
                AcceptedCdpRisk = snapshot.AcceptedCdpRisk;
                IsMediaMissing =
                    media is not null &&
                    IsDirectMedia(media) &&
                    !_previewMedia.IsAvailable(media);
            });
    }

    public SettingsV3 ProjectOnto(SettingsV3 baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var snapshot = baseline.CreateSnapshot();
        var selectedProfile = snapshot.ResolveProfile(SemanticRegion.Global);
        var mediaCatalog = snapshot.MediaCatalog.ToList();
        Guid? mediaId = null;

        if (_selectedMediaReference is { } selectedReference)
        {
            var selected = selectedReference.Snapshot();
            var existing = mediaCatalog.FirstOrDefault(
                media =>
                    media.SourceKind == selected.SourceKind &&
                    SourceIdentifiersEqual(media, selected));
            if (existing is null)
            {
                existing = selected;
                mediaCatalog.Add(existing);
            }
            else if (existing.LastKnownKind != selected.LastKnownKind ||
                     existing.LastKnownContentKind != selected.LastKnownContentKind ||
                     !string.Equals(
                         existing.LastKnownDisplayName,
                         selected.LastKnownDisplayName,
                         StringComparison.Ordinal))
            {
                var index = mediaCatalog.IndexOf(existing);
                existing = existing with
                {
                    LastKnownKind = selected.LastKnownKind,
                    LastKnownContentKind = selected.LastKnownContentKind,
                    LastKnownDisplayName = selected.LastKnownDisplayName,
                };
                mediaCatalog[index] = existing;
            }

            mediaId = existing.MediaId;
        }

        var updatedProfile = selectedProfile with
        {
            MediaId = mediaId,
            Fit = Fit,
            FocusX = FocusX,
            FocusY = FocusY,
            PanelOpacity = PanelOpacity,
            BlurPx = BlurPx,
            DarkOverlay = DarkOverlay,
            LightOverlay = LightOverlay,
        };
        var candidate = snapshot with
        {
            Profiles = snapshot.Profiles
                .Select(
                    profile => profile.ProfileId == selectedProfile.ProfileId
                        ? updatedProfile
                        : profile)
                .ToArray(),
            MediaCatalog = mediaCatalog.ToArray(),
            AcceptedCdpRisk = AcceptedCdpRisk,
        };
        return candidate.CreateSnapshot();
    }

    public void SetFocus(double focusX, double focusY)
    {
        if (!double.IsFinite(focusX) || !double.IsFinite(focusY))
        {
            return;
        }

        var normalizedX = Math.Clamp(focusX, 0, 1);
        var normalizedY = Math.Clamp(focusY, 0, 1);
        if (_focusX.Equals(normalizedX) && _focusY.Equals(normalizedY))
        {
            return;
        }

        RunBatch(
            () =>
            {
                FocusX = normalizedX;
                FocusY = normalizedY;
            });
    }

    public void ResetFocus() => SetFocus(0.5, 0.5);

    public void NudgeFocus(double horizontalDelta, double verticalDelta) =>
        SetFocus(FocusX + horizontalDelta, FocusY + verticalDelta);

    internal void SetRiskAccepted(bool accepted) => AcceptedCdpRisk = accepted;

    internal void SetEditingEnabled(bool enabled)
    {
        if (_isEditingEnabled == enabled)
        {
            return;
        }

        _isEditingEnabled = enabled;
        OnPropertyChanged(nameof(IsEditingEnabled));
        OnPropertyChanged(nameof(CanAdjustFocus));
    }

    internal static MediaKind InferMediaKind(string path)
    {
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return MediaKind.Image;
        }

        return VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? MediaKind.Video
            : MediaKind.None;
    }

    private static double ClampOverlay(double value) =>
        Math.Clamp(value, 0, MaximumOverlay);

    private void SetSelectedMediaReference(
        MediaReference? reference,
        string? displayName)
    {
        var snapshot = reference?.Snapshot();
        var normalizedDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? snapshot?.LastKnownDisplayName
            : displayName.Trim();
        if (Equals(_selectedMediaReference, snapshot) &&
            string.Equals(
                _selectedMediaDisplayName,
                normalizedDisplayName,
                StringComparison.Ordinal))
        {
            return;
        }

        _selectedMediaReference = snapshot;
        _selectedMediaDisplayName = normalizedDisplayName;
        OnPropertyChanged(nameof(SelectedMediaReference));
        OnPropertyChanged(nameof(SelectedMediaIdentifier));
        OnPropertyChanged(nameof(SelectedMediaPath));
        OnPropertyChanged(nameof(SelectedMediaName));
        OnPropertyChanged(nameof(HasSelectedMedia));
        OnPropertyChanged(nameof(SelectedMediaKind));
        OnPropertyChanged(nameof(IsVideoSelected));
        OnPropertyChanged(nameof(IsPreviewUnavailable));
        OnPropertyChanged(nameof(CanAdjustFocus));
        OnPropertyChanged(nameof(RequiresCdpRisk));
        NotifyDraftChanged();
    }

    private string GetDefaultDisplayName(MediaReference reference)
    {
        if (reference.SourceKind is MediaSourceKind.LocalFile or
            MediaSourceKind.WallpaperEngineLocalProject)
        {
            var fileName = Path.GetFileName(reference.SourceIdentifier);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return reference.LastKnownKind switch
        {
            MediaKind.Image => _text.GetStringOrFallback("Media_Image", "Image"),
            MediaKind.Video => _text.GetStringOrFallback("Media_Video", "Video"),
            _ => _text.GetStringOrFallback("Profile_Media", "Media"),
        };
    }

    private static bool IsDirectMedia(MediaReference reference) =>
        reference.LastKnownKind is MediaKind.Image or MediaKind.Video;

    private static bool SourceIdentifiersEqual(
        MediaReference left,
        MediaReference right) =>
        string.Equals(
            left.SourceIdentifier,
            right.SourceIdentifier,
            left.SourceKind is MediaSourceKind.LocalFile or
                MediaSourceKind.WallpaperEngineLocalProject
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private void RunBatch(Action update)
    {
        _isApplyingSettings = true;
        try
        {
            update();
        }
        finally
        {
            _isApplyingSettings = false;
        }

        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyDraftChanged()
    {
        if (!_isApplyingSettings)
        {
            DraftChanged?.Invoke(this, EventArgs.Empty);
        }
    }

}
