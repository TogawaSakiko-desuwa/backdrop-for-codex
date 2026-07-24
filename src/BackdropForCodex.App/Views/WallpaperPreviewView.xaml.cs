using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BackdropForCodex.App.Models;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Settings;
using Wpf.Ui.Appearance;

namespace BackdropForCodex.App.Views;

public sealed partial class WallpaperPreviewView : UserControl
{
    public static readonly DependencyProperty MediaPathProperty =
        DependencyProperty.Register(
            nameof(MediaPath),
            typeof(string),
            typeof(WallpaperPreviewView),
            new PropertyMetadata(null, PreviewMediaPropertyChanged));

    public static readonly DependencyProperty MediaKindProperty =
        DependencyProperty.Register(
            nameof(MediaKind),
            typeof(MediaKind),
            typeof(WallpaperPreviewView),
            new PropertyMetadata(MediaKind.None, PreviewMediaPropertyChanged));

    public static readonly DependencyProperty FitProperty =
        DependencyProperty.Register(
            nameof(Fit),
            typeof(WallpaperFit),
            typeof(WallpaperPreviewView),
            new PropertyMetadata(WallpaperFit.Cover, PreviewLayoutPropertyChanged));

    public static readonly DependencyProperty FocusXProperty =
        DependencyProperty.Register(
            nameof(FocusX),
            typeof(double),
            typeof(WallpaperPreviewView),
            new PropertyMetadata(0.5, PreviewLayoutPropertyChanged));

    public static readonly DependencyProperty FocusYProperty =
        DependencyProperty.Register(
            nameof(FocusY),
            typeof(double),
            typeof(WallpaperPreviewView),
            new PropertyMetadata(0.5, PreviewLayoutPropertyChanged));

    public static readonly DependencyProperty CanAdjustFocusProperty =
        DependencyProperty.Register(
            nameof(CanAdjustFocus),
            typeof(bool),
            typeof(WallpaperPreviewView),
            new PropertyMetadata(false, CanAdjustFocusPropertyChanged));

    public static readonly DependencyProperty IsPlaybackPausedProperty =
        DependencyProperty.Register(
            nameof(IsPlaybackPaused),
            typeof(bool),
            typeof(WallpaperPreviewView),
            new PropertyMetadata(false, PlaybackPropertyChanged));

    public static readonly DependencyProperty DarkOverlayProperty =
        DependencyProperty.Register(
            nameof(DarkOverlay),
            typeof(double),
            typeof(WallpaperPreviewView),
            new PropertyMetadata(0.30, OverlayPropertyChanged));

    public static readonly DependencyProperty LightOverlayProperty =
        DependencyProperty.Register(
            nameof(LightOverlay),
            typeof(double),
            typeof(WallpaperPreviewView),
            new PropertyMetadata(0.18, OverlayPropertyChanged));

    private readonly DispatcherTimer _focusFadeTimer;
    private readonly ISafeMediaPreviewService _previewMedia;
    private ISafeMediaPreviewLease? _previewLease;
    private bool _isDraggingFocus;
    private bool _isThemeSubscribed;
    private bool _mediaRefreshPending;
    private bool _videoPreviewSelected;
    private bool _previewMediaReady;
    private bool _reducedMotion;
    private double _previewMediaWidth;
    private double _previewMediaHeight;
    private string? _previewPath;
    private MediaKind _previewKind;

    public WallpaperPreviewView()
        : this(SafeMediaPreviewService.Shared)
    {
    }

