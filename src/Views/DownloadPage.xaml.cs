using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DLsiteMedia.Core;
using DLsiteMedia.Services;

namespace DLsiteMedia.Views;

/// <summary>下载页子行：单个分卷文件。</summary>
public class DownloadFileItem : ObservableBase
{
    public string Uuid { get; init; } = "";

    /// <summary>原始网盘下载链接（点击"复制链接"复制此值）。</summary>
    public string Url { get; set; } = "";

    private string _fileName = "";
    public string FileName { get => _fileName; set => Set(ref _fileName, value); }

    /// <summary>作品内相对路径（含子目录），用于构建目录树；论坛源即文件名。</summary>
    public string RelPath { get; set; } = "";

    private double _pct;
    public double Pct { get => _pct; set => Set(ref _pct, value); }

    private string _speedText = "";
    public string SpeedText { get => _speedText; set => Set(ref _speedText, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private Brush _statusBrush = Brushes.Gray;
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    // 解析失败原因（仅解析失败的分卷显示）
    private string _errorReason = "";
    public string ErrorReason { get => _errorReason; set => Set(ref _errorReason, value); }

    private Visibility _errorVisibility = Visibility.Collapsed;
    public Visibility ErrorVisibility { get => _errorVisibility; set => Set(ref _errorVisibility, value); }

    public string CopyUrlText => I18n.Tr("复制链接");
}

/// <summary>下载页目录树的文件夹节点（子项可为文件夹或文件，递归展示作品内目录层级）。</summary>
public class DownloadFolderItem : ObservableBase
{
    public string Name { get; init; } = "";

    /// <summary>子节点：DownloadFolderItem（子目录）或 DownloadFileItem（文件）混合。</summary>
    public ObservableCollection<object> Children { get; } = [];

    private bool _isExpanded = true;   // 默认展开，便于直接看到文件
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                Raise(nameof(ChildrenVisibility));
        }
    }

    public Visibility ChildrenVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>下载页父行：番号分组。</summary>
public class DownloadGroupItem : ObservableBase
{
    public string WorkId { get; init; } = "";

    /// <summary>全部分卷文件（扁平，按 UUID 就地更新进度/状态）。</summary>
    public ObservableCollection<DownloadFileItem> Children { get; } = [];

    /// <summary>展示用目录树根节点（文件夹/文件混合），仅在文件集合结构变化时重建。</summary>
    public ObservableCollection<object> Nodes { get; } = [];

    /// <summary>上次构建目录树时的结构签名（相对路径序列），用于判断是否需要重建。</summary>
    public string? TreeSignature { get; set; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                Raise(nameof(ChildrenVisibility));
        }
    }

    public Visibility ChildrenVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    private double _pct;
    public double Pct
    {
        get => _pct;
        set
        {
            if (Set(ref _pct, value))
                Raise(nameof(PctText));
        }
    }

    public string PctText => $"{(int)Pct}%";

