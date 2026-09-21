using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace R18MediaLibrary.Controls;

/// <summary>
/// 自适应列宽的虚拟化 WrapPanel：列数与列宽按可用宽度实时计算（规则见 <see cref="CardGrid"/>，
/// 与 Web 端 CSS Grid 的 auto-fill + minmax(…, 1fr) 一致），各列平分整行、卡片随窗口缩放；
/// 只实例化视口内（含上下各一行缓冲）的容器，滚动出视口的容器被虚拟化回收，
/// 大列表滚动时可见元素数量恒定、内存不随滚动线性增长。
/// 行高由 <see cref="ItemAspect"/>（按列宽的高宽比，作品卡 1.5）或 <see cref="ItemHeight"/>（固定高，分组卡）给出；
/// 间距由面板自己排（<see cref="ItemGap"/>），卡片不要再带外边距、并且要拉伸填满单元格。
/// 采用 WPF 经典标准虚拟化（非回收）实现：稳定可靠；配合 ThumbnailCache 后重建容器的图片解码走缓存，代价很低。
/// 需置于启用 CanContentScroll 的 ScrollViewer / ListBox 内（本类实现 IScrollInfo 自行处理滚动与偏移）。
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(CardGrid.MinWidth, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemGapProperty = DependencyProperty.Register(
        nameof(ItemGap), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(CardGrid.Gap, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemAspectProperty = DependencyProperty.Register(
        nameof(ItemAspect), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>列宽下限（对应 CSS minmax 的最小值）。</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>单行最多列数；0 = 不限（对应 CSS 里 (100% - gap×(n-1)) / n 那半边）。</summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>卡片间距（行列同值，对应 CSS 的 gap）。</summary>
    public double ItemGap
    {
        get => (double)GetValue(ItemGapProperty);
        set => SetValue(ItemGapProperty, value);
    }

    /// <summary>行高 = 列宽 × 本值（对应 CSS aspect-ratio）；&lt;= 0 时改用 <see cref="ItemHeight"/>。</summary>
    public double ItemAspect
    {
        get => (double)GetValue(ItemAspectProperty);
        set => SetValue(ItemAspectProperty, value);
    }

    /// <summary>固定行高（<see cref="ItemAspect"/> &lt;= 0 时生效）。</summary>
    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    private int _columns = 1;      // 最近一次测量的列数
    private double _cellW = 1;     // 最近一次测量的单列宽
    private double _cellH = 1;     // 最近一次测量的行高

    /// <summary>按可用宽度算出列数与单元格尺寸（规则同 Web，见 <see cref="CardGrid"/>）。</summary>
    private (int Columns, double CellWidth, double CellHeight) Layout(double availableWidth)
    {
        var (columns, cellW) = CardGrid.Measure(availableWidth, MinItemWidth, MaxColumns, ItemGap);
        var aspect = ItemAspect;
        var cellH = aspect > 0 && !double.IsNaN(aspect) ? cellW * aspect : ItemHeight;
        if (double.IsNaN(cellH) || cellH <= 0)
            cellH = 1;
        return (columns, cellW, cellH);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 访问 InternalChildren 会强制生成器初始化（虚拟化必需的一步）
        var _ = InternalChildren;
        var generator = ItemContainerGenerator;
        var itemsOwner = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsOwner?.Items.Count ?? 0;

        var availW = double.IsInfinity(availableSize.Width) ? (ScrollOwner?.ActualWidth ?? 0) : availableSize.Width;
        if (availW <= 0)
            availW = MinItemWidth;
        var gap = Math.Max(0, ItemGap);
        (_columns, _cellW, _cellH) = Layout(availW);
        var columns = _columns;
        var itemW = _cellW;
        var itemH = _cellH;
        var pitchY = itemH + gap;     // 行距（行高 + 间距）
        var rows = itemCount == 0 ? 0 : (itemCount + columns - 1) / columns;

        // 网格整行铺满可用宽度（列已按 1fr 平分），高度为 行数×行高 + 行间距
        var extent = new Size(availW, rows == 0 ? 0 : rows * itemH + (rows - 1) * gap);
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
        var firstRow = Math.Max(0, (int)Math.Floor(viewTop / pitchY) - 1);
        var lastRow = (int)Math.Ceiling((viewTop + viewHeight) / pitchY) + 1;
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
        var gap = Math.Max(0, ItemGap);
        var (columns, itemW, itemH) = Layout(finalSize.Width);
        (_columns, _cellW, _cellH) = (columns, itemW, itemH);
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0)
                continue;
            var row = itemIndex / columns;
            var col = itemIndex % columns;
            var x = col * (itemW + gap);
            var y = row * (itemH + gap) - _offset.Y;   // 自行处理垂直偏移
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
        var columns = Math.Max(1, _columns);
        var row = index / columns;
        var top = row * (_cellH + Math.Max(0, ItemGap));
        var bottom = top + _cellH;
        if (top < _offset.Y)
            SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height)
            SetVerticalOffset(bottom - _viewport.Height);
        return rectangle;
    }
}
