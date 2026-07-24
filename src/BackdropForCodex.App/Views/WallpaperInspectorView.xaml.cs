using System.Windows;
using System.Windows.Controls;

namespace BackdropForCodex.App.Views;

public partial class WallpaperInspectorView : UserControl
{
    public WallpaperInspectorView()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler? ChooseMediaRequested;

    public event RoutedEventHandler? CenterFocusRequested;

    public event RoutedEventHandler? ReviewRiskRequested;

    private void ChooseMedia_Click(object sender, RoutedEventArgs e) =>
        ChooseMediaRequested?.Invoke(this, e);

    private void CenterFocus_Click(object sender, RoutedEventArgs e) =>
        CenterFocusRequested?.Invoke(this, e);

    private void ReviewRisk_Click(object sender, RoutedEventArgs e) =>
        ReviewRiskRequested?.Invoke(this, e);
}
