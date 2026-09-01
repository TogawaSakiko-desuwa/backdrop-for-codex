using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
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
    private bool _isViewModelSubscribed;

    public SettingsDialogContent(
        MainWindowViewModel viewModel,
        IAppTextProvider text)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        InitializeComponent();
        ThemeComboBox.SelectedValue = _viewModel.ThemeMode;
        RefreshPreferencesProtectionState();
        VersionText.Text =
            $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";
        RefreshRiskState();
        RecoveryCard.Visibility = _viewModel.HasVersion1Backup
            ? Visibility.Visible
            : Visibility.Collapsed;
        RestoreBackupButton.IsEnabled = _viewModel.CanRestoreVersion1Backup;
        Loaded += SettingsDialogContent_Loaded;
        Unloaded += SettingsDialogContent_Unloaded;
        _initialized = true;
    }

    public event EventHandler<ThemeModeChangedEventArgs>? ThemeChangeRequested;

    public event EventHandler? RiskRevokeRequested;

    public event EventHandler? DiagnosticExportRequested;

    public event EventHandler? RestoreBackupRequested;

    public event EventHandler? ResetRequested;

    private void SettingsDialogContent_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (!_isViewModelSubscribed)
        {
            _viewModel.Settings.PropertyChanged += Settings_PropertyChanged;
            _isViewModelSubscribed = true;
        }

        RefreshPreferencesProtectionState();
    }

    private void SettingsDialogContent_Unloaded(object sender, RoutedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        if (_isViewModelSubscribed)
        {
            _viewModel.Settings.PropertyChanged -= Settings_PropertyChanged;
            _isViewModelSubscribed = false;
        }
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName is
            nameof(SettingsManagementViewModel.HasProtectedPreferences) or
            nameof(SettingsManagementViewModel.FuturePreferencesVersionDisplay))
        {
            RefreshPreferencesProtectionState();
        }
    }

    private void RefreshPreferencesProtectionState()
    {
        ThemeComboBox.IsEnabled = !_viewModel.HasProtectedPreferences;
        if (!_viewModel.HasProtectedPreferences)
        {
            ProtectedPreferencesNotice.Visibility = Visibility.Collapsed;
            AutomationProperties.SetHelpText(ThemeComboBox, string.Empty);
            return;
        }

        ProtectedPreferencesNotice.Text =
            _viewModel.Settings.FuturePreferencesVersionDisplay is { } version
            ? string.Format(
                CultureInfo.CurrentCulture,
                _text.GetStringOrFallback(
                    "Settings_ProtectedPreferencesFuture",
                    "App preferences use newer schema version {0} and are read-only. Use Reset app to replace them."),
                version)
            : _text.GetStringOrFallback(
                "Settings_ProtectedPreferencesUnreadable",
                "App preferences could not be safely read and are protected from replacement. Use Reset app to replace them.");
        ProtectedPreferencesNotice.Visibility = Visibility.Visible;
        AutomationProperties.SetHelpText(ThemeComboBox, ProtectedPreferencesNotice.Text);
    }

    public void RefreshRiskState()
    {
        RiskStateText.Text = _viewModel.Editor.AcceptedCdpRisk
            ? _text.GetStringOrFallback("Risk_AcknowledgementSaved", "Acknowledgement saved")
            : _text.GetStringOrFallback(
                "Risk_NotAcknowledged",
                "Not confirmed. You will be asked before enhanced launch.");
        RevokeRiskButton.IsEnabled =
            _viewModel.Editor.AcceptedCdpRisk &&
            _viewModel.CanEdit;
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

    private void RevokeRiskButton_Click(object sender, RoutedEventArgs e)
    {
        RevokeRiskButton.IsEnabled = false;
        RiskRevokeRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) =>
        ResetRequested?.Invoke(this, EventArgs.Empty);

    private void RestoreBackupButton_Click(object sender, RoutedEventArgs e) =>
        RestoreBackupRequested?.Invoke(this, EventArgs.Empty);

    private void DiagnosticExportButton_Click(object sender, RoutedEventArgs e) =>
        DiagnosticExportRequested?.Invoke(this, EventArgs.Empty);

}
