using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

namespace BackdropForCodex.Core.Tests.AppSupport;

internal static class StaTest
{
    private static readonly Lazy<Dispatcher> SharedDispatcher =
        new(CreateSharedDispatcher);
    private static Application? _application;
    private static Window? _lifetimeWindow;

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        ExceptionDispatchInfo? capturedException = null;
        using var completed = new ManualResetEventSlim();
        _ = SharedDispatcher.Value.BeginInvoke(
            () =>
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    capturedException = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    completed.Set();
                }
            },
            DispatcherPriority.Send);

        if (!completed.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("The shared STA WPF test did not finish in time.");
        }

        capturedException?.Throw();
    }

    private static Dispatcher CreateSharedDispatcher()
    {
        var ready = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    _application = new Application
                    {
                        ShutdownMode = ShutdownMode.OnExplicitShutdown,
                    };
                    ConfigureApplicationResources(_application);
                    _lifetimeWindow = new Window
                    {
                        // Keep the test dispatcher alive without inheriting the
                        // application's implicit Window style. That style changes
                        // AllowsTransparency while the HWND is being created,
                        // which is illegal for an already materialized Window.
                        Style = new Style(typeof(Window)),
                        Width = 1,
                        Height = 1,
                        Left = -32000,
                        Top = -32000,
                        Opacity = 0,
                        ShowActivated = false,
                        ShowInTaskbar = false,
                        WindowStyle = WindowStyle.None,
                    };
                    ready.TrySetResult(Dispatcher.CurrentDispatcher);
                    _ = _application.Run(_lifetimeWindow);
                }
                catch (Exception exception)
                {
                    ready.TrySetException(exception);
                }
            })
        {
            IsBackground = true,
            Name = "BackdropForCodex.WpfTests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Task.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("The shared STA WPF dispatcher did not start in time.");
        }

        return ready.Task.GetAwaiter().GetResult();
    }

    private static void ConfigureApplicationResources(
        Application application)
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(
            new ThemesDictionary
            {
                Theme = ApplicationTheme.Light,
            });
        resources.MergedDictionaries.Add(new ControlsDictionary());
        application.Resources = resources;

        resources["AppIconImage"] = CreateAppIconImage();
        resources["PageHeadingStyle"] = CreateTextStyle(
            application,
            fontSize: 28,
            fontWeight: FontWeights.SemiBold,
            foregroundResource: "TextFillColorPrimaryBrush");
        resources["SectionHeadingStyle"] = CreateTextStyle(
            application,
            fontSize: 16,
            fontWeight: FontWeights.SemiBold,
            foregroundResource: "TextFillColorPrimaryBrush");

        var captionStyle = CreateTextStyle(
            application,
            fontSize: 12,
            foregroundResource: "TextFillColorSecondaryBrush");
        captionStyle.Setters.Add(
            new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        resources["CaptionTextStyle"] = captionStyle;

        var inspectorLabelStyle = CreateTextStyle(
            application,
            fontSize: 13,
            fontWeight: FontWeights.SemiBold,
            foregroundResource: "TextFillColorPrimaryBrush");
        inspectorLabelStyle.Setters.Add(
            new Setter(
                FrameworkElement.MarginProperty,
                new Thickness(0, 0, 0, 6)));
        resources["InspectorLabelStyle"] = inspectorLabelStyle;
    }

    private static Style CreateTextStyle(
        Application application,
        double fontSize,
        FontWeight? fontWeight = null,
        string? foregroundResource = null)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(
            new Setter(TextBlock.FontSizeProperty, fontSize));
        if (fontWeight is { } weight)
        {
            style.Setters.Add(
                new Setter(TextBlock.FontWeightProperty, weight));
        }

        if (foregroundResource is not null)
        {
            style.Setters.Add(
                new Setter(
                    TextBlock.ForegroundProperty,
                    application.FindResource(foregroundResource)));
        }

        return style;
    }

    private static DrawingImage CreateAppIconImage()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(
            new GeometryDrawing(
                Brush("#0F6CBD"),
                pen: null,
                new RectangleGeometry(
                    new Rect(0, 0, 32, 32),
                    radiusX: 7,
                    radiusY: 7)));
        drawing.Children.Add(
            new GeometryDrawing(
                Brush("#F7FAFF"),
                pen: null,
                new RectangleGeometry(
                    new Rect(5, 6, 22, 18),
                    radiusX: 3,
                    radiusY: 3)));
        drawing.Children.Add(
            new GeometryDrawing(
                Brush("#8AB4F8"),
                pen: null,
                Geometry.Parse(
                    "M 5,19 L 12,12 L 17,17 L 21,13 L 27,19 " +
                    "L 27,24 L 5,24 Z")));
        drawing.Children.Add(
            new GeometryDrawing(
                Brush("#DCEAFF"),
                pen: null,
                new EllipseGeometry(
                    new Point(22, 11),
                    radiusX: 2.5,
                    radiusY: 2.5)));
        drawing.Children.Add(
            new GeometryDrawing(
                Brushes.White,
                pen: null,
                new RectangleGeometry(
                    new Rect(10, 26, 12, 2),
                    radiusX: 1,
                    radiusY: 1)));
        drawing.Freeze();
        return new DrawingImage(drawing);
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
