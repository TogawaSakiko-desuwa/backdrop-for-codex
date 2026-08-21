using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BackdropForCodex.App.Services.Appearance;
using BackdropForCodex.App.Services.Diagnostics;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.App.Views;
using BackdropForCodex.Core.Settings;
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
    public static readonly DependencyProperty IsWorkbenchHighContrastProperty =
        DependencyProperty.Register(
            nameof(IsWorkbenchHighContrast),
            typeof(bool),
            typeof(MainWindow),
            new FrameworkPropertyMetadata(false));

    private const double MobileBreakpoint = 960;
    private const double ExpandedRailBreakpoint = 1280;
    private const double DesignedMinimumWidth = 640;
    private const double DesignedMinimumHeight = 520;
    private const double DesignedInitialWidth = 1440;
    private const double DesignedInitialHeight = 860;

    private readonly MainWindowViewModel _viewModel;
    private readonly IAppTextProvider _text;
    private readonly IDiagnosticReportService _diagnosticReports;
    private readonly ThemeController _themeController;
    private bool _allowClose;
    private bool _closeTipInProgress;
    private bool _isLibraryDrawerOpen;
    private bool _isWallpaperEngineLibraryOpen;
    private bool _statusAnnouncementPending;
    private MobileWorkbenchPane _mobilePane = MobileWorkbenchPane.Preview;
    private Task? _initializationTask;

    public bool IsWorkbenchHighContrast
    {
        get => (bool)GetValue(IsWorkbenchHighContrastProperty);
        set => SetValue(IsWorkbenchHighContrastProperty, value);
    }

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
        _viewModel.RenameProfilePromptAsync = ShowRenameProfileDialogAsync;
        _viewModel.DeleteProfilePromptAsync = ShowDeleteProfileDialogAsync;
        _viewModel.WebWallpaperPrivacyPromptAsync = ShowWebWallpaperPrivacyDialogAsync;
        _viewModel.RestoreProfileFocus = LibraryPane.FocusSelectedProfile;
        _themeController = new ThemeController(this);
        IsWorkbenchHighContrast = SystemParameters.HighContrast;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
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

            if (!await _viewModel.EnsureWebWallpaperPrivacyAcknowledgedAsync())
            {
                return;
            }

            if (_viewModel.Editor.RequiresCdpRisk &&
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

    internal async Task RequestShutdownAsync(Func<Task> shutdown)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        if (_viewModel.IsDraftDirty)
        {
            Show();
            Activate();
            if (!await ConfirmDiscardDraftAsync(
                    _text.GetStringOrFallback("Exit_DirtyTitle", "Discard draft and exit?"),
                    _text.GetStringOrFallback(
                        "Exit_DirtyMessage",
                        "The current profile draft has unsaved changes. Exit will discard them without applying."),
                    _text.GetStringOrFallback("Exit_DiscardAction", "Discard and exit")))
            {
                return;
            }
        }

        await shutdown();
    }

    private async Task InitializeCoreAsync()
    {
        // Size the shell before provider discovery begins. Wallpaper Engine discovery can outlive
        // the first layout pass, and a delayed clamp must never undo a user's responsive resize.
        ClampInitialSizeToWorkArea();
        UpdateResponsiveLayout(ActualWidth);
        await _viewModel.InitializeAsync();
        ApplyTheme();
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
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        _viewModel.RestoreProfileFocus = null;
        _viewModel.WebWallpaperPrivacyPromptAsync = null;
        _viewModel.Dispose();
        _themeController.Dispose();
        PreviewView.ReleaseMedia();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(MainWindowViewModel.ThemeMode))
        {
            ApplyTheme();
        }

        if (e.PropertyName is
            nameof(MainWindowViewModel.FooterStatusText) or
            nameof(MainWindowViewModel.StatusTitle) or
            nameof(MainWindowViewModel.StatusMessage) or
            nameof(MainWindowViewModel.IsStatusOpen))
        {
            QueueStatusAnnouncement();
        }
    }

    private void QueueStatusAnnouncement()
    {
        if (_statusAnnouncementPending || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _statusAnnouncementPending = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(
                () =>
                {
                    _statusAnnouncementPending = false;
                    if (!IsVisible)
                    {
                        return;
                    }

                    var peer = UIElementAutomationPeer.FromElement(StatusLiveRegion) ??
                        UIElementAutomationPeer.CreatePeerForElement(StatusLiveRegion);
                    peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                }));
    }

    private void ApplyTheme()
    {
        _themeController.Apply(_viewModel.ThemeMode);
        SetCurrentValue(
            IsWorkbenchHighContrastProperty,
            SystemParameters.HighContrast);
    }

    private void SystemParameters_StaticPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (eventArgs.PropertyName != nameof(SystemParameters.HighContrast))
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            SetCurrentValue(
                IsWorkbenchHighContrastProperty,
                SystemParameters.HighContrast);
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Normal,
            new Action(
                () => SetCurrentValue(
                    IsWorkbenchHighContrastProperty,
                    SystemParameters.HighContrast)));
    }

    private void ChooseMedia_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                Title = _text.GetStringOrFallback(
                    "Dialog_SelectWallpaperMediaTitle",
                    "Choose wallpaper media"),
                Filter =
                    _text.GetStringOrFallback(
                        "Dialog_WallpaperMediaFilter",
                        "Supported media|*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.webm|Images|*.png;*.jpg;*.jpeg;*.webp|Videos|*.mp4;*.webm"),
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

    private void ClearMedia_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            _viewModel.ClearSelectedMedia();
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private async Task<string?> ShowRenameProfileDialogAsync(
        ProfileRenameRequestedEventArgs request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var showValidation = false;
        while (true)
        {
            var input = new System.Windows.Controls.TextBox
            {
                Text = request.CurrentName,
                MaxLength = WallpaperProfile.MaximumNameLength,
                MinWidth = 360,
                Margin = new Thickness(0, 8, 0, 0),
            };
            input.SelectAll();
            var content = new StackPanel();
            _ = content.Children.Add(
                new TextBlock
                {
                    Text = _text.GetStringOrFallback(
                        "Profile_RenamePrompt",
                        "Enter a profile name (1–128 characters)."),
                    TextWrapping = TextWrapping.Wrap,
                });
            _ = content.Children.Add(input);
            if (showValidation)
            {
                _ = content.Children.Add(
                    new TextBlock
                    {
                        Text = _text.GetStringOrFallback(
                            "Profile_NameRequired",
                            "The profile name cannot be empty."),
                        Margin = new Thickness(0, 8, 0, 0),
                        Foreground = System.Windows.Media.Brushes.IndianRed,
                        TextWrapping = TextWrapping.Wrap,
                    });
            }

            var dialog = new ContentDialog(DialogHost)
            {
                Title = _text.GetStringOrFallback(
                    "Dialog_RenameProfileTitle",
                    "Rename profile"),
                Content = content,
                PrimaryButtonText = _text.GetStringOrFallback("Action_Confirm", "Confirm"),
                CloseButtonText = _text.GetStringOrFallback("Action_Cancel", "Cancel"),
                PrimaryButtonAppearance = ControlAppearance.Primary,
                DialogMaxWidth = 520,
            };
            if (await dialog.ShowAsync(CancellationToken.None) !=
                ContentDialogResult.Primary)
            {
                return null;
            }

            var trimmed = input.Text.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }

            showValidation = true;
        }
    }

    private async Task<bool> ShowDeleteProfileDialogAsync(
        ProfileDeleteRequestedEventArgs request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialog = new ContentDialog(DialogHost)
        {
            Title = _text.GetStringOrFallback("Profile_DeleteTitle", "Delete profile?"),
            Content = new TextBlock
            {
                Text = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    _text.GetStringOrFallback(
                        "Profile_DeleteMessage",
                        "\"{0}\" will be deleted. Regions using it will be rebound to \"{1}\". Media remains in the catalog."),
                    request.ProfileName,
                    request.ReplacementProfileName),
                MaxWidth = 500,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = _text.GetStringOrFallback("Action_DeleteProfile", "Delete"),
            CloseButtonText = _text.GetStringOrFallback("Action_Cancel", "Cancel"),
            PrimaryButtonAppearance = ControlAppearance.Danger,
            DialogMaxWidth = 580,
        };
        return await dialog.ShowAsync(CancellationToken.None) ==
            ContentDialogResult.Primary;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await _viewModel.EnsureWebWallpaperPrivacyAcknowledgedAsync())
            {
                return;
            }

            if (_viewModel.Editor.RequiresCdpRisk &&
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

    private async Task<bool> ShowWebWallpaperPrivacyDialogAsync(
        CancellationToken cancellationToken)
    {
        var content = new StackPanel
        {
            MaxWidth = 540,
        };
        content.Children.Add(
            new TextBlock
            {
                Text = _text.GetStringOrFallback(
                    "WebPrivacy_Message",
                    "Third-party Web wallpapers run inside Wallpaper Engine, may access the network, and may play sound. Backdrop does not enumerate, mute, or change Wallpaper Engine audio sessions. It sends captured pixels, but no audio, into Codex and never exposes Codex content to the wallpaper."),
                TextWrapping = TextWrapping.Wrap,
            });
        content.Children.Add(
            new TextBlock
            {
                Margin = new Thickness(0, 12, 0, 0),
                Text = _text.GetStringOrFallback(
                    "WebPrivacy_TrustNote",
                    "Only continue with wallpapers whose publisher and contents you trust."),
                Foreground = SystemColors.GrayTextBrush,
                TextWrapping = TextWrapping.Wrap,
            });

        var dialog = new ContentDialog(DialogHost)
        {
            Title = _text.GetStringOrFallback(
                "WebPrivacy_Title",
                "Before using a Web wallpaper"),
            Content = content,
            PrimaryButtonText = _text.GetStringOrFallback(
                "WebPrivacy_Acknowledge",
                "I understand and want to continue"),
            CloseButtonText = _text.GetStringOrFallback("Action_Cancel", "Cancel"),
            PrimaryButtonAppearance = ControlAppearance.Primary,
            DialogMaxWidth = 620,
        };
        return await dialog.ShowAsync(cancellationToken) == ContentDialogResult.Primary;
    }

    private void RetryStatusApply_Click(object sender, RoutedEventArgs e) =>
        Apply_Click(sender, e);

    private void DismissStatus_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _viewModel.IsStatusOpen = false;
    }

    private async void ViewStatusDetails_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        try
        {
            var content = new StackPanel
            {
                MaxWidth = 520,
            };
            content.Children.Add(
                new TextBlock
                {
                    Text = _viewModel.StatusMessage,
                    TextWrapping = TextWrapping.Wrap,
                });
            content.Children.Add(
                new TextBlock
                {
                    Margin = new Thickness(0, 12, 0, 0),
                    Text = _viewModel.FooterStatusText,
                    Foreground = SystemColors.GrayTextBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
            var dialog = new ContentDialog(DialogHost)
            {
                Title = _viewModel.StatusTitle,
                Content = content,
                SecondaryButtonText = _text.GetStringOrFallback(
                    "Diagnostics_Export",
                    "Export diagnostic report"),
                CloseButtonText = _text.GetStringOrFallback("Action_Close", "Close"),
                DialogMaxWidth = 600,
            };
            if (await dialog.ShowAsync(CancellationToken.None) ==
                ContentDialogResult.Secondary)
            {
                await ExportDiagnosticReportAsync();
            }
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
            _ = await ShowRiskDialogAsync(
                allowRevoke: _viewModel.Editor.AcceptedCdpRisk);
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
                Text = _text.GetStringOrFallback(
                    "Risk_Summary",
                    "Enhanced launch starts Codex with a local Chromium debugging endpoint."),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
        _ = content.Children.Add(
            new TextBlock
            {
                Text = _text.GetStringOrFallback(
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
            Title = _text.GetStringOrFallback("Risk_Title", "Allow local Codex debugging?"),
            Content = content,
            PrimaryButtonText = _viewModel.Editor.AcceptedCdpRisk
                ? _text.GetStringOrFallback("Action_Close", "Close")
                : _text.GetStringOrFallback("Risk_Acknowledgement", "I understand and want to continue"),
            CloseButtonText = _viewModel.Editor.AcceptedCdpRisk
                ? string.Empty
                : _text.GetStringOrFallback("Action_Cancel", "Cancel"),
            SecondaryButtonText = allowRevoke
                ? _text.GetStringOrFallback("Action_RevokeRisk", "Revoke acknowledgement")
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

        if (!_viewModel.Editor.AcceptedCdpRisk)
        {
            await _viewModel.AcceptRiskAsync();
        }

        return _viewModel.Editor.AcceptedCdpRisk;
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
            Title = _text.GetStringOrFallback("Action_Settings", "Settings"),
            Content = content,
            CloseButtonText = _text.GetStringOrFallback("Action_Close", "Close"),
            DialogWidth = 640,
            DialogMaxWidth = 680,
            DialogMaxHeight = Math.Max(420, ActualHeight - 80),
        };
        _ = await dialog.ShowAsync(CancellationToken.None);

        if (restoreBackupRequested)
        {
            if (!_viewModel.IsDraftDirty ||
                await ConfirmDiscardDraftAsync(
                    _text.GetStringOrFallback("Restore_DirtyTitle", "Discard draft and restore backup?"),
                    _text.GetStringOrFallback(
                        "Restore_DirtyMessage",
                        "Restoring the preserved backup will discard the current unsaved draft without applying it."),
                    _text.GetStringOrFallback("Restore_DiscardAction", "Discard and restore")))
            {
                await _viewModel.RestoreVersion1BackupAsync();
            }
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
            Title = _text.GetStringOrFallback(
                "Dialog_DiagnosticsExportTitle",
                "Export diagnostic report?"),
            Content = new TextBlock
            {
                Text = _text.GetStringOrFallback(
                    "Diagnostics_Disclosure",
                    "The report contains app, Windows, runtime stage, and capability summaries. It does not include media paths, file names, page titles, URLs, DOM or chat text, settings JSON, or stable identifiers."),
                MaxWidth = 520,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = _text.GetStringOrFallback(
                "Action_ChooseSaveLocation",
                "Choose save location"),
            CloseButtonText = _text.GetStringOrFallback("Action_Cancel", "Cancel"),
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
            Filter = _text.GetStringOrFallback(
                "Diagnostics_FileFilter",
                "Diagnostic report (*.json)|*.json"),
            OverwritePrompt = true,
            Title = _text.GetStringOrFallback("Diagnostics_SaveTitle", "Save diagnostic report"),
        };
        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var runtime = _diagnosticReports.CreateRuntimeSnapshot(
            _viewModel.RuntimePhase,
            _viewModel.IsActive,
            _viewModel.IsPaused,
            _viewModel.RuntimeError);
        var compatibility = _diagnosticReports.CreateCompatibilitySnapshot(
            _viewModel.WallpaperCompatibility);
        var report = _diagnosticReports.CreateReport(runtime, compatibility);
        await _diagnosticReports.WriteAsync(
            picker.FileName,
            report,
            CancellationToken.None);

        var complete = new ContentDialog(DialogHost)
        {
            Title = _text.GetStringOrFallback("Diagnostics_CompleteTitle", "Diagnostic report saved"),
            Content = new TextBlock
            {
                Text = _text.GetStringOrFallback(
                    "Diagnostics_CompleteMessage",
                    "The local report was saved to the location you selected."),
                MaxWidth = 480,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = _text.GetStringOrFallback("Action_Close", "Close"),
            DialogMaxWidth = 560,
        };
        _ = await complete.ShowAsync(CancellationToken.None);
    }

    private async Task ShowResetConfirmationAsync()
    {
        var dialog = new ContentDialog(DialogHost)
        {
            Title = _text.GetStringOrFallback("Settings_ResetTitle", "Reset Backdrop for Codex?"),
            Content = new TextBlock
            {
                Text = _text.GetStringOrFallback(
                "Settings_ResetDescription",
                "This restores the official background and permanently deletes all app settings, recent media, protected backups, enhanced-launch confirmation, appearance preferences, and the enhanced shortcut created by Backdrop."),
                MaxWidth = 520,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = _viewModel.IsDraftDirty
                ? _text.GetStringOrFallback("Reset_DiscardAction", "Discard and reset")
                : _text.GetStringOrFallback("Action_Reset", "Reset"),
            CloseButtonText = _text.GetStringOrFallback("Action_Cancel", "Cancel"),
            PrimaryButtonAppearance = ControlAppearance.Danger,
            DialogMaxWidth = 600,
        };
        if (await dialog.ShowAsync(CancellationToken.None) == ContentDialogResult.Primary)
        {
            await _viewModel.ResetEverythingAsync();
            ApplyTheme();
        }
    }

    private async Task<bool> ConfirmDiscardDraftAsync(
        string title,
        string message,
        string continueAction)
    {
        var dialog = new ContentDialog(DialogHost)
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                MaxWidth = 500,
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = continueAction,
            CloseButtonText = _text.GetStringOrFallback("Action_Cancel", "Cancel"),
            PrimaryButtonAppearance = ControlAppearance.Danger,
            DialogMaxWidth = 580,
        };
        return await dialog.ShowAsync(CancellationToken.None) ==
            ContentDialogResult.Primary;
    }

    private void Library_ChooseLocalMediaRequested(
        object sender,
        RoutedEventArgs e)
    {
        CloseLibraryDrawer();
        ChooseMedia_Click(sender, e);
    }

    private void LibraryPane_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
    {
        _ = sender;
        PrepareLibraryContextMenu(e.OriginalSource as DependencyObject);
    }

    internal void PrepareLibraryContextMenu(DependencyObject? originalSource)
    {
        var recent = FindAncestorDataContext<RecentMediaItem>(
            originalSource,
            LibraryPane);
        var removeItem = LibraryPane.ContextMenu?.Items
            .OfType<System.Windows.Controls.MenuItem>()
            .FirstOrDefault(
                item => AutomationProperties.GetAutomationId(item) ==
                    "RemoveSelectedRecentMenuItem");
        if (removeItem is not null)
        {
            removeItem.CommandParameter = recent;
        }
    }

    private static T? FindAncestorDataContext<T>(
        DependencyObject? source,
        DependencyObject boundary)
        where T : class
    {
        for (var current = source; current is not null;)
        {
            if (current is FrameworkElement { DataContext: T match })
            {
                return match;
            }

            if (ReferenceEquals(current, boundary))
            {
                return null;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void Library_SelectedProfileChanged(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        CloseLibraryDrawer(restoreFocus: true);
    }

    private void Library_SelectedRecentMediaChanged(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (LibraryPane.SelectedRecentMedia is not { } item)
        {
            return;
        }

        LibraryPane.SelectedRecentMedia = null;
        try
        {
            _viewModel.SelectSource(item.Reference);
            CloseLibraryDrawer(restoreFocus: true);
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private void Library_SourceInvoked(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (LibraryPane.SelectedSource is not { } source)
        {
            return;
        }

        LibraryPane.SelectedSource = null;
        try
        {
            _viewModel.SelectSource(source);
            CloseLibraryDrawer(restoreFocus: true);
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private async void Library_WallpaperEngineRequested(
        object sender,
        RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        CloseLibraryDrawer();
        _isWallpaperEngineLibraryOpen = true;
        WallpaperEngineLibraryModalLayer.Visibility = Visibility.Visible;
        UpdateWallpaperEngineLibraryLayout(ActualWidth);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(WallpaperEngineLibrary.FocusSearch));
        try
        {
            await _viewModel
                .RefreshWallpaperEngineLibraryAsync()
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A newer library-open request owns the latest discovery snapshot.
        }
        catch (ObjectDisposedException)
        {
            // Window shutdown can supersede an in-flight discovery.
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private void WallpaperEngineLibrary_AssignRequested(
        object? sender,
        EventArgs e)
    {
        _ = sender;
        _ = e;
        if (WallpaperEngineLibrary.SelectedSource is not { } source)
        {
            return;
        }

        try
        {
            _viewModel.SelectSource(source);
            CloseWallpaperEngineLibrary(restoreFocus: true);
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private void WallpaperEngineLibrary_CloseRequested(
        object? sender,
        EventArgs e)
    {
        _ = sender;
        _ = e;
        CloseWallpaperEngineLibrary(restoreFocus: true);
    }

    private void WallpaperEngineLibraryScrim_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        CloseWallpaperEngineLibrary(restoreFocus: true);
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
        if (!_viewModel.Editor.CanAdjustFocus)
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
            // Release the native theme watcher while the HWND still exists.
            // Closed is too late for Wpf.Ui.SystemThemeWatcher.UnWatch.
            _themeController.Dispose();
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
                    Title = _text.GetStringOrFallback("Tray_FirstCloseTitle", "Still running"),
                    Content = new TextBlock
                    {
                        Text = _text.GetStringOrFallback(
                            "Tray_FirstCloseMessage",
                            "Backdrop for Codex moved to the notification area so the wallpaper can stay active."),
                        MaxWidth = 440,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = _text.GetStringOrFallback("Action_GotIt", "Got it"),
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
        var isMobile = UsesStackedLayout(width);
        var useCompactRail = UsesCompactRail(width);
        UpdateWallpaperEngineLibraryLayout(width);
        InspectorHost.BorderThickness = isMobile
            ? new Thickness(0)
            : new Thickness(1, 0, 0, 0);
        FooterCommandBar.Padding = isMobile
            ? new Thickness(12, 8, 12, 8)
            : new Thickness(16, 10, 16, 10);
        FooterSecondaryRow.Height = isMobile
            ? GridLength.Auto
            : new GridLength(0);
        Grid.SetRow(StatusLiveRegion, 0);
        Grid.SetColumn(StatusLiveRegion, 0);
        Grid.SetColumnSpan(StatusLiveRegion, isMobile ? 2 : 1);
        Grid.SetRow(FooterActions, isMobile ? 1 : 0);
        Grid.SetColumn(FooterActions, isMobile ? 0 : 1);
        Grid.SetColumnSpan(FooterActions, isMobile ? 2 : 1);
        FooterActions.Margin = isMobile
            ? new Thickness(0, 8, 0, 0)
            : new Thickness(0);
        FooterStatusHost.Margin = isMobile
            ? new Thickness(0)
            : new Thickness(0, 0, 16, 0);
        StatusMessageText.Visibility = isMobile
            ? Visibility.Collapsed
            : Visibility.Visible;
        MobileToolbarRow.Height = isMobile
            ? new GridLength(48)
            : new GridLength(0);
        MobileToolbar.Visibility = isMobile
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!isMobile)
        {
            _isLibraryDrawerOpen = false;
            LibraryScrim.Visibility = Visibility.Collapsed;
            LibraryPane.Visibility = Visibility.Visible;
            LibraryPane.IsCompact = useCompactRail;
            LibraryPane.HorizontalAlignment = HorizontalAlignment.Stretch;
            LibraryColumn.Width = new GridLength(
                useCompactRail
                    ? WallpaperLibraryView.CompactWidth
                    : WallpaperLibraryView.ExpandedWidth);
            PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
            InspectorColumn.Width = new GridLength(360);
            Grid.SetRow(LibraryPane, 1);
            Grid.SetRowSpan(LibraryPane, 1);
            Grid.SetColumn(LibraryPane, 0);
            Grid.SetColumnSpan(LibraryPane, 1);
            Grid.SetRow(PreviewPane, 1);
            Grid.SetColumn(PreviewPane, 1);
            Grid.SetColumnSpan(PreviewPane, 1);
            Grid.SetRow(InspectorHost, 1);
            Grid.SetColumn(InspectorHost, 2);
            Grid.SetColumnSpan(InspectorHost, 1);
            PreviewPane.Visibility = Visibility.Visible;
            InspectorHost.Visibility = Visibility.Visible;
            PreviewView.SurfaceMinimumHeight = 220;
            UpdateMobileModeButtons();
            return;
        }

        LibraryColumn.Width = new GridLength(0);
        PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
        InspectorColumn.Width = new GridLength(0);
        LibraryPane.IsCompact = false;
        LibraryPane.HorizontalAlignment = HorizontalAlignment.Left;
        LibraryPane.Visibility = _isLibraryDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        LibraryScrim.Visibility = _isLibraryDrawerOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        Grid.SetRow(LibraryPane, 0);
        Grid.SetRowSpan(LibraryPane, 2);
        Grid.SetColumn(LibraryPane, 0);
        Grid.SetColumnSpan(LibraryPane, 3);
        Grid.SetRow(PreviewPane, 1);
        Grid.SetColumn(PreviewPane, 0);
        Grid.SetColumnSpan(PreviewPane, 3);
        Grid.SetRow(InspectorHost, 1);
        Grid.SetColumn(InspectorHost, 0);
        Grid.SetColumnSpan(InspectorHost, 3);
        PreviewPane.Visibility = _mobilePane == MobileWorkbenchPane.Preview
            ? Visibility.Visible
            : Visibility.Collapsed;
        InspectorHost.Visibility = _mobilePane == MobileWorkbenchPane.Adjust
            ? Visibility.Visible
            : Visibility.Collapsed;
        PreviewView.SurfaceMinimumHeight = 120;
        UpdateMobileModeButtons();
    }

    internal static bool UsesStackedLayout(double width) =>
        width < MobileBreakpoint;

    internal static bool UsesCompactRail(double width) =>
        width >= MobileBreakpoint && width < ExpandedRailBreakpoint;

    private void LibraryDrawer_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        _isLibraryDrawerOpen = !_isLibraryDrawerOpen;
        UpdateResponsiveLayout(ActualWidth);
        if (_isLibraryDrawerOpen)
        {
            LibraryPane.FocusSelectedProfile();
        }
    }

    private void LibraryScrim_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        CloseLibraryDrawer(restoreFocus: true);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (CloseWallpaperEngineLibrary(restoreFocus: true) ||
            CloseLibraryDrawer(restoreFocus: true))
        {
            e.Handled = true;
        }
    }

    private void UpdateWallpaperEngineLibraryLayout(double width)
    {
        var isMobile = UsesStackedLayout(width);
        WallpaperEngineLibrary.Margin = isMobile
            ? new Thickness(0)
            : new Thickness(32);
        WallpaperEngineLibrary.MaxWidth = isMobile
            ? double.PositiveInfinity
            : 1120;
        WallpaperEngineLibrary.MaxHeight = isMobile
            ? double.PositiveInfinity
            : 760;
        WallpaperEngineLibrary.HorizontalAlignment = isMobile
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Center;
        WallpaperEngineLibrary.VerticalAlignment = isMobile
            ? VerticalAlignment.Stretch
            : VerticalAlignment.Center;
    }

    private bool CloseWallpaperEngineLibrary(bool restoreFocus = false)
    {
        if (!_isWallpaperEngineLibraryOpen)
        {
            return false;
        }

        _isWallpaperEngineLibraryOpen = false;
        WallpaperEngineLibraryModalLayer.Visibility = Visibility.Collapsed;
        if (restoreFocus)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(LibraryPane.FocusWallpaperEngineEntry));
        }

        return true;
    }

    private void ShowPreviewMode_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SetMobilePane(MobileWorkbenchPane.Preview);
    }

    private void ShowAdjustMode_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        SetMobilePane(MobileWorkbenchPane.Adjust);
    }

    private void SetMobilePane(MobileWorkbenchPane pane)
    {
        _mobilePane = pane;
        CloseLibraryDrawer();
        UpdateResponsiveLayout(ActualWidth);
    }

    private bool CloseLibraryDrawer(bool restoreFocus = false)
    {
        if (!_isLibraryDrawerOpen)
        {
            return false;
        }

        _isLibraryDrawerOpen = false;
        LibraryPane.Visibility = Visibility.Collapsed;
        LibraryScrim.Visibility = Visibility.Collapsed;
        if (restoreFocus)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(() => LibraryDrawerButton.Focus()));
        }

        return true;
    }

    private void UpdateMobileModeButtons()
    {
        var isPreviewSelected = _mobilePane == MobileWorkbenchPane.Preview;
        PreviewModeButton.Appearance = ControlAppearance.Transparent;
        AdjustModeButton.Appearance = ControlAppearance.Transparent;
        PreviewModeButton.SetResourceReference(
            System.Windows.Controls.Control.BackgroundProperty,
            isPreviewSelected
                ? "ControlFillColorSecondaryBrush"
                : "WorkbenchTransparentBrush");
        AdjustModeButton.SetResourceReference(
            System.Windows.Controls.Control.BackgroundProperty,
            isPreviewSelected
                ? "WorkbenchTransparentBrush"
                : "ControlFillColorSecondaryBrush");
        PreviewModeIndicator.Visibility = isPreviewSelected
            ? Visibility.Visible
            : Visibility.Collapsed;
        AdjustModeIndicator.Visibility = isPreviewSelected
            ? Visibility.Collapsed
            : Visibility.Visible;
        var selected = _text.GetStringOrFallback("State_Selected", "Selected");
        var notSelected = _text.GetStringOrFallback("State_NotSelected", "Not selected");
        AutomationProperties.SetItemStatus(
            PreviewModeButton,
            isPreviewSelected ? selected : notSelected);
        AutomationProperties.SetItemStatus(
            AdjustModeButton,
            isPreviewSelected ? notSelected : selected);
    }

    internal static Size ResolveMinimumWindowSize(double workAreaWidth, double workAreaHeight) =>
        new(
            Math.Min(DesignedMinimumWidth, Math.Max(1, workAreaWidth)),
            Math.Min(DesignedMinimumHeight, Math.Max(1, workAreaHeight)));

    internal static Size ResolveInitialWindowSize(double workAreaWidth, double workAreaHeight)
    {
        var availableWidth = Math.Max(1, workAreaWidth);
        var availableHeight = Math.Max(1, workAreaHeight);
        var minimum = ResolveMinimumWindowSize(availableWidth, availableHeight);
        return new Size(
            Math.Clamp(DesignedInitialWidth, minimum.Width, availableWidth),
            Math.Clamp(DesignedInitialHeight, minimum.Height, availableHeight));
    }

    private void ClampInitialSizeToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, workArea.Width);
        var availableHeight = Math.Max(1, workArea.Height);
        var minimum = ResolveMinimumWindowSize(availableWidth, availableHeight);
        var initial = ResolveInitialWindowSize(availableWidth, availableHeight);
        MinWidth = minimum.Width;
        MinHeight = minimum.Height;
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = initial.Width;
        Height = initial.Height;
    }

    private enum MobileWorkbenchPane
    {
        Preview,
        Adjust,
    }
}
