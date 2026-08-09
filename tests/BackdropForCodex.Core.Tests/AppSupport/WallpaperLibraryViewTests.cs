using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
                var source = CreateSource(
                    WallpaperContentKind.Scene,
                    WallpaperDeliveryKind.WallpaperEngineWindow);
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
                    SourceItemsSource = new ObservableCollection<WallpaperSourceDescriptor>
                    {
                        source,
                    },
                };
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                var profiles = Assert.IsType<ListBox>(FindElement(view, "ProfileList"));
                var recents = Assert.IsType<ListBox>(FindElement(view, "RecentList"));
                var sources = Assert.IsType<ListBox>(FindElement(view, "SourceList"));
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    ScrollViewer.GetHorizontalScrollBarVisibility(profiles));
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    ScrollViewer.GetHorizontalScrollBarVisibility(recents));
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    ScrollViewer.GetHorizontalScrollBarVisibility(sources));

                var profileItem = Assert.IsType<ListBoxItem>(
                    profiles.ItemContainerGenerator.ContainerFromItem(profile));
                var recentItem = Assert.IsType<ListBoxItem>(
                    recents.ItemContainerGenerator.ContainerFromItem(recent));
                var sourceItem = Assert.IsType<ListBoxItem>(
                    sources.ItemContainerGenerator.ContainerFromItem(source));
                Assert.True(profileItem.MinHeight >= 32);
                Assert.True(recentItem.MinHeight >= 32);
                Assert.True(sourceItem.MinHeight >= 32);
                Assert.Equal(
                    profile.AutomationName,
                    AutomationProperties.GetName(profileItem));
                Assert.Equal(
                    recent.DisplayName,
                    AutomationProperties.GetName(recentItem));
                Assert.Equal(
                    source.DisplayName,
                    AutomationProperties.GetName(sourceItem));
                var unavailableStatus = AutomationProperties.GetItemStatus(sourceItem);
                Assert.False(sourceItem.IsEnabled);
                Assert.False(string.IsNullOrWhiteSpace(unavailableStatus));
                Assert.Equal(
                    unavailableStatus,
                    AutomationProperties.GetHelpText(sourceItem));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(sources));
                Assert.Equal(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(sources));
                Assert.True(ScrollViewer.GetCanContentScroll(sources));
                Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(sources));
                Assert.Equal(320, sources.MaxHeight);

                view.IsCompact = true;

                Assert.Equal(240, sources.MaxHeight);
            });
    }

    [Fact]
    public void SourceSection_TracksRealProviderItemsAndOtherwiseStaysHidden()
    {
        StaTest.Run(
            () =>
            {
                var sourceItems = new ObservableCollection<WallpaperSourceDescriptor>();
                var view = new WallpaperLibraryView
                {
                    SourceItemsSource = sourceItems,
                };
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                var sourceSection = FindElement(view, "SourceSection");
                Assert.Equal(Visibility.Collapsed, sourceSection.Visibility);

                sourceItems.Add(
                    CreateSource(
                        WallpaperContentKind.Web,
                        WallpaperDeliveryKind.WallpaperEngineWindow));
                view.UpdateLayout();

                Assert.Equal(Visibility.Visible, sourceSection.Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    FindElement(view, "ExpandedSourcesHeader").Visibility);

                view.IsCompact = true;

                Assert.Equal(
                    Visibility.Collapsed,
                    FindElement(view, "ExpandedSourcesHeader").Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    FindElement(view, "CompactSourcesHeader").Visibility);

                sourceItems.Clear();
                view.UpdateLayout();

                Assert.Equal(Visibility.Collapsed, sourceSection.Visibility);
            });
    }

    [Fact]
    public void SourceSelection_InvokesThroughTheKeyboardEventSeam()
    {
        StaTest.Run(
            () =>
            {
                var source = CreateSource(
                    WallpaperContentKind.Image,
                    WallpaperDeliveryKind.DirectMedia);
                var view = new WallpaperLibraryView
                {
                    SourceItemsSource = new[] { source },
                    SelectedSource = source,
                };
                var invoked = 0;
                view.SourceInvoked += (_, _) => invoked++;
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                var sourceList = Assert.IsType<ListBox>(FindElement(view, "SourceList"));
                sourceList.RaiseEvent(
                    new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        new TestPresentationSource(view),
                        timestamp: 0,
                        Key.Enter)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    });

                var sourceItem = Assert.IsType<ListBoxItem>(
                    sourceList.ItemContainerGenerator.ContainerFromItem(source));
                sourceList.RaiseEvent(
                    new MouseButtonEventArgs(
                        Mouse.PrimaryDevice,
                        timestamp: 0,
                        MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent,
                        Source = sourceItem,
                    });

                Assert.Equal(2, invoked);
                Assert.Same(source, view.SelectedSource);
            });
    }

    [Fact]
    public void RendererBackedSource_IsDisabledAndCannotInvoke()
    {
        StaTest.Run(
            () =>
            {
                var source = CreateSource(
                    WallpaperContentKind.Scene,
                    WallpaperDeliveryKind.WallpaperEngineWindow);
                var view = new WallpaperLibraryView
                {
                    SourceItemsSource = new[] { source },
                    SelectedSource = source,
                };
                var invoked = 0;
                view.SourceInvoked += (_, _) => invoked++;
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                var sourceList = Assert.IsType<ListBox>(FindElement(view, "SourceList"));
                var sourceItem = Assert.IsType<ListBoxItem>(
                    sourceList.ItemContainerGenerator.ContainerFromItem(source));
                Assert.False(sourceItem.IsEnabled);
                Assert.False(WallpaperSourcesSection.CanInvokeSource(source));

                view.SelectedSource = source;
                sourceList.RaiseEvent(
                    new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        new TestPresentationSource(view),
                        timestamp: 0,
                        Key.Enter)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    });
                sourceList.RaiseEvent(
                    new MouseButtonEventArgs(
                        Mouse.PrimaryDevice,
                        timestamp: 0,
                        MouseButton.Left)
                    {
                        RoutedEvent = Control.MouseDoubleClickEvent,
                        Source = sourceItem,
                    });

                Assert.Equal(0, invoked);
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

                Assert.Equal(Visibility.Visible, FindElement(view, "SourceSection").Visibility);
                var expandedFailure = FindElement(view, "ExpandedSourceFailure");
                var compactFailure = Assert.IsAssignableFrom<ButtonBase>(
                    FindElement(view, "CompactSourceFailureButton"));
                Assert.Equal(Visibility.Visible, expandedFailure.Visibility);
                Assert.Equal(Visibility.Collapsed, compactFailure.Visibility);

                var retryButton = Assert.IsAssignableFrom<Button>(
                    FindElement(view, "ExpandedSourceRetryButton"));
                Assert.Same(view.RefreshSourcesCommand, retryButton.Command);
                retryButton.Command.Execute(retryButton.CommandParameter);
                Assert.Equal(1, retries);

                view.IsCompact = true;

                Assert.Equal(Visibility.Collapsed, expandedFailure.Visibility);
                Assert.Equal(Visibility.Visible, compactFailure.Visibility);
                var compactRetryButton = Assert.IsAssignableFrom<Button>(compactFailure);
                Assert.Same(view.RefreshSourcesCommand, compactRetryButton.Command);
                compactRetryButton.Command.Execute(compactRetryButton.CommandParameter);
                Assert.Equal(2, retries);

                view.HasSourceDiscoveryFailures = false;
                view.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, FindElement(view, "SourceSection").Visibility);
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
                    SourceItemsSource = new[]
                    {
                        CreateSource(
                            WallpaperContentKind.Video,
                            WallpaperDeliveryKind.DirectMedia),
                    },
                    CanChooseMedia = false,
                };
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
                view.Measure(new Size(WallpaperLibraryView.ExpandedWidth, 720));
                view.Arrange(new Rect(0, 0, WallpaperLibraryView.ExpandedWidth, 720));
                view.UpdateLayout();

                Assert.False(FindElement(view, "SourceList").IsEnabled);
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
    public void LibraryFacade_ComposesThreeAssemblyInternalSections()
    {
        StaTest.Run(
            () =>
            {
                var view = new WallpaperLibraryView();

                Assert.False(typeof(WallpaperProfilesSection).IsPublic);
                Assert.False(typeof(WallpaperSourcesSection).IsPublic);
                Assert.False(typeof(WallpaperRecentsSection).IsPublic);
                Assert.IsType<WallpaperProfilesSection>(
                    FindElement(view, "ProfilesSection"));
                Assert.IsType<WallpaperSourcesSection>(
                    FindElement(view, "SourcesSection"));
                Assert.IsType<WallpaperRecentsSection>(
                    FindElement(view, "RecentsSection"));
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

    private static WallpaperSourceDescriptor CreateSource(
        WallpaperContentKind contentKind,
        WallpaperDeliveryKind deliveryKind) =>
        new(
            contentKind is WallpaperContentKind.Image or WallpaperContentKind.Video
                ? MediaSourceKind.LocalFile
                : MediaSourceKind.WallpaperEngineLocalProject,
            contentKind is WallpaperContentKind.Image or WallpaperContentKind.Video
                ? @"C:\wallpapers\sample.png"
                : @"C:\wallpaper-engine\projects\sample",
            $"{contentKind} source",
            contentKind,
            deliveryKind,
            deliveryKind == WallpaperDeliveryKind.WallpaperEngineWindow
                ? WallpaperDeliveryCapabilities.DynamicFrames
                : WallpaperDeliveryCapabilities.None);

    private sealed class TestPresentationSource(Visual rootVisual) : PresentationSource
    {
        public override bool IsDisposed => false;

        public override Visual RootVisual { get; set; } = rootVisual;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
