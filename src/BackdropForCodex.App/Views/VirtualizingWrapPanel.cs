using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BackdropForCodex.App.Views;

/// <summary>
/// A fixed-cell, recycling wrap panel that realizes only viewport rows. Wallpaper cards keep a
/// stable measure at every zoom level while the number of columns follows the available width.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(
            nameof(ItemWidth),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(
                232d,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(
            nameof(ItemHeight),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(
                220d,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _itemsPerRow = 1;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(VerticalOffset - ItemHeight);

    public void LineDown() => SetVerticalOffset(VerticalOffset + ItemHeight);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - (ItemHeight * 3));

    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + (ItemHeight * 3));

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);

    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void SetHorizontalOffset(double offset)
    {
        _ = offset;
    }

    public void SetVerticalOffset(double offset)
    {
        var normalized = Math.Clamp(
            double.IsNaN(offset) ? 0 : offset,
            0,
            Math.Max(0, ExtentHeight - ViewportHeight));
        if (Math.Abs(normalized - _offset.Y) < 0.1)
        {
            return;
        }

        _offset.Y = normalized;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        ArgumentNullException.ThrowIfNull(visual);
        var owner = ItemsControl.GetItemsOwner(this);
        var index = owner?.ItemContainerGenerator.IndexFromContainer(visual) ?? -1;
        if (index >= 0)
        {
            BringIndexIntoView(index);
        }

        return rectangle;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        ValidateCellSize();

        // VirtualizingPanel connects its item generator when InternalChildren is first
        // accessed. A collapsed items host can receive items before its first measure, so
        // establish that connection before the non-empty realization path uses the generator.
        _ = InternalChildren;

        var owner = ItemsControl.GetItemsOwner(this);
        var itemCount = owner?.Items.Count ?? 0;
        var viewportWidth = ResolveViewportWidth(availableSize.Width);
        var viewportHeight = ResolveViewportHeight(availableSize.Height);
        _itemsPerRow = Math.Max(1, (int)Math.Floor(viewportWidth / ItemWidth));
        var rowCount = itemCount == 0
            ? 0
            : (int)Math.Ceiling((double)itemCount / _itemsPerRow);
        UpdateScrollInfo(
            new Size(viewportWidth, rowCount * ItemHeight),
            new Size(viewportWidth, viewportHeight));

        if (itemCount == 0)
        {
            CleanupChildren(0, -1);
            return availableSize;
        }

        var firstRow = Math.Max(0, (int)Math.Floor(VerticalOffset / ItemHeight));
        var lastRow = Math.Min(
            rowCount - 1,
            (int)Math.Ceiling((VerticalOffset + viewportHeight) / ItemHeight));
        var firstIndex = firstRow * _itemsPerRow;
        var lastIndex = Math.Min(itemCount - 1, ((lastRow + 1) * _itemsPerRow) - 1);
        RealizeRange(firstIndex, lastIndex);
        CleanupChildren(firstIndex, lastIndex);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(
                new GeneratorPosition(childIndex, 0));
            if (itemIndex < 0)
            {
                continue;
            }

            var row = itemIndex / _itemsPerRow;
            var column = itemIndex % _itemsPerRow;
            InternalChildren[childIndex].Arrange(
                new Rect(
                    column * ItemWidth,
                    (row * ItemHeight) - VerticalOffset,
                    ItemWidth,
                    ItemHeight));
        }

        return finalSize;
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index < 0)
        {
            return;
        }

        var top = (index / _itemsPerRow) * ItemHeight;
        if (top < VerticalOffset)
        {
            SetVerticalOffset(top);
        }
        else if (top + ItemHeight > VerticalOffset + ViewportHeight)
        {
            SetVerticalOffset(top + ItemHeight - ViewportHeight);
        }
    }

    private void RealizeRange(int firstIndex, int lastIndex)
    {
        var startPosition = ItemContainerGenerator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPosition.Offset == 0
            ? startPosition.Index
            : startPosition.Index + 1;
        using (ItemContainerGenerator.StartAt(
                   startPosition,
                   GeneratorDirection.Forward,
                   allowStartAtRealizedItem: true))
        {
            for (var itemIndex = firstIndex;
                 itemIndex <= lastIndex;
                 itemIndex++, childIndex++)
            {
                var child = ItemContainerGenerator.GenerateNext(out var newlyRealized)
                    as UIElement;
                if (child is null)
                {
                    continue;
                }

                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    ItemContainerGenerator.PrepareItemContainer(child);
                }

                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
    }

    private void CleanupChildren(int firstIndex, int lastIndex)
    {
        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0);
            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(position);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
            {
                continue;
            }

            ItemContainerGenerator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        var changed = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;
        _offset.Y = Math.Clamp(
            _offset.Y,
            0,
            Math.Max(0, ExtentHeight - ViewportHeight));
        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private static double ResolveViewportWidth(double width) =>
        double.IsInfinity(width) || double.IsNaN(width) || width <= 0 ? 232 : width;

    private static double ResolveViewportHeight(double height) =>
        double.IsInfinity(height) || double.IsNaN(height) || height <= 0 ? 220 : height;

    private void ValidateCellSize()
    {
        if (double.IsNaN(ItemWidth) || double.IsInfinity(ItemWidth) || ItemWidth <= 0)
        {
            throw new InvalidOperationException("ItemWidth must be a finite positive value.");
        }

        if (double.IsNaN(ItemHeight) || double.IsInfinity(ItemHeight) || ItemHeight <= 0)
        {
            throw new InvalidOperationException("ItemHeight must be a finite positive value.");
        }
    }
}
