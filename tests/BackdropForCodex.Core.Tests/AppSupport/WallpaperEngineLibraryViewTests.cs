using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.Services.Media;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.App.Views;
using BackdropForCodex.Core.Media;
using Wpf.Ui.Appearance;
using Xunit;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace BackdropForCodex.Core.Tests.AppSupport;

[Collection("Wpf")]
public sealed class WallpaperEngineLibraryViewTests
{
    [Fact]
    public void ModalSurfaceIsOpaqueInDarkAndLightThemes()
    {
        StaTest.Run(
            () =>
            {
                using var viewModel = CreateViewModel(itemCount: 0);
                var view = CreateArrangedView(viewModel, 640, 620);
                var surface = Assert.IsType<Border>(view.Content);

                try
                {
                    ApplicationThemeManager.Apply(
                        ApplicationTheme.Dark,
                        WindowBackdropType.None,
                        updateAccent: true);
                    view.UpdateLayout();
                    AssertOpaque(surface.Background);

                    ApplicationThemeManager.Apply(
                        ApplicationTheme.Light,
                        WindowBackdropType.None,
                        updateAccent: true);
                    view.UpdateLayout();
                    AssertOpaque(surface.Background);
                }
                finally
                {
                    ApplicationThemeManager.Apply(
                        ApplicationTheme.Light,
                        WindowBackdropType.None,
                        updateAccent: true);
                }
            });
    }

    [Fact]
    public void EmptySelectionUsesTheLocalizedProjectPrompt()
    {
        StaTest.Run(
            () =>
            {
                var originalCulture = CultureInfo.CurrentUICulture;
                try
                {
                    CultureInfo.CurrentUICulture =
                        CultureInfo.GetCultureInfo("zh-Hans");
                    using var viewModel = CreateViewModel(itemCount: 0);
                    viewModel.RefreshAsync().GetAwaiter().GetResult();
                    var view = CreateArrangedView(viewModel, 920, 620);
                    var textBlocks = FindVisualDescendants<TextBlock>(view).ToArray();
                    var expected =
                        new AppTextProvider().GetString("Source_SelectProject");

                    Assert.Contains(
                        textBlocks,
                        text => string.Equals(text.Text, expected, StringComparison.Ordinal));
                    Assert.DoesNotContain(
                        textBlocks,
                        text => string.Equals(
                            text.Text,
                            "Select a project",
                            StringComparison.Ordinal));
                }
                finally
                {
                    CultureInfo.CurrentUICulture = originalCulture;
                }
            });
    }