    private string _speedText = "";
    public string SpeedText { get => _speedText; set => Set(ref _speedText, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private Brush _statusBrush = Brushes.Gray;
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    private Visibility _actionsVisibility = Visibility.Collapsed;
    public Visibility ActionsVisibility { get => _actionsVisibility; set => Set(ref _actionsVisibility, value); }

    // 单独"停止"（有待下载/下载中分卷时显示）/"下载"（有已暂停分卷时显示）/"删除"（始终显示）
    private Visibility _pauseVisibility = Visibility.Collapsed;
    public Visibility PauseVisibility { get => _pauseVisibility; set => Set(ref _pauseVisibility, value); }

    private Visibility _resumeVisibility = Visibility.Collapsed;
    public Visibility ResumeVisibility { get => _resumeVisibility; set => Set(ref _resumeVisibility, value); }

    public string ReparseText => I18n.Tr("重新解析");
    public string ResearchText => I18n.Tr("重新搜索");
    public string DownloadText => I18n.Tr("下载");
    public string StopText => I18n.Tr("停止");
    public string DeleteText => I18n.Tr("删除");
}

/// <summary>下载页（对应 Python 版 download_UI.py）。</summary>
public partial class DownloadPage : UserControl
{
    /// <summary>解析失败的番号点击"重新搜索"时触发，由主窗口切回搜索页。</summary>
    public event Action<string>? ResearchRequested;

    /// <summary>点击"已下载"按钮时触发，由主窗口切到已下载视图。</summary>
    public event Action? ShowDownloadedRequested;

    /// <summary>点击"搜索作品"按钮时触发，由主窗口切到搜索视图。</summary>
    public event Action? ShowSearchRequested;

    // download_list.status -> 显示文本（中文原文，渲染时经 Tr 翻译）与颜色
    private static readonly Dictionary<string, (string Text, string Color)> StatusMap = new()
    {
        ["0"] = ("等待下载", "#facc15"),
        ["3"] = ("下载中", "#60a5fa"),
        ["1"] = ("已完成", "#4ade80"),
        ["2"] = ("解析失败", "#f87171"),
        ["4"] = ("已暂停", "#9aa4b2"),
        ["5"] = ("搜索可用下载连接", "#facc15"),
        ["6"] = ("无可用下载连接", "#f87171"),
    };

    private readonly ObservableCollection<DownloadGroupItem> _groups = [];
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _usageLoading;

    public DownloadPage()
    {
        InitializeComponent();
        GroupList.ItemsSource = _groups;
        _refreshTimer.Tick += (_, _) => Refresh();
        _usageTimer.Tick += (_, _) => _ = FetchUsageAsync();
        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    private void RetranslateUi()
    {
        SearchWorkButton.Content = I18n.Tr("搜索作品");
        RefreshButton.Content = I18n.Tr("刷新");
        ClearDoneButton.Content = I18n.Tr("清除已完成");
        ClearAllButton.Content = I18n.Tr("清空列表");
        ShowDownloadedButton.Content = I18n.Tr("已下载");
        UpdateStartButton();
        Refresh();
    }

    private void ShowDownloadedButton_Click(object sender, RoutedEventArgs e) =>
        ShowDownloadedRequested?.Invoke();

    private void SearchWorkButton_Click(object sender, RoutedEventArgs e) =>
        ShowSearchRequested?.Invoke();

    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            Refresh();
            _refreshTimer.Start();
            _ = FetchUsageAsync();
            _usageTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
            _usageTimer.Stop();
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        // 开始/暂停切换
        if (DownloadEngine.IsRunning)
            DownloadEngine.Stop();
        else
            DownloadEngine.Start();
        UpdateStartButton();
    }

    private void UpdateStartButton()
    {
        // 运行中=暂停按钮(红)；已停止=开始按钮(绿)
        if (DownloadEngine.IsRunning)
        {
            StartButton.Style = (Style)FindResource("DangerButton");
            if (DownloadEngine.StopRequested)
            {
                // 已请求暂停，等待当前文件停到断点
                StartButton.Content = I18n.Tr("暂停中…");
                StartButton.IsEnabled = false;
            }
            else
            {
                StartButton.Content = I18n.Tr("暂停下载");
                StartButton.IsEnabled = true;
            }
        }
        else
        {
            StartButton.Style = (Style)FindResource("SuccessButton");
            StartButton.Content = I18n.Tr("开始下载");
            StartButton.IsEnabled = true;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

    // ---------- debrid-link 用量 ----------

    private async Task FetchUsageAsync()
    {
        if (_usageLoading)
            return;
        _usageLoading = true;
        try
        {
            JsonElement? value;
            using (var client = new DebridLinkClient())
                value = await client.DownloadLimitsAsync();
            UpdateUsage(value);
        }
        catch (Exception)
        {
            UpdateUsage(null);
        }
        finally
        {
            _usageLoading = false;
        }
    }

    private void UpdateUsage(JsonElement? value)
    {
        double? current = null;
        double resetSeconds = 0;
        if (value is { } v && v.ValueKind == JsonValueKind.Object)
        {
            if (v.TryGetProperty("usagePercent", out var usage) &&
                usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("current", out var cur) &&
                cur.ValueKind == JsonValueKind.Number)
                current = cur.GetDouble();
            if (v.TryGetProperty("nextResetSeconds", out var reset) &&
                reset.ValueKind == JsonValueKind.Object &&
                reset.TryGetProperty("value", out var rv) &&
                rv.ValueKind == JsonValueKind.Number)
                resetSeconds = rv.GetDouble();
        }
        if (current is null)
        {
            UsageLabel.Text = I18n.Tr("debrid-link 使用量 --");
            UsageBar.Value = 0;
            return;
        }
        UsageBar.Value = Math.Min(100, Math.Round(current.Value));
        var text = I18n.Tr("debrid-link 使用量");
        var reset2 = FormatReset(resetSeconds);
        if (reset2.Length > 0)
            text += I18n.Format(I18n.Tr(" · {reset} 后重置"), ("reset", reset2));
        UsageLabel.Text = text;
    }

    /// <summary>把距离流量重置的秒数格式化为 Xh Ym（不足 1 分钟显示 &lt;1m）。</summary>
    private static string FormatReset(double seconds)
    {
        var s = (long)seconds;
        if (s <= 0)
            return "";
        var hours = s / 3600;
        var minutes = s % 3600 / 60;
        if (hours > 0)
            return $"{hours}h{minutes}m";
        return minutes > 0 ? $"{minutes}m" : "<1m";
    }

    // ---------- 列表刷新 ----------

    private static string FormatSpeed(double speed)
    {
        if (speed >= 1024 * 1024)
            return $"{speed / 1024 / 1024:F1} MB/s";
        if (speed >= 1024)
            return $"{speed / 1024:F0} KB/s";
        return $"{speed:F0} B/s";
    }

    /// <summary>从下载链接提取文件名（前台不直接显示链接）。</summary>
    private static string FileNameOf(string url)
    {
        var raw = url.TrimEnd('/').Split('/')[^1].Split('?')[0];
        // 直链文件名常为 URL 百分号编码（asmr.one 的日文名尤其如此），解码为明文显示；
        // 普通未编码文件名解码后保持不变
        try { return Uri.UnescapeDataString(raw); }
        catch (Exception) { return raw; }
    }

    /// <summary>返回 (进度百分比, 实时速度 B/s 或 null)。</summary>
    private static (int Pct, double? Speed) FileProgress(string uuid, string status, string dbLong)
    {
        if (status == "1")
            return (100, null);
        if (status == "3" && DownloadEngine.DownloadProgress.TryGetValue(uuid, out var info) &&
            info.Total > 0)
            return ((int)(info.Downloaded * 100 / info.Total), info.Speed);
        // 等待/失败/无实时数据时，退回数据库中记录的进度（断点续传的已完成部分）
        var pct = int.TryParse(dbLong, out var p) ? p : 0;
        return (Math.Min(pct, 100), null);
    }

    private (string Text, string Color) AggregateStatus(string workId, List<string> statuses)
    {
        var done = statuses.Count(s => s == "1");
        if (statuses.Contains("3"))
            return (I18n.Format(I18n.Tr("下载中 {done}/{total}"),
                ("done", done), ("total", statuses.Count)), "#60a5fa");
        if (statuses.Contains("5"))
            return (I18n.Tr("搜索可用下载连接"), "#facc15");
        if (statuses.Contains("0"))
            return (I18n.Format(I18n.Tr("等待下载 {done}/{total}"),
                ("done", done), ("total", statuses.Count)), "#facc15");
        if (statuses.Contains("4"))
            return (I18n.Format(I18n.Tr("已暂停 {done}/{total}"),
                ("done", done), ("total", statuses.Count)), "#9aa4b2");
        if (statuses.Contains("2"))
            return (I18n.Format(I18n.Tr("{n} 个解析失败"),
                ("n", statuses.Count(s => s == "2"))), "#f87171");
        if (statuses.Contains("6"))
            return (I18n.Tr("无可用下载连接"), "#f87171");
        // 全部分卷已下载完成：解压前/解压中/移动中显示对应状态
        if (DownloadEngine.UnzipProgress.TryGetValue(workId, out var unzip))
        {
            if (unzip.State == "pending")
                return (I18n.Tr("待解压"), "#facc15");
            if (unzip.State == "movewait")
                return (I18n.Tr("等待移动"), "#facc15");
            if (unzip.State == "moving")
                return (I18n.Format(I18n.Tr("移动中 {pct}%"), ("pct", unzip.Pct)), "#a78bfa");
            return (I18n.Format(I18n.Tr("解压中 {pct}%"), ("pct", unzip.Pct)), "#60a5fa");
        }
        return (I18n.Tr("已完成"), "#4ade80");
    }

    // 状态颜色只有固定几种，冻结并按 hex 缓存复用，避免每秒每条目重复分配画刷；
    // 同一 hex 返回同一实例，使未变化的 StatusBrush 赋值命中相等短路、不触发多余通知。
    private static readonly Dictionary<string, Brush> BrushCache = new();
    private static Brush BrushOf(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var cached))
            return cached;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        BrushCache[hex] = brush;
        return brush;
    }

    private bool _refreshing;   // 上一次刷新（含后台查询）未完成时跳过本次，防止 tick 重入堆积

    private async void Refresh()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            UpdateStartButton();
            // 全表读放到后台线程，避免每秒在 UI 线程阻塞（连接池 + WAL 下并发安全）
            var rows = await Task.Run(() => Db.Select(
                "SELECT \"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"error\", \"sub_path\" FROM \"download_list\" ORDER BY rowid"));
            if (rows != null)
                ApplyRows(rows);   // 集合改动回到 UI 线程执行（await 后续接续在 UI 上下文）
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyRows(List<object?[]> rows)
    {
        // 按番号分组，同一番号合并为一个父条目
        var groups = new Dictionary<string, List<(string Uuid, string Url, string Status, string Long, string? Error, string? SubPath)>>();
        var order = new List<string>();
        foreach (var row in rows)
        {
            var workId = row[1] as string ?? "";
            if (!groups.TryGetValue(workId, out var list))
            {
                groups[workId] = list = [];
                order.Add(workId);
            }
            list.Add((row[0] as string ?? "", row[2] as string ?? "",
                row[3] as string ?? "", row[4]?.ToString() ?? "", row[5] as string, row[6] as string));
        }

        // 同步到现有集合（保留展开状态与滚动位置）
        var anyActive = false;   // 是否存在下载中/等待/解压中的任务，用于空闲降频
        var existing = _groups.ToDictionary(g => g.WorkId);
        for (var i = _groups.Count - 1; i >= 0; i--)
            if (!groups.ContainsKey(_groups[i].WorkId))
                _groups.RemoveAt(i);

        foreach (var workId in order)
        {
            var items = groups[workId];
            if (!existing.TryGetValue(workId, out var group))
            {
                group = new DownloadGroupItem { WorkId = workId };
                _groups.Add(group);
            }

            var statuses = items.Select(it => it.Status).ToList();
            var (aggText, aggColor) = AggregateStatus(workId, statuses);
            group.StatusText = aggText;
            group.StatusBrush = BrushOf(aggColor);
            // 该番号有分卷解析失败时显示"重新解析/重新搜索"按钮
            group.ActionsVisibility = statuses.Contains("2") && workId.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            // 单独控制按钮：有下载中/等待分卷时可"停止"；有已暂停分卷时可"下载"（继续）
            var hasActive = statuses.Any(s => s is "0" or "3");
            var hasPaused = statuses.Contains("4");
            anyActive |= hasActive || DownloadEngine.UnzipProgress.ContainsKey(workId);
            group.PauseVisibility = hasActive && workId.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            group.ResumeVisibility = hasPaused && workId.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            var totalPct = 0;
            var totalSpeed = 0.0;
            var children = group.Children;
            var existingChildren = children.ToDictionary(c => c.Uuid);
            for (var i = children.Count - 1; i >= 0; i--)
                if (!items.Any(it => it.Uuid == children[i].Uuid))
                    children.RemoveAt(i);
            foreach (var it in items)
            {
                var (pct, speed) = FileProgress(it.Uuid, it.Status, it.Long);
                totalPct += pct;
                if (speed is { } s)
                    totalSpeed += s;

                var (rawText, color) = StatusMap.TryGetValue(it.Status, out var mapped)
                    ? mapped
                    : ((string?)null, "#cdd3de");
                var text = rawText != null
                    ? I18n.Tr(rawText)
                    : I18n.Format(I18n.Tr("未知({status})"), ("status", it.Status));

                if (!existingChildren.TryGetValue(it.Uuid, out var child))
                {
                    child = new DownloadFileItem { Uuid = it.Uuid };
                    children.Add(child);
                }
                child.Url = it.Url;
                // asmr 直链有作品内目录层级（sub_path 含子目录），用于构建目录树；
                // 论坛源无 sub_path，回退到 URL 解码后的文件名（位于根层级）
                var rel = string.IsNullOrEmpty(it.SubPath) ? FileNameOf(it.Url) : it.SubPath!;
                child.RelPath = rel;
                // 树中文件夹单独成节点，文件叶子只显示最后一段文件名
                var slash = rel.LastIndexOf('/');
                child.FileName = slash >= 0 ? rel[(slash + 1)..] : rel;
                child.Pct = pct;
                child.SpeedText = speed is { } sp ? FormatSpeed(sp) : "";
                child.StatusText = text;
                child.StatusBrush = BrushOf(color);
                // 解析失败的分卷显示失败原因
                if (it.Status == "2")
                {
                    child.ErrorReason = MapParseError(it.Error);
                    child.ErrorVisibility = Visibility.Visible;
                }
                else
                {
                    child.ErrorVisibility = Visibility.Collapsed;
                }
            }

            group.SpeedText = totalSpeed > 0 ? FormatSpeed(totalSpeed) : "";
            // 解压中时父进度条改为显示解压进度，否则显示分卷下载进度均值
            group.Pct = DownloadEngine.UnzipProgress.TryGetValue(workId, out var unzip)
                ? unzip.Pct
                : (double)totalPct / Math.Max(1, items.Count);

            // 目录树仅在文件集合结构变化时重建（保留文件夹展开状态、避免每秒刷新闪烁）；
            // 结构不变时叶子对象就地更新进度，树中引用同一对象自动刷新
            var signature = string.Join("|", group.Children.Select(c => c.RelPath));
            if (group.TreeSignature != signature)
            {
                group.TreeSignature = signature;
                RebuildTree(group);
            }
        }

        // 有活动任务时每秒刷新（进度平滑）；全部完成/失败/空闲时降到 3 秒，减少空转开销
        var wanted = TimeSpan.FromSeconds(anyActive ? 1 : 3);
        if (_refreshTimer.Interval != wanted)
            _refreshTimer.Interval = wanted;
    }

    /// <summary>按各分卷的相对路径（含子目录）把扁平文件列表重建为文件夹/文件目录树。</summary>
    private static void RebuildTree(DownloadGroupItem group)
    {
        group.Nodes.Clear();
        // 按完整子目录路径缓存文件夹节点；空路径表示根（直接放入 group.Nodes）
        var folderByPath = new Dictionary<string, DownloadFolderItem>(StringComparer.Ordinal);
        foreach (var child in group.Children)
        {
            var parts = child.RelPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            ObservableCollection<object> parent = group.Nodes;
            var accum = "";
            for (var i = 0; i < parts.Length - 1; i++)   // 末段为文件名，前面均为目录
            {
                accum = accum.Length == 0 ? parts[i] : accum + "/" + parts[i];
                if (!folderByPath.TryGetValue(accum, out var folder))
                {
                    folder = new DownloadFolderItem { Name = parts[i] };
                    folderByPath[accum] = folder;
                    parent.Add(folder);
                }
                parent = folder.Children;
            }
            parent.Add(child);
        }
    }

    // ---------- 操作 ----------

    /// <summary>重新解析：把该作品解析失败的分卷重新排队（已完成的保留不动）。</summary>
    private void Reparse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadGroupItem group)
            return;
        Db.Execute(
            "UPDATE \"download_list\" SET \"status\" = '0', \"error\" = NULL WHERE \"work_id\" = @w AND \"status\" = '2'",
            ("@w", group.WorkId));
        DownloadEngine.Start();
        Refresh();
    }

