using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.ViewModels;
using AppThemeMode = BackdropForCodex.App.Services.Preferences.ThemeMode;

namespace BackdropForCodex.App.Views;

public sealed class ThemeModeChangedEventArgs : EventArgs
{
    public ThemeModeChangedEventArgs(AppThemeMode mode)
    {
        Mode = mode;
    }

    public AppThemeMode Mode { get; }
}

public partial class SettingsDialogContent : UserControl
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IAppTextProvider _text;
    private bool _initialized;

    public SettingsDialogContent(
        MainWindowViewModel viewModel,
        IAppTextProvider text)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        InitializeComponent();
        ThemeComboBox.SelectedValue = _viewModel.ThemeMode;
        VersionText.Text =
            $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";
        RefreshRiskState();
        _initialized = true;
    }

    public event EventHandler<ThemeModeChangedEventArgs>? ThemeChangeRequested;

    public event EventHandler? RiskRevokeRequested;

    public event EventHandler? ResetRequested;

    public void RefreshRiskState()
    {
        RiskStateText.Text = _viewModel.AcceptedCdpRisk
            ? Text("Risk_Acknowledgement", "Acknowledgement saved")
            : Text("Risk_Revoked", "Acknowledgement is not currently saved.");
        RevokeRiskButton.IsEnabled = _viewModel.AcceptedCdpRisk;
    }

    private void ThemeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_initialized ||
            ThemeComboBox.SelectedValue is not AppThemeMode mode)
        {
            return;
        }

        ThemeChangeRequested?.Invoke(this, new ThemeModeChangedEventArgs(mode));
    }

    private void RevokeRiskButton_Click(object sender, RoutedEventArgs e) =>
        RiskRevokeRequested?.Invoke(this, EventArgs.Empty);

    private void ResetButton_Click(object sender, RoutedEventArgs e) =>
        ResetRequested?.Invoke(this, EventArgs.Empty);

    private string Text(string key, string fallback)
    {
        var value = _text.GetString(key);
        return string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }
}
