using System.Windows;

namespace BackdropForCodex.App.Services.Appearance;

/// <summary>
/// Inherited attached state describing the resolved shell appearance. Styles switch between the
/// editor palette and the stock Fluent palette from it, so the window tree does not need to know
/// which theme mode produced the current look.
/// </summary>
public static class WorkbenchAppearance
{
    public static readonly DependencyProperty IsDarkProperty =
        DependencyProperty.RegisterAttached(
            "IsDark",
            typeof(bool),
            typeof(WorkbenchAppearance),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsDark(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsDarkProperty);
    }

    public static void SetIsDark(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsDarkProperty, value);
    }
}
