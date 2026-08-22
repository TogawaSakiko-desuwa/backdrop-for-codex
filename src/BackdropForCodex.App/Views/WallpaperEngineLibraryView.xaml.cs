using System.Windows;
using System.Windows.Controls;
using BackdropForCodex.App.Services.Localization;
using BackdropForCodex.App.ViewModels;
using BackdropForCodex.Core.Media;
using Microsoft.Win32;

namespace BackdropForCodex.App.Views;

public sealed partial class WallpaperEngineLibraryView : UserControl
{
    private readonly IAppTextProvider _text = new AppTextProvider();

    public WallpaperEngineLibraryView()
    {
        InitializeComponent();
    }

    public event EventHandler? AssignRequested;

    public event EventHandler? CloseRequested;

    public WallpaperSourceItemViewModel? SelectedItem =>
        SourceGridList.SelectedItem as WallpaperSourceItemViewModel;

    public WallpaperSourceDescriptor? SelectedSource => SelectedItem?.Descriptor;

    public void FocusSearch() => SearchBox.Focus();

    private void SourceGridList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is WallpaperSourceLibraryViewModel viewModel)
        {
            viewModel.SelectedItem = SelectedItem;
        }
    }

    private void Assign_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        if (SelectedItem?.CanAssign == true)
        {
            AssignRequested?.Invoke(this, EventArgs.Empty);
        }

        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private async void ChooseInstallation_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        if (DataContext is not WallpaperSourceLibraryViewModel viewModel ||
            !viewModel.CanManageWallpaperEngineInstallation)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = _text.GetStringOrFallback(
                "Source_ChooseInstallationDialogTitle",
                "Choose Wallpaper Engine wallpaper64.exe"),
            Filter = _text.GetStringOrFallback(
                "Source_InstallationFileFilter",
                "Wallpaper Engine|wallpaper64.exe"),
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _ = await viewModel
                .SelectInstallationAsync(dialog.FileName)
                .ConfigureAwait(true);
        }
    }

    private async void ChooseInstallationFolder_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        if (DataContext is not WallpaperSourceLibraryViewModel viewModel ||
            !viewModel.CanManageWallpaperEngineInstallation)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = _text.GetStringOrFallback(
                "Source_ChooseInstallFolderDialogTitle",
                "Choose the Wallpaper Engine installation folder"),
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _ = await viewModel
                .SelectInstallationAsync(dialog.FolderName)
                .ConfigureAwait(true);
        }
    }

    private async void ClearInstallation_Click(object sender, RoutedEventArgs e)
    {
        _ = sender;
        e.Handled = true;
        if (DataContext is WallpaperSourceLibraryViewModel viewModel)
        {
            _ = await viewModel
                .ClearInstallationSelectionAsync()
                .ConfigureAwait(true);
        }
    }

    private async void SourceCard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: WallpaperSourceItemViewModel item,
            })
        {
            return;
        }

        try
        {
            await item.EnsureThumbnailAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A thumbnail is optional presentation metadata. The item remains selectable and its
            // typed provider state is preserved by the view model.
        }

        e.Handled = false;
    }

    private void SourceCard_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: WallpaperSourceItemViewModel item,
            })
        {
            item.CancelThumbnailLoad();
        }

        e.Handled = false;
    }

    private void ContentFilter_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is WallpaperSourceLibraryViewModel viewModel &&
            ContentFilterBox?.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<WallpaperSourceContentFilter>(tag, out var filter))
        {
            viewModel.ContentFilter = filter;
        }
    }

    private void OriginFilter_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is WallpaperSourceLibraryViewModel viewModel &&
            OriginFilterBox?.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<WallpaperSourceOriginFilter>(tag, out var filter))
        {
            viewModel.OriginFilter = filter;
        }
    }
}
