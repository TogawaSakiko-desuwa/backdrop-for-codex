using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BackdropForCodex.App.Services.Appearance;
using BackdropForCodex.App.Services.Diagnostics;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.App.Views;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace BackdropForCodex.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF Closed lifecycle releases the theme watcher deterministically.")]
public partial class MainWindow : FluentWindow
{
    private const double ResponsiveBreakpoint = 960;

    private readonly MainWindowViewModel _viewModel;
    private readonly IAppTextProvider _text;
    private readonly IDiagnosticReportService _diagnosticReports;
    private readonly ThemeController _themeController;
    private bool _allowClose;
    private bool _closeTipInProgress;
    private Task? _initializationTask;

    public MainWindow(
        MainWindowViewModel viewModel,
        IAppTextProvider text,
        IDiagnosticReportService diagnosticReports)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _diagnosticReports =
            diagnosticReports ?? throw new ArgumentNullException(nameof(diagnosticReports));
        InitializeComponent();
        DataContext = _viewModel;
        _themeController = new ThemeController(this);
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public Task InitializeAsync() =>
        _initializationTask ??= InitializeCoreAsync();

    public async Task BeginAutoLaunchAsync()
    {
        try
        {
            await InitializeAsync();
            if (_viewModel.HasProtectedSettings)
            {
                return;
            }

            if (!_viewModel.AcceptedCdpRisk &&
                !await ShowRiskDialogAsync(allowRevoke: false))
            {
                return;
            }

            var outcome = await _viewModel.AutoLaunchAsync();
            if (outcome == AutoLaunchOutcome.Applied)
            {
                Hide();
            }
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    public Task DisableWallpaperAsync() => _viewModel.DisableAsync();

    internal void ReportUnexpectedError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _viewModel.ShowUnexpectedError(exception);
    }

    internal void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    private async Task InitializeCoreAsync()
    {
        await _viewModel.InitializeAsync();
        _themeController.Apply(_viewModel.ThemeMode);
        ClampInitialSizeToWorkArea();
        UpdateResponsiveLayout(ActualWidth);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync();
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Dispose();
        _themeController.Dispose();
        PreviewView.ReleaseMedia();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(MainWindowViewModel.ThemeMode))
        {
            _themeController.Apply(_viewModel.ThemeMode);
        }
    }

