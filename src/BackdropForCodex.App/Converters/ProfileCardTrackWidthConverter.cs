using System.Globalization;
using System.Windows.Data;

namespace BackdropForCodex.App.Converters;

public sealed class ProfileCardTrackWidthConverter : IValueConverter
{
    private const double ProfileCardExtent = 204;

    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;
        return value is int count && count > 0
            ? count * ProfileCardExtent
            : 0d;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
