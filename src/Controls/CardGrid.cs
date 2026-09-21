using System;

namespace R18MediaLibrary.Controls;

/// <summary>
/// 卡片网格的列宽/列数计算：Web 端 <c>src/Web/app.css</c> 那套 CSS Grid 规则的等价实现
/// （UI 以 Web 为准，见 CLAUDE.md「UI 对齐基准」）。
///
/// <code>
/// .grid       { gap: 12px; }
/// .grid.cards { grid-template-columns: repeat(auto-fill, minmax(max(150px, (100% - 84px) / 8), 1fr)); }
/// .grid.cards &gt; .card { aspect-ratio: 2 / 3; }       /* 高 = 宽 × 1.5 */
/// .grid.groups{ grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); }
/// .fgrid      { gap: 10px; repeat(auto-fill, minmax(max(150px, (100% - 90px) / 10), 1fr)); }
/// </code>
///
/// 要点：列宽下限取 <c>max(最小宽, 排满 MaxColumns 列时的单列宽)</c>，宽屏下下限自动抬高从而不再加列；
/// 列数定下来后各列按 <c>1fr</c> 平分整行（右侧不留白），卡片宽度随窗口连续变化。
/// </summary>
public static class CardGrid
{
    /// <summary>作品卡列宽下限（Web <c>.grid.cards</c> 的 150px）。</summary>
    public const double MinWidth = 150;

    /// <summary>作品卡单行最多列数（Web 同为 8）。</summary>
    public const int MaxColumns = 8;

    /// <summary>卡片间距（Web <c>.grid</c> 的 gap: 12px）。</summary>
    public const double Gap = 12;

    /// <summary>作品卡高宽比（Web <c>aspect-ratio: 2 / 3</c>）。</summary>
    public const double Aspect = 1.5;

    /// <summary>封面占卡片高度的比重（Web <c>.cover { flex: 2 }</c> 对 <c>.wt { flex: 1 }</c>）。</summary>
    public const double CoverFlex = 2;

    /// <summary>标题区占卡片高度的比重（Web <c>.wt { flex: 1 }</c>）。</summary>
    public const double TextFlex = 1;

    /// <summary>分组卡（媒体库/社团/标签/形式）列宽下限（Web <c>.grid.groups</c> 的 220px）。</summary>
    public const double GroupMinWidth = 220;

    /// <summary>「查看作品」缩略图网格：列宽下限 / 最多列数 / 间距（Web <c>.fgrid</c>）。</summary>
    public const double FileMinWidth = 150;
    public const int FileMaxColumns = 10;
    public const double FileGap = 10;

    /// <summary>
    /// 按可用宽度算出列数与单列宽度（等价 CSS 的 auto-fill + minmax(…, 1fr)）。
    /// </summary>
    /// <param name="availableWidth">网格可用宽度（已扣除滚动条）。</param>
    /// <param name="minItemWidth">列宽下限。</param>
    /// <param name="maxColumns">单行最多列数；&lt;= 0 表示不限。</param>
    /// <param name="gap">列/行间距。</param>
    public static (int Columns, double CellWidth) Measure(
        double availableWidth, double minItemWidth, int maxColumns, double gap)
    {
        var min = Math.Max(1, double.IsNaN(minItemWidth) ? 1 : minItemWidth);
        if (gap < 0 || double.IsNaN(gap))
            gap = 0;
        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
            return (1, min);
        // CSS 的 max(150px, (100% - 84px) / 8)：宽屏时把下限抬到"恰好排满 MaxColumns 列"的列宽
        if (maxColumns > 0)
            min = Math.Max(min, (availableWidth - gap * (maxColumns - 1)) / maxColumns);
        // + 1e-6：整除边界上浮点误差会平白少算一列
        var columns = Math.Max(1, (int)Math.Floor((availableWidth + gap) / (min + gap) + 1e-6));
        if (maxColumns > 0)
            columns = Math.Min(columns, maxColumns);
        var cellWidth = (availableWidth - gap * (columns - 1)) / columns;
        return (columns, Math.Max(1, cellWidth));
    }
}
