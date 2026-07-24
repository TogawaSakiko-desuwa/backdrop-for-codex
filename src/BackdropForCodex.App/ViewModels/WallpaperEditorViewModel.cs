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
    private string? _selectedMediaPath;
    private MediaKind _selectedMediaKind;
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
        _previewMedia = previewMedia ?? SafeMediaPreviewService.Shared;
    }

    /// <summary>
    /// Raised once after an editable value changes, including a batched settings hydration.
    /// </summary>
    public event EventHandler? DraftChanged;

    public string? SelectedMediaPath
    {
        get => _selectedMediaPath;
        private set
        {
            if (SetProperty(ref _selectedMediaPath, value))
            {
                OnPropertyChanged(nameof(SelectedMediaName));
                OnPropertyChanged(nameof(HasSelectedMedia));
                OnPropertyChanged(nameof(IsVideoSelected));
                OnPropertyChanged(nameof(CanAdjustFocus));
                NotifyDraftChanged();
            }
        }
    }

    public string SelectedMediaName => SelectedMediaPath is null
        ? Text("Media_None", "No media selected")
        : Path.GetFileName(SelectedMediaPath);

    public bool HasSelectedMedia => SelectedMediaPath is not null;

    public MediaKind SelectedMediaKind
    {
        get => _selectedMediaKind;
        private set
        {
            if (SetProperty(ref _selectedMediaKind, value))
            {
                OnPropertyChanged(nameof(IsVideoSelected));
                NotifyDraftChanged();
            }
        }
    }

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

    public bool CanAdjustFocus => _isEditingEnabled && HasSelectedMedia && IsCoverFit;

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

    public string FocusLabel => $"{FocusX:P0}, {FocusY:P0}";

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
                NotifyDraftChanged();
            }
        }
    }

    public void SelectMedia(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        var normalizedPath = Path.GetFullPath(mediaPath);
        var kind = InferMediaKind(normalizedPath);
        if (kind == MediaKind.None)
        {
            throw new MediaValidationException("The selected extension is not supported.");
        }

        RunBatch(
            () =>
            {
                SelectedMediaKind = kind;
                SelectedMediaPath = normalizedPath;
                IsMediaMissing = !_previewMedia.IsAvailable(normalizedPath);
            });
    }

    public void ApplySettings(SettingsV1 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        RunBatch(
            () =>
            {
                SelectedMediaKind = settings.MediaKind;
                SelectedMediaPath = settings.MediaPath;
                Fit = settings.Fit;
                FocusX = settings.FocusX;
                FocusY = settings.FocusY;
                PanelOpacity = settings.PanelOpacity;
                BlurPx = settings.BlurPx;
                DarkOverlay = settings.DarkOverlay;
                LightOverlay = settings.LightOverlay;
                AcceptedCdpRisk = settings.AcceptedCdpRisk;
                IsMediaMissing =
                    settings.MediaPath is not null &&
                    !_previewMedia.IsAvailable(settings.MediaPath);
            });
    }

    public SettingsV1 ProjectOnto(SettingsV1 baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        return baseline with
        {
            MediaPath = SelectedMediaPath,
            MediaKind = SelectedMediaPath is null ? MediaKind.None : SelectedMediaKind,
            Fit = Fit,
            FocusX = FocusX,
            FocusY = FocusY,
            PanelOpacity = PanelOpacity,
            BlurPx = BlurPx,
            DarkOverlay = DarkOverlay,
            LightOverlay = LightOverlay,
            AcceptedCdpRisk = AcceptedCdpRisk,
        };
    }

    public void SetFocus(double focusX, double focusY)
    {
        FocusX = focusX;
        FocusY = focusY;
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

    internal static SettingsV1 ClampLegacyOverlays(SettingsV1 settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings with
        {
            DarkOverlay = ClampOverlay(settings.DarkOverlay),
            LightOverlay = ClampOverlay(settings.LightOverlay),
        };
    }

    private static double ClampOverlay(double value) =>
        Math.Clamp(value, 0, MaximumOverlay);

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

    private string Text(string key, string fallback)
    {
        var localized = _text.GetString(key);
        return string.Equals(localized, key, StringComparison.Ordinal)
            ? fallback
            : localized;
    }
}
