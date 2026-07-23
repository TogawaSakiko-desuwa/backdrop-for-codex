using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Media.Imaging;
using BackdropForCodex.Core.Codex;
using BackdropForCodex.Core.Injection;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;
using BackdropForCodex.Core.Settings;
using BackdropForCodex.Core.Shortcuts;

namespace BackdropForCodex.App;

public partial class MainWindow : Window
{
    private readonly WallpaperCoordinator _coordinator;
    private SettingsV1 _settings = SettingsV1.CreateDefault();
    private string? _selectedMediaPath;
    private bool _initialized;
    private bool _suppressRiskPersistence;
    private bool _videoPreviewSelected;
    private bool _previewPlaybackRequested;
    private Task? _initializationTask;

    public MainWindow(WallpaperCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        InitializeComponent();
        _coordinator.StatusChanged += Coordinator_StatusChanged;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync();
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            _settings = await _coordinator.LoadSettingsAsync();
        }
        catch (SettingsStoreException)
        {
            _settings = SettingsV1.CreateDefault();
            SetStatus("设置文件无法读取，已使用安全默认值；保存后会重建。", isError: true);
        }

        PanelOpacitySlider.Value = _settings.PanelOpacity;
        BlurSlider.Value = _settings.BlurPx;
        FitComboBox.SelectedIndex = _settings.Fit == WallpaperFit.Cover ? 0 : 1;
        _suppressRiskPersistence = true;
        try
        {
            RiskAcknowledgement.IsChecked = _settings.AcceptedCdpRisk;
        }
        finally
        {
            _suppressRiskPersistence = false;
        }

        if (_settings.MediaPath is { } mediaPath && File.Exists(mediaPath))
        {
            _selectedMediaPath = mediaPath;
            MediaPathText.Text = Path.GetFileName(mediaPath);
            MediaPathText.ToolTip = mediaPath;
            ShowPreview(mediaPath);
        }

