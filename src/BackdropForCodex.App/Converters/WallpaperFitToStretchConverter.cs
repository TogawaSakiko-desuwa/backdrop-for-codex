using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using BackdropForCodex.Core.Settings;

namespace BackdropForCodex.App.Converters;

public sealed class WallpaperFitToStretchConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is WallpaperFit.Contain ? Stretch.Uniform : Stretch.UniformToFill;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is Stretch.Uniform ? WallpaperFit.Contain : WallpaperFit.Cover;
}
