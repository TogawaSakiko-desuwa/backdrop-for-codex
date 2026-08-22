using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.App.Views;
using BackdropForCodex.Core.Media;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace BackdropForCodex.Core.Tests.AppSupport;

[Collection("Wpf")]
public sealed class WallpaperLibraryViewTests
{
    [Fact]
    public void CompactState_UsesTheApprovedRailWidthsAndVisibility()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperLibraryView();
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                Assert.Equal(WallpaperLibraryView.ExpandedWidth, view.Width);
                Assert.Equal(
                    Visibility.Visible,
                    FindElement(view, "ExpandedProfilesHeader").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    FindElement(view, "CompactProfilesHeader").Visibility);

                view.IsCompact = true;

                Assert.Equal(WallpaperLibraryView.CompactWidth, view.Width);
                Assert.Equal(
                    Visibility.Collapsed,
                    FindElement(view, "ExpandedProfilesHeader").Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    FindElement(view, "CompactProfilesHeader").Visibility);
            });
    }

    [Fact]
    public void Lists_KeepVerticalItemsKeyboardNamedAndHorizontalScrollDisabled()
    {
        StaTest.Run(
            () =>
            {
                var profile = new WallpaperProfileCardItem(
                    Guid.CreateVersion7(),
                    "Profile",
                    "Profile",
                    null,
                    null,
                    null,
                    MediaKind.None,
                    false,
                    "Official background",
                    "Official background",
                    "Profile, Official background",
                    "More actions for Profile");
                var recent = new RecentMediaItem(
                    new MediaReference
                    {
                        MediaId = Guid.CreateVersion7(),
                        SourceKind = MediaSourceKind.LocalFile,
                        SourceIdentifier = @"C:\missing\wallpaper.png",
                        LastKnownKind = MediaKind.Image,
                    },
                    "wallpaper.png",
                    false);
                var view = new WallpaperLibraryView
                {
                    ProfileItemsSource = new ObservableCollection<WallpaperProfileCardItem>
                    {
                        profile,
                    },
                    RecentItemsSource = new ObservableCollection<RecentMediaItem>
                    {
                        recent,
                    },
                    WallpaperEngineProjectCount = 37,
                };
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                var profiles = Assert.IsType<ListBox>(FindElement(view, "ProfileList"));
                var recents = Assert.IsType<ListBox>(FindElement(view, "RecentList"));
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    ScrollViewer.GetHorizontalScrollBarVisibility(profiles));
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    ScrollViewer.GetHorizontalScrollBarVisibility(recents));
                var profileItem = Assert.IsType<ListBoxItem>(
                    profiles.ItemContainerGenerator.ContainerFromItem(profile));
                var recentItem = Assert.IsType<ListBoxItem>(
                    recents.ItemContainerGenerator.ContainerFromItem(recent));
                Assert.True(profileItem.MinHeight >= 32);
                Assert.True(recentItem.MinHeight >= 32);
                Assert.Equal(
                    profile.AutomationName,
                    AutomationProperties.GetName(profileItem));
                Assert.Equal(
                    recent.DisplayName,
                    AutomationProperties.GetName(recentItem));

                var wallpaperEngineEntry = Assert.IsAssignableFrom<ButtonBase>(
                    FindElement(view, "ExpandedWallpaperEngineButton"));
                Assert.True(wallpaperEngineEntry.MinHeight >= 32);
                Assert.False(
                    string.IsNullOrWhiteSpace(
                        AutomationProperties.GetName(wallpaperEngineEntry)));

                view.IsCompact = true;

                Assert.Equal(
                    Visibility.Visible,
                    FindElement(view, "CompactWallpaperEngineButton").Visibility);
            });
    }

    [Fact]
    public void WallpaperEngineEntry_IsASinglePersistentProviderGateway()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperLibraryView
                {
                    WallpaperEngineProjectCount = 12,
                    WallpaperEngineAvailability = WallpaperSourceAvailability.NotInstalled,
                };
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                Assert.Equal(
                    Visibility.Visible,
                    FindElement(view, "ExpandedWallpaperEngineButton").Visibility);
                Assert.Null(view.FindName("SourceList"));
                Assert.Null(view.FindName("SourcesSection"));

                view.IsCompact = true;

                Assert.Equal(
                    Visibility.Collapsed,
                    FindElement(view, "ExpandedWallpaperEngineButton").Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    FindElement(view, "CompactWallpaperEngineButton").Visibility);
            });
    }

    [Fact]
    public void WallpaperEngineEntry_DefaultStateIsNotLoadedAndUsesANeutralStatus()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperLibraryView();
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                Assert.Equal(
                    WallpaperSourceAvailability.NotLoaded,
                    view.WallpaperEngineAvailability);
                var button = FindElement(view, "ExpandedWallpaperEngineButton");
                Assert.Contains(
                    FindVisualDescendants<TextBlock>(button),
                    text => string.Equals(
                        text.Text,
                        new AppTextProvider().GetString("Source_NotLoaded"),
                        StringComparison.Ordinal));
                var statusDot = Assert.IsType<Ellipse>(
                    FindVisualDescendant<Ellipse>(button));
                var expectedBrush = Assert.IsType<SolidColorBrush>(
                    view.FindResource("TextFillColorDisabledBrush"));
                var actualBrush = Assert.IsType<SolidColorBrush>(statusDot.Fill);
                Assert.Equal(expectedBrush.Color, actualBrush.Color);
            });
    }

    [Fact]
    public void WallpaperEngineEntry_RequestsTheDedicatedLibrary()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperLibraryView();
                var invoked = 0;
                view.WallpaperEngineLibraryRequested += (_, _) => invoked++;
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                var button = Assert.IsAssignableFrom<ButtonBase>(
                    FindElement(view, "ExpandedWallpaperEngineButton"));
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

                Assert.Equal(1, invoked);
            });
    }

    [Fact]
    public void RendererUnavailable_DoesNotDisableBrowsingTheLibrary()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperLibraryView
                {
                    WallpaperEngineAvailability = WallpaperSourceAvailability.RendererUnavailable,
                };
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                Assert.True(
                    FindElement(view, "ExpandedWallpaperEngineButton").IsEnabled);
            });
    }

    [Fact]
    public void SourceFailure_RemainsVisibleAndOffersRetryInBothRailModes()
    {
        StaTest.Run(
            () =>
            {
                var retries = 0;
                var view = new WallpaperLibraryView
                {
                    HasSourceDiscoveryFailures = true,
                    RefreshSourcesCommand = new RelayCommand(() => retries++),
                };
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                var retryButton = Assert.IsAssignableFrom<Button>(
                    FindElement(view, "ExpandedWallpaperEngineRefreshButton"));
                Assert.Same(view.RefreshSourcesCommand, retryButton.Command);
                retryButton.Command.Execute(retryButton.CommandParameter);
                Assert.Equal(1, retries);

                view.IsCompact = true;

                Assert.Equal(Visibility.Collapsed, retryButton.Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    FindElement(view, "CompactWallpaperEngineButton").Visibility);
            });
    }

    [Fact]
    public void DisabledEditing_DisablesProviderSourceSelection()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperLibraryView
                {
                    CanChooseMedia = false,
                };
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                Assert.False(FindElement(view, "ExpandedChooseMediaButton").IsEnabled);
                Assert.True(FindElement(view, "ExpandedWallpaperEngineButton").IsEnabled);
            });
    }

    [Fact]
    public void SelectionAndLocalMediaRequest_AreExposedWithoutAViewModelDependency()
    {
        StaTest.Run(
            () =>
            {
                var profile = new WallpaperProfileCardItem(
                    Guid.CreateVersion7(),
                    "Profile",
                    "Profile",
                    null,
                    null,
                    null,
                    MediaKind.None,
                    false,
                    "Official background",
                    "Official background",
                    "Profile, Official background",
                    "More actions for Profile");
                var view = new WallpaperLibraryView();
                var profileChanged = 0;
                var chooseRequested = 0;
                view.SelectedProfileChanged += (_, _) => profileChanged++;
                view.ChooseLocalMediaRequested += (_, _) => chooseRequested++;

                view.SelectedProfile = profile;
                view.RaiseEvent(
                    new RoutedEventArgs(
                        WallpaperLibraryView.ChooseLocalMediaRequestedEvent,
                        view));

                Assert.Equal(1, profileChanged);
                Assert.Equal(1, chooseRequested);
                Assert.Same(profile, view.SelectedProfile);
            });
    }

    [Fact]
    public void WorkbenchTheme_ExposesTheApprovedScaleAndSystemOwnedStyles()
    {
        StaTest.Run(
            () =>
            {
                var resources = new ResourceDictionary
                {
                    Source = new Uri(
                        "/BackdropForCodex;component/Themes/WorkbenchTheme.xaml",
                        UriKind.Relative),
                };

                Assert.Equal(4d, resources["WorkbenchSpace4"]);
                Assert.Equal(32d, resources["WorkbenchSpace32"]);
                Assert.Equal(20d, resources["WorkbenchTypeDisplay"]);
                Assert.Equal(12d, resources["WorkbenchTypeCaption"]);
                Assert.Equal(32d, resources["WorkbenchTargetMinimum"]);
                Assert.Equal(44d, resources["WorkbenchPrimaryTargetMinimum"]);
                Assert.IsType<Style>(resources["WorkbenchFocusVisualStyle"]);
                Assert.IsType<Style>(resources["WorkbenchRailBorderStyle"]);
                Assert.IsType<Style>(resources["WorkbenchPrimaryActionButtonStyle"]);
            });
    }

    [Fact]
    public void LibraryFacade_ComposesProfilesAndRecentsAroundTheProviderGateway()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperLibraryView();

                Assert.False(typeof(WallpaperProfilesSection).IsPublic);
                Assert.False(typeof(WallpaperRecentsSection).IsPublic);
                Assert.IsType<WallpaperProfilesSection>(
                    FindElement(view, "ProfilesSection"));
                Assert.IsType<WallpaperRecentsSection>(
                    FindElement(view, "RecentsSection"));
                Assert.IsAssignableFrom<ButtonBase>(
                    FindElement(view, "ExpandedWallpaperEngineButton"));
            });
    }

    private static FrameworkElement FindElement(FrameworkElement view, string name)
    {
        if (view.FindName(name) is FrameworkElement directMatch)
        {
            return directMatch;
        }

        return Assert.IsAssignableFrom<FrameworkElement>(
            FindVisualElement(view, name));
    }

    private static FrameworkElement? FindVisualElement(
        DependencyObject parent,
        string name)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            if (child is FrameworkElement nameScope &&
                nameScope.FindName(name) is FrameworkElement scopedMatch)
            {
                return scopedMatch;
            }

            if (FindVisualElement(child, name) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

}