    /// <summary>复制该分卷的原始网盘下载链接到剪贴板。</summary>
    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadFileItem file || file.Url.Length == 0)
            return;
        try
        {
            Clipboard.SetText(file.Url);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "复制下载链接");
        }
    }

    /// <summary>把 debrid-link 解析失败错误码翻译为可读说明。</summary>
    private static string MapParseError(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return I18n.Tr("解析失败（原因未知，可能是链接失效或网盘不支持）");
        return raw switch
        {
            "badToken" => I18n.Tr("debrid-link API Key 无效或已过期"),
            "maxData" or "maxDataHost" => I18n.Tr("debrid-link 流量额度已用尽"),
            "maxLink" or "maxLinkHost" => I18n.Tr("链接数超过 debrid-link 限制"),
            "hostUnsupported" or "notDebrid" or "hostNotValid" or "noServer"
                => I18n.Tr("该网盘不被 debrid-link 支持"),
            "notFreeHost" or "hostNotFree" or "disabledHost" or "disabledServerHost"
                => I18n.Tr("该网盘需要会员或已停用"),
            "fileNotFound" or "fileUnavailable" or "notFound" or "fileError"
                => I18n.Tr("文件已失效或被删除"),
            "floodDetected" => I18n.Tr("请求过于频繁，请稍后再试"),
            "badFileType" => I18n.Tr("不支持的文件类型"),
            _ => I18n.Format(I18n.Tr("解析失败（{code}）"), ("code", raw)),
        };
    }

    /// <summary>重新搜索：先确认并删除已下载的分卷与文件夹，再切回搜索页重新搜索。</summary>
    private void Research_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadGroupItem group)
            return;
        if (!InAppDialog.Confirm(this,
                I18n.Format(I18n.Tr("将删除 {id} 已下载的分卷与文件夹，并重新搜索。是否继续？"),
                    ("id", group.WorkId)),
                I18n.Tr("重新搜索")))
            return;
        DownloadEngine.PurgeWorkDownload(group.WorkId);
        Refresh();
        ResearchRequested?.Invoke(group.WorkId);
    }

    /// <summary>单独下载（继续）该作品：解除暂停并启动下载。</summary>
    private void DownloadOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadGroupItem group)
            return;
        DownloadEngine.ResumeWork(group.WorkId);
        UpdateStartButton();
        Refresh();
    }

    /// <summary>单独停止该作品的下载（停到断点）。</summary>
    private void StopOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadGroupItem group)
            return;
        DownloadEngine.PauseWork(group.WorkId);
        Refresh();
    }

    /// <summary>单独删除该作品：从下载列表移除，并删除 works 表记录。</summary>
    private void DeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadGroupItem group)
            return;
        if (!InAppDialog.Confirm(this,
                I18n.Format(I18n.Tr("将从下载列表删除 {id}，并删除其作品记录与下载缓存。是否继续？"),
                    ("id", group.WorkId)),
                I18n.Tr("删除")))
            return;
        DownloadEngine.DeleteWork(group.WorkId);
        Refresh();
    }

    private void ClearDoneButton_Click(object sender, RoutedEventArgs e)
    {
        // 正在解压（待解压/解压中）的番号其分卷虽已是 '1'，但还未真正完成，保留不清除
        var unzipping = DownloadEngine.UnzipProgress.Keys.ToList();
        if (unzipping.Count > 0)
        {
            var names = new List<string>();
            var args = new List<(string, object?)>();
            for (var i = 0; i < unzipping.Count; i++)
            {
                names.Add($"@u{i}");
                args.Add(($"@u{i}", unzipping[i]));
            }
            Db.Execute(
                $"DELETE FROM \"download_list\" WHERE \"status\" = '1' AND \"work_id\" NOT IN ({string.Join(",", names)})",
                args.ToArray());
        }
        else
        {
            Db.Execute("DELETE FROM \"download_list\" WHERE \"status\" = '1'");
        }
        Refresh();
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_groups.Count == 0)
            return;
        if (!InAppDialog.Confirm(this,
                I18n.Tr("确定要清空整个下载列表吗？等待中的任务也会被删除。"),
                I18n.Tr("清空下载列表")))
            return;
        // 没有下载完成就被删除的番号，从已下载（works 表）中移除
        var rows = Db.Select(
            "SELECT DISTINCT \"work_id\" FROM \"download_list\" WHERE \"status\" != '1'");
        if (rows != null)
            foreach (var row in rows)
                Db.Execute(
                    "DELETE FROM \"works\" WHERE \"work_id\" = @w AND \"state\" = '下载中'",
                    ("@w", row[0] as string ?? ""));
        Db.Execute("DELETE FROM \"download_list\"");
        Refresh();
    }
}
