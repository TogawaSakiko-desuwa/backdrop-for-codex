using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BackdropForCodex.App;
using BackdropForCodex.App.Services.Diagnostics;
using BackdropForCodex.App.Views;
using Wpf.Ui.Appearance;
using Xunit;
using ContentDialog = Wpf.Ui.Controls.ContentDialog;
using ContentDialogHost = Wpf.Ui.Controls.ContentDialogHost;
using ContentDialogResult = Wpf.Ui.Controls.ContentDialogResult;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace BackdropForCodex.Core.Tests.AppSupport;

[Collection("Wpf")]
public sealed class WorkbenchThemeRegressionTests
{
    [Fact]
    public void CommandBar_ResolvesPrimaryAndQuietStylesFromRealThemeResources()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = CreateWindow(fixture);
                    var apply = Assert.IsAssignableFrom<Wpf.Ui.Controls.Button>(
                        window.FindName("ApplyActionButton"));
                    var pause = Assert.IsAssignableFrom<Wpf.Ui.Controls.Button>(
                        window.FindName("PauseActionButton"));
                    var primaryStyle = Assert.IsType<Style>(
                        window.FindResource("WorkbenchPrimaryActionButtonStyle"));
                    var implicitButtonStyle = Assert.IsType<Style>(
                        window.FindResource(typeof(Wpf.Ui.Controls.Button)));