    public WallpaperPreviewView(ISafeMediaPreviewService previewMedia)
    {
        _previewMedia =
            previewMedia ?? throw new ArgumentNullException(nameof(previewMedia));
        InitializeComponent();
        _focusFadeTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(850),
            DispatcherPriority.Background,
            FocusFadeTimer_Tick,
            Dispatcher);
        _focusFadeTimer.Stop();
        Loaded += WallpaperPreviewView_Loaded;
    }

    public event EventHandler<WallpaperFocusChangeRequestedEventArgs>? FocusChangeRequested;

    public string? MediaPath
    {
        get => (string?)GetValue(MediaPathProperty);
        set => SetValue(MediaPathProperty, value);
    }

    public MediaKind MediaKind
    {
        get => (MediaKind)GetValue(MediaKindProperty);
        set => SetValue(MediaKindProperty, value);
    }

    public WallpaperFit Fit
    {
        get => (WallpaperFit)GetValue(FitProperty);
        set => SetValue(FitProperty, value);
    }

    public double FocusX
    {
        get => (double)GetValue(FocusXProperty);
        set => SetValue(FocusXProperty, value);
    }

    public double FocusY
    {
        get => (double)GetValue(FocusYProperty);
        set => SetValue(FocusYProperty, value);
    }

    public bool CanAdjustFocus
    {
        get => (bool)GetValue(CanAdjustFocusProperty);
        set => SetValue(CanAdjustFocusProperty, value);
    }

    public bool IsPlaybackPaused
    {
        get => (bool)GetValue(IsPlaybackPausedProperty);
        set => SetValue(IsPlaybackPausedProperty, value);
    }

    public double DarkOverlay
    {
        get => (double)GetValue(DarkOverlayProperty);
        set => SetValue(DarkOverlayProperty, value);
    }

    public double LightOverlay
    {
        get => (double)GetValue(LightOverlayProperty);
        set => SetValue(LightOverlayProperty, value);
    }

    public double SurfaceMinimumHeight
    {
        get => PreviewSurface.MinHeight;
        set => PreviewSurface.MinHeight = Math.Max(0, value);
    }

    public void SetDropTargetVisible(bool isVisible) =>
        DropOverlay.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

    public void ShowCurrentFocus()
    {
        if (!CanAdjustFocus)
        {
            return;
        }

        _ = FocusInteractionSurface.Focus();
        ShowFocusIndicator(scheduleFade: true);
    }

    public void ReleaseMedia()
    {
        DisconnectThemeNotifications();
        _focusFadeTimer.Stop();
        StopAndClearPreview();
        _previewPath = null;
        _previewKind = MediaKind.None;
    }

    private static void PreviewMediaPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((WallpaperPreviewView)dependencyObject).ScheduleMediaRefresh();
    }

    private static void PreviewLayoutPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        var preview = (WallpaperPreviewView)dependencyObject;
        preview.ApplyPreviewLayout();
        preview.UpdateFocusIndicatorPosition();
    }

    private static void CanAdjustFocusPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        var preview = (WallpaperPreviewView)dependencyObject;
        if (eventArgs.NewValue is false)
        {
            preview.EndFocusDrag();
            preview.HideFocusIndicator();
        }
    }

    private static void PlaybackPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((WallpaperPreviewView)dependencyObject).SynchronizePreviewPlayback();
    }

    private static void OverlayPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((WallpaperPreviewView)dependencyObject).UpdatePreviewThemeOverlay();
    }

    private void WallpaperPreviewView_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_isThemeSubscribed)
        {
            ApplicationThemeManager.Changed += ApplicationThemeManager_Changed;
            _isThemeSubscribed = true;
        }

        RefreshMedia();
        UpdatePreviewThemeOverlay();
        SynchronizePreviewPlayback();
    }

    private void WallpaperPreviewView_Unloaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ReleaseMedia();
    }

    private void WallpaperPreviewView_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        SynchronizePreviewPlayback();
    }

    private void ScheduleMediaRefresh()
    {
        if (!IsLoaded || _mediaRefreshPending)
        {
            return;
        }

        _mediaRefreshPending = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            () =>
            {
                _mediaRefreshPending = false;
                RefreshMedia();
            });
    }

    private void RefreshMedia()
    {
        var path = MediaPath;
        var kind = MediaKind;
        if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase) &&
            _previewKind == kind)
        {
            return;
        }

        _previewPath = path;
        _previewKind = kind;
        StopAndClearPreview();
        if (path is null ||
            kind is not (MediaKind.Image or MediaKind.Video))
        {
            return;
        }

        ISafeMediaPreviewLease? pendingLease = null;
        try
        {
            pendingLease = _previewMedia.Acquire(path);
            if (pendingLease.Metadata.Kind != kind)
            {
                throw new MediaValidationException(
                    "The validated media kind does not match the preview request.");
            }

            if (kind == MediaKind.Video)
            {
                VideoPreview.Source = pendingLease.CreateVideoSource();
                VideoPreview.Position = TimeSpan.Zero;
                VideoPreview.Visibility = Visibility.Visible;
                EmptyPreview.Visibility = Visibility.Collapsed;
                PreviewThemeOverlay.Visibility = Visibility.Visible;
                _videoPreviewSelected = true;
                _previewLease = pendingLease;
                pendingLease = null;
                SetPreviewFallbackBounds(VideoPreview);
                UpdatePreviewThemeOverlay();
                SynchronizePreviewPlayback();
                return;
            }

            var bitmap = pendingLease.LoadBitmap(decodePixelWidth: 1600);
            ImagePreview.Source = bitmap;
            ImagePreview.Visibility = Visibility.Visible;
            EmptyPreview.Visibility = Visibility.Collapsed;
            PreviewThemeOverlay.Visibility = Visibility.Visible;
            _previewMediaWidth = bitmap.PixelWidth;
            _previewMediaHeight = bitmap.PixelHeight;
            _previewMediaReady = true;
            ApplyPreviewLayout();
            UpdatePreviewThemeOverlay();
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure();
        }
        finally
        {
            DisposePreviewLease(pendingLease);
        }
    }

    private void MediaViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_previewMediaReady &&
            _videoPreviewSelected &&
            VideoPreview.Source is not null)
        {
            SetPreviewFallbackBounds(VideoPreview);
        }

        ApplyPreviewLayout();
        UpdateFocusIndicatorPosition();
    }

    private void VideoPreview_MediaOpened(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _previewMediaWidth = VideoPreview.NaturalVideoWidth;
        _previewMediaHeight = VideoPreview.NaturalVideoHeight;
        _previewMediaReady =
            _previewMediaWidth > 0 &&
            _previewMediaHeight > 0;
        if (_previewMediaReady)
        {
            ApplyPreviewLayout();
        }

        if (_reducedMotion)
        {
            try
            {
                VideoPreview.Pause();
            }
            catch (InvalidOperationException)
            {
                // The media graph closed between MediaOpened and the reduced-motion pause.
            }
        }
    }

    private void VideoPreview_MediaEnded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            VideoPreview.Position = TimeSpan.Zero;
            SynchronizePreviewPlayback();
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure();
        }
    }

    private void VideoPreview_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ShowPreviewFailure();
    }

    private void ApplyPreviewLayout()
    {
        if (!_previewMediaReady)
        {
            return;
        }

        var plan = MediaPreviewLayout.CalculateForMedia(
            _previewKind,
            MediaViewport.ActualWidth,
            MediaViewport.ActualHeight,
            _previewMediaWidth,
            _previewMediaHeight,
            Fit,
            FocusX,
            FocusY);
        if (plan.IsEmpty)
        {
            return;
        }

        FrameworkElement previewElement = plan.MediaKind == MediaKind.Video
            ? VideoPreview
            : ImagePreview;
        var placement = plan.Placement;
        previewElement.Width = placement.Width;
        previewElement.Height = placement.Height;
        Canvas.SetLeft(previewElement, placement.OffsetX);
        Canvas.SetTop(previewElement, placement.OffsetY);
    }

    private void SetPreviewFallbackBounds(FrameworkElement previewElement)
    {
        previewElement.Width = Math.Max(0, MediaViewport.ActualWidth);
        previewElement.Height = Math.Max(0, MediaViewport.ActualHeight);
        Canvas.SetLeft(previewElement, 0);
        Canvas.SetTop(previewElement, 0);
    }

    private void ApplicationThemeManager_Changed(
        ApplicationTheme currentApplicationTheme,
        Color systemAccent)
    {
        _ = currentApplicationTheme;
        _ = systemAccent;
        if (Dispatcher.CheckAccess())
        {
            UpdatePreviewThemeOverlay();
            return;
        }

        _ = Dispatcher.BeginInvoke(UpdatePreviewThemeOverlay);
    }

    private void UpdatePreviewThemeOverlay()
    {
        var applicationTheme = ApplicationThemeManager.GetAppTheme();
        var systemTheme = ApplicationThemeManager.GetSystemTheme();
        var overlay = PreviewThemeOverlayResolver.Resolve(
            applicationTheme,
            systemTheme,
            DarkOverlay,
            LightOverlay);
        PreviewThemeOverlay.Background =
            overlay.IsLight ? Brushes.White : Brushes.Black;
        PreviewThemeOverlay.Opacity = overlay.Opacity;
    }

    private void SynchronizePreviewPlayback()
    {
        if (!_videoPreviewSelected || VideoPreview.Source is null)
        {
            return;
        }

        _reducedMotion = !SystemParameters.ClientAreaAnimation;
        try
        {
            if (IsVisible && !IsPlaybackPaused && !_reducedMotion)
            {
                VideoPreview.Play();
            }
            else
            {
                VideoPreview.Pause();
            }
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure();
        }
    }

    private void StopAndClearPreview()
    {
        try
        {
            VideoPreview.Stop();
            VideoPreview.Source = null;
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            // The media graph may already be torn down or may have rejected malformed media.
        }
        finally
        {
            var previewLease = Interlocked.Exchange(ref _previewLease, null);
            DisposePreviewLease(previewLease);
        }

        ImagePreview.Source = null;
        ImagePreview.Visibility = Visibility.Collapsed;
        VideoPreview.Visibility = Visibility.Collapsed;
        PreviewThemeOverlay.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible;
        _previewMediaWidth = 0;
        _previewMediaHeight = 0;
        _previewMediaReady = false;
        _videoPreviewSelected = false;
        _isDraggingFocus = false;
        HideFocusIndicator();
    }

    private void ShowPreviewFailure()
    {
        StopAndClearPreview();
        _previewPath = null;
        _previewKind = MediaKind.None;
    }

    private static void DisposePreviewLease(ISafeMediaPreviewLease? lease)
    {
        if (lease is null)
        {
            return;
        }

        try
        {
            lease.Dispose();
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            // A failed preview must never retain an app-owned media lease.
        }
    }

    private void FocusInteractionSurface_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _ = sender;
        if (!CanAdjustFocus)
        {
            return;
        }

        _isDraggingFocus = true;
        _ = FocusInteractionSurface.Focus();
        _ = FocusInteractionSurface.CaptureMouse();
        RequestFocusFromPointer(e.GetPosition(FocusInteractionSurface));
        ShowFocusIndicator(scheduleFade: false);
        e.Handled = true;
    }

    private void FocusInteractionSurface_MouseMove(object sender, MouseEventArgs e)
    {
        _ = sender;
        if (!_isDraggingFocus)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndFocusDrag();
            return;
        }

        RequestFocusFromPointer(e.GetPosition(FocusInteractionSurface));
        ShowFocusIndicator(scheduleFade: false);
        e.Handled = true;
    }

    private void FocusInteractionSurface_MouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        _ = sender;
        if (!_isDraggingFocus)
        {
            return;
        }

        RequestFocusFromPointer(e.GetPosition(FocusInteractionSurface));
        EndFocusDrag();
        e.Handled = true;
    }

    private void FocusInteractionSurface_LostMouseCapture(
        object sender,
        MouseEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_isDraggingFocus)
        {
            return;
        }

        _isDraggingFocus = false;
        ShowFocusIndicator(scheduleFade: true);
    }

    private void FocusInteractionSurface_KeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (!CanAdjustFocus ||
            !MediaFocusInput.TryGetKeyboardDelta(
                e.Key,
                Keyboard.Modifiers,
                out var delta))
        {
            return;
        }

        RequestFocus(
            Math.Clamp(FocusX + delta.Horizontal, 0, 1),
            Math.Clamp(FocusY + delta.Vertical, 0, 1));
        ShowFocusIndicator(scheduleFade: true);
        e.Handled = true;
    }

    private void RequestFocusFromPointer(Point point)
    {
        if (MediaFocusInput.TryNormalizePointer(
                point.X,
                point.Y,
                FocusInteractionSurface.ActualWidth,
                FocusInteractionSurface.ActualHeight,
                out var focus))
        {
            RequestFocus(focus.X, focus.Y);
        }
    }

    private void RequestFocus(double focusX, double focusY) =>
        FocusChangeRequested?.Invoke(
            this,
            new WallpaperFocusChangeRequestedEventArgs(focusX, focusY));

    private void EndFocusDrag()
    {
        _isDraggingFocus = false;
        if (FocusInteractionSurface.IsMouseCaptured)
        {
            FocusInteractionSurface.ReleaseMouseCapture();
        }

        ShowFocusIndicator(scheduleFade: true);
    }

    private void ShowFocusIndicator(bool scheduleFade)
    {
        if (!CanAdjustFocus)
        {
            HideFocusIndicator();
            return;
        }

        _focusFadeTimer.Stop();
        FocusIndicator.BeginAnimation(OpacityProperty, null);
        FocusIndicator.Opacity = 1;
        UpdateFocusIndicatorPosition();
        if (scheduleFade && !_isDraggingFocus)
        {
            _focusFadeTimer.Start();
        }
    }

    private void HideFocusIndicator()
    {
        _focusFadeTimer.Stop();
        FocusIndicator.BeginAnimation(OpacityProperty, null);
        FocusIndicator.Opacity = 0;
    }

    private void FocusFadeTimer_Tick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        _focusFadeTimer.Stop();
        if (!SystemParameters.ClientAreaAnimation)
        {
            HideFocusIndicator();
            return;
        }

        FocusIndicator.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                fromValue: FocusIndicator.Opacity,
                toValue: 0,
                duration: TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseOut,
                },
            });
    }

    private void UpdateFocusIndicatorPosition()
    {
        if (FocusInteractionSurface.ActualWidth <= 0 ||
            FocusInteractionSurface.ActualHeight <= 0)
        {
            return;
        }

        Canvas.SetLeft(
            FocusIndicator,
            (FocusX * FocusInteractionSurface.ActualWidth) -
            (FocusIndicator.Width / 2));
        Canvas.SetTop(
            FocusIndicator,
            (FocusY * FocusInteractionSurface.ActualHeight) -
            (FocusIndicator.Height / 2));
    }

    private void DisconnectThemeNotifications()
    {
        if (!_isThemeSubscribed)
        {
            return;
        }

        ApplicationThemeManager.Changed -= ApplicationThemeManager_Changed;
        _isThemeSubscribed = false;
    }

    private static bool IsControlledPreviewException(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        FormatException or
        ArgumentException or
        MediaReferenceValidationException or
        COMException or
        SecurityException;
}

public sealed class WallpaperFocusChangeRequestedEventArgs(double focusX, double focusY)
    : EventArgs
{
    public double FocusX { get; } = focusX;

    public double FocusY { get; } = focusY;
}
