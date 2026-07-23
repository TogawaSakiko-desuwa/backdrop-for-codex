using BackdropForCodex.App.Models;
using BackdropForCodex.Core.Settings;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class MediaPreviewLayoutTests
{
    [Fact]
    public void ContainCentersTheEntireMediaWithoutCropping()
    {
        var placement = MediaPreviewLayout.Calculate(
            viewportWidth: 100,
            viewportHeight: 100,
            mediaWidth: 200,
            mediaHeight: 100,
            WallpaperFit.Contain,
            focusX: 0,
            focusY: 1);

        Assert.Equal(100, placement.Width, precision: 6);
        Assert.Equal(50, placement.Height, precision: 6);
        Assert.Equal(0, placement.OffsetX, precision: 6);
        Assert.Equal(25, placement.OffsetY, precision: 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.5, -50)]
    [InlineData(1, -100)]
    public void CoverUsesContinuousObjectPositionSemantics(
        double focusX,
        double expectedOffsetX)
    {
        var placement = MediaPreviewLayout.Calculate(
            viewportWidth: 100,
            viewportHeight: 100,
            mediaWidth: 200,
            mediaHeight: 100,
            WallpaperFit.Cover,
            focusX,
            focusY: 0.5);

        Assert.Equal(200, placement.Width, precision: 6);
        Assert.Equal(100, placement.Height, precision: 6);
        Assert.Equal(expectedOffsetX, placement.OffsetX, precision: 6);
        Assert.Equal(0, placement.OffsetY, precision: 6);
    }

    [Fact]
    public void CoverAppliesVerticalEdgeFocusForPortraitMedia()
    {
        var top = MediaPreviewLayout.Calculate(
            100,
            100,
            100,
            200,
            WallpaperFit.Cover,
            focusX: 0.5,
            focusY: 0);
        var bottom = MediaPreviewLayout.Calculate(
            100,
            100,
            100,
            200,
            WallpaperFit.Cover,
            focusX: 0.5,
            focusY: 1);

        Assert.Equal(0, top.OffsetY, precision: 6);
        Assert.Equal(-100, bottom.OffsetY, precision: 6);
    }

    [Fact]
    public void StretchMatchesTheViewportWithoutPreservingAspectRatio()
    {
        var placement = MediaPreviewLayout.Calculate(
            viewportWidth: 160,
            viewportHeight: 90,
            mediaWidth: 50,
            mediaHeight: 200,
            WallpaperFit.Stretch,
            focusX: 0.9,
            focusY: 0.1);

        Assert.Equal(160, placement.Width, precision: 6);
        Assert.Equal(90, placement.Height, precision: 6);
        Assert.Equal(0, placement.OffsetX, precision: 6);
        Assert.Equal(0, placement.OffsetY, precision: 6);
    }

    [Fact]
    public void InvalidDimensionsReturnAnEmptyPlacement()
    {
        Assert.True(
            MediaPreviewLayout
                .Calculate(0, 100, 100, 100, WallpaperFit.Cover, 0.5, 0.5)
                .IsEmpty);
        Assert.True(
            MediaPreviewLayout
                .Calculate(100, double.NaN, 100, 100, WallpaperFit.Cover, 0.5, 0.5)
                .IsEmpty);
        Assert.True(
            MediaPreviewLayout
                .Calculate(100, 100, -1, 100, WallpaperFit.Contain, 0.5, 0.5)
                .IsEmpty);
        Assert.True(
            MediaPreviewLayout
                .Calculate(
                    100,
                    100,
                    100,
                    double.PositiveInfinity,
                    WallpaperFit.Stretch,
                    0.5,
                    0.5)
                .IsEmpty);
    }
}
