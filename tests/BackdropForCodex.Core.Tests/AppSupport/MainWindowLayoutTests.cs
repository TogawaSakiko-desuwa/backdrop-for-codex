using System.Globalization;
using BackdropForCodex.App;
using BackdropForCodex.App.Converters;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

public sealed class MainWindowLayoutTests
{
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
}
