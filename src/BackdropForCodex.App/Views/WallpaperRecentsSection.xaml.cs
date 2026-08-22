using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BackdropForCodex.App.ViewModels;

namespace BackdropForCodex.App.Views;

internal partial class WallpaperRecentsSection : UserControl
{
    public static readonly DependencyProperty RecentItemsSourceProperty =
        DependencyProperty.Register(
            nameof(RecentItemsSource),
            typeof(IEnumerable),
            typeof(WallpaperRecentsSection),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedRecentMediaProperty =
        DependencyProperty.Register(
            nameof(SelectedRecentMedia),
            typeof(RecentMediaItem),
            typeof(WallpaperRecentsSection),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty OpenRecentMediaCommandProperty =
        RegisterCommand(nameof(OpenRecentMediaCommand));

    public static readonly DependencyProperty RemoveRecentCommandProperty =
        RegisterCommand(nameof(RemoveRecentCommand));

    public static readonly DependencyProperty ClearRecentsCommandProperty =
        RegisterCommand(nameof(ClearRecentsCommand));

    public static readonly DependencyProperty CanChooseMediaProperty =
        DependencyProperty.Register(
            nameof(CanChooseMedia),
            typeof(bool),
            typeof(WallpaperRecentsSection),
            new PropertyMetadata(true));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(
            nameof(IsCompact),
            typeof(bool),
            typeof(WallpaperRecentsSection),
            new PropertyMetadata(false, OnIsCompactChanged));

    public WallpaperRecentsSection()
    {
        InitializeComponent();
    }

    public event EventHandler? RecentMediaInvoked;

    public IEnumerable? RecentItemsSource
    {
        get => (IEnumerable?)GetValue(RecentItemsSourceProperty);
        set => SetValue(RecentItemsSourceProperty, value);
    }

    public RecentMediaItem? SelectedRecentMedia
    {
        get => (RecentMediaItem?)GetValue(SelectedRecentMediaProperty);
        set => SetValue(SelectedRecentMediaProperty, value);
    }

    public ICommand? OpenRecentMediaCommand
    {
        get => GetCommand(OpenRecentMediaCommandProperty);
        set => SetValue(OpenRecentMediaCommandProperty, value);
    }

    public ICommand? RemoveRecentCommand
    {
        get => GetCommand(RemoveRecentCommandProperty);
        set => SetValue(RemoveRecentCommandProperty, value);
    }

    public ICommand? ClearRecentsCommand
    {
        get => GetCommand(ClearRecentsCommandProperty);
        set => SetValue(ClearRecentsCommandProperty, value);
    }

    public bool CanChooseMedia
    {
        get => (bool)GetValue(CanChooseMediaProperty);
        set => SetValue(CanChooseMediaProperty, value);
    }

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    private static DependencyProperty RegisterCommand(string propertyName) =>
        DependencyProperty.Register(
            propertyName,
            typeof(ICommand),
            typeof(WallpaperRecentsSection),
            new PropertyMetadata(null));

    private static void OnIsCompactChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((WallpaperRecentsSection)dependencyObject).UpdateCompactState();
    }

    private ICommand? GetCommand(DependencyProperty property) =>
        (ICommand?)GetValue(property);

    private void UpdateCompactState()
    {
        ExpandedRecentHeader.Visibility =
            IsCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactRecentHeader.Visibility =
            IsCompact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        if (e.AddedItems.Count > 0)
        {
            RecentList.ScrollIntoView(e.AddedItems[0]);
        }
    }

    private void RecentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(RecentList, source) is not ListBoxItem item ||
            item.DataContext is not RecentMediaItem recent)
        {
            return;
        }

        SetCurrentValue(SelectedRecentMediaProperty, recent);
        InvokeSelectedRecentMedia();
        e.Handled = true;
    }

    private void RecentList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        InvokeSelectedRecentMedia();
        e.Handled = true;
    }

    private void InvokeSelectedRecentMedia()
    {
        if (SelectedRecentMedia is not { } recent)
        {
            return;
        }

        var command = OpenRecentMediaCommand;
        if (command is not null && !command.CanExecute(recent))
        {
            return;
        }

        command?.Execute(recent);
        RecentMediaInvoked?.Invoke(this, EventArgs.Empty);
    }
}
