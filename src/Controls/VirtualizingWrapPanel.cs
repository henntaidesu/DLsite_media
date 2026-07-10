using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DLsiteMedia.Controls;

/// <summary>
/// 均匀尺寸的虚拟化 WrapPanel：只实例化视口内（含上下各一行缓冲）的容器，滚动出视口的容器被虚拟化回收，
/// 大列表滚动时可见元素数量恒定、内存不随滚动线性增长。要求每项尺寸一致（ItemWidth/ItemHeight，含项自身外边距）。
/// 采用 WPF 经典标准虚拟化（非回收）实现：稳定可靠；配合 ThumbnailCache 后重建容器的图片解码走缓存，代价很低。
/// 需置于启用 CanContentScroll 的 ScrollViewer / ListBox 内（本类实现 IScrollInfo 自行处理滚动与偏移）。
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>单项占位宽（含右侧外边距）。</summary>
    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <summary>单项占位高（含下侧外边距）。</summary>
    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    private int Columns(double availableWidth)
    {
        var w = ItemWidth;
        if (w <= 0 || double.IsNaN(w))
            return 1;
        return Math.Max(1, (int)Math.Floor(availableWidth / w));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 访问 InternalChildren 会强制生成器初始化（虚拟化必需的一步）
        var _ = InternalChildren;
        var generator = ItemContainerGenerator;
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsOwner?.Items.Count ?? 0;

        var itemW = ItemWidth;
        var itemH = ItemHeight;

        var availW = double.IsInfinity(availableSize.Width) ? (ScrollOwner?.ActualWidth ?? 0) : availableSize.Width;
        if (availW <= 0)
            availW = itemW;
        var columns = Columns(availW);
        var rows = itemCount == 0 ? 0 : (itemCount + columns - 1) / columns;

        var extent = new Size(columns * itemW, rows * itemH);
        UpdateScrollInfo(availableSize, extent);

        if (itemCount == 0)
        {
            CleanupRange(0, -1, generator);
            return new Size(
                double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        }

        // 视口覆盖的行范围（上下各扩一行作缓冲，滚动更顺滑）
        var viewTop = _offset.Y;
        var viewHeight = _viewport.Height > 0 ? _viewport.Height : extent.Height;
        var firstRow = Math.Max(0, (int)Math.Floor(viewTop / itemH) - 1);
        var lastRow = (int)Math.Ceiling((viewTop + viewHeight) / itemH) + 1;
        var firstIndex = firstRow * columns;
        var lastIndex = Math.Min(itemCount - 1, (lastRow + 1) * columns - 1);
        if (firstIndex > lastIndex)
            firstIndex = lastIndex;   // 偏移越界时至少实例化最后一项

        RealizeRange(firstIndex, lastIndex, generator, new Size(itemW, itemH));
        CleanupRange(firstIndex, lastIndex, generator);

        return new Size(
            double.IsInfinity(availableSize.Width) ? extent.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
    }

    private void RealizeRange(int firstIndex, int lastIndex, IItemContainerGenerator generator, Size itemSize)
    {
        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (var i = firstIndex; i <= lastIndex; i++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var newlyRealized);
                if (child == null)
                    break;
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(itemSize);
            }
        }
    }

    /// <summary>移除落在 [minIndex, maxIndex] 之外的已实例化容器（maxIndex &lt; minIndex 表示全部移除）。</summary>
    private void CleanupRange(int minIndex, int maxIndex, IItemContainerGenerator generator)
    {
        var children = InternalChildren;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(pos);
            if (itemIndex < minIndex || itemIndex > maxIndex)
            {
                generator.Remove(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var itemW = ItemWidth;
        var itemH = ItemHeight;
        var columns = Columns(finalSize.Width);
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0)
                continue;
            var row = itemIndex / columns;
            var col = itemIndex % columns;
            var x = col * itemW;
            var y = row * itemH - _offset.Y;   // 自行处理垂直偏移
            children[i].Arrange(new Rect(x, y, itemW, itemH));
        }
        return finalSize;
    }

    protected override void OnClearChildren()
    {
        base.OnClearChildren();
        _offset = new Point(0, 0);
        ScrollOwner?.InvalidateScrollInfo();
    }

    private void UpdateScrollInfo(Size availableSize, Size extent)
    {
        var viewport = availableSize;
        if (double.IsInfinity(viewport.Width))
            viewport.Width = extent.Width;
        if (double.IsInfinity(viewport.Height))
            viewport.Height = extent.Height;

        var changed = false;
        if (extent != _extent)
        {
            _extent = extent;
            changed = true;
        }
        if (viewport != _viewport)
        {
            _viewport = viewport;
            changed = true;
        }
        var maxOffset = Math.Max(0, extent.Height - viewport.Height);
        if (_offset.Y > maxOffset)
        {
            _offset.Y = maxOffset;
            changed = true;
        }
        if (_offset.Y < 0)
        {
            _offset.Y = 0;
            changed = true;
        }
        if (changed)
            ScrollOwner?.InvalidateScrollInfo();
    }

    // ---------- IScrollInfo ----------

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    private const double ScrollLine = 48;

    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset) || double.IsInfinity(offset))
            return;
        var maxOffset = Math.Max(0, _extent.Height - _viewport.Height);
        offset = Math.Max(0, Math.Min(offset, maxOffset));
        if (Math.Abs(offset - _offset.Y) < 0.01)
            return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset)
    {
        // 仅垂直换行滚动，忽略水平偏移
    }

    public void LineUp() => SetVerticalOffset(_offset.Y - ScrollLine);
    public void LineDown() => SetVerticalOffset(_offset.Y + ScrollLine);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - ScrollLine * 2);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + ScrollLine * 2);

    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        // 把目标元素滚入视口（用于键盘/选中导航）
        var child = visual as UIElement;
        if (child == null)
            return rectangle;
        var index = -1;
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
            if (ReferenceEquals(children[i], child))
            {
                index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
                break;
            }
        if (index < 0)
            return rectangle;
        var columns = Columns(_viewport.Width > 0 ? _viewport.Width : _extent.Width);
        var row = index / columns;
        var top = row * ItemHeight;
        var bottom = top + ItemHeight;
        if (top < _offset.Y)
            SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height)
            SetVerticalOffset(bottom - _viewport.Height);
        return rectangle;
    }
}
