using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using BackdropForCodex.App.Views;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

[Collection("Wpf")]
public sealed class WallpaperPreviewViewGlassTests
{
    private static readonly string[] GlassSurfaceNames =
    [
        "LeftGlassSurface",
        "TopBarGlassSurface",
        "MessageGlassSurface",
        "ComposerGlassSurface",
        "RightGlassSurface",
    ];

    [Fact]
    public void GlassBlur_ReusesTheClearBackdropAndMatchesAllFiveSurfaces()
    {
        StaTest.Run(
            () =>
            {
                var view = CreateArrangedPreview(
                    new PreviewState(HasSelectedMedia: true, BlurPx: 24));
                try
                {
                    var image = FindElement<Image>(view, "ImagePreview");
                    var video = FindElement<MediaElement>(view, "VideoPreview");
                    var backdrop = FindElement<Grid>(view, "BackdropSource");
                    var themeOverlay =
                        FindElement<Border>(view, "PreviewThemeOverlay");
                    var blurHost =
                        FindElement<Grid>(view, "GlassBlurClipHost");
                    var blurVisual =
                        FindElement<Rectangle>(view, "GlassBlurVisual");
                    var previewSurface =
                        FindElement<Grid>(view, "PreviewSurface");

                    Assert.Null(image.Effect);
                    Assert.Null(video.Effect);
                    Assert.Same(
                        backdrop,
                        Assert.IsType<VisualBrush>(blurVisual.Fill).Visual);
                    Assert.Same(
                        backdrop,
                        VisualTreeHelper.GetParent(themeOverlay));
                    var effect = Assert.IsType<BlurEffect>(blurVisual.Effect);
                    Assert.Equal(24, effect.Radius);
                    Assert.Equal(RenderingBias.Performance, effect.RenderingBias);
                    Assert.False(blurHost.IsHitTestVisible);

                    var geometry = Assert.IsType<GeometryGroup>(blurHost.Clip);
                    Assert.Equal(GlassSurfaceNames.Length, geometry.Children.Count);
                    for (var index = 0; index < GlassSurfaceNames.Length; index++)
                    {
                        var surface =
                            FindElement<Border>(view, GlassSurfaceNames[index]);
                        var expectedBounds = surface
                            .TransformToAncestor(previewSurface)
                            .TransformBounds(new Rect(surface.RenderSize));
                        var rectangle = Assert.IsType<RectangleGeometry>(
                            geometry.Children[index]);

                        AssertRectEqual(expectedBounds, rectangle.Rect);
                        Assert.Equal(
                            surface.CornerRadius.TopLeft,
                            rectangle.RadiusX,
                            precision: 6);
                        Assert.Equal(rectangle.RadiusX, rectangle.RadiusY);
                    }
                }
                finally
                {
                    view.ReleaseMedia();
                }
            });
    }

    [Theory]
    [InlineData(MediaKind.Image, 0, Visibility.Collapsed)]
    [InlineData(MediaKind.Image, 24, Visibility.Visible)]
    [InlineData(MediaKind.Video, 0, Visibility.Collapsed)]
    [InlineData(MediaKind.Video, 24, Visibility.Visible)]
    public void GlassBlur_OnlyRendersForSelectedMediaWithPositiveRadius(
        MediaKind mediaKind,
        double blurPx,
        Visibility expected)
    {
        StaTest.Run(
            () =>
            {
                var view = CreateArrangedPreview(
                    new PreviewState(HasSelectedMedia: true, blurPx));
                view.MediaKind = mediaKind;
                SetRenderedMedia(view, mediaKind);
                PumpDispatcher(view.Dispatcher);
                try
                {
                    Assert.Equal(
                        expected,
                        FindElement<Grid>(view, "GlassBlurClipHost").Visibility);
                }
                finally
                {
                    view.ReleaseMedia();
                }
            });
    }

    [Fact]
    public void GlassBlur_IsCollapsedWhenSelectedMediaCannotBeRendered()
    {
        StaTest.Run(
            () =>
            {
                var view = CreateArrangedPreview(
                    new PreviewState(HasSelectedMedia: true, BlurPx: 24));
                try
                {
                    Assert.Equal(
                        Visibility.Collapsed,
                        FindElement<Grid>(
                            view,
                            "GlassBlurClipHost").Visibility);
                }
                finally
                {
                    view.ReleaseMedia();
                }
            });
    }