    [Fact]
    public void SelectingACardDoesNotAssignUntilTheExplicitActionIsInvoked()
    {
        StaTest.Run(
            () =>
            {
                using var viewModel = CreateViewModel(itemCount: 2);
                viewModel.RefreshAsync().GetAwaiter().GetResult();
                var view = CreateArrangedView(viewModel, 920, 620);
                var sourceList = FindElement<ListBox>(view, "SourceGridList");
                var first = viewModel.Items[0];
                var assignmentCount = 0;
                view.AssignRequested += (_, _) => assignmentCount++;

                sourceList.SelectedItem = first;
                Assert.Equal(0, assignmentCount);
                Assert.Same(first, view.SelectedItem);

                var assign = FindElement<ButtonBase>(view, "AssignSourceButton");
                assign.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, assign));

                Assert.Equal(1, assignmentCount);
                Assert.Same(first.Descriptor, view.SelectedSource);
            });
    }

    [Fact]
    public void SourceGridUsesRecyclingVirtualizationAndDoesNotRealizeHundredsOfCards()
    {
        StaTest.Run(
            () =>
            {
                using var viewModel = CreateViewModel(itemCount: 480);
                viewModel.RefreshAsync().GetAwaiter().GetResult();
                var view = CreateArrangedView(viewModel, 920, 620);
                var sourceList = FindElement<ListBox>(view, "SourceGridList");

                Assert.True(VirtualizingPanel.GetIsVirtualizing(sourceList));
                Assert.Equal(
                    VirtualizationMode.Recycling,
                    VirtualizingPanel.GetVirtualizationMode(sourceList));
                Assert.True(ScrollViewer.GetCanContentScroll(sourceList));
                Assert.Equal(
                    ScrollBarVisibility.Disabled,
                    ScrollViewer.GetHorizontalScrollBarVisibility(sourceList));
                Assert.Equal(
                    ScrollBarVisibility.Auto,
                    ScrollViewer.GetVerticalScrollBarVisibility(sourceList));

                var panel = FindVisualDescendant<VirtualizingWrapPanel>(sourceList);
                Assert.NotNull(panel);
                Assert.True(panel.Children.Count < 80);
                Assert.Equal(480, sourceList.Items.Count);
            });
    }

    [Fact]
    public void FirstVisibleLayoutRealizesItemsPublishedWhileLibraryWasCollapsed()
    {
        StaTest.Run(
            () =>
            {
                using var viewModel = CreateViewModel(itemCount: 1);
                var view = new WallpaperEngineLibraryView
                {
                    DataContext = viewModel,
                    Visibility = Visibility.Collapsed,
                };
                var window = new Window
                {
                    Style = new Style(typeof(Window)),
                    Content = view,
                    Width = 920,
                    Height = 620,
                    Left = -10000,
                    Top = -10000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };
                try
                {
                    window.Show();
                    window.Dispatcher.Invoke(
                        static () => { },
                        DispatcherPriority.ApplicationIdle);

                    viewModel.RefreshAsync().GetAwaiter().GetResult();
                    var item = Assert.Single(viewModel.Items);

                    view.Visibility = Visibility.Visible;
                    window.UpdateLayout();

                    var sourceList = FindElement<ListBox>(view, "SourceGridList");
                    Assert.NotNull(
                        sourceList.ItemContainerGenerator.ContainerFromItem(item));
                }
                finally
                {
                    window.Close();
                }
            });
    }

    [Fact]
    public void DynamicProjectCardExposesStaticPreviewAndUnavailableActivationStatus()
    {
        StaTest.Run(
            () =>
            {
                using var viewModel = CreateViewModel(
                    itemCount: 1,
                    WallpaperContentKind.Scene);
                viewModel.RefreshAsync().GetAwaiter().GetResult();
                var view = CreateArrangedView(viewModel, 720, 560);
                var sourceList = FindElement<ListBox>(view, "SourceGridList");
                var item = Assert.Single(viewModel.Items);
                sourceList.ScrollIntoView(item);
                view.UpdateLayout();

                var container = Assert.IsType<ListBoxItem>(
                    sourceList.ItemContainerGenerator.ContainerFromItem(item));
                var text = new AppTextProvider();
                Assert.Equal(
                    text.GetString("Source_RendererUnavailable"),
                    AutomationProperties.GetItemStatus(container));
                Assert.Equal(
                    text.GetString("Media_Scene"),
                    AutomationProperties.GetHelpText(container));
                Assert.NotEqual(
                    item.Availability.ToString(),
                    AutomationProperties.GetItemStatus(container));
                Assert.Contains(
                    FindVisualDescendants<TextBlock>(container),
                    text => string.Equals(
                        text.Text,
                        new AppTextProvider().GetString("Source_StaticPreview"),
                        StringComparison.Ordinal));
                Assert.False(item.CanApply);
                Assert.True(item.CanAssign);
            });
    }

    [Fact]
    public void MultipleInstallationsExposeAKeyboardReachableSelectionEntry()
    {
        StaTest.Run(
            () =>
            {
                var provider = new FailingProvider(
                    new WallpaperEngineUnavailableException(
                        WallpaperEngineAvailabilityReason.MultipleInstallations));
                using var viewModel = new WallpaperSourceLibraryViewModel(
                    new WallpaperSourceProviderRegistry([provider]),
                    NullWallpaperThumbnailPreviewService.Instance,
                    installationSelectionService: new NoOpInstallationSelectionService());
                viewModel.RefreshAsync().GetAwaiter().GetResult();
                var view = CreateArrangedView(viewModel, 920, 620);
                var choose = FindElement<ButtonBase>(view, "ChooseInstallationButton");
                var clear = FindElement<ButtonBase>(view, "ClearInstallationButton");

                Assert.True(choose.IsEnabled);
                Assert.Equal(Visibility.Visible, choose.Visibility);
                Assert.Equal(Visibility.Collapsed, clear.Visibility);
                Assert.Contains(
                    FindVisualDescendants<TextBlock>(view),
                    text => string.Equals(
                        text.Text,
                        new AppTextProvider().GetString(
                            "Source_InstallationSelectionRequiredTitle"),
                        StringComparison.Ordinal));
            });
    }

    private static WallpaperSourceLibraryViewModel CreateViewModel(
        int itemCount,
        WallpaperContentKind contentKind = WallpaperContentKind.Video)
    {
        var sources = Enumerable
            .Range(1, itemCount)
            .Select(
                index =>
                {
                    var dynamic = contentKind is
                        WallpaperContentKind.Scene or WallpaperContentKind.Web;
                    return new WallpaperSourceDescriptor(
                        MediaSourceKind.WallpaperEngineWorkshopProject,
                        index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        $"Source {index:D3}",
                        contentKind,
                        dynamic
                            ? WallpaperDeliveryKind.WallpaperEngineWindow
                            : WallpaperDeliveryKind.DirectMedia,
                        dynamic
                            ? WallpaperDeliveryCapabilities.DynamicFrames
                            : WallpaperDeliveryCapabilities.None);
                })
            .ToArray();
        return new WallpaperSourceLibraryViewModel(
            new WallpaperSourceProviderRegistry([new StaticProvider(sources)]),
            NullWallpaperThumbnailPreviewService.Instance);
    }

    private static WallpaperEngineLibraryView CreateArrangedView(
        WallpaperSourceLibraryViewModel viewModel,
        double width,
        double height)
    {
        var view = new WallpaperEngineLibraryView
        {
            DataContext = viewModel,
        };
        view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, view));
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        return view;
    }

    private static T FindElement<T>(FrameworkElement view, string name)
        where T : FrameworkElement
    {
        if (view.FindName(name) is T direct)
        {
            return direct;
        }

        return Assert.IsType<T>(FindNamedDescendant(view, name));
    }

    private static FrameworkElement? FindNamedDescendant(
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

            if (FindNamedDescendant(child, name) is { } descendant)
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

    private static void AssertOpaque(Brush brush)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        Assert.Equal(byte.MaxValue, solid.Color.A);
        Assert.Equal(1D, solid.Opacity);
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

    private sealed class StaticProvider(
        IReadOnlyList<WallpaperSourceDescriptor> sources)
        : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKind =>
            MediaSourceKind.WallpaperEngineWorkshopProject;

        public ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(sources);
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FailingProvider(Exception failure) : IWallpaperSourceProvider
    {
        public MediaSourceKind SourceKind =>
            MediaSourceKind.WallpaperEngineWorkshopProject;

        public ValueTask<IReadOnlyList<WallpaperSourceDescriptor>> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<IReadOnlyList<WallpaperSourceDescriptor>>(failure);
        }

        public ValueTask<WallpaperSourceResolution> ResolveAsync(
            MediaReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoOpInstallationSelectionService
        : IWallpaperEngineInstallationSelectionService
    {
        public bool HasPreferredWallpaperEngineInstallation => false;

        public Task SelectWallpaperEngineInstallationAsync(
            string selectedPath,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClearWallpaperEngineInstallationAsync(
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
