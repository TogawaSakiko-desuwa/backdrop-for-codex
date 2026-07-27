using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BackdropForCodex.App.Views;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

[Collection("Wpf")]
public sealed class WallpaperPreviewViewLayoutTests
{
    [Fact]
    public void PreviewHost_FillsItsParentAndMaximizesTheVisibleCanvas()
    {
        StaTest.Run(
            () =>
            {
                AssertParentLayout(
                    ArrangePreviewInParent(width: 871, height: 290));
                AssertParentLayout(
                    ArrangePreviewInParent(width: 1458, height: 746));
                AssertParentLayout(
                    ArrangePreviewInParent(width: 592, height: 120));
            });
    }

    [Fact]
    public void PreviewCard_UsesOneUniformScaleAtEveryWindowSize()
    {
        StaTest.Run(
            () =>
            {
                var normal = ArrangePreview(width: 1000, height: 300);
                var fullscreen = ArrangePreview(width: 1500, height: 500);
                var minimum = ArrangePreview(width: 592, height: 120);

                AssertSnapshot(normal);
                AssertSnapshot(fullscreen);
                AssertSnapshot(minimum);

                Assert.True(
                    fullscreen.BadgeBounds.Width > normal.BadgeBounds.Width,
                    "The simulated Codex badge should grow when the preview grows.");
            });
    }

    [Fact]
    public void SurfaceMinimumHeight_IsAppliedToTheOuterPreviewHost()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperPreviewView
                {
                    SurfaceMinimumHeight = 120,
                };
                view.Measure(
                    new Size(
                        WallpaperPreviewView.PreviewDesignWidth,
                        WallpaperPreviewView.PreviewDesignHeight));
                view.Arrange(
                    new Rect(
                        0,
                        0,
                        WallpaperPreviewView.PreviewDesignWidth,
                        WallpaperPreviewView.PreviewDesignHeight));
                view.UpdateLayout();

                var host = FindElement(view, "PreviewHost");
                var card = FindElement(view, "PreviewCard");
                var surface = FindElement(view, "PreviewSurface");