    [Fact]
    public void GlassBlur_SoftensOnlyTheRoundedGlassRegions()
    {
        StaTest.Run(
            () =>
            {
                var clear = RenderCheckerboard(blurPx: 0);
                var blurred = RenderCheckerboard(blurPx: 24);

                var clearEnergy = MeanEdgeEnergy(
                    clear.Pixels,
                    clear.PixelWidth,
                    Inset(clear.MessageBounds, 5));
                var blurredEnergy = MeanEdgeEnergy(
                    blurred.Pixels,
                    blurred.PixelWidth,
                    Inset(blurred.MessageBounds, 5));
                Assert.True(
                    blurredEnergy < clearEnergy * 0.25,
                    $"Expected glass blur to reduce edge energy. " +
                    $"Clear={clearEnergy:F3}, blurred={blurredEnergy:F3}.");

                var gap = new Rect(
                    clear.LeftBounds.Right + 2,
                    clear.LeftBounds.Top + 20,
                    3,
                    Math.Max(1, clear.LeftBounds.Height - 40));
                var gapDifference = MeanPixelDifference(
                    clear.Pixels,
                    blurred.Pixels,
                    clear.PixelWidth,
                    gap);
                Assert.True(
                    gapDifference < 1,
                    $"Expected no blur between panels. Delta={gapDifference:F3}.");

                var roundedCornerOutside = new Rect(
                    clear.LeftBounds.Left + 1,
                    clear.LeftBounds.Top + 1,
                    2,
                    2);
                var outsideDifference = MeanPixelDifference(
                    clear.Pixels,
                    blurred.Pixels,
                    clear.PixelWidth,
                    roundedCornerOutside);
                Assert.True(
                    outsideDifference < 3,
                    $"Expected no blur outside the rounded corner. " +
                    $"Delta={outsideDifference:F3}.");

                var roundedCornerInside = new Rect(
                    clear.LeftBounds.Left + 8,
                    clear.LeftBounds.Top + 8,
                    3,
                    3);
                var insideDifference = MeanPixelDifference(
                    clear.Pixels,
                    blurred.Pixels,
                    clear.PixelWidth,
                    roundedCornerInside);
                Assert.True(
                    insideDifference > 10,
                    $"Expected blur inside the rounded corner. " +
                    $"Delta={insideDifference:F3}.");
            });
    }

    private static RenderedPreview RenderCheckerboard(double blurPx)
    {
        var view = CreateArrangedPreview(
            new PreviewState(
                HasSelectedMedia: true,
                BlurPx: blurPx,
                PanelOpacity: 0));
        try
        {
            var backdrop = FindElement<Grid>(view, "BackdropSource");
            backdrop.Background = CreateCheckerboardBrush();
            FindElement<Image>(view, "ImagePreview").Visibility =
                Visibility.Visible;
            FindElement<Border>(view, "PreviewThemeOverlay").Visibility =
                Visibility.Collapsed;
            FindElement<Border>(view, "EmptyPreview").Visibility =
                Visibility.Collapsed;
            FindElement<Grid>(view, "PreviewChromeLayer").Opacity = 0;
            view.UpdateLayout();
            PumpDispatcher(view.Dispatcher);

            const int pixelWidth =
                (int)WallpaperPreviewView.PreviewDesignWidth;
            const int pixelHeight =
                (int)WallpaperPreviewView.PreviewDesignHeight;
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(view);
            var pixels = new byte[pixelWidth * pixelHeight * 4];
            bitmap.CopyPixels(pixels, pixelWidth * 4, 0);

            return new RenderedPreview(
                pixels,
                pixelWidth,
                GetBounds(FindElement<Border>(view, "MessageGlassSurface"), view),
                GetBounds(FindElement<Border>(view, "LeftGlassSurface"), view));
        }
        finally
        {
            view.ReleaseMedia();
        }
    }

