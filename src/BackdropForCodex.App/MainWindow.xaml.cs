using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using BackdropForCodex.App.Services.Appearance;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.App.Views;
using BackdropForCodex.Core.Media;
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
    private readonly ThemeController _themeController;
    private bool _allowClose;
    private bool _closeTipInProgress;
    private bool _videoPreviewSelected;
    private bool _previewPlaybackRequested;
    private bool _reducedMotion;
    private string? _previewPath;
    private Task? _initializationTask;

    public MainWindow(
        MainWindowViewModel viewModel,
        IAppTextProvider text)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _text = text ?? throw new ArgumentNullException(nameof(text));
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
        UpdatePreview(_viewModel.SelectedMediaPath);
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
        _themeController.Dispose();
        StopAndClearPreview();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedMediaPath))
        {
            UpdatePreview(_viewModel.SelectedMediaPath);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsPaused))
        {
            _previewPlaybackRequested = !_viewModel.IsPaused;
            SynchronizePreviewPlayback();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.ThemeMode))
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
                    "The endpoint is limited to this device and remains available until Codex exits. Backdrop verifies the official package and a reviewed version before connecting."),
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.Gray,
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
                ? Text("Action_Reset", "Revoke acknowledgement")
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

        if (resetRequested)
        {
            await ShowResetConfirmationAsync();
        }
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
                    "This restores the official background, clears settings and recent media, revokes acknowledgement, resets UI preferences, and removes only a shortcut verified as owned by this app."),
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
            UpdatePreview(_viewModel.SelectedMediaPath);
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
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        try
        {
            if (!TryGetSingleDroppedFile(e.Data, out var path))
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
        var valid = TryGetSingleDroppedFile(e.Data, out _);
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private static bool TryGetSingleDroppedFile(IDataObject data, out string path)
    {
        path = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths ||
            !File.Exists(paths[0]))
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

    private void UpdatePreview(string? path)
    {
        if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _previewPath = path;
        StopAndClearPreview();
        if (path is null || !File.Exists(path))
        {
            EmptyPreview.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            if (_viewModel.SelectedMediaKind == MediaKind.Video)
            {
                VideoPreview.Source = new Uri(path, UriKind.Absolute);
                VideoPreview.Position = TimeSpan.Zero;
                VideoPreview.Visibility = Visibility.Visible;
                EmptyPreview.Visibility = Visibility.Collapsed;
                _videoPreviewSelected = true;
                _previewPlaybackRequested = !_viewModel.IsPaused;
                SynchronizePreviewPlayback();
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1600;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            ImagePreview.Source = bitmap;
            ImagePreview.Visibility = Visibility.Visible;
            EmptyPreview.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure();
        }
    }

    private void VideoPreview_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_reducedMotion)
        {
            try
            {
                VideoPreview.Pause();
            }
            catch (InvalidOperationException)
            {
                // The media graph closed between MediaOpened and the reduced-motion pause.
            }
        }
    }

    private void VideoPreview_MediaEnded(object sender, RoutedEventArgs e)
    {
        try
        {
            VideoPreview.Position = TimeSpan.Zero;
            SynchronizePreviewPlayback();
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure();
        }
    }

    private void VideoPreview_MediaFailed(object sender, ExceptionRoutedEventArgs e) =>
        ShowPreviewFailure();

    private void Window_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e) =>
        SynchronizePreviewPlayback();

    private void SynchronizePreviewPlayback()
    {
        if (!_videoPreviewSelected || VideoPreview.Source is null)
        {
            return;
        }

        _reducedMotion = !SystemParameters.ClientAreaAnimation;
        try
        {
            if (IsVisible && _previewPlaybackRequested && !_reducedMotion)
            {
                VideoPreview.Play();
            }
            else
            {
                VideoPreview.Pause();
            }
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure();
        }
    }

    private void StopAndClearPreview()
    {
        try
        {
            VideoPreview.Stop();
            VideoPreview.Source = null;
        }
        catch (Exception) when (!IsLoaded)
        {
            // The media graph may already be torn down while the final window closes.
        }

        ImagePreview.Source = null;
        ImagePreview.Visibility = Visibility.Collapsed;
        VideoPreview.Visibility = Visibility.Collapsed;
        EmptyPreview.Visibility = Visibility.Visible;
        _videoPreviewSelected = false;
        _previewPlaybackRequested = false;
    }

    private void ShowPreviewFailure()
    {
        StopAndClearPreview();
        EmptyPreview.Visibility = Visibility.Visible;
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
                await _viewModel.MarkTrayTipShownAsync();
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
            Grid.SetRow(InspectorScroller, 0);
            Grid.SetColumn(InspectorScroller, 2);
            PreviewPane.MaxHeight = double.PositiveInfinity;
            return;
        }

        PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
        ColumnGap.Width = new GridLength(0);
        InspectorColumn.Width = new GridLength(0);
        InspectorColumn.MinWidth = 0;
        MainTopRow.Height = new GridLength(290);
        MainGapRow.Height = new GridLength(14);
        MainBottomRow.Height = new GridLength(1, GridUnitType.Star);
        Grid.SetRow(PreviewPane, 0);
        Grid.SetColumn(PreviewPane, 0);
        Grid.SetRow(InspectorScroller, 2);
        Grid.SetColumn(InspectorScroller, 0);
        PreviewPane.MaxHeight = 290;
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

    private static bool IsControlledPreviewException(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        FormatException or
        ArgumentException or
        InvalidOperationException or
        ExternalException or
        SecurityException;
}
