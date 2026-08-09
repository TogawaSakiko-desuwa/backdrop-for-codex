using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.App.Views;

[DefaultProperty(nameof(ProfileItemsSource))]
public partial class WallpaperLibraryView : UserControl
{
    public const double ExpandedWidth = 224;
    public const double CompactWidth = 56;

    public static readonly DependencyProperty ProfileItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ProfileItemsSource),
            typeof(IEnumerable),
            typeof(WallpaperLibraryView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedProfileProperty =
        DependencyProperty.Register(
            nameof(SelectedProfile),
            typeof(WallpaperProfileCardItem),
            typeof(WallpaperLibraryView),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedProfileChanged));

    public static readonly DependencyProperty SourceItemsSourceProperty =
        DependencyProperty.Register(
            nameof(SourceItemsSource),
            typeof(IEnumerable),
            typeof(WallpaperLibraryView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedSourceProperty =
        DependencyProperty.Register(
            nameof(SelectedSource),
            typeof(WallpaperSourceDescriptor),
            typeof(WallpaperLibraryView),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HasSourceDiscoveryFailuresProperty =
        DependencyProperty.Register(
            nameof(HasSourceDiscoveryFailures),
            typeof(bool),
            typeof(WallpaperLibraryView),
            new PropertyMetadata(false));

    public static readonly DependencyProperty RecentItemsSourceProperty =
        DependencyProperty.Register(
            nameof(RecentItemsSource),
            typeof(IEnumerable),
            typeof(WallpaperLibraryView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedRecentMediaProperty =
        DependencyProperty.Register(
            nameof(SelectedRecentMedia),
            typeof(RecentMediaItem),
            typeof(WallpaperLibraryView),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedRecentMediaChanged));

    public static readonly DependencyProperty CreateProfileCommandProperty =
        RegisterCommand(nameof(CreateProfileCommand));

    public static readonly DependencyProperty DuplicateProfileCommandProperty =
        RegisterCommand(nameof(DuplicateProfileCommand));

    public static readonly DependencyProperty RenameProfileCommandProperty =
        RegisterCommand(nameof(RenameProfileCommand));

    public static readonly DependencyProperty DeleteProfileCommandProperty =
        RegisterCommand(nameof(DeleteProfileCommand));

    public static readonly DependencyProperty ChooseLocalMediaCommandProperty =
        RegisterCommand(nameof(ChooseLocalMediaCommand));

    public static readonly DependencyProperty RefreshSourcesCommandProperty =
        RegisterCommand(nameof(RefreshSourcesCommand));

    public static readonly DependencyProperty OpenRecentMediaCommandProperty =
        RegisterCommand(nameof(OpenRecentMediaCommand));

    public static readonly DependencyProperty RemoveRecentCommandProperty =
        RegisterCommand(nameof(RemoveRecentCommand));

    public static readonly DependencyProperty ClearRecentsCommandProperty =
        RegisterCommand(nameof(ClearRecentsCommand));

    public static readonly DependencyProperty CanEditProfilesProperty =
        DependencyProperty.Register(
            nameof(CanEditProfiles),
            typeof(bool),
            typeof(WallpaperLibraryView),
            new PropertyMetadata(true));

    public static readonly DependencyProperty CanChooseMediaProperty =
        DependencyProperty.Register(
            nameof(CanChooseMedia),
            typeof(bool),
            typeof(WallpaperLibraryView),
            new PropertyMetadata(true));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(
            nameof(IsCompact),
            typeof(bool),
            typeof(WallpaperLibraryView),
            new PropertyMetadata(false, OnIsCompactChanged));

    public static readonly RoutedEvent SelectedProfileChangedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(SelectedProfileChanged),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(WallpaperLibraryView));

    public static readonly RoutedEvent SelectedRecentMediaChangedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(SelectedRecentMediaChanged),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(WallpaperLibraryView));

    public static readonly RoutedEvent RecentMediaInvokedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(RecentMediaInvoked),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(WallpaperLibraryView));

    public static readonly RoutedEvent SourceInvokedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(SourceInvoked),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(WallpaperLibraryView));

    public static readonly RoutedEvent ChooseLocalMediaRequestedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(ChooseLocalMediaRequested),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(WallpaperLibraryView));

    public WallpaperLibraryView()
    {
        InitializeComponent();
        Loaded += WallpaperLibraryView_Loaded;
    }

    public event RoutedEventHandler SelectedProfileChanged
    {
        add => AddHandler(SelectedProfileChangedEvent, value);
        remove => RemoveHandler(SelectedProfileChangedEvent, value);
    }

    public event RoutedEventHandler SelectedRecentMediaChanged
    {
        add => AddHandler(SelectedRecentMediaChangedEvent, value);
        remove => RemoveHandler(SelectedRecentMediaChangedEvent, value);
    }

    public event RoutedEventHandler RecentMediaInvoked
    {
        add => AddHandler(RecentMediaInvokedEvent, value);
        remove => RemoveHandler(RecentMediaInvokedEvent, value);
    }

    public event RoutedEventHandler SourceInvoked
    {
        add => AddHandler(SourceInvokedEvent, value);
        remove => RemoveHandler(SourceInvokedEvent, value);
    }

    public event RoutedEventHandler ChooseLocalMediaRequested
    {
        add => AddHandler(ChooseLocalMediaRequestedEvent, value);
        remove => RemoveHandler(ChooseLocalMediaRequestedEvent, value);
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

    public IEnumerable? SourceItemsSource
    {
        get => (IEnumerable?)GetValue(SourceItemsSourceProperty);
        set => SetValue(SourceItemsSourceProperty, value);
    }

    public WallpaperSourceDescriptor? SelectedSource
    {
        get => (WallpaperSourceDescriptor?)GetValue(SelectedSourceProperty);
        set => SetValue(SelectedSourceProperty, value);
    }

    public bool HasSourceDiscoveryFailures
    {
        get => (bool)GetValue(HasSourceDiscoveryFailuresProperty);
        set => SetValue(HasSourceDiscoveryFailuresProperty, value);
    }

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

    public ICommand? ChooseLocalMediaCommand
    {
        get => GetCommand(ChooseLocalMediaCommandProperty);
        set => SetValue(ChooseLocalMediaCommandProperty, value);
    }

    public ICommand? RefreshSourcesCommand
    {
        get => GetCommand(RefreshSourcesCommandProperty);
        set => SetValue(RefreshSourcesCommandProperty, value);
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

    public bool CanEditProfiles
    {
        get => (bool)GetValue(CanEditProfilesProperty);
        set => SetValue(CanEditProfilesProperty, value);
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

    public void FocusSelectedProfile() =>
        ProfilesSection.FocusSelectedProfile();

    private static DependencyProperty RegisterCommand(string propertyName) =>
        DependencyProperty.Register(
            propertyName,
            typeof(ICommand),
            typeof(WallpaperLibraryView),
            new PropertyMetadata(null));

    private static void OnSelectedProfileChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        var view = (WallpaperLibraryView)dependencyObject;
        view.RaiseEvent(new RoutedEventArgs(SelectedProfileChangedEvent, view));
    }

    private static void OnSelectedRecentMediaChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        var view = (WallpaperLibraryView)dependencyObject;
        view.RaiseEvent(new RoutedEventArgs(SelectedRecentMediaChangedEvent, view));
    }

    private static void OnIsCompactChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((WallpaperLibraryView)dependencyObject).UpdateCompactState(useTransitions: true);
    }

    private ICommand? GetCommand(DependencyProperty property) =>
        (ICommand?)GetValue(property);

    private void WallpaperLibraryView_Loaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        UpdateCompactState(useTransitions: false);
    }

    private void UpdateCompactState(bool useTransitions)
    {
        if (RailLayout is null)
        {
            return;
        }

        SetCurrentValue(WidthProperty, IsCompact ? CompactWidth : ExpandedWidth);
        ExpandedChooseMediaButton.Visibility =
            IsCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactChooseMediaButton.Visibility =
            IsCompact ? Visibility.Visible : Visibility.Collapsed;
        _ = VisualStateManager.GoToElementState(
            RailLayout,
            IsCompact ? "Compact" : "Expanded",
            useTransitions);
    }

    private void ChooseLocalMedia_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        var command = ChooseLocalMediaCommand;
        if (command?.CanExecute(parameter: null) == true)
        {
            command.Execute(parameter: null);
        }

        RaiseEvent(new RoutedEventArgs(ChooseLocalMediaRequestedEvent, this));
        e.Handled = true;
    }

    private void SourcesSection_SourceInvoked(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        SetCurrentValue(SelectedSourceProperty, SourcesSection.SelectedSource);
        RaiseEvent(new RoutedEventArgs(SourceInvokedEvent, this));
    }

    private void RecentsSection_RecentMediaInvoked(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        SetCurrentValue(
            SelectedRecentMediaProperty,
            RecentsSection.SelectedRecentMedia);
        RaiseEvent(new RoutedEventArgs(RecentMediaInvokedEvent, this));
    }
}
