using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BackdropForCodex.App;
using BackdropForCodex.App.Converters;
using BackdropForCodex.App.Services.Diagnostics;
using BackdropForCodex.App.Views;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

[Collection("Wpf")]
public sealed class MainWindowLayoutTests
{
    [Fact]
    public void PreviewSurface_MaximizesTheRealFullscreenPreviewPane()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;

                try
                {
                    window = new MainWindow(
                        fixture.ViewModel,
                        fixture.Text,
                        new DiagnosticReportService())
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000,
                        Top = -10000,
                        ShowActivated = false,
                    };
                    window.Show();
                    window.Width = 2048;
                    window.Height = 1224;
                    window.Dispatcher.Invoke(
                        static () => { },
                        DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();

                    var previewPane = FindElement(window, "PreviewPane");
                    var previewView = FindElement(window, "PreviewView");
                    var previewHost = FindElement(previewView, "PreviewHost");
                    var previewCard = FindElement(previewView, "PreviewCard");
                    var previewSurface =
                        FindElement(previewView, "PreviewSurface");
                    var hostBounds = GetBounds(previewHost, previewPane);
                    var cardBounds = GetBounds(previewCard, previewPane);
                    var surfaceBounds =
                        GetBounds(previewSurface, previewPane);
                    Assert.True(
                        hostBounds.Width >= previewPane.ActualWidth * 0.99,
                        $"PreviewHost width {hostBounds.Width:F2} did not fill " +
                        $"PreviewPane width {previewPane.ActualWidth:F2}.");
                    var expectedScale = Math.Min(
                        hostBounds.Width /
                        WallpaperPreviewView.PreviewDesignWidth,
                        hostBounds.Height /
                        WallpaperPreviewView.PreviewDesignHeight);
                    Assert.Equal(
                        WallpaperPreviewView.PreviewDesignWidth *
                        expectedScale,
                        surfaceBounds.Width,
                        precision: 6);
                    Assert.Equal(
                        WallpaperPreviewView.PreviewDesignHeight *
                        expectedScale,
                        surfaceBounds.Height,
                        precision: 6);
                    var hostAspect =
                        hostBounds.Width / hostBounds.Height;
                    if (hostAspect >= 16d / 9d)
                    {
                        Assert.True(
                            surfaceBounds.Height >=
                            hostBounds.Height * 0.99,
                            $"PreviewSurface height " +
                            $"{surfaceBounds.Height:F2} left excessive " +
                            $"vertical space in a {hostAspect:F3}:1 host.");
                    }
                    else
                    {
                        Assert.True(
                            surfaceBounds.Width >=
                            hostBounds.Width * 0.99,
                            $"PreviewSurface width " +
                            $"{surfaceBounds.Width:F2} left excessive " +
                            $"horizontal space in a {hostAspect:F3}:1 host.");
                    }
                    Assert.True(
                        surfaceBounds.Width >=
                        previewPane.ActualWidth * 0.7,
                        $"Visible PreviewSurface width " +
                        $"{surfaceBounds.Width:F2} used only " +
                        $"{surfaceBounds.Width / previewPane.ActualWidth:P1} " +
                        $"of PreviewPane width " +
                        $"{previewPane.ActualWidth:F2}.");
                    Assert.Equal(
                        16d / 9d,
                        surfaceBounds.Width / surfaceBounds.Height,
                        precision: 6);
                    AssertRectEqual(cardBounds, surfaceBounds);
                    var dpi = VisualTreeHelper.GetDpi(window);
                    const double physicalPixelAllowance = 1.01;
                    Assert.InRange(
                        Math.Abs(
                            (surfaceBounds.Top - hostBounds.Top) -
                            (hostBounds.Bottom - surfaceBounds.Bottom)),
                        0,
                        physicalPixelAllowance / dpi.DpiScaleY);
                    Assert.InRange(
                        Math.Abs(
                            (surfaceBounds.Left - hostBounds.Left) -
                            (hostBounds.Right - surfaceBounds.Right)),
                        0,
                        physicalPixelAllowance / dpi.DpiScaleX);
                }
                finally
                {
                    if (window is null)
                    {
                        fixture.ViewModel.Dispose();
                    }
                    else
                    {
                        window.CloseForShutdown();
                    }
                }
            });
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(640, true)]
    [InlineData(959, true)]
    [InlineData(959.999, true)]
    [InlineData(960, false)]
    [InlineData(1200, false)]
    public void UsesStackedLayout_HonorsExact960PixelBoundary(
        double width,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.UsesStackedLayout(width));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 204)]
    [InlineData(3, 612)]
    public void ProfileCardTrackWidth_AllocatesEveryFixedCard(
        int profileCount,
        double expectedWidth)
    {
        var converter = new ProfileCardTrackWidthConverter();

        var actual = converter.Convert(
            profileCount,
            typeof(double),
            parameter: null,
            CultureInfo.InvariantCulture);

        Assert.Equal(expectedWidth, Assert.IsType<double>(actual));
    }

    private static FrameworkElement FindElement(
        FrameworkElement root,
        string name) =>
        Assert.IsAssignableFrom<FrameworkElement>(root.FindName(name));

    private static Rect GetBounds(
        FrameworkElement element,
        Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(element.RenderSize));

    private static void AssertRectEqual(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 6);
        Assert.Equal(expected.Y, actual.Y, precision: 6);
        Assert.Equal(expected.Width, actual.Width, precision: 6);
        Assert.Equal(expected.Height, actual.Height, precision: 6);
    }
}
