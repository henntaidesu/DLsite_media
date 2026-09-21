using System;
using System.Windows;
using System.Windows.Controls;

namespace R18MediaLibrary.Controls;

/// <summary>
/// 自适应列宽的等高换行网格（非虚拟化版，规则同 <see cref="CardGrid"/> / <see cref="VirtualizingWrapPanel"/>）：
/// 列数与列宽按可用宽度算、各列平分整行，行高 = 列宽 × <see cref="ItemAspect"/> + <see cref="ExtraHeight"/>。
/// 用在已被外层 ScrollViewer 承载、不能再嵌一层自滚动列表的地方（「查看作品」的缩略图墙，对应 Web 的 .fgrid）。
/// </summary>
public class ResponsiveWrapPanel : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(ResponsiveWrapPanel),
        new FrameworkPropertyMetadata(CardGrid.MinWidth, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsiveWrapPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemGapProperty = DependencyProperty.Register(
        nameof(ItemGap), typeof(double), typeof(ResponsiveWrapPanel),
        new FrameworkPropertyMetadata(CardGrid.Gap, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemAspectProperty = DependencyProperty.Register(
        nameof(ItemAspect), typeof(double), typeof(ResponsiveWrapPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ExtraHeightProperty = DependencyProperty.Register(
        nameof(ExtraHeight), typeof(double), typeof(ResponsiveWrapPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>列宽下限。</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>单行最多列数；0 = 不限。</summary>
    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>行列间距。</summary>
    public double ItemGap
    {
        get => (double)GetValue(ItemGapProperty);
        set => SetValue(ItemGapProperty, value);
    }

    /// <summary>按比例部分的高宽比（缩略图 1 = 正方形）。</summary>
    public double ItemAspect
    {
        get => (double)GetValue(ItemAspectProperty);
        set => SetValue(ItemAspectProperty, value);
    }

    /// <summary>比例部分之外再加的固定高度（如文件名行）。</summary>
    public double ExtraHeight
    {
        get => (double)GetValue(ExtraHeightProperty);
        set => SetValue(ExtraHeightProperty, value);
    }

    private int _columns = 1;
    private double _cellW = 1;
    private double _cellH = 1;

    private void UpdateLayout(double availableWidth)
    {
        (_columns, _cellW) = CardGrid.Measure(availableWidth, MinItemWidth, MaxColumns, ItemGap);
        var aspect = ItemAspect > 0 && !double.IsNaN(ItemAspect) ? ItemAspect : 0;
        _cellH = Math.Max(1, _cellW * aspect + Math.Max(0, ExtraHeight));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var gap = Math.Max(0, ItemGap);
        var availW = double.IsInfinity(availableSize.Width) ? MinItemWidth : availableSize.Width;
        UpdateLayout(availW);
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(_cellW, _cellH));
        var rows = (InternalChildren.Count + _columns - 1) / _columns;
        return new Size(availW, rows == 0 ? 0 : rows * _cellH + (rows - 1) * gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var gap = Math.Max(0, ItemGap);
        UpdateLayout(finalSize.Width);
        for (var i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(
                i % _columns * (_cellW + gap), i / _columns * (_cellH + gap), _cellW, _cellH));
        return finalSize;
    }
}
