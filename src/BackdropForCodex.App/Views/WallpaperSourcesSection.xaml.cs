using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BackdropForCodex.Core.Media;

namespace BackdropForCodex.App.Views;

internal partial class WallpaperSourcesSection : UserControl
{
    public static readonly DependencyProperty SourceItemsSourceProperty =
        DependencyProperty.Register(
            nameof(SourceItemsSource),
            typeof(IEnumerable),
            typeof(WallpaperSourcesSection),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedSourceProperty =
        DependencyProperty.Register(
            nameof(SelectedSource),
            typeof(WallpaperSourceDescriptor),
            typeof(WallpaperSourcesSection),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(
            nameof(IsCompact),
            typeof(bool),
            typeof(WallpaperSourcesSection),
            new PropertyMetadata(false, OnDisplayStateChanged));

    public static readonly DependencyProperty HasDiscoveryFailuresProperty =
        DependencyProperty.Register(
            nameof(HasDiscoveryFailures),
            typeof(bool),
            typeof(WallpaperSourcesSection),
            new PropertyMetadata(false, OnDisplayStateChanged));

    public static readonly DependencyProperty RefreshSourcesCommandProperty =
        DependencyProperty.Register(
            nameof(RefreshSourcesCommand),
            typeof(ICommand),
            typeof(WallpaperSourcesSection),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CanChooseSourcesProperty =
        DependencyProperty.Register(
            nameof(CanChooseSources),
            typeof(bool),
            typeof(WallpaperSourcesSection),
            new PropertyMetadata(true));

    public WallpaperSourcesSection()
    {
        InitializeComponent();
    }

    public event EventHandler? SourceInvoked;

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

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public bool HasDiscoveryFailures
    {
        get => (bool)GetValue(HasDiscoveryFailuresProperty);
        set => SetValue(HasDiscoveryFailuresProperty, value);
    }

    public ICommand? RefreshSourcesCommand
    {
        get => (ICommand?)GetValue(RefreshSourcesCommandProperty);
        set => SetValue(RefreshSourcesCommandProperty, value);
    }

    public bool CanChooseSources
    {
        get => (bool)GetValue(CanChooseSourcesProperty);
        set => SetValue(CanChooseSourcesProperty, value);
    }

    private static void OnDisplayStateChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        _ = eventArgs;
        ((WallpaperSourcesSection)dependencyObject).UpdateCompactState();
    }

    private void UpdateCompactState()
    {
        ExpandedSourcesHeader.Visibility =
            IsCompact ? Visibility.Collapsed : Visibility.Visible;
        CompactSourcesHeader.Visibility =
            IsCompact ? Visibility.Visible : Visibility.Collapsed;
        ExpandedSourceFailure.Visibility =
            HasDiscoveryFailures && !IsCompact
                ? Visibility.Visible
                : Visibility.Collapsed;
        CompactSourceFailureButton.Visibility =
            HasDiscoveryFailures && IsCompact
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void SourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _ = sender;
        if (e.AddedItems.Count > 0)
        {
            SourceList.ScrollIntoView(e.AddedItems[0]);
        }
    }

    private void SourceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(SourceList, source) is not ListBoxItem item ||
            item.DataContext is not WallpaperSourceDescriptor descriptor)
        {
            return;
        }

        SetCurrentValue(SelectedSourceProperty, descriptor);
        InvokeSelectedSource();
        e.Handled = true;
    }

    private void SourceList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Key.Enter)
        {
            return;
        }

        InvokeSelectedSource();
        e.Handled = true;
    }

    private void InvokeSelectedSource()
    {
        if (!CanInvokeSource(SelectedSource))
        {
            return;
        }

        SourceInvoked?.Invoke(this, EventArgs.Empty);
    }

    internal static bool CanInvokeSource(WallpaperSourceDescriptor? source) =>
        source?.DeliveryKind == WallpaperDeliveryKind.DirectMedia;
}
