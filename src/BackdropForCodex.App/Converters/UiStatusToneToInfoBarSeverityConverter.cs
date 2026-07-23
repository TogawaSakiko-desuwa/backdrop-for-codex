using System.Globalization;
using System.Windows.Data;
using BackdropForCodex.App.ViewModels;
using Wpf.Ui.Controls;

namespace BackdropForCodex.App.Converters;

public sealed class UiStatusToneToInfoBarSeverityConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value switch
        {
            UiStatusTone.Success => InfoBarSeverity.Success,
            UiStatusTone.Warning => InfoBarSeverity.Warning,
            UiStatusTone.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
