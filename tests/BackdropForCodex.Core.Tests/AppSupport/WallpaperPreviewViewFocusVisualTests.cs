using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.App.Views;
using BackdropForCodex.Core.Media;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

[Collection("Wpf")]
public sealed class WallpaperPreviewViewFocusVisualTests
{
    [Fact]
    public void FocusSurfaceUsesPersistentPrecisionReticleWithoutOverlayTooltip()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperPreviewView(new ImagePreviewService());
                var surface = FindElement<Border>(view, "FocusInteractionSurface");
                var indicator = FindElement<Grid>(view, "FocusIndicator");

                Assert.Null(surface.ToolTip);
                Assert.False(string.IsNullOrWhiteSpace(
                    AutomationProperties.GetHelpText(surface)));
                Assert.Equal(40, indicator.Width);
                Assert.Equal(40, indicator.Height);
                Assert.InRange(
                    WallpaperPreviewView.IdleFocusIndicatorOpacity,
                    0.55,
                    0.62);

                var outerRing = Assert.IsType<Ellipse>(indicator.Children[0]);
                Assert.Equal(38, outerRing.Width);
                Assert.Equal(38, outerRing.Height);
                Assert.Equal(2, outerRing.StrokeThickness);
                Assert.NotEqual(
                    DependencyProperty.UnsetValue,
                    outerRing.ReadLocalValue(Shape.StrokeProperty));

                var verticalCrosshair =
                    Assert.IsType<Line>(indicator.Children[1]);
                var horizontalCrosshair =
                    Assert.IsType<Line>(indicator.Children[2]);
                Assert.Equal(1.25, verticalCrosshair.StrokeThickness);
                Assert.Equal(1.25, horizontalCrosshair.StrokeThickness);