    private void ChooseMedia_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = Text("Action_SelectMedia", "Choose wallpaper media"),
                Filter =
                    "Supported media|*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.webm|" +
                    "Images|*.png;*.jpg;*.jpeg;*.webp|Videos|*.mp4;*.webm",
                CheckFileExists = true,
                Multiselect = false,
            };
            if (dialog.ShowDialog(this) == true)
            {
                _viewModel.SelectMedia(dialog.FileName);
            }
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_viewModel.AcceptedCdpRisk &&
                !await ShowRiskDialogAsync(allowRevoke: false))
            {
                return;
            }

            _ = await _viewModel.ApplyAsync();
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private async void ReviewRisk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = await ShowRiskDialogAsync(allowRevoke: _viewModel.AcceptedCdpRisk);
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private async Task<bool> ShowRiskDialogAsync(bool allowRevoke)
    {
        var content = new StackPanel
        {
            MaxWidth = 520,
        };
        _ = content.Children.Add(
            new TextBlock
            {
                Text = Text(
                    "Risk_Summary",
                    "Enhanced launch starts Codex with a local Chromium debugging endpoint."),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
        _ = content.Children.Add(
            new TextBlock
            {
                Text = Text(
                    "Risk_Detail",
                    "The endpoint is limited to this device and remains available until Codex exits. Backdrop verifies the official package, process, session, endpoint, and target before runtime capability probes decide which visual effects may run."),
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground =
                    TryFindResource("TextFillColorSecondaryBrush") as
                        System.Windows.Media.Brush ??
                    SystemColors.GrayTextBrush,
            });

        var dialog = new ContentDialog(DialogHost)
        {
            Title = Text("Risk_Title", "Allow local Codex debugging?"),
            Content = content,
            PrimaryButtonText = _viewModel.AcceptedCdpRisk
                ? Text("Action_Close", "Close")
                : Text("Risk_Acknowledgement", "I understand and want to continue"),
            CloseButtonText = _viewModel.AcceptedCdpRisk
                ? string.Empty
                : Text("Action_Cancel", "Cancel"),
            SecondaryButtonText = allowRevoke
                ? Text("Action_RevokeRisk", "Revoke acknowledgement")
                : string.Empty,
            PrimaryButtonAppearance = ControlAppearance.Primary,
            DialogMaxWidth = 600,
        };

        var result = await dialog.ShowAsync(CancellationToken.None);
        if (result == ContentDialogResult.Secondary && allowRevoke)
        {
            await _viewModel.RevokeRiskAsync();
            return false;
        }

        if (result != ContentDialogResult.Primary)
        {
            return false;
        }

        if (!_viewModel.AcceptedCdpRisk)
        {
            await _viewModel.AcceptRiskAsync();
        }

        return true;
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShowSettingsDialogAsync();
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private async Task ShowSettingsDialogAsync()
    {
        var content = new SettingsDialogContent(_viewModel, _text);
        var resetRequested = false;
        var diagnosticExportRequested = false;
        var restoreBackupRequested = false;
        ContentDialog? dialog = null;

        content.ThemeChangeRequested += async (_, eventArgs) =>
        {
            try
            {
                await _viewModel.SetThemeModeAsync(eventArgs.Mode);
            }
            catch (Exception exception)
            {
                ReportUnexpectedError(exception);
            }
        };
        content.RiskRevokeRequested += async (_, _) =>
        {
            try
            {
                await _viewModel.RevokeRiskAsync();
                content.RefreshRiskState();
            }
            catch (Exception exception)
            {
                ReportUnexpectedError(exception);
            }
        };
        content.ResetRequested += (_, _) =>
        {
            resetRequested = true;
            dialog?.Hide(ContentDialogResult.Secondary);
        };
        content.DiagnosticExportRequested += (_, _) =>
        {
            diagnosticExportRequested = true;
            dialog?.Hide(ContentDialogResult.Secondary);
        };
        content.RestoreBackupRequested += (_, _) =>
        {
            restoreBackupRequested = true;
            dialog?.Hide(ContentDialogResult.Secondary);
        };

        dialog = new ContentDialog(DialogHost)
        {
            Title = Text("Action_Settings", "Settings"),
            Content = content,
            CloseButtonText = Text("Action_Close", "Close"),
            DialogWidth = 640,
            DialogMaxWidth = 680,
            DialogMaxHeight = Math.Max(420, ActualHeight - 80),
        };
        _ = await dialog.ShowAsync(CancellationToken.None);

        if (restoreBackupRequested)
        {
            await _viewModel.RestoreVersion1BackupAsync();
        }
        else if (resetRequested)
        {
            await ShowResetConfirmationAsync();
        }
        else if (diagnosticExportRequested)
        {
            await ExportDiagnosticReportAsync();
        }
    }

    private async Task ExportDiagnosticReportAsync()
    {
        var disclosure = new ContentDialog(DialogHost)
        {
            Title = Text("Diagnostics_Title", "Export diagnostic report?"),
            Content = new TextBlock
            {
                Text = Text(
                    "Diagnostics_Disclosure",
                    "The report contains app, Windows, runtime stage, and capability summaries. It does not include media paths, file names, page titles, URLs, DOM or chat text, settings JSON, or stable identifiers."),
                MaxWidth = 520,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Text("Diagnostics_Export", "Choose save location"),
            CloseButtonText = Text("Action_Cancel", "Cancel"),
            PrimaryButtonAppearance = ControlAppearance.Primary,
            DialogMaxWidth = 600,
        };
        if (await disclosure.ShowAsync(CancellationToken.None) != ContentDialogResult.Primary)
        {
            return;
        }

        var picker = new SaveFileDialog
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".json",
            FileName = "BackdropForCodex-diagnostic.json",
            Filter = "JSON diagnostic report (*.json)|*.json",
            OverwritePrompt = true,
            Title = Text("Diagnostics_SaveTitle", "Save diagnostic report"),
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var runtime = _diagnosticReports.CreateRuntimeSnapshot(
            _viewModel.RuntimePhase,
            _viewModel.IsActive,
            _viewModel.IsPaused);
        var compatibility = _diagnosticReports.CreateCompatibilitySnapshot(
            _viewModel.WallpaperCompatibility);
        var report = _diagnosticReports.CreateReport(runtime, compatibility);
        await _diagnosticReports.WriteAsync(
            picker.FileName,
            report,
            CancellationToken.None);

        var complete = new ContentDialog(DialogHost)
        {
            Title = Text("Diagnostics_CompleteTitle", "Diagnostic report saved"),
            Content = new TextBlock
            {
                Text = Text(
                    "Diagnostics_CompleteMessage",
                    "The allow-listed local report was saved to the location you selected."),
                MaxWidth = 480,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = Text("Action_Close", "Close"),
            DialogMaxWidth = 560,
        };
        _ = await complete.ShowAsync(CancellationToken.None);
    }

    private async Task ShowResetConfirmationAsync()
    {
        var dialog = new ContentDialog(DialogHost)
        {
            Title = Text("Settings_ResetTitle", "Reset Backdrop for Codex?"),
            Content = new TextBlock
            {
                Text = Text(
                    "Settings_ResetDescription",
                    "This restores the official background; permanently deletes settings, recent media, and any preserved V1 migration backup; revokes acknowledgement; resets UI preferences; and removes only a shortcut verified as owned by this app."),
                MaxWidth = 520,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = Text("Action_Reset", "Reset"),
            CloseButtonText = Text("Action_Cancel", "Cancel"),
            PrimaryButtonAppearance = ControlAppearance.Danger,
            DialogMaxWidth = 600,
        };
        if (await dialog.ShowAsync(CancellationToken.None) == ContentDialogResult.Primary)
        {
            await _viewModel.ResetEverythingAsync();
            _themeController.Apply(_viewModel.ThemeMode);
        }
    }

    private async void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path })
        {
            return;
        }

        try
        {
            await _viewModel.RemoveRecentAsync(path);
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private void RecentMediaList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (RecentMediaList.SelectedItem is not RecentMediaItem item)
        {
            return;
        }

        RecentMediaList.SelectedItem = null;
        try
        {
            _viewModel.SelectMedia(item.Path);
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private void Window_DragEnter(object sender, DragEventArgs e) =>
        UpdateDragState(e);

    private void Window_DragOver(object sender, DragEventArgs e) =>
        UpdateDragState(e);

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        PreviewView.SetDropTargetVisible(false);
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        PreviewView.SetDropTargetVisible(false);
        try
        {
            if (!_viewModel.CanEdit ||
                !TryGetSingleDroppedFile(e.Data, out var path))
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            _viewModel.SelectMedia(path);
            e.Effects = DragDropEffects.Copy;
        }
        catch (Exception exception)
        {
            e.Effects = DragDropEffects.None;
            ReportUnexpectedError(exception);
        }
        finally
        {
            e.Handled = true;
        }
    }

    private void UpdateDragState(DragEventArgs e)
    {
        var valid =
            _viewModel.CanEdit &&
            TryGetSingleDroppedFile(e.Data, out _);
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        PreviewView.SetDropTargetVisible(valid);
        e.Handled = true;
    }

    private static bool TryGetSingleDroppedFile(IDataObject data, out string path)
    {
        path = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths ||
            string.IsNullOrWhiteSpace(paths[0]) ||
            !Path.IsPathFullyQualified(paths[0]))
        {
            return false;
        }

        path = paths[0];
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);
    }

    private void PreviewView_FocusChangeRequested(
        object? sender,
        WallpaperFocusChangeRequestedEventArgs e)
    {
        _ = sender;
        _viewModel.SetFocus(e.FocusX, e.FocusY);
    }

    private void CenterFocus_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!_viewModel.CanAdjustFocus)
        {
            return;
        }

        _viewModel.ResetFocus();
        PreviewView.ShowCurrentFocus();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closeTipInProgress)
        {
            return;
        }

        _closeTipInProgress = true;
        try
        {
            if (!_viewModel.HasShownTrayTip)
            {
                var dialog = new ContentDialog(DialogHost)
                {
                    Title = Text("Tray_FirstCloseTitle", "Still running"),
                    Content = new TextBlock
                    {
                        Text = Text(
                            "Tray_FirstCloseMessage",
                            "Backdrop for Codex moved to the notification area so the wallpaper can stay active."),
                        MaxWidth = 440,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = Text("Action_Confirm", "Got it"),
                    PrimaryButtonAppearance = ControlAppearance.Primary,
                    DialogMaxWidth = 520,
                };
                _ = await dialog.ShowAsync(CancellationToken.None);
                try
                {
                    await _viewModel.MarkTrayTipShownAsync();
                }
                catch (Exception exception)
                {
                    ReportUnexpectedError(exception);
                }
            }

            Hide();
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
        finally
        {
            _closeTipInProgress = false;
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveLayout(e.NewSize.Width);

    private void UpdateResponsiveLayout(double width)
    {
        var isNarrow = width < ResponsiveBreakpoint;
        if (!isNarrow)
        {
            PreviewColumn.Width = new GridLength(3, GridUnitType.Star);
            ColumnGap.Width = new GridLength(20);
            InspectorColumn.Width = new GridLength(2, GridUnitType.Star);
            InspectorColumn.MinWidth = 330;
            MainTopRow.Height = new GridLength(1, GridUnitType.Star);
            MainGapRow.Height = new GridLength(0);
            MainBottomRow.Height = new GridLength(0);
            Grid.SetRow(PreviewPane, 0);
            Grid.SetColumn(PreviewPane, 0);
            Grid.SetRow(InspectorPane, 0);
            Grid.SetColumn(InspectorPane, 2);
            PreviewPane.MaxHeight = double.PositiveInfinity;
            PreviewView.SurfaceMinimumHeight = 220;
            RecentMediaCard.Visibility = Visibility.Visible;
            return;
        }

        PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
        ColumnGap.Width = new GridLength(0);
        InspectorColumn.Width = new GridLength(0);
        InspectorColumn.MinWidth = 0;
        MainTopRow.Height = new GridLength(1, GridUnitType.Star);
        MainGapRow.Height = new GridLength(12);
        MainBottomRow.Height = new GridLength(1.15, GridUnitType.Star);
        Grid.SetRow(PreviewPane, 0);
        Grid.SetColumn(PreviewPane, 0);
        Grid.SetRow(InspectorPane, 2);
        Grid.SetColumn(InspectorPane, 0);
        PreviewPane.MaxHeight = double.PositiveInfinity;
        PreviewView.SurfaceMinimumHeight = 120;
        RecentMediaCard.Visibility = Visibility.Collapsed;
    }

    private void ClampInitialSizeToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(MinWidth, workArea.Width);
        MaxHeight = Math.Max(MinHeight, workArea.Height);
        Width = Math.Clamp(1040, MinWidth, MaxWidth);
        Height = Math.Clamp(700, MinHeight, MaxHeight);
    }

    private string Text(string key, string fallback)
    {
        var value = _text.GetString(key);
        return string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }
}
