using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BackdropForCodex.App.ViewModels;

namespace BackdropForCodex.App.Views;

[DefaultProperty(nameof(ItemsSource))]
public partial class WallpaperProfileStripView : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(WallpaperProfileStripView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(
            nameof(SelectedItem),
            typeof(WallpaperProfileCardItem),
            typeof(WallpaperProfileStripView),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty CreateProfileCommandProperty =
        DependencyProperty.Register(
            nameof(CreateProfileCommand),
            typeof(ICommand),
            typeof(WallpaperProfileStripView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DuplicateProfileCommandProperty =
        DependencyProperty.Register(
            nameof(DuplicateProfileCommand),
            typeof(ICommand),
            typeof(WallpaperProfileStripView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty RenameProfileCommandProperty =
        DependencyProperty.Register(
            nameof(RenameProfileCommand),
            typeof(ICommand),
            typeof(WallpaperProfileStripView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty DeleteProfileCommandProperty =
        DependencyProperty.Register(
            nameof(DeleteProfileCommand),
            typeof(ICommand),
            typeof(WallpaperProfileStripView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CanEditProfilesProperty =
        DependencyProperty.Register(
            nameof(CanEditProfiles),
            typeof(bool),
            typeof(WallpaperProfileStripView),
            new PropertyMetadata(true));

    public WallpaperProfileStripView()
    {
        InitializeComponent();
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public WallpaperProfileCardItem? SelectedItem
    {
        get => (WallpaperProfileCardItem?)GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public ICommand? CreateProfileCommand
    {
        get => (ICommand?)GetValue(CreateProfileCommandProperty);
        set => SetValue(CreateProfileCommandProperty, value);
    }

    public ICommand? DuplicateProfileCommand
    {
        get => (ICommand?)GetValue(DuplicateProfileCommandProperty);
        set => SetValue(DuplicateProfileCommandProperty, value);
    }

    public ICommand? RenameProfileCommand
    {
        get => (ICommand?)GetValue(RenameProfileCommandProperty);
        set => SetValue(RenameProfileCommandProperty, value);
    }

    public ICommand? DeleteProfileCommand
    {
        get => (ICommand?)GetValue(DeleteProfileCommandProperty);
        set => SetValue(DeleteProfileCommandProperty, value);
    }

    public bool CanEditProfiles
    {
        get => (bool)GetValue(CanEditProfilesProperty);
        set => SetValue(CanEditProfilesProperty, value);
    }

    /// <summary>
    /// Restores keyboard focus after profile CRUD without exposing the internal list to callers.
    /// </summary>
    public void FocusSelectedProfile()
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (SelectedItem is not null &&
                    ProfileList.ItemContainerGenerator.ContainerFromItem(SelectedItem)
                        is ListBoxItem container)
                {
                    _ = container.Focus();
                    return;
                }

                _ = ProfileList.Focus();
            });
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        if (e.AddedItems.Count > 0)
        {
            ProfileList.ScrollIntoView(e.AddedItems[0]);
        }
    }

    private void ProfileItem_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs e)
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
