using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BackdropForCodex.App.Converters;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.App.Views;

/// <summary>
/// Draws a cover-fitted, rounded thumbnail for a wallpaper reference. Provider thumbnails come
/// from the shared thumbnail service; local images use the bounded in-memory decoder and local
/// videos render one still frame. Loading is best effort: a missing thumbnail leaves the
/// placeholder fill in place.
/// </summary>
public sealed class MediaThumbnail : FrameworkElement
{
    private const int DecodePixelWidth = 192;

    public static readonly DependencyProperty ReferenceProperty =
        DependencyProperty.Register(
            nameof(Reference),
            typeof(MediaReference),
            typeof(MediaThumbnail),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty LocalPathProperty =
        DependencyProperty.Register(
            nameof(LocalPath),
            typeof(string),
            typeof(MediaThumbnail),
            new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(double),
            typeof(MediaThumbnail),
            new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(
            nameof(Fill),
            typeof(Brush),
            typeof(MediaThumbnail),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(
            nameof(Stroke),
            typeof(Brush),
            typeof(MediaThumbnail),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyPropertyKey HasImagePropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasImage),
            typeof(bool),
            typeof(MediaThumbnail),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasImageProperty =
        HasImagePropertyKey.DependencyProperty;

    private static readonly MediaThumbnailConverter LocalThumbnails = new();

    private BitmapSource? _bitmap;
    private int _loadGeneration;

    public MediaThumbnail()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SnapsToDevicePixels = true;
        Loaded += MediaThumbnail_Loaded;
        Unloaded += MediaThumbnail_Unloaded;
    }

    public MediaReference? Reference
    {
        get => (MediaReference?)GetValue(ReferenceProperty);
        set => SetValue(ReferenceProperty, value);
    }

    public string? LocalPath
    {
        get => (string?)GetValue(LocalPathProperty);
        set => SetValue(LocalPathProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public bool HasImage
    {
        get => (bool)GetValue(HasImageProperty);
        private set => SetValue(HasImagePropertyKey, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        base.OnRender(drawingContext);
        var bounds = new Rect(RenderSize);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var radius = Math.Max(
            0,
            Math.Min(CornerRadius, Math.Min(bounds.Width, bounds.Height) / 2));
        if (Fill is { } fill)
        {
            drawingContext.DrawRoundedRectangle(fill, null, bounds, radius, radius);
        }

        if (_bitmap is { PixelWidth: > 0, PixelHeight: > 0 } bitmap)
        {
            var scale = Math.Max(
                bounds.Width / bitmap.PixelWidth,
                bounds.Height / bitmap.PixelHeight);
            var width = bitmap.PixelWidth * scale;
            var height = bitmap.PixelHeight * scale;
            var target = new Rect(
                (bounds.Width - width) / 2,
                (bounds.Height - height) / 2,
                width,
                height);
            drawingContext.PushClip(new RectangleGeometry(bounds, radius, radius));
            drawingContext.DrawImage(bitmap, target);
            drawingContext.Pop();
        }

        if (Stroke is { } stroke)
        {
            var inset = bounds;
            inset.Inflate(-0.5, -0.5);
            drawingContext.DrawRoundedRectangle(
                null,
                new Pen(stroke, 1),
                inset,
                radius,
                radius);
        }
    }

    private static void OnSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        var thumbnail = (MediaThumbnail)dependencyObject;
        thumbnail.SetBitmap(null);
        if (thumbnail.IsLoaded)
        {
            thumbnail.BeginLoad();
        }
    }

    private void MediaThumbnail_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        BeginLoad();
    }

    private void MediaThumbnail_Unloaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        // Results that arrive after this point are discarded by the generation check.
        _loadGeneration++;
    }

    private void BeginLoad()
    {
        var generation = ++_loadGeneration;
        _ = LoadAsync(generation, Reference, LocalPath);
    }

    private async Task LoadAsync(int generation, MediaReference? reference, string? localPath)
    {
        BitmapSource? bitmap = null;
        try
        {
            if (reference is not null)
            {
                bitmap = await AppWallpaperSources.Thumbnails
                    .LoadAsync(reference, DecodePixelWidth, CancellationToken.None)
                    .ConfigureAwait(true);
            }

            if (bitmap is null && !string.IsNullOrEmpty(localPath))
            {
                bitmap = LocalThumbnails.Convert(
                    localPath,
                    typeof(ImageSource),
                    string.Empty,
                    CultureInfo.InvariantCulture) as BitmapSource;
            }

            if (bitmap is null && LocalVideoThumbnailCache.SupportsPath(localPath))
            {
                bitmap = await LocalVideoThumbnailCache
                    .LoadAsync(localPath!, AppWallpaperSources.Preview)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception)
        {
            // A thumbnail is optional presentation; the placeholder fill stays visible.
            bitmap = null;
        }

        if (generation != _loadGeneration)
        {
            return;
        }

        SetBitmap(bitmap);
    }

    private void SetBitmap(BitmapSource? bitmap)
    {
        if (ReferenceEquals(_bitmap, bitmap))
        {
            return;
        }

        _bitmap = bitmap;
        HasImage = bitmap is not null;
        InvalidateVisual();
    }
}