                Assert.Equal(120, host.MinHeight);
                Assert.IsType<Border>(card);
                Assert.Equal(
                    WallpaperPreviewView.PreviewDesignWidth,
                    card.Width);
                Assert.Equal(
                    WallpaperPreviewView.PreviewDesignHeight,
                    card.Height);
                Assert.Equal(card.Width, surface.ActualWidth);
                Assert.Equal(card.Height, surface.ActualHeight);
            });
    }

    [Fact]
    public void PreviewViewbox_PreservesLogicalPointerCoordinates()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperPreviewView
                {
                    SurfaceMinimumHeight = 220,
                };
                view.Measure(new Size(1500, 500));
                view.Arrange(new Rect(0, 0, 1500, 500));
                view.UpdateLayout();

                var host = FindElement(view, "PreviewHost");
                var focusSurface =
                    FindElement(view, "FocusInteractionSurface");
                var logicalPoint = new Point(
                    focusSurface.ActualWidth * 0.3,
                    focusSurface.ActualHeight * 0.7);
                var transform = focusSurface.TransformToAncestor(host);
                var projectedPoint = transform.Transform(logicalPoint);
                var inverse = Assert.IsAssignableFrom<GeneralTransform>(
                    transform.Inverse);
                var recoveredPoint = inverse.Transform(projectedPoint);

                Assert.Equal(logicalPoint.X, recoveredPoint.X, precision: 6);
                Assert.Equal(logicalPoint.Y, recoveredPoint.Y, precision: 6);
            });
    }

    private static PreviewLayoutSnapshot ArrangePreview(
        double width,
        double height)
    {
        var view = new WallpaperPreviewView
        {
            SurfaceMinimumHeight = width < 960 ? 120 : 220,
        };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();

        var host = FindElement(view, "PreviewHost");
        var card = FindElement(view, "PreviewCard");
        var surface = FindElement(view, "PreviewSurface");
        var badge = FindElement(view, "CodexBadge");
        var cardBounds = GetBounds(card, host);
        var surfaceBounds = GetBounds(surface, host);
        var badgeBounds = GetBounds(badge, host);

        return new PreviewLayoutSnapshot(
            host.RenderSize,
            card.RenderSize,
            cardBounds,
            surface.RenderSize,
            surfaceBounds,
            badge.RenderSize,
            badgeBounds);
    }

    private static ParentLayoutSnapshot ArrangePreviewInParent(
        double width,
        double height)
    {
        var parent = new Grid();
        var view = new WallpaperPreviewView
        {
            SurfaceMinimumHeight = width < 960 ? 120 : 220,
        };
        parent.Children.Add(view);
        parent.Measure(new Size(width, height));
        parent.Arrange(new Rect(0, 0, width, height));
        parent.UpdateLayout();

        var host = FindElement(view, "PreviewHost");
        var card = FindElement(view, "PreviewCard");
        var surface = FindElement(view, "PreviewSurface");
        return new ParentLayoutSnapshot(
            parent.RenderSize,
            GetBounds(host, parent),
            GetBounds(card, parent),
            GetBounds(surface, parent));
    }

    private static void AssertParentLayout(ParentLayoutSnapshot snapshot)
    {
        Assert.Equal(
            new Rect(snapshot.ParentSize),
            snapshot.HostBounds);
        var expectedScale = Math.Min(
            snapshot.ParentSize.Width /
            WallpaperPreviewView.PreviewDesignWidth,
            snapshot.ParentSize.Height /
            WallpaperPreviewView.PreviewDesignHeight);
        Assert.Equal(
            WallpaperPreviewView.PreviewDesignWidth * expectedScale,
            snapshot.CardBounds.Width,
            precision: 6);
        Assert.Equal(
            WallpaperPreviewView.PreviewDesignHeight * expectedScale,
            snapshot.CardBounds.Height,
            precision: 6);
        AssertRectEqual(snapshot.CardBounds, snapshot.SurfaceBounds);
        Assert.Equal(
            snapshot.CardBounds.Left,
            snapshot.ParentSize.Width - snapshot.CardBounds.Right,
            precision: 6);
        Assert.Equal(
            snapshot.CardBounds.Top,
            snapshot.ParentSize.Height - snapshot.CardBounds.Bottom,
            precision: 6);
    }

    private static void AssertSnapshot(PreviewLayoutSnapshot snapshot)
    {
        Assert.Equal(
            WallpaperPreviewView.PreviewDesignWidth,
            snapshot.LogicalCardSize.Width);
        Assert.Equal(
            WallpaperPreviewView.PreviewDesignHeight,
            snapshot.LogicalCardSize.Height);
        Assert.Equal(
            snapshot.LogicalCardSize,
            snapshot.LogicalSurfaceSize);
        AssertRectEqual(snapshot.CardBounds, snapshot.SurfaceBounds);
        Assert.Equal(
            snapshot.CardBounds.Width / snapshot.LogicalCardSize.Width,
            snapshot.CardBounds.Height / snapshot.LogicalCardSize.Height,
            precision: 6);
        Assert.Equal(
            snapshot.CardBounds.Left,
            snapshot.HostSize.Width - snapshot.CardBounds.Right,
            precision: 6);
        Assert.Equal(
            snapshot.CardBounds.Top,
            snapshot.HostSize.Height - snapshot.CardBounds.Bottom,
            precision: 6);
        Assert.True(snapshot.CardBounds.Left >= -0.001);
        Assert.True(snapshot.CardBounds.Top >= -0.001);
        Assert.True(snapshot.CardBounds.Right <= snapshot.HostSize.Width + 0.001);
        Assert.True(snapshot.CardBounds.Bottom <= snapshot.HostSize.Height + 0.001);
        Assert.Equal(
            snapshot.CardScale,
            snapshot.BadgeScale,
            precision: 6);
    }

    private static void AssertRectEqual(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 6);
        Assert.Equal(expected.Y, actual.Y, precision: 6);
        Assert.Equal(expected.Width, actual.Width, precision: 6);
        Assert.Equal(expected.Height, actual.Height, precision: 6);
    }

    private static FrameworkElement FindElement(
        FrameworkElement view,
        string name) =>
        Assert.IsAssignableFrom<FrameworkElement>(view.FindName(name));

    private static Rect GetBounds(
        FrameworkElement element,
        Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(element.RenderSize));

    private sealed record PreviewLayoutSnapshot(
        Size HostSize,
        Size LogicalCardSize,
        Rect CardBounds,
        Size LogicalSurfaceSize,
        Rect SurfaceBounds,
        Size LogicalBadgeSize,
        Rect BadgeBounds)
    {
        public double CardScale => CardBounds.Width / LogicalCardSize.Width;

        public double BadgeScale => BadgeBounds.Width / LogicalBadgeSize.Width;
    }

    private sealed record ParentLayoutSnapshot(
        Size ParentSize,
        Rect HostBounds,
        Rect CardBounds,
        Rect SurfaceBounds);
}