                var centerPoint = Assert.IsType<Ellipse>(indicator.Children[3]);
                Assert.Equal(8, centerPoint.Width);
                Assert.Equal(8, centerPoint.Height);
                Assert.Equal(1.5, centerPoint.StrokeThickness);
            });
    }

    [Fact]
    public void FocusIndicatorIsIdleWhenMediaBecomesAdjustableAndHiddenWithoutMedia()
    {
        StaTest.Run(
            () =>
            {
                var service = new ImagePreviewService();
                var withoutMedia = new WallpaperPreviewView(service)
                {
                    CanAdjustFocus = true,
                };
                var hiddenIndicator =
                    FindElement<Grid>(withoutMedia, "FocusIndicator");
                withoutMedia.ShowCurrentFocus();
                Assert.Equal(0, hiddenIndicator.Opacity);

                var withMedia = new WallpaperPreviewView(service)
                {
                    CanAdjustFocus = true,
                    MediaReference = new MediaReference
                    {
                        MediaId = Guid.CreateVersion7(),
                        SourceKind = MediaSourceKind.LocalFile,
                        SourceIdentifier = @"C:\wallpaper.png",
                        LastKnownKind = MediaKind.Image,
                    },
                };
                Arrange(withMedia);
                withMedia.RaiseEvent(
                    new RoutedEventArgs(FrameworkElement.LoadedEvent, withMedia));
                try
                {
                    var indicator = FindElement<Grid>(withMedia, "FocusIndicator");
                    Assert.Equal(
                        WallpaperPreviewView.IdleFocusIndicatorOpacity,
                        indicator.Opacity);

                    withMedia.ShowCurrentFocus();
                    Assert.Equal(1, indicator.Opacity);

                    withMedia.CanAdjustFocus = false;
                    Assert.Equal(0, indicator.Opacity);
                }
                finally
                {
                    withMedia.ReleaseMedia();
                    withoutMedia.ReleaseMedia();
                }
            });
    }

    [Fact]
    public void PreviewOverlaysUseTheWorkbenchCornerScale()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperPreviewView(new ImagePreviewService());
                var mediaPill = FindElement<Border>(view, "MediaNamePill");
                var dropOverlay = FindElement<Border>(view, "DropOverlay");

                Assert.Equal(new CornerRadius(7), mediaPill.CornerRadius);
                Assert.Equal(new Thickness(0), mediaPill.BorderThickness);
                Assert.Equal(new CornerRadius(8), dropOverlay.CornerRadius);
            });
    }

    [Fact]
    public void FocusGuideShowsThirdsOnlyWhileTheCropFocusIsAdjustableAndHovered()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperPreviewView(new ImagePreviewService());
                var overlay = FindElement<Grid>(view, "FocusGuideOverlay");
                var verticalLeft = FindElement<Border>(view, "FocusGuideVerticalLeft");
                var verticalRight = FindElement<Border>(view, "FocusGuideVerticalRight");
                var horizontalTop = FindElement<Border>(view, "FocusGuideHorizontalTop");
                var horizontalBottom = FindElement<Border>(view, "FocusGuideHorizontalBottom");

                Assert.False(overlay.IsHitTestVisible);
                Assert.Equal(3, overlay.ColumnDefinitions.Count);
                Assert.Equal(3, overlay.RowDefinitions.Count);
                AssertStyleSetter(
                    overlay.Style,
                    UIElement.OpacityProperty,
                    0d);
                AssertFocusHoverEmphasis(overlay.Style, 0.45d);
                AssertHighContrastOverride(
                    overlay.Style,
                    UIElement.OpacityProperty,
                    1d);
                Assert.Equal(1, verticalLeft.Width);
                Assert.Equal(1, verticalRight.Width);
                Assert.Equal(1, horizontalTop.Height);
                Assert.Equal(1, horizontalBottom.Height);
                Assert.Equal(0, Grid.GetColumn(verticalLeft));
                Assert.Equal(1, Grid.GetColumn(verticalRight));
                Assert.Equal(0, Grid.GetRow(horizontalTop));
                Assert.Equal(1, Grid.GetRow(horizontalBottom));
                Assert.Null(view.FindName("CalibrationOverlay"));
                Assert.Null(view.FindName("SafeAreaBoundary"));
            });
    }

    private static void AssertStyleSetter(
        Style style,
        DependencyProperty property,
        object expectedValue)
    {
        var setter = Assert.Single(
            style.Setters.OfType<Setter>(),
            candidate => candidate.Property == property);
        Assert.Equal(expectedValue, setter.Value);
    }

    private static void AssertFocusHoverEmphasis(Style style, double expectedOpacity)
    {
        var trigger = Assert.Single(style.Triggers.OfType<MultiDataTrigger>());
        Assert.Collection(
            trigger.Conditions.OrderBy(
                condition => ((Binding)condition.Binding).Path.Path,
                StringComparer.Ordinal),
            condition =>
            {
                Assert.Equal(
                    "CanAdjustFocus",
                    ((Binding)condition.Binding).Path.Path);
                Assert.Equal("True", condition.Value?.ToString());
            },
            condition =>
            {
                var binding = (Binding)condition.Binding;
                Assert.Equal("IsMouseOver", binding.Path.Path);
                Assert.Equal(
                    typeof(WallpaperPreviewView),
                    binding.RelativeSource?.AncestorType);
                Assert.Equal("True", condition.Value?.ToString());
            });
        var setter = Assert.Single(
            trigger.Setters.OfType<Setter>(),
            candidate => candidate.Property == UIElement.OpacityProperty);
        Assert.Equal(expectedOpacity, setter.Value);
    }

    private static void AssertHighContrastOverride(
        Style style,
        DependencyProperty property,
        object? expectedValue = null)
    {
        var trigger = Assert.Single(style.Triggers.OfType<DataTrigger>());
        Assert.True(
            bool.TryParse(trigger.Value?.ToString(), out var isHighContrast) &&
            isHighContrast);
        var binding = Assert.IsType<Binding>(trigger.Binding);
        Assert.Equal("IsWorkbenchHighContrast", binding.Path.Path);
        Assert.Equal(typeof(Window), binding.RelativeSource?.AncestorType);
        Assert.Null(binding.Source);
        var setter = Assert.Single(
            trigger.Setters.OfType<Setter>(),
            candidate => candidate.Property == property);
        if (expectedValue is not null)
        {
            Assert.Equal(expectedValue, setter.Value);
        }
    }

    private static void Arrange(WallpaperPreviewView view)
    {
        var size = new Size(
            WallpaperPreviewView.PreviewDesignWidth,
            WallpaperPreviewView.PreviewDesignHeight);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
    }

    private static T FindElement<T>(FrameworkElement view, string name)
        where T : FrameworkElement =>
        Assert.IsType<T>(view.FindName(name));

    private sealed class ImagePreviewService : ISafeMediaPreviewService
    {
        public ISafeMediaPreviewLease Acquire(MediaReference reference)
        {
            _ = reference;
            return new ImagePreviewLease();
        }

        public ISafeMediaPreviewLease Acquire(string mediaPath)
        {
            _ = mediaPath;
            return new ImagePreviewLease();
        }

        public bool IsAvailable(MediaReference reference)
        {
            _ = reference;
            return true;
        }

        public bool IsAvailable(string mediaPath)
        {
            _ = mediaPath;
            return true;
        }
    }

    private sealed class ImagePreviewLease : ISafeMediaPreviewLease
    {
        public MediaFileMetadata Metadata { get; } =
            new(MediaFormat.Png, MediaKind.Image, "image/png", 16, 2, 2);

        public BitmapSource LoadBitmap(int decodePixelWidth)
        {
            _ = decodePixelWidth;
            var bitmap = BitmapSource.Create(
                2,
                2,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[16],
                8);
            bitmap.Freeze();
            return bitmap;
        }

        public Uri CreateVideoSource() =>
            throw new InvalidOperationException("Image previews do not expose video URIs.");

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