        _initialized = true;
    }

    public async Task BeginAutoLaunchAsync()
    {
        try
        {
            await InitializeAsync();
            if (_selectedMediaPath is null || !_settings.AcceptedCdpRisk)
            {
                SetStatus("请先选择壁纸并确认本机调试端口风险，然后再使用增强启动快捷方式。", isError: true);
                return;
            }

            await StartOrUpdateAsync();
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private void ChooseMedia_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 Codex 壁纸",
                Filter = "支持的媒体|*.png;*.jpg;*.jpeg;*.webp;*.mp4;*.webm|图片|*.png;*.jpg;*.jpeg;*.webp|视频|*.mp4;*.webm",
                CheckFileExists = true,
                Multiselect = false,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            _selectedMediaPath = dialog.FileName;
            MediaPathText.Text = Path.GetFileName(dialog.FileName);
            MediaPathText.ToolTip = dialog.FileName;
            ShowPreview(dialog.FileName);
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private void ShowPreview(string path)
    {
        try
        {
            VideoPreview.Stop();
            VideoPreview.Source = null;
            ImagePreview.Source = null;
            ImagePreview.Visibility = Visibility.Collapsed;
            VideoPreview.Visibility = Visibility.Collapsed;
            EmptyPreview.Visibility = Visibility.Collapsed;
            _videoPreviewSelected = false;
            _previewPlaybackRequested = false;

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".mp4" or ".webm")
            {
                VideoPreview.Source = new Uri(path, UriKind.Absolute);
                VideoPreview.Position = TimeSpan.Zero;
                VideoPreview.Visibility = Visibility.Visible;
                _videoPreviewSelected = true;
                _previewPlaybackRequested = true;
                ResumePreviewIfRequested();
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1600;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            ImagePreview.Source = bitmap;
            ImagePreview.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure("此文件无法在设置窗口中预览；启动前仍会进行格式与签名校验。");
        }
    }

    private void VideoPreview_MediaEnded(object sender, RoutedEventArgs e)
    {
        try
        {
            VideoPreview.Position = TimeSpan.Zero;
            ResumePreviewIfRequested();
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure("系统预览组件无法继续播放此视频；Codex 使用 Chromium 播放，启动前仍会校验格式。");
        }
    }

    private void VideoPreview_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ShowPreviewFailure("系统预览组件无法播放此视频；Codex 使用 Chromium 播放，启动前仍会校验格式。");
    }

    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        try
        {
            if (IsVisible)
            {
                ResumePreviewSafely();
            }
            else
            {
                SuspendPreviewSafely();
            }
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure("系统预览组件暂时不可用；这不会改变已选择的壁纸文件。");
        }
    }

    private async void RiskAcknowledgement_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressRiskPersistence)
        {
            return;
        }

        try
        {
            await InitializeAsync();
            _settings = await _coordinator.SaveSettingsAsync(
                _settings with { AcceptedCdpRisk = false });
            SetStatus("已撤销增强启动确认；桌面快捷方式下次不会自动开启调试端口。", isError: false);
        }
        catch (ObjectDisposedException)
        {
            // The app is already completing an explicit shutdown.
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StartOrUpdateAsync();
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private async Task StartOrUpdateAsync()
    {
        var controlsDisabled = false;
        try
        {
            await InitializeAsync();
            if (_selectedMediaPath is null)
            {
                SetStatus("请先选择壁纸。", isError: true);
                return;
            }

            if (RiskAcknowledgement.IsChecked != true)
            {
                SetStatus("增强启动前需要确认本机 Chromium 调试端口风险。", isError: true);
                return;
            }

            SetControlsEnabled(false);
            controlsDisabled = true;
            var request = _settings with
            {
                MediaPath = _selectedMediaPath,
                Fit = FitComboBox.SelectedIndex == 1 ? WallpaperFit.Contain : WallpaperFit.Cover,
                PanelOpacity = PanelOpacitySlider.Value,
                BlurPx = BlurSlider.Value,
                AcceptedCdpRisk = true,
            };
            _settings = await _coordinator.StartOrUpdateAsync(request);
            LaunchButton.Content = "应用壁纸更改";
            PauseButton.IsEnabled = _settings.MediaKind == MediaKind.Video;
            PauseButton.Content = _coordinator.IsPaused ? "继续视频" : "暂停视频";
            if (_settings.MediaKind == MediaKind.Video)
            {
                _previewPlaybackRequested = !_coordinator.IsPaused;
                if (_previewPlaybackRequested)
                {
                    ResumePreviewSafely();
                }
                else
                {
                    SuspendPreviewSafely();
                }
            }

            var shortcutCreated = TryCreateEnhancedLaunchShortcut();
            SetStatus(
                shortcutCreated
                    ? "壁纸已生效，并已创建或更新桌面增强启动快捷方式。"
                    : "壁纸已生效；桌面快捷方式创建失败，但不影响本次运行。",
                isError: !shortcutCreated);
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
        finally
        {
            if (controlsDisabled)
            {
                SetControlsEnabled(true);
            }
        }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pause = !_coordinator.IsPaused;
            await _coordinator.SetPausedAsync(pause);
            PauseButton.Content = pause ? "继续视频" : "暂停视频";
            _previewPlaybackRequested = !pause;
            if (pause)
            {
                SuspendPreviewSafely();
            }
            else
            {
                ResumePreviewSafely();
            }

            SetStatus(pause ? "视频壁纸已暂停。" : "视频壁纸已继续播放。", isError: false);
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private async void Disable_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await DisableWallpaperAsync();
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    public async Task DisableWallpaperAsync()
    {
        try
        {
            _previewPlaybackRequested = false;
            SuspendPreviewSafely();
            await _coordinator.DisableAsync();
            LaunchButton.Content = "保存并增强启动 Codex";
            PauseButton.Content = "暂停视频";
            PauseButton.IsEnabled = false;
            SetStatus("壁纸已关闭，官方背景已恢复；Codex 本身保持运行。", isError: false);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown can race a final tray action; the in-page lease still restores the UI.
        }
        catch (Exception exception)
        {
            ReportUnexpectedError(exception);
        }
    }

    private void Coordinator_StatusChanged(
        object? sender,
        WallpaperRuntimeStatusChangedEventArgs eventArgs)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var message = eventArgs.Phase switch
            {
                WallpaperRuntimePhase.Validating => "正在校验官方 Codex 与媒体文件…",
                WallpaperRuntimePhase.LaunchingCodex => "正在增强启动官方 Codex…",
                WallpaperRuntimePhase.DiscoveringEndpoint => "正在等待仅限本机的调试端口…",
                WallpaperRuntimePhase.Applying => "正在应用壁纸与玻璃效果…",
                WallpaperRuntimePhase.Active => "壁纸已启用。",
                WallpaperRuntimePhase.Paused => "视频壁纸已暂停。",
                WallpaperRuntimePhase.Stopping => "正在移除壁纸并恢复官方背景…",
                WallpaperRuntimePhase.Faulted =>
                    "壁纸心跳或运行连接已中止；已尝试停止本地媒体服务，页面会恢复官方背景。",
                _ => null,
            };
            if (message is not null)
            {
                SetStatus(
                    message,
                    isError: eventArgs.Phase == WallpaperRuntimePhase.Faulted);
            }
        });
    }

    private void SetControlsEnabled(bool enabled)
    {
        ChooseMediaButton.IsEnabled = enabled;
        LaunchButton.IsEnabled = enabled;
        FitComboBox.IsEnabled = enabled;
        PanelOpacitySlider.IsEnabled = enabled;
        BlurSlider.IsEnabled = enabled;
        RiskAcknowledgement.IsEnabled = enabled;
    }

    private static string ToUserMessage(Exception exception) => exception switch
    {
        CodexAlreadyRunningException =>
            "检测到 Codex 已在运行。请手动完全退出 Codex 后重试；本工具不会强制结束它。",
        UnsupportedCodexVersionException unsupported =>
            $"当前官方 Codex 版本尚未通过兼容性验证。{unsupported.Result.Reason}",
        CdpRiskNotAcceptedException => "请先确认本机 Chromium 调试端口风险。",
        WallpaperNotActiveException => "壁纸尚未启用，无法更改视频播放状态。",
        CdpEndpointTimeoutException =>
            "Codex 已启动，但未在限定时间内发布可验证的本机调试端口。请完全退出 Codex 后重试。",
        AmbiguousCdpEndpointException =>
            "发现多个可验证的 Codex 调试端口，已按安全策略停止注入。请完全退出 Codex 后重试。",
        CodexPackageDiscoveryException => "未找到受支持的官方 Microsoft Store/MSIX Codex。",
        MediaValidationException => "壁纸文件未通过扩展名与文件签名校验，或文件无法读取。",
        SettingsValidationException => "壁纸设置无效，请恢复滑块默认值后重试。",
        SettingsStoreException => "设置无法安全保存，请检查本地应用数据目录权限。",
        WallpaperMediaLoadException =>
            "壁纸文件已交给 Codex，但 Chromium 无法加载或解码该媒体；未保留无效背景。",
        WallpaperInjectionException => "已连接 Codex，但当前页面结构未通过注入前检查。",
        OperationCanceledException => "操作已取消。",
        _ => "操作失败；未对 Codex 做破坏性修改。请完全退出 Codex 后重试。",
    };

    private static bool TryCreateEnhancedLaunchShortcut()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return false;
        }

        try
        {
            _ = WindowsDesktopShortcutService.CreateOrUpdate();
            return true;
        }
        catch (Exception)
        {
            // Shortcut creation is optional and must never turn an active wallpaper into a failed run.
            return false;
        }
    }

    private void ResumePreviewIfRequested()
    {
        if (!_videoPreviewSelected ||
            !_previewPlaybackRequested ||
            !IsVisible ||
            VideoPreview.Source is null)
        {
            return;
        }

        VideoPreview.Play();
    }

    private void SuspendPreview()
    {
        if (_videoPreviewSelected && VideoPreview.Source is not null)
        {
            VideoPreview.Pause();
        }
    }

    private void ResumePreviewSafely()
    {
        try
        {
            ResumePreviewIfRequested();
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure("系统预览组件暂时不可用；这不会改变 Codex 中的壁纸状态。");
        }
    }

    private void SuspendPreviewSafely()
    {
        try
        {
            SuspendPreview();
        }
        catch (Exception exception) when (IsControlledPreviewException(exception))
        {
            ShowPreviewFailure("系统预览组件暂时不可用；这不会阻止恢复 Codex 官方背景。");
        }
    }

    private void ShowPreviewFailure(string message)
    {
        try
        {
            _previewPlaybackRequested = false;
            VideoPreview.Pause();
            VideoPreview.Visibility = Visibility.Collapsed;
            EmptyPreview.Text = message;
            EmptyPreview.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // The visual tree or media graph is already unavailable; no preview failure is fatal.
        }
    }

    private static bool IsControlledPreviewException(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        FormatException or
        ArgumentException or
        InvalidOperationException or
        ExternalException or
        SecurityException;

    internal void ReportUnexpectedError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            SetStatus(ToUserMessage(exception), isError: true);
        }
        catch (Exception)
        {
            // The WPF visual tree may already be unavailable during explicit shutdown.
        }
    }

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = $"状态：{message}";
        StatusText.Foreground = isError
            ? System.Windows.Media.Brushes.IndianRed
            : System.Windows.Media.Brushes.LightGray;
    }
}