    private static WallpaperPreviewView CreateArrangedPreview(
        PreviewState state)
    {
        var view = new WallpaperPreviewView
        {
            DataContext = state,
            SurfaceMinimumHeight = 220,
        };
        view.ApplyTemplate();
        var designSize = new Size(
            WallpaperPreviewView.PreviewDesignWidth,
            WallpaperPreviewView.PreviewDesignHeight);
        view.Measure(designSize);
        view.Arrange(new Rect(designSize));
        view.UpdateLayout();
        PumpDispatcher(view.Dispatcher);
        return view;
    }

    private static void SetRenderedMedia(
        WallpaperPreviewView view,
        MediaKind mediaKind)
    {
        FindElement<Image>(view, "ImagePreview").Visibility =
            mediaKind == MediaKind.Image
                ? Visibility.Visible
                : Visibility.Collapsed;
        FindElement<MediaElement>(view, "VideoPreview").Visibility =
            mediaKind == MediaKind.Video
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static void PumpDispatcher(Dispatcher dispatcher) =>
        dispatcher.Invoke(
            static () => { },
            DispatcherPriority.ApplicationIdle);

    private static DrawingBrush CreateCheckerboardBrush()
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 8, 8));
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 4, 4));
            context.DrawRectangle(Brushes.Black, null, new Rect(4, 4, 4, 4));
        }

        drawing.Freeze();
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, 8, 8),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        brush.Freeze();
        return brush;
    }

    private static double MeanEdgeEnergy(
        byte[] pixels,
        int pixelWidth,
        Rect region)
    {
        var bounds = ToPixelBounds(region, pixelWidth, pixels.Length);
        long total = 0;
        long samples = 0;
        for (var y = bounds.Top + 1; y < bounds.Bottom; y++)
        {
            for (var x = bounds.Left + 1; x < bounds.Right; x++)
            {
                var offset = ((y * pixelWidth) + x) * 4;
                total += Math.Abs(pixels[offset] - pixels[offset - 4]);
                total += Math.Abs(
                    pixels[offset] - pixels[offset - (pixelWidth * 4)]);
                samples += 2;
            }
        }

        return samples == 0 ? 0 : (double)total / samples;
    }

    private static double MeanPixelDifference(
        byte[] first,
        byte[] second,
        int pixelWidth,
        Rect region)
    {
        var bounds = ToPixelBounds(region, pixelWidth, first.Length);
        long total = 0;
        long samples = 0;
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var offset = ((y * pixelWidth) + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    total += Math.Abs(
                        first[offset + channel] - second[offset + channel]);
                    samples++;
                }
            }
        }

        return samples == 0 ? 0 : (double)total / samples;
    }

    private static PixelBounds ToPixelBounds(
        Rect region,
        int pixelWidth,
        int bufferLength)
    {
        var pixelHeight = bufferLength / (pixelWidth * 4);
        return new PixelBounds(
            Math.Clamp((int)Math.Ceiling(region.Left), 0, pixelWidth),
            Math.Clamp((int)Math.Ceiling(region.Top), 0, pixelHeight),
            Math.Clamp((int)Math.Floor(region.Right), 0, pixelWidth),
            Math.Clamp((int)Math.Floor(region.Bottom), 0, pixelHeight));
    }

    private static Rect Inset(Rect rectangle, double inset) =>
        new(
            rectangle.Left + inset,
            rectangle.Top + inset,
            Math.Max(0, rectangle.Width - (inset * 2)),
            Math.Max(0, rectangle.Height - (inset * 2)));

    private static Rect GetBounds(
        FrameworkElement element,
        Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(element.RenderSize));

    private static T FindElement<T>(
        FrameworkElement view,
        string name)
        where T : FrameworkElement =>
        Assert.IsType<T>(view.FindName(name));

    private static void AssertRectEqual(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 6);
        Assert.Equal(expected.Y, actual.Y, precision: 6);
        Assert.Equal(expected.Width, actual.Width, precision: 6);
        Assert.Equal(expected.Height, actual.Height, precision: 6);
    }

    private sealed record PreviewState(
        bool HasSelectedMedia,
        double BlurPx,
        double PanelOpacity = 0.78);

    private sealed record RenderedPreview(
        byte[] Pixels,
        int PixelWidth,
        Rect MessageBounds,
        Rect LeftBounds);

    private readonly record struct PixelBounds(
        int Left,
        int Top,
        int Right,
        int Bottom);
}
