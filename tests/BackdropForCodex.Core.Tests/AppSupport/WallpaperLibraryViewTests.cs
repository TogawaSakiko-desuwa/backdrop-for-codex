using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.App.Views;
using BackdropForCodex.Core.Media;
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
                Assert.Equal(24d, resources["WorkbenchTypeDisplay"]);
                Assert.Equal(12d, resources["WorkbenchTypeCaption"]);
                Assert.Equal(32d, resources["WorkbenchTargetMinimum"]);
                Assert.Equal(36d, resources["WorkbenchPrimaryTargetMinimum"]);
                Assert.IsType<Style>(resources["WorkbenchFocusVisualStyle"]);
                Assert.IsType<Style>(resources["WorkbenchRailBorderStyle"]);
                Assert.IsType<Style>(resources["WorkbenchPrimaryActionButtonStyle"]);
            });
    }

    private static FrameworkElement FindElement(FrameworkElement view, string name) =>
        Assert.IsAssignableFrom<FrameworkElement>(view.FindName(name));
}