                    Assert.Same(implicitButtonStyle, primaryStyle.BasedOn);
                    Assert.Same(primaryStyle, apply.Style);
                    Assert.Equal(
                        Wpf.Ui.Controls.ControlAppearance.Primary,
                        apply.Appearance);
                    Assert.Same(
                        DependencyProperty.UnsetValue,
                        pause.ReadLocalValue(Control.BackgroundProperty));
                    Assert.Same(
                        DependencyProperty.UnsetValue,
                        pause.ReadLocalValue(Control.BorderBrushProperty));
                    Assert.NotNull(pause.MouseOverBackground);
                }
                finally
                {
                    CloseWindow(window, fixture);
                }
            });
    }

    [Fact]
    public void DarkTheme_RailProfileAndActionsUseReadableThemeTextRoles()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                try
                {
                    window = CreateWindow(fixture);
                    ApplyDarkTheme(window);
                    fixture.ViewModel.IsStatusOpen = true;
                    window.Dispatcher.Invoke(
                        static () => { },
                        DispatcherPriority.DataBind);
                    window.UpdateLayout();

                    var expected = ResourceColor("TextFillColorPrimaryBrush");
                    var profileList = FindVisualElement<ListBox>(
                        window,
                        element => element.Name == "ProfileList");
                    var profile = fixture.ViewModel.ProfileCards[0];
                    var profileText = FindVisualElement<TextBlock>(
                        profileList,
                        element => element.Text == profile.DisplayName);
                    var chooseMedia = FindVisualElement<Wpf.Ui.Controls.Button>(
                        window,
                        element => element.Name == "ExpandedChooseMediaButton");
                    var createProfile = FindVisualElement<Wpf.Ui.Controls.Button>(
                        window,
                        element => element.Name == "ExpandedCreateProfileButton");
                    var dismissStatus = FindVisualElement<Wpf.Ui.Controls.Button>(
                        window,
                        element => element.Name == "DismissStatusButton");
                    var pause = FindVisualElement<Wpf.Ui.Controls.Button>(
                        window,
                        element => element.Name == "PauseActionButton");
                    var restore = FindVisualElement<Wpf.Ui.Controls.Button>(
                        window,
                        element => element.Name == "RestoreActionButton");

                    Assert.Equal(expected, BrushColor(profileText.Foreground));
                    Assert.Equal(expected, BrushColor(chooseMedia.Foreground));
                    Assert.Equal(expected, BrushColor(createProfile.Foreground));
                    Assert.Equal(expected, BrushColor(dismissStatus.Foreground));
                    Assert.NotEqual(Colors.Black, BrushColor(pause.Foreground));
                    Assert.NotEqual(Colors.Black, BrushColor(restore.Foreground));

                    ApplyTheme(window, ApplicationTheme.Light);
                    var lightExpected = ResourceColor(
                        "TextFillColorPrimaryBrush");
                    Assert.Equal(
                        lightExpected,
                        BrushColor(profileText.Foreground));
                    Assert.Equal(
                        lightExpected,
                        BrushColor(chooseMedia.Foreground));
                    Assert.Equal(
                        lightExpected,
                        BrushColor(createProfile.Foreground));
                    Assert.Equal(
                        lightExpected,
                        BrushColor(dismissStatus.Foreground));
                }
                finally
                {
                    RestoreLightTheme();
                    CloseWindow(window, fixture);
                }
            });
    }

    [Fact]
    public void ContentDialog_DoesNotReplaceTheBackdropWithDisabledControlFills()
    {
        StaTest.Run(
            () =>
            {
                var fixture = MainWindowViewModelTests.CreateLayoutFixture();
                MainWindow? window = null;
                ContentDialog? dialog = null;
                try
                {
                    window = CreateWindow(fixture);
                    ApplyDarkTheme(window);
                    var dialogHost = Assert.IsType<ContentDialogHost>(
                        window.FindName("DialogHost"));
                    dialog = new ContentDialog(dialogHost)
                    {
                        Title = "Settings",
                        Content = new SettingsDialogContent(
                            fixture.ViewModel,
                            fixture.Text),
                        CloseButtonText = "Close",
                    };

                    var showTask = dialog.ShowAsync(CancellationToken.None);
                    window.Dispatcher.Invoke(
                        static () => { },
                        DispatcherPriority.ApplicationIdle);

                    var library = Assert.IsType<WallpaperLibraryView>(
                        window.FindName("LibraryPane"));
                    Assert.True(
                        library.IsEnabled,
                        "The dialog host must block pointer input with its modal layer " +
                        "without forcing the workbench through disabled control templates.");
                    var libraryCenter = library.TranslatePoint(
                        new Point(
                            library.ActualWidth / 2,
                            library.ActualHeight / 2),
                        window);
                    var visualHit = VisualTreeHelper.HitTest(
                        window,
                        libraryCenter)?.VisualHit;
                    Assert.NotNull(visualHit);
                    Assert.False(
                        IsDescendantOf(visualHit, library),
                        "The active dialog layer must intercept pointer hit testing " +
                        "before it reaches the enabled workbench.");

                    dialog.Hide(ContentDialogResult.None);
                    window.Dispatcher.Invoke(
                        static () => { },
                        DispatcherPriority.ApplicationIdle);
                    Assert.True(showTask.IsCompletedSuccessfully);
                }
                finally
                {
                    dialog?.Hide(ContentDialogResult.None);
                    RestoreLightTheme();
                    CloseWindow(window, fixture);
                }
            });
    }

    private static MainWindow CreateWindow(
        (BackdropForCodex.App.ViewModels.MainWindowViewModel ViewModel,
            BackdropForCodex.App.Services.Localization.IAppTextProvider Text) fixture)
    {
        var window = new MainWindow(
            fixture.ViewModel,
            fixture.Text,
            new DiagnosticReportService())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -10000,
            Top = -10000,
            ShowActivated = false,
        };
        window.Resources.MergedDictionaries.Add(
            new ResourceDictionary
            {
                Source = new Uri(
                    "/BackdropForCodex;component/Themes/WorkbenchTheme.xaml",
                    UriKind.Relative),
            });
        window.Show();
        window.Dispatcher.Invoke(
            static () => { },
            DispatcherPriority.ApplicationIdle);
        return window;
    }

    private static void ApplyDarkTheme(Window window)
    {
        ApplyTheme(window, ApplicationTheme.Dark);
    }

    private static void ApplyTheme(
        Window window,
        ApplicationTheme theme)
    {
        ApplicationThemeManager.Apply(
            theme,
            WindowBackdropType.None,
            updateAccent: true);
        window.Dispatcher.Invoke(
            static () => { },
            DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void RestoreLightTheme() =>
        ApplicationThemeManager.Apply(
            ApplicationTheme.Light,
            WindowBackdropType.None,
            updateAccent: true);

    private static void CloseWindow(
        MainWindow? window,
        (BackdropForCodex.App.ViewModels.MainWindowViewModel ViewModel,
            BackdropForCodex.App.Services.Localization.IAppTextProvider Text) fixture)
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

    private static T FindVisualElement<T>(
        DependencyObject root,
        Predicate<T> predicate)
        where T : DependencyObject
    {
        if (root is T candidate && predicate(candidate))
        {
            return candidate;
        }

        for (var index = 0;
             index < VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            try
            {
                return FindVisualElement(
                    VisualTreeHelper.GetChild(root, index),
                    predicate);
            }
            catch (InvalidOperationException)
            {
                // Continue searching the remaining visual branches.
            }
        }

        throw new InvalidOperationException(
            $"No matching {typeof(T).Name} was found in the visual tree.");
    }

    private static Color ResourceColor(string key) =>
        BrushColor(Assert.IsType<SolidColorBrush>(
            Application.Current.FindResource(key)));

    private static bool IsDescendantOf(
        DependencyObject candidate,
        DependencyObject ancestor)
    {
        for (DependencyObject? current = candidate;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static Color BrushColor(Brush brush) =>
        Assert.IsType<SolidColorBrush>(brush).Color;
}
