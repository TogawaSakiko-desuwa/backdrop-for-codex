using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BackdropForCodex.App.ViewModels;

namespace BackdropForCodex.App.Views;

internal partial class WallpaperProfilesSection : UserControl
{
    public static readonly DependencyProperty ProfileItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ProfileItemsSource),
            typeof(IEnumerable),
            typeof(WallpaperProfilesSection),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedProfileProperty =
        DependencyProperty.Register(
            nameof(SelectedProfile),
            typeof(WallpaperProfileCardItem),
            typeof(WallpaperProfilesSection),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty CreateProfileCommandProperty =
        RegisterCommand(nameof(CreateProfileCommand));

    public static readonly DependencyProperty DuplicateProfileCommandProperty =
        RegisterCommand(nameof(DuplicateProfileCommand));

    public static readonly DependencyProperty RenameProfileCommandProperty =
        RegisterCommand(nameof(RenameProfileCommand));

    public static readonly DependencyProperty DeleteProfileCommandProperty =
        RegisterCommand(nameof(DeleteProfileCommand));

    public static readonly DependencyProperty CanEditProfilesProperty =
        DependencyProperty.Register(
            nameof(CanEditProfiles),
            typeof(bool),
            typeof(WallpaperProfilesSection),
            new PropertyMetadata(true));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(
            nameof(IsCompact),
            typeof(bool),
            typeof(WallpaperProfilesSection),
            new PropertyMetadata(false, OnIsCompactChanged));

    public WallpaperProfilesSection()
    {
        InitializeComponent();
    }

    public IEnumerable? ProfileItemsSource
    {
        get => (IEnumerable?)GetValue(ProfileItemsSourceProperty);
        set => SetValue(ProfileItemsSourceProperty, value);
    }

    public WallpaperProfileCardItem? SelectedProfile
    {
        get => (WallpaperProfileCardItem?)GetValue(SelectedProfileProperty);
        set => SetValue(SelectedProfileProperty, value);
    }

    public ICommand? CreateProfileCommand
    {
        get => GetCommand(CreateProfileCommandProperty);
        set => SetValue(CreateProfileCommandProperty, value);
    }

    public ICommand? DuplicateProfileCommand
    {
        get => GetCommand(DuplicateProfileCommandProperty);
        set => SetValue(DuplicateProfileCommandProperty, value);
    }

    public ICommand? RenameProfileCommand
    {
        get => GetCommand(RenameProfileCommandProperty);
        set => SetValue(RenameProfileCommandProperty, value);
    }

    public ICommand? DeleteProfileCommand
    {
        get => GetCommand(DeleteProfileCommandProperty);
        set => SetValue(DeleteProfileCommandProperty, value);
    }

    public bool CanEditProfiles
    {
        get => (bool)GetValue(CanEditProfilesProperty);
        set => SetValue(CanEditProfilesProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public void FocusSelectedProfile()
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (SelectedProfile is not null &&
                    ProfileList.ItemContainerGenerator.ContainerFromItem(SelectedProfile)
                        is ListBoxItem container)
                {
                    _ = container.Focus();
                    return;
                }

                _ = ProfileList.Focus();
            });
    }

    private static DependencyProperty RegisterCommand(string propertyName) =>
        DependencyProperty.Register(
            propertyName,
            typeof(ICommand),
            typeof(WallpaperProfilesSection),
            new PropertyMetadata(null));

    private static void OnIsCompactChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((WallpaperProfilesSection)dependencyObject).UpdateCompactState();
    }

    private ICommand? GetCommand(DependencyProperty property) =>
        (ICommand?)GetValue(property);

    private void UpdateCompactState()
    {
        var expandedVisibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;
        var compactVisibility = IsCompact ? Visibility.Visible : Visibility.Collapsed;
        ExpandedProfilesHeader.Visibility = expandedVisibility;
        ExpandedCreateProfileButton.Visibility = expandedVisibility;
        CompactProfilesHeader.Visibility = compactVisibility;
        CompactCreateProfileButton.Visibility = compactVisibility;
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        if (e.AddedItems.Count > 0)
        {
            ProfileList.ScrollIntoView(e.AddedItems[0]);
        }
    }

    private void ProfileItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _ = e;
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
        }
    }

    private void ProfileActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(ProfileList, button) is ListBoxItem item)
        {
            item.IsSelected = true;
        }

        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
