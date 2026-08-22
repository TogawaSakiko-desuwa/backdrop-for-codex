using System.Windows;
using System.Windows.Controls;

namespace BackdropForCodex.App.Views;

/// <summary>
/// Sizes its child to a fixed aspect ratio and centres it in the available space.
/// The ratio applies after <see cref="ContentInset"/>, allowing border and padding
/// to remain outside the measured content box.
/// </summary>
public sealed class AspectRatioDecorator : Decorator
{
    public static readonly DependencyProperty RatioProperty =
        DependencyProperty.Register(
            nameof(Ratio),
            typeof(double),
            typeof(AspectRatioDecorator),
            new FrameworkPropertyMetadata(
                16d / 9d,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ContentInsetProperty =
        DependencyProperty.Register(
            nameof(ContentInset),
            typeof(Thickness),
            typeof(AspectRatioDecorator),
            new FrameworkPropertyMetadata(
                default(Thickness),
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Width divided by height of the child's content box.</summary>
    public double Ratio
    {
        get => (double)GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    /// <summary>Insets such as border and padding that are excluded from the ratio.</summary>
    public Thickness ContentInset
    {
        get => (Thickness)GetValue(ContentInsetProperty);
        set => SetValue(ContentInsetProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return default;
        }

        var box = ResolveChildSize(constraint);
        child.Measure(box);
        return box;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is not { } child)
        {
            return arrangeSize;
        }

        var box = ResolveChildSize(arrangeSize);
        child.Arrange(
            new Rect(
                Math.Max(0, (arrangeSize.Width - box.Width) / 2),
                Math.Max(0, (arrangeSize.Height - box.Height) / 2),
                box.Width,
                box.Height));
        return arrangeSize;
    }

    /// <summary>
    /// Resolves the child's outer size so that its content box honours <see cref="Ratio"/>.
    /// Content dimensions are floored to whole DIPs to keep seams pixel-crisp.
    /// </summary>
    internal Size ResolveChildSize(Size available)
    {
        var inset = ContentInset;
        var horizontal = inset.Left + inset.Right;
        var vertical = inset.Top + inset.Bottom;
        var ratio = Ratio;
        var contentWidth = available.Width - horizontal;
        var contentHeight = available.Height - vertical;
        if (!double.IsFinite(ratio) ||
            ratio <= 0 ||
            !double.IsFinite(contentWidth) ||
            !double.IsFinite(contentHeight) ||
            contentWidth <= 0 ||
            contentHeight <= 0)
        {
            return available;
        }

        var width = Math.Floor(Math.Min(contentWidth, contentHeight * ratio));
        var height = Math.Floor(width / ratio);
        return new Size(width + horizontal, height + vertical);
    }
}
