using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BackdropForCodex.App;
using BackdropForCodex.App.Converters;
using BackdropForCodex.App.Services.Diagnostics;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.App.Views;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

[Collection("Wpf")]
public sealed class MainWindowLayoutTests
{
    private static readonly string[] MetricIconNames =
    [
        "FitMetricIcon",
        "FocusMetricIcon",
        "PanelOpacityMetricIcon",
        "BlurMetricIcon",
        "DarkOverlayMetricIcon",
        "LightOverlayMetricIcon",
    ];

    [Fact]
    public void EditorPanes_UseTheEditorAsTheirDirectDataContext()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = new MainWindow(
                        fixture.ViewModel,
                        fixture.Text,
                        new DiagnosticReportService())
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000,
                        Top = -10000,
                        ShowActivated = false,
                    };
                    window.Show();
                    window.Dispatcher.Invoke(
                        static () => { },
                        DispatcherPriority.ApplicationIdle);

                    Assert.Same(fixture.ViewModel, window.DataContext);
                    Assert.Same(
                        fixture.ViewModel.Editor,
                        FindElement(window, "PreviewView").DataContext);
                    Assert.Same(
                        fixture.ViewModel.Editor,
                        FindElement(window, "InspectorPane").DataContext);
                }
                finally
                {
                    if (window is null)
                    {
                        fixture.ViewModel.Dispose();
                    }
                    else
                    {
                        window.CloseForShutdown();
                    }
                }
            });
    }

    [Fact]
    public void PreviewSurface_MaximizesTheRealFullscreenPreviewPane()
    {
        StaTest.Run(
            () =>
            {
                AssertPreviewSurfaceLayout(width: 2048, height: 1224);
                AssertPreviewSurfaceLayout(width: 1200, height: 760);
            });
    }

    [Fact]
    public void InspectorUsesOneFluentMetricIconForEveryCalibrationRow()
    {
        StaTest.Run(
            () =>
            {
                var inspector = new WallpaperInspectorView();

                Assert.All(
                    MetricIconNames,
                    name => Assert.IsType<Wpf.Ui.Controls.SymbolIcon>(
                        inspector.FindName(name)));
            });
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(640, true)]
    [InlineData(959, true)]
    [InlineData(959.999, true)]
    [InlineData(960, false)]
    [InlineData(1200, false)]
    public void UsesStackedLayout_HonorsExact960PixelBoundary(
        double width,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.UsesStackedLayout(width));
    }

    [Theory]
    [InlineData(959.999, false)]
    [InlineData(960, true)]
    [InlineData(1100, true)]
    [InlineData(1279.999, true)]
    [InlineData(1280, false)]
    [InlineData(1600, false)]
    public void UsesCompactRail_HonorsWorkbenchBoundaries(
        double width,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.UsesCompactRail(width));
    }

    [Fact]
    public void WindowSizing_YieldsToAWorkAreaShorterThanTheDesignedMinimum()
    {
        var minimum = MainWindow.ResolveMinimumWindowSize(960, 500);
        var initial = MainWindow.ResolveInitialWindowSize(960, 500);

        Assert.Equal(new Size(640, 500), minimum);
        Assert.Equal(new Size(960, 500), initial);
    }

    [Fact]
    public void Workbench_UsesExpandedCompactAndMobileModes()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = new MainWindow(
                        fixture.ViewModel,
                        fixture.Text,
                        new DiagnosticReportService())
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -10000,
                        Top = -10000,
                        ShowActivated = false,
                    };
                    window.Show();

                    ArrangeWindow(window, width: 1440, height: 860);
                    var library = FindElement(window, "LibraryPane");
                    var preview = FindElement(window, "PreviewPane");
                    var inspector = Assert.IsType<Border>(
                        FindElement(window, "InspectorHost"));
                    var mobileToolbar = FindElement(window, "MobileToolbar");
                    var footerCommandBar = Assert.IsType<Border>(
                        FindElement(window, "FooterCommandBar"));
                    var statusMessage = Assert.IsType<TextBlock>(
                        FindElement(window, "StatusMessageText"));
                    var pauseAction = Assert.IsAssignableFrom<Wpf.Ui.Controls.Button>(
                        FindElement(window, "PauseActionButton"));
                    var restoreAction = Assert.IsAssignableFrom<Wpf.Ui.Controls.Button>(
                        FindElement(window, "RestoreActionButton"));
                    var applyAction = Assert.IsAssignableFrom<Wpf.Ui.Controls.Button>(
                        FindElement(window, "ApplyActionButton"));
                    Assert.Equal(224, library.ActualWidth, precision: 3);
                    Assert.Equal(Visibility.Visible, preview.Visibility);
                    Assert.Equal(Visibility.Visible, inspector.Visibility);
                    Assert.Equal(new Thickness(1, 0, 0, 0), inspector.BorderThickness);
                    Assert.Equal(Visibility.Collapsed, mobileToolbar.Visibility);
                    Assert.Equal(2, Assert.IsType<Grid>(footerCommandBar.Child).RowDefinitions.Count);
                    Assert.Equal(Visibility.Visible, statusMessage.Visibility);
                    Assert.Equal(
                        Wpf.Ui.Controls.ControlAppearance.Transparent,
                        pauseAction.Appearance);
                    Assert.Equal(new Thickness(0), pauseAction.BorderThickness);
                    Assert.Equal(
                        Wpf.Ui.Controls.ControlAppearance.Secondary,
                        restoreAction.Appearance);
                    Assert.Equal(
                        Wpf.Ui.Controls.ControlAppearance.Primary,
                        applyAction.Appearance);
                    Assert.Equal(FontWeights.SemiBold, applyAction.FontWeight);
                    Assert.True(applyAction.MinHeight >= 44);

                    ArrangeWindow(window, width: 1200, height: 760);
                    Assert.Equal(56, library.ActualWidth, precision: 3);
                    Assert.Equal(Visibility.Visible, inspector.Visibility);

                    ArrangeWindow(window, width: 800, height: 700);
                    Assert.Equal(Visibility.Collapsed, library.Visibility);
                    Assert.Equal(Visibility.Visible, mobileToolbar.Visibility);
                    Assert.Equal(Visibility.Visible, preview.Visibility);
                    Assert.Equal(Visibility.Collapsed, inspector.Visibility);
                    Assert.Equal(new Thickness(0), inspector.BorderThickness);
                    Assert.Equal(Visibility.Collapsed, statusMessage.Visibility);
                    var previewMode = FindElement(window, "PreviewModeButton");
                    var adjustMode = FindElement(window, "AdjustModeButton");
                    var previewModeIndicator = FindElement(
                        window,
                        "PreviewModeIndicator");
                    var adjustModeIndicator = FindElement(
                        window,
                        "AdjustModeIndicator");
                    Assert.Equal(
                        Wpf.Ui.Controls.ControlAppearance.Transparent,
                        Assert.IsAssignableFrom<Wpf.Ui.Controls.Button>(previewMode).Appearance);
                    Assert.Equal(
                        Wpf.Ui.Controls.ControlAppearance.Transparent,
                        Assert.IsAssignableFrom<Wpf.Ui.Controls.Button>(adjustMode).Appearance);
                    Assert.Equal(Visibility.Visible, previewModeIndicator.Visibility);
                    Assert.Equal(Visibility.Collapsed, adjustModeIndicator.Visibility);
                    Assert.Equal(
                        "Selected",
                        AutomationProperties.GetItemStatus(previewMode));
                    Assert.Equal(
                        "Not selected",
                        AutomationProperties.GetItemStatus(adjustMode));

                    adjustMode.RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Assert.Equal(Visibility.Collapsed, preview.Visibility);
                    Assert.Equal(Visibility.Visible, inspector.Visibility);
                    Assert.Equal(
                        "Not selected",
                        AutomationProperties.GetItemStatus(previewMode));
                    Assert.Equal(
                        "Selected",
                        AutomationProperties.GetItemStatus(adjustMode));
                    Assert.Equal(Visibility.Collapsed, previewModeIndicator.Visibility);
                    Assert.Equal(Visibility.Visible, adjustModeIndicator.Visibility);

                    FindElement(window, "LibraryDrawerButton").RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Assert.Equal(Visibility.Visible, library.Visibility);
                    Assert.Equal(
                        Visibility.Visible,
                        FindElement(window, "LibraryScrim").Visibility);

                    var escape = new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window)!,
                        Environment.TickCount,
                        Key.Escape)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    };
                    window.RaiseEvent(escape);
                    window.UpdateLayout();
                    Assert.True(escape.Handled);
                    Assert.Equal(Visibility.Collapsed, library.Visibility);
                    Assert.Equal(
                        Visibility.Collapsed,
                        FindElement(window, "LibraryScrim").Visibility);
                }
                finally
                {
                    if (window is null)
                    {
                        fixture.ViewModel.Dispose();
                    }
                    else
                    {
                        window.CloseForShutdown();
                    }
                }
            });
    }

    [Theory]
    [InlineData(640)]
    [InlineData(800)]
    public void MobileFooter_StacksStatusAboveActionsWithoutOverlap(double width)
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = CreateWindow(fixture);
                    ArrangeWindow(window, width, height: 700);

                    var footer = FindElement(window, "FooterCommandBar");
                    var status = FindElement(window, "FooterStatusHost");
                    var actions = FindElement(window, "FooterActions");
                    var statusBounds = GetBounds(status, footer);
                    var actionBounds = GetBounds(actions, footer);

                    Assert.True(
                        statusBounds.Bottom <= actionBounds.Top,
                        $"Status bottom {statusBounds.Bottom:F2} overlapped " +
                        $"actions top {actionBounds.Top:F2} at {width:F0} DIP.");
                    Assert.True(actions.ActualWidth <= footer.ActualWidth);
                }
                finally
                {
                    CloseWindow(window, fixture);
                }
            });
    }

    [Fact]
    public void MobilePaneSelection_RemainsBoundToDynamicThemeResources()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = CreateWindow(fixture);
                    var firstBrush = new SolidColorBrush(Colors.Crimson);
                    var secondBrush = new SolidColorBrush(Colors.CornflowerBlue);
                    window.Resources["ControlFillColorSecondaryBrush"] = firstBrush;
                    ArrangeWindow(window, width: 800, height: 700);
                    var previewMode = Assert.IsAssignableFrom<Control>(
                        FindElement(window, "PreviewModeButton"));
                    Assert.Same(firstBrush, previewMode.Background);

                    window.Resources["ControlFillColorSecondaryBrush"] = secondBrush;
                    window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

                    Assert.Same(secondBrush, previewMode.Background);
                }
                finally
                {
                    CloseWindow(window, fixture);
                }
            });
    }

    [Fact]
    public void HighContrastState_UpdatesEveryStageSurfaceFromTheWindowProperty()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = CreateWindow(fixture);
                    window.Resources.MergedDictionaries.Add(
                        new ResourceDictionary
                        {
                            Source = new Uri(
                                "/BackdropForCodex;component/Themes/WorkbenchTheme.xaml",
                                UriKind.Relative),
                        });
                    var highContrastProperty = typeof(MainWindow).GetProperty(
                        "IsWorkbenchHighContrast");
                    Assert.NotNull(highContrastProperty);
                    highContrastProperty.SetValue(window, true);
                    window.UpdateLayout();

                    var stage = Assert.IsType<Grid>(FindElement(window, "PreviewPane"));
                    var header = Assert.IsType<Border>(
                        FindElement(window, "PreviewStageHeader"));
                    var frame = Assert.IsType<Border>(
                        FindElement(window, "CalibrationFrame"));

                    Assert.Same(SystemColors.WindowBrush, stage.Background);
                    Assert.Same(SystemColors.WindowBrush, header.Background);
                    Assert.Same(SystemColors.WindowTextBrush, header.BorderBrush);
                    Assert.Same(SystemColors.WindowBrush, frame.Background);
                    Assert.Same(SystemColors.WindowTextBrush, frame.BorderBrush);
                }
                finally
                {
                    CloseWindow(window, fixture);
                }
            });
    }

    [Fact]
    public void FooterStatus_UsesOnePoliteLiveRegionWithAnAutomationPeer()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = CreateWindow(fixture);
                    var liveRegion = Assert.IsType<StatusBar>(
                        FindElement(window, "StatusLiveRegion"));

                    Assert.Equal(
                        AutomationLiveSetting.Polite,
                        AutomationProperties.GetLiveSetting(liveRegion));
                    Assert.NotNull(
                        UIElementAutomationPeer.CreatePeerForElement(liveRegion));
                    Assert.Equal(
                        AutomationLiveSetting.Off,
                        AutomationProperties.GetLiveSetting(
                            FindElement(window, "ActiveStatusSurface")));

                    fixture.ViewModel.ShowUnexpectedError(
                        new InvalidOperationException("Simulated status failure."));
                    window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                    Assert.Equal(
                        fixture.ViewModel.StatusTitle,
                        AutomationProperties.GetName(liveRegion));
                    Assert.Equal(
                        fixture.ViewModel.StatusMessage,
                        AutomationProperties.GetHelpText(liveRegion));

                    fixture.ViewModel.IsStatusOpen = false;
                    window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ContextIdle);
                    Assert.Equal(
                        fixture.ViewModel.FooterStatusText,
                        AutomationProperties.GetName(liveRegion));
                }
                finally
                {
                    CloseWindow(window, fixture);
                }
            });
    }

    [Fact]
    public void FooterStatusLayout_ShowsOnlyTheCurrentOperationSurface()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = CreateWindow(fixture);
                    var passive = FindElement(window, "PassiveWorkspaceStatus");
                    var active = FindElement(window, "ActiveStatusSurface");

                    fixture.ViewModel.IsStatusOpen = true;
                    window.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
                    Assert.Equal(Visibility.Collapsed, passive.Visibility);
                    Assert.Equal(Visibility.Visible, active.Visibility);

                    fixture.ViewModel.IsStatusOpen = false;
                    window.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
                    Assert.Equal(Visibility.Visible, passive.Visibility);
                    Assert.Equal(Visibility.Collapsed, active.Visibility);
                }
                finally
                {
                    CloseWindow(window, fixture);
                }
            });
    }

    [Fact]
    public void MobileDrawerActivation_RestoresFocusToItsOpener()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = CreateWindow(fixture, showActivated: true);
                    ArrangeWindow(window, width: 800, height: 700);
                    var opener = Assert.IsAssignableFrom<Control>(
                        FindElement(window, "LibraryDrawerButton"));
                    var library = Assert.IsType<WallpaperLibraryView>(
                        FindElement(window, "LibraryPane"));
                    opener.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Assert.Equal(Visibility.Visible, library.Visibility);

                    library.RaiseEvent(
                        new RoutedEventArgs(
                            WallpaperLibraryView.SelectedProfileChangedEvent,
                            library));
                    window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Input);

                    Assert.Equal(Visibility.Collapsed, library.Visibility);
                    Assert.True(opener.IsKeyboardFocusWithin);
                }
                finally
                {
                    CloseWindow(window, fixture);
                }
            });
    }

    [Fact]
    public void CompactRail_ExposesRecentRemoveAndClearCommands()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = CreateWindow(fixture);
                    ArrangeWindow(window, width: 1200, height: 760);
                    var library = Assert.IsType<WallpaperLibraryView>(
                        FindElement(window, "LibraryPane"));
                    var recent = new RecentMediaItem(
                        new BackdropForCodex.Core.Media.MediaReference
                        {
                            MediaId = Guid.CreateVersion7(),
                            SourceKind = BackdropForCodex.Core.Media.MediaSourceKind.LocalFile,
                            SourceIdentifier = @"C:\wallpapers\recent.png",
                            LastKnownKind = BackdropForCodex.Core.Media.MediaKind.Image,
                        },
                        "recent.png",
                        true);
                    var removeCommand = new RecordingCommand();
                    var clearCommand = new RecordingCommand();
                    library.RecentItemsSource = new ObservableCollection<RecentMediaItem>
                    {
                        recent,
                    };
                    library.RemoveRecentCommand = removeCommand;
                    library.ClearRecentsCommand = clearCommand;
                    window.UpdateLayout();
                    var menu = Assert.IsType<ContextMenu>(library.ContextMenu);
                    var commands = menu.Items.OfType<MenuItem>().ToArray();
                    var removeItem = Assert.Single(
                        commands,
                        item => AutomationProperties.GetAutomationId(item) ==
                            "RemoveSelectedRecentMenuItem");
                    var clearItem = Assert.Single(
                        commands,
                        item => AutomationProperties.GetAutomationId(item) ==
                            "ClearRecentsMenuItem");
                    var recentList = Assert.IsType<ListBox>(
                        FindVisualElement(library, "RecentList"));
                    var recentContainer = Assert.IsType<ListBoxItem>(
                        recentList.ItemContainerGenerator.ContainerFromItem(recent));
                    window.PrepareLibraryContextMenu(recentContainer);
                    menu.PlacementTarget = library;
                    removeItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();
                    clearItem.GetBindingExpression(MenuItem.CommandProperty)?.UpdateTarget();

                    Assert.True(library.IsCompact);
                    Assert.Same(recent, removeItem.CommandParameter);
                    Assert.Same(removeCommand, removeItem.Command);
                    Assert.Same(clearCommand, clearItem.Command);
                    removeItem.Command.Execute(removeItem.CommandParameter);
                    clearItem.Command.Execute(parameter: null);
                    Assert.Same(recent, removeCommand.LastParameter);
                    Assert.Equal(1, clearCommand.ExecutionCount);
                }
                finally
                {
                    CloseWindow(window, fixture);
                }
            });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 204)]
    [InlineData(3, 612)]
    public void ProfileCardTrackWidth_AllocatesEveryFixedCard(
        int profileCount,
        double expectedWidth)
    {
        var converter = new ProfileCardTrackWidthConverter();

        var actual = converter.Convert(
            profileCount,
            typeof(double),
            parameter: null,
            CultureInfo.InvariantCulture);

        Assert.Equal(expectedWidth, Assert.IsType<double>(actual));
    }

    private static void AssertPreviewSurfaceLayout(
        double width,
        double height)
    {
        var fixture = MainWindowViewModelTests.CreateLayoutFixture();
        MainWindow? window = null;

        try
        {
            window = new MainWindow(
                fixture.ViewModel,
                fixture.Text,
                new DiagnosticReportService())
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
            };
            window.Show();
            window.Width = width;
            window.Height = height;
            window.Dispatcher.Invoke(
                static () => { },
                DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();

            var previewPane = FindElement(window, "PreviewPane");
            var previewView = FindElement(window, "PreviewView");
            var previewHost = FindElement(previewView, "PreviewHost");
            var calibrationFrame = FindElement(window, "CalibrationFrame");
            var previewCard = FindElement(previewView, "PreviewCard");
            var previewSurface =
                FindElement(previewView, "PreviewSurface");
            var hostBounds = GetBounds(previewHost, previewPane);
            var calibrationBounds = GetBounds(calibrationFrame, previewPane);
            var cardBounds = GetBounds(previewCard, previewPane);
            var surfaceBounds =
                GetBounds(previewSurface, previewPane);
            Assert.Equal(
                16,
                calibrationBounds.Width - hostBounds.Width,
                precision: 6);
            Assert.True(
                calibrationBounds.Width >= previewPane.ActualWidth * 0.9,
                $"Calibration frame width {calibrationBounds.Width:F2} used too little " +
                $"of PreviewPane width {previewPane.ActualWidth:F2}.");
            var expectedScale = Math.Min(
                hostBounds.Width /
                WallpaperPreviewView.PreviewDesignWidth,
                hostBounds.Height /
                WallpaperPreviewView.PreviewDesignHeight);
            Assert.Equal(
                WallpaperPreviewView.PreviewDesignWidth *
                expectedScale,
                surfaceBounds.Width,
                precision: 6);
            Assert.Equal(
                WallpaperPreviewView.PreviewDesignHeight *
                expectedScale,
                surfaceBounds.Height,
                precision: 6);
            var hostAspect =
                hostBounds.Width / hostBounds.Height;
            if (hostAspect >= 16d / 9d)
            {
                Assert.True(
                    surfaceBounds.Height >=
                    hostBounds.Height * 0.99,
                    $"PreviewSurface height " +
                    $"{surfaceBounds.Height:F2} left excessive " +
                    $"vertical space in a {hostAspect:F3}:1 host.");
            }
            else
            {
                Assert.True(
                    surfaceBounds.Width >=
                    hostBounds.Width * 0.99,
                    $"PreviewSurface width " +
                    $"{surfaceBounds.Width:F2} left excessive " +
                    $"horizontal space in a {hostAspect:F3}:1 host.");
            }
            Assert.True(
                surfaceBounds.Width >=
                previewPane.ActualWidth * 0.7,
                $"Visible PreviewSurface width " +
                $"{surfaceBounds.Width:F2} used only " +
                $"{surfaceBounds.Width / previewPane.ActualWidth:P1} " +
                $"of PreviewPane width " +
                $"{previewPane.ActualWidth:F2}.");
            Assert.Equal(
                16d / 9d,
                surfaceBounds.Width / surfaceBounds.Height,
                precision: 6);
            AssertRectEqual(cardBounds, surfaceBounds);
            AssertVisuallyCentered(
                hostBounds,
                surfaceBounds,
                expectedScale,
                VisualTreeHelper.GetDpi(window),
                previewHost.UseLayoutRounding);
        }
        finally
        {
            if (window is null)
            {
                fixture.ViewModel.Dispose();
            }
            else
            {
                window.CloseForShutdown();
            }
        }
    }

    private static void ArrangeWindow(
        MainWindow window,
        double width,
        double height)
    {
        window.Width = width;
        window.Height = height;
        window.Dispatcher.Invoke(
            static () => { },
            DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static MainWindow CreateWindow(
        (MainWindowViewModel ViewModel, IAppTextProvider Text) fixture,
        bool showActivated = false)
    {
        var window = new MainWindow(
            fixture.ViewModel,
            fixture.Text,
            new DiagnosticReportService())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowActivated = showActivated,
        };
        window.Show();
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
        return window;
    }

    private static void CloseWindow(
        MainWindow? window,
        (MainWindowViewModel ViewModel, IAppTextProvider Text) fixture)
    {
        if (window is null)
        {
            fixture.ViewModel.Dispose();
        }
        else
        {
            window.CloseForShutdown();
        }
    }

    private static void AssertVisuallyCentered(
        Rect hostBounds,
        Rect surfaceBounds,
        double scale,
        DpiScale dpi,
        bool useLayoutRounding)
    {
        // FluentWindow enables layout rounding. Viewbox placement can therefore
        // round once in logical canvas units and once again in device pixels.
        // A centered surface may move by at most half of each unit.
        const double floatingPointTolerance = 0.000001;
        var horizontalOffset = Math.Abs(
            (surfaceBounds.Left + (surfaceBounds.Width / 2)) -
            (hostBounds.Left + (hostBounds.Width / 2)));
        var verticalOffset = Math.Abs(
            (surfaceBounds.Top + (surfaceBounds.Height / 2)) -
            (hostBounds.Top + (hostBounds.Height / 2)));
        var logicalRoundingAllowance = useLayoutRounding ? scale / 2 : 0;
        var horizontalTolerance = floatingPointTolerance;
        var verticalTolerance = floatingPointTolerance;
        if (useLayoutRounding)
        {
            horizontalTolerance +=
                logicalRoundingAllowance + (0.5 / dpi.DpiScaleX);
            verticalTolerance +=
                logicalRoundingAllowance + (0.5 / dpi.DpiScaleY);
        }

        Assert.True(
            horizontalOffset <= horizontalTolerance,
            $"PreviewSurface horizontal center offset " +
            $"{horizontalOffset:F6} exceeded layout-rounding tolerance " +
            $"{horizontalTolerance:F6}.");
        Assert.True(
            verticalOffset <= verticalTolerance,
            $"PreviewSurface vertical center offset " +
            $"{verticalOffset:F6} exceeded layout-rounding tolerance " +
            $"{verticalTolerance:F6}.");
    }

    private static FrameworkElement FindElement(
        FrameworkElement root,
        string name) =>
        Assert.IsAssignableFrom<FrameworkElement>(root.FindName(name));

    private static FrameworkElement? FindVisualElement(
        DependencyObject root,
        string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            if (FindVisualElement(child, name) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static Rect GetBounds(
        FrameworkElement element,
        Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(element.RenderSize));

    private static void AssertRectEqual(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 6);
        Assert.Equal(expected.Y, actual.Y, precision: 6);
        Assert.Equal(expected.Width, actual.Width, precision: 6);
        Assert.Equal(expected.Height, actual.Height, precision: 6);
    }

    private sealed class RecordingCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public int ExecutionCount { get; private set; }

        public object? LastParameter { get; private set; }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            ExecutionCount++;
            LastParameter = parameter;
        }

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
