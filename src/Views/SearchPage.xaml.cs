using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DLsiteMedia.Core;
using DLsiteMedia.Services;

namespace DLsiteMedia.Views;

/// <summary>搜索结果条目（AS 论坛一个帖子，内联展示其网盘卡片与自动检测状态）。</summary>
public class SearchResultItem : INotifyPropertyChanged
{
    public string Title { get; init; } = "";
    public string Minor { get; init; } = "";
    public string Snippet { get; init; } = "";
    public string Url { get; init; } = "";
    public string ThumbUrl { get; init; } = "";

    // 自动扫描进度标记（非绑定，仅供逻辑判断，避免重复扫描）
    public bool Scanning;
    public bool Scanned;

    /// <summary>本帖子抓取到的网盘卡片（按域名分组）。</summary>
    public ObservableCollection<HostCardItem> Hosts { get; } = [];

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; OnPropertyChanged(); }
    }

    private string _scanText = "";
    public string ScanText
    {
        get => _scanText;
        set { _scanText = value; OnPropertyChanged(); }
    }

    private Brush _scanBrush = Brushes.Gray;
    public Brush ScanBrush
    {
        get => _scanBrush;
        set { _scanBrush = value; OnPropertyChanged(); }
    }

    private Visibility _hostsVisibility = Visibility.Collapsed;
    public Visibility HostsVisibility
    {
        get => _hostsVisibility;
        set { _hostsVisibility = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>网盘卡片下单条链接的检测状态：待检测 → 检测中… → 有效/失效/已暂停，让用户能明确看到具体是哪一条链接失效。</summary>
public class LinkStatusItem : INotifyPropertyChanged
{
    public string Url { get; init; } = "";

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private Brush _statusBrush = Brushes.Gray;
    public Brush StatusBrush
    {
        get => _statusBrush;
        set { _statusBrush = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>下载网站卡片：host -> 链接组与检测状态。</summary>
public class HostCardItem : INotifyPropertyChanged
{
    public string Host { get; init; } = "";
    public List<string> Urls { get; init; } = [];
    public string CountText { get; init; } = "";

    /// <summary>逐条链接的检测状态，供 UI 显示具体是哪一条失效（与 Urls 同序）。</summary>
    public ObservableCollection<LinkStatusItem> Links { get; } = [];

    /// <summary>null=检测中 / true=全部有效 / false=失效或部分 / "queued"=已加入下载。</summary>
    public object? Status { get; set; }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private Brush _statusBrush = Brushes.Gray;
    public Brush StatusBrush
    {
        get => _statusBrush;
        set { _statusBrush = value; OnPropertyChanged(); }
    }

    // 检测通过后允许在列表中直接点击下载
    private bool _canDownload;
    public bool CanDownload
    {
        get => _canDownload;
        set { _canDownload = value; OnPropertyChanged(); OnPropertyChanged(nameof(DownloadVisibility)); }
    }

    public Visibility DownloadVisibility => CanDownload ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>社团作品列表条目（RG 搜索结果）。</summary>
public class MakerWorkItem : INotifyPropertyChanged
{
    public string WorkId { get; init; } = "";
    public string Title { get; init; } = "";
    public string ThumbUrl { get; init; } = "";

    /// <summary>已在库（works 表已有记录）：卡片置灰降亮。</summary>
    public bool InLib { get; init; }

    /// <summary>在库状态文本（下载中/已下载/已品悦），仅 InLib 时显示。</summary>
    public string StateText { get; init; } = "";

    /// <summary>卡片不透明度：在库作品降到 0.45 以示区别。</summary>
    public double CardOpacity { get; init; } = 1.0;

    // 下载状态角标（与下载页同步）：加入下载后展示 待下载 / 下载中 N/M / 解压中 X% / 已完成 …，优先于 AS/在库角标
    private bool _downActive;
    public bool DownActive
    {
        get => _downActive;
        set
        {
            _downActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LibBadgeVisibility));
            OnPropertyChanged(nameof(AsBadgeVisibility));
            OnPropertyChanged(nameof(DownBadgeVisibility));
        }
    }

    private string _downText = "";
    public string DownText
    {
        get => _downText;
        set { _downText = value; OnPropertyChanged(); }
    }

    private Brush _downBrush = Brushes.Gray;
    public Brush DownBrush
    {
        get => _downBrush;
        set { _downBrush = value; OnPropertyChanged(); }
    }

    public Visibility LibBadgeVisibility => !DownActive && InLib ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AsBadgeVisibility => !DownActive && !InLib ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DownBadgeVisibility => DownActive ? Visibility.Visible : Visibility.Collapsed;

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; OnPropertyChanged(); }
    }

    // AS 扫描状态（仅未在库作品）：待扫描 → 扫描中 → AS·N帖 / AS·无 / AS·失败
    private string _asStatusText = "";
    public string AsStatusText
    {
        get => _asStatusText;
        set { _asStatusText = value; OnPropertyChanged(); }
    }

    private Brush _asStatusBrush = Brushes.Gray;
    public Brush AsStatusBrush
    {
        get => _asStatusBrush;
        set { _asStatusBrush = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>搜索页（对应 Python 版 select_UI.py）。</summary>
public partial class SearchPage : UserControl
{
    private readonly ObservableCollection<SearchResultItem> _results = [];
    private readonly ObservableCollection<MakerWorkItem> _makerWorks = [];

    private string? _selectId;
    private DlWork? _workData;       // 当前搜索作品的 DL API 数据，加入下载时写入 works 表
    private bool _asmrForCurrent;    // 当前作品是否走 asmr.one：SOU + 优先 asmr + asmr.one 确实有该作品
    private int _searchGeneration;   // 作品（AS 帖子）搜索代际：新一轮作品搜索作废旧缩略图/帖子扫描
    private int _makerGeneration;    // 社团（RG）搜索代际：独立于作品搜索，使社团扫描可在后台持续
    // 用户点击下载后暂停链接校验：已选定下载源，无需再消耗网络去检测其余网盘/帖子。
    // 新搜索或用户手动点击帖子扫描时解除。
    private volatile bool _checksPaused;
    // 与 _checksPaused 配合：取消正在进行中的网络请求（HTTP GET/POST），
    // 让点击下载后校验立即中止，而不是等当前这一条链接的请求超时才停下。
    private CancellationTokenSource _checkCts = new();

    // 社团（RG）搜索状态
    private bool _fromMaker;         // 当前 AS 结果是否来自社团作品列表（决定返回去向）
    private string? _makerId;
    private string? _catalogUrl;     // fsr 搜索/筛选列表页 URL（与社团搜索复用同一作品网格与翻页逻辑）
    private int _makerPage;          // 已加载到第几页（0=未加载）
    private bool _makerHasMore;
    private bool _makerLoading;

    // 未在库作品的 AS 扫描队列：按 3 秒间隔逐个探测可下载性
    private readonly Queue<MakerWorkItem> _scanQueue = new();
    private bool _makerScanning;

    // 社团卡片与下载页状态同步：每秒把下载列表的聚合状态写回对应卡片角标
    private readonly DispatcherTimer _downSyncTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>点击"← 下载列表"按钮时触发，由主窗口切回下载视图。</summary>
    public event Action? BackToDownloadRequested;

    public SearchPage()
    {
        InitializeComponent();
        ResultList.ItemsSource = _results;
        MakerList.ItemsSource = _makerWorks;
        _downSyncTimer.Tick += (_, _) => SyncMakerDownloadStates();
        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    private void RetranslateUi()
    {
        InputBox.ToolTip = I18n.Tr("输入作品号(RJ/BJ/VJ)、社团号(RG)或 DLsite 链接");
        BackToDownloadButton.Content = I18n.Tr("← 下载列表");
        SearchButton.Content = I18n.Tr("查询");
        BackButton.Content = I18n.Tr("← 返回社团作品");
        LoadingText.Text = I18n.Tr("正在查询…");
    }

    /// <summary>由下载页"重新搜索"触发：填入番号并自动搜索。</summary>
    public async void SearchFor(string workId)
    {
        InputBox.Text = workId;
        await RunSearchAsync();
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await RunSearchAsync();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        // 结果列表（来自社团）→ 社团作品列表
        if (ResultList.Visibility == Visibility.Visible && _fromMaker)
            ShowMakerPage();
    }

    private void BackToDownloadButton_Click(object sender, RoutedEventArgs e) =>
        BackToDownloadRequested?.Invoke();

    private void ShowResultsPage()
    {
        ResultList.Visibility = Visibility.Visible;
        MakerList.Visibility = Visibility.Collapsed;
        BackButton.Visibility = _fromMaker ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Content = _catalogUrl != null ? I18n.Tr("← 返回作品列表") : I18n.Tr("← 返回社团作品");
        // 仅当当前作品确认走 asmr.one 时，结果页顶部展示 asmr.one 直链下载横幅
        AsmrBanner.Visibility = _asmrForCurrent ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMakerPage()
    {
        ResultList.Visibility = Visibility.Collapsed;
        MakerList.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        AsmrBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>搜索入口：识别输入是作品号还是社团号，分流到对应流程。</summary>
    private async Task RunSearchAsync()
    {
        var (kind, id) = DlsiteApi.ParseSearchInput(InputBox.Text);
        switch (kind)
        {
            case SearchKind.Maker:
                await RunMakerSearchAsync(id);
                break;
            case SearchKind.Catalog:
                await RunCatalogSearchAsync(id);
                break;
            case SearchKind.Work:
                _fromMaker = false;
                await RunWorkSearchAsync(id);
                break;
            default:
                InAppDialog.Warn(this,
                    I18n.Tr("请输入有效的作品号（RJ/BJ/VJ + 数字）、社团号（RG + 数字）或 DLsite 链接"),
                    I18n.Tr("输入错误"));
                break;
        }
    }

    // ---------- 作品搜索（AS 论坛）----------

    /// <summary>
    /// 按作品号搜索 Anime-sharing 论坛并展示结果。
    /// 注意：只递增 _searchGeneration、不触碰 _makerGeneration / _scanQueue，
    /// 使来自社团的后台 AS 扫描能在查看本帖子列表时继续进行。
    /// </summary>
    private async Task RunWorkSearchAsync(string workId)
    {
        _searchGeneration++;   // 作废旧的作品缩略图加载与帖子扫描
        _checksPaused = false;  // 新搜索：解除上次点击下载造成的校验暂停
        var checkToken = RestartCheckCts();
        _results.Clear();
        _selectId = workId;

        // 已下载过的作品提示用户（DB 查询放后台线程，点击搜索后按钮即时响应）
        var existed = await Task.Run(() => Db.Select(
            "SELECT \"work_name\", \"down_time\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", _selectId)));
        if (existed is { Count: > 0 })
        {
            var workName = existed[0][0] as string ?? "";
            var downTime = existed[0][1]?.ToString() ?? "";
            if (!InAppDialog.Confirm(this,
                    I18n.Format(I18n.Tr("{id} {name}\n该作品已于 {time} 加入过下载，是否继续搜索？"),
                        ("id", _selectId), ("name", workName),
                        ("time", downTime.Length > 19 ? downTime[..19] : downTime)),
                    I18n.Tr("已下载")))
            {
                if (_fromMaker) ShowMakerPage();
                return;
            }
        }

        // 查询期间显示加载遮罩，网络请求在后台执行
        LoadingOverlay.Visibility = Visibility.Visible;
        var generation = _searchGeneration;
        List<AsSearchResult> results;
        try
        {
            // 先取 DL API 作品数据判定类型；音声(SOU)作品判断是否走 asmr.one
            _workData = await DlsiteApi.GetWorkDataAsync(_selectId);
            var isSou = string.Equals(_workData?.WorkType, "SOU", StringComparison.OrdinalIgnoreCase);
            _asmrForCurrent = false;

            // SOU 且优先来源为 asmr.one：先校验 asmr.one 是否真的有该作品
            if (isSou && AppConfig.SouUsesAsmr)
            {
                // 未配置 asmr 账号：仍显示横幅（点下载时会提示配置），不做可用性校验也不回退论坛
                if (string.IsNullOrEmpty(AppConfig.AsmrUsername) || string.IsNullOrEmpty(AppConfig.AsmrPassword))
                {
                    _asmrForCurrent = true;
                    ResetAsmrBanner();
                    ShowResultsPage();
                    return;
                }
                LoadingText.Text = I18n.Tr("正在检查 asmr.one…");
                var detail = await AsmrApi.GetWorkDetailAsync(AsmrApi.RjToId(_selectId));
                if (generation != _searchGeneration)
                    return;
                if (detail is { Files.Count: > 0 })
                {
                    // asmr.one 有该作品：直接走直链，不再请求 AS 论坛列表
                    _asmrForCurrent = true;
                    ResetAsmrBanner();
                    ShowResultsPage();   // 空结果列表 + asmr.one 横幅
                    return;
                }
                // asmr.one 查无此作品：自动回退到 Anime-sharing 论坛
                Logger.Info($"{_selectId} asmr.one 无此作品，回退到 Anime-sharing 搜索");
                LoadingText.Text = I18n.Tr("正在查询…");
            }

            results = await AnimeSharing.SearchWorkAsync(_selectId);
        }
        finally
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        if (generation != _searchGeneration)
            return;   // 期间又发起了新搜索

        ResetAsmrBanner();   // 非 asmr 路径：横幅按"是否 SOU 且优先 asmr"决定显隐（此处为隐藏）

        if (results.Count == 0)
        {
            InAppDialog.Info(this, I18n.Tr("无匹配数据"), I18n.Tr("提示"));
            if (_fromMaker) ShowMakerPage();
            return;
        }

        foreach (var res in results)
            _results.Add(new SearchResultItem
            {
                Title = res.Title,
                Minor = res.Minor,
                Snippet = TrimSnippet(res.Snippet),
                Url = res.Url,
                ThumbUrl = res.Thumb,
                ScanText = I18n.Tr("待扫描"),
                ScanBrush = (Brush)FindResource("CaptionBrush"),
            });
        ShowResultsPage();
        _ = LoadThumbnailsAsync(generation);
        _ = AutoScanPostsAsync(generation, checkToken);   // 自动逐帖抓链接 + 检测，命中有效下载即止
    }

    /// <summary>自动从上到下扫描帖子：抓取网盘链接并检测，命中一个含有效下载的帖子即停止。</summary>
    private async Task AutoScanPostsAsync(int generation, CancellationToken ct)
    {
        foreach (var post in _results.ToList())
        {
            if (ChecksStopped(generation))
                return;
            if (post.Scanned || post.Scanning)
                continue;
            var hasValid = await ScanPostAsync(post, generation, ct);
            if (ChecksStopped(generation))
                return;
            if (hasValid)
                return;   // 已找到含有效下载网盘的帖子，停止继续测试后续帖子
        }
    }

    /// <summary>扫描单个帖子：抓取其网盘链接、按域名分组内联展示并逐组检测。返回是否存在全部有效的网盘组。</summary>
    private async Task<bool> ScanPostAsync(SearchResultItem post, int generation, CancellationToken ct)
    {
        post.Scanning = true;
        post.HostsVisibility = Visibility.Collapsed;
        post.Hosts.Clear();
        post.ScanText = I18n.Tr("扫描中…");
        post.ScanBrush = (Brush)FindResource("CaptionBrush");

        List<string> urls;
        try
        {
            (urls, _) = await AnimeSharing.GetWorkDownUrlsAsync(post.Url);
        }
        catch (Exception)
        {
            urls = [];
        }
        if (ChecksStopped(generation))
        {
            post.Scanning = false;
            return false;
        }
        if (urls.Count == 0)
        {
            post.ScanText = I18n.Tr("无下载链接");
            post.ScanBrush = (Brush)FindResource("CaptionBrush");
            post.Scanning = false;
            post.Scanned = true;
            return false;
        }

        // 按域名分组，保持链接出现顺序
        var groups = new Dictionary<string, List<string>>();
        foreach (var url in urls)
        {
            var host = HostOf(url);
            if (!groups.TryGetValue(host, out var list))
                groups[host] = list = [];
            list.Add(url);
        }
        var cards = new List<HostCardItem>();
        foreach (var (host, hostUrls) in groups)
        {
            var card = new HostCardItem
            {
                Host = host,
                Urls = hostUrls,
                CountText = I18n.Format(I18n.Tr("{count} 个文件"), ("count", hostUrls.Count)),
                StatusText = I18n.Tr("检测中…"),
                StatusBrush = (Brush)FindResource("CaptionBrush"),
            };
            foreach (var u in hostUrls)
                card.Links.Add(new LinkStatusItem
                {
                    Url = u,
                    StatusText = I18n.Tr("待检测"),
                    StatusBrush = (Brush)FindResource("CaptionBrush"),
                });
            post.Hosts.Add(card);
            cards.Add(card);
        }
        post.HostsVisibility = Visibility.Visible;
        post.ScanText = I18n.Tr("检测中…");
        post.ScanBrush = (Brush)FindResource("CaptionBrush");

        var anyValid = false;
        foreach (var card in cards)
        {
            if (ChecksStopped(generation))
            {
                if (_checksPaused)
                    foreach (var c in cards)
                        MarkCardPaused(c);   // 把本帖剩余未检测的网盘组标为已暂停
                post.Scanning = false;
                return false;
            }
            await CheckHostAsync(card, generation, ct);
            if (ChecksStopped(generation))
            {
                if (_checksPaused)
                    foreach (var c in cards)
                        MarkCardPaused(c);
                post.Scanning = false;
                return false;
            }
            if (card.Status is true)
                anyValid = true;
        }
        post.ScanText = anyValid ? I18n.Tr("有有效下载") : I18n.Tr("无有效下载");
        post.ScanBrush = (Brush)FindResource(anyValid ? "GreenBrush" : "YellowBrush");
        post.Scanning = false;
        post.Scanned = true;
        return anyValid;
    }

    // ---------- 社团搜索（DLsite 作品列表）----------

    /// <summary>按社团号（RG）拉取该社团作品列表，展示供用户点选。</summary>
    private async Task RunMakerSearchAsync(string makerId)
    {
        _searchGeneration++;
        _makerGeneration++;
        _results.Clear();
        _makerWorks.Clear();
        _scanQueue.Clear();
        _makerId = makerId;
        _catalogUrl = null;
        _makerPage = 0;
        _makerHasMore = true;
        _fromMaker = false;
        ShowMakerPage();
        await LoadMakerPageAsync();
    }

    /// <summary>按 DLsite 搜索/筛选列表页（fsr URL）拉取作品列表，复用社团作品网格展示供用户点选。</summary>
    private async Task RunCatalogSearchAsync(string searchUrl)
    {
        _searchGeneration++;
        _makerGeneration++;
        _results.Clear();
        _makerWorks.Clear();
        _scanQueue.Clear();
        _makerId = null;
        _catalogUrl = searchUrl;
        _makerPage = 0;
        _makerHasMore = true;
        _fromMaker = false;
        ShowMakerPage();
        await LoadMakerPageAsync();
    }

    /// <summary>加载作品列表的下一页（社团或 fsr 搜索；首页显示遮罩，后续页静默追加）。</summary>
    private async Task LoadMakerPageAsync()
    {
        if ((_makerId == null && _catalogUrl == null) || _makerLoading || !_makerHasMore)
            return;
        _makerLoading = true;
        var gen = _makerGeneration;
        var firstPage = _makerPage == 0;
        if (firstPage)
            LoadingOverlay.Visibility = Visibility.Visible;
        (List<DlMakerWork> Works, bool HasMore) page;
        try
        {
            page = _catalogUrl != null
                ? await DlsiteApi.GetCatalogWorksAsync(_catalogUrl, _makerPage + 1)
                : await DlsiteApi.GetMakerWorksAsync(_makerId!, _makerPage + 1);
        }
        finally
        {
            if (firstPage)
                LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        if (gen != _makerGeneration)   // 期间发起了新搜索，丢弃本次结果
        {
            _makerLoading = false;
            return;
        }
        if (page.Works.Count == 0)
        {
            if (firstPage)
                InAppDialog.Info(this,
                    _catalogUrl != null ? I18n.Tr("未找到匹配的作品") : I18n.Tr("未找到该社团的作品"),
                    I18n.Tr("提示"));
            _makerHasMore = false;
            _makerLoading = false;
            return;
        }
        _makerPage++;
        _makerHasMore = page.HasMore;
        // 校验后台：本页作品号在 works 表中的状态。
        // 下载中 → 下载状态角标（实时同步下载页）；已下载/已品悦 → 置灰在库角标；无记录 → 排入 AS 扫描。
        var states = LookupWorkStates(page.Works.Select(w => w.WorkId));
        var added = new List<MakerWorkItem>();
        var hasDownloading = false;
        foreach (var w in page.Works)
        {
            var state = states.GetValueOrDefault(w.WorkId);
            var inLib = state is "已品悦" or "已下载";
            var downloading = state == "下载中";
            var item = new MakerWorkItem
            {
                WorkId = w.WorkId, Title = w.Title, ThumbUrl = w.Thumb,
                InLib = inLib, StateText = inLib ? state ?? "" : "",
                CardOpacity = inLib ? 0.45 : 1.0,
                DownActive = downloading,
                DownText = downloading ? I18n.Tr("下载中") : "",
                DownBrush = (Brush)FindResource("BlueBrush"),
                AsStatusText = inLib || downloading ? "" : I18n.Tr("待扫描"),
                AsStatusBrush = (Brush)FindResource("CaptionBrush"),
            };
            _makerWorks.Add(item);
            added.Add(item);
            if (downloading)
                hasDownloading = true;
            else if (!inLib)
                _scanQueue.Enqueue(item);   // 无记录 → 排入 AS 扫描队列
        }
        _makerLoading = false;
        _ = LoadMakerThumbnailsAsync(added, gen);
        _ = RunMakerScansAsync(gen);
        if (hasDownloading)
            StartDownSync();
    }

    /// <summary>启动下载状态同步定时器（已运行则忽略；无活动下载时会自行停止）。</summary>
    private void StartDownSync()
    {
        if (!_downSyncTimer.IsEnabled)
            _downSyncTimer.Start();
    }

    /// <summary>每秒把下载列表聚合状态写回社团卡片角标（镜像下载页 AggregateStatus），无活动下载时停表。</summary>
    private void SyncMakerDownloadStates()
    {
        if (_makerWorks.Count == 0)
        {
            _downSyncTimer.Stop();
            return;
        }
        var rows = Db.Select("SELECT \"work_id\", \"status\" FROM \"download_list\"");
        var groups = new Dictionary<string, List<string>>();
        foreach (var row in rows ?? [])
        {
            var wid = row[0] as string ?? "";
            if (!groups.TryGetValue(wid, out var list))
                groups[wid] = list = [];
            list.Add(row[1] as string ?? "");
        }
        // 已不在下载列表、但此前处于活动角标的作品需回查 works 最终状态：
        // 先收集全部再一次性批量 IN 查询，避免逐作品 Db.Scalar 的 N+1。
        var states = LookupWorkStates(
            _makerWorks.Where(it => it.DownActive && !groups.ContainsKey(it.WorkId))
                       .Select(it => it.WorkId));
        foreach (var item in _makerWorks)
        {
            if (groups.TryGetValue(item.WorkId, out var statuses))
            {
                var (text, color) = AggregateStatus(item.WorkId, statuses);
                item.DownText = text;
                item.DownBrush = BrushOf(color);
                item.DownActive = true;
            }
            else if (item.DownActive)
            {
                // 已不在下载列表：显示 works 最终状态（已下载/已品悦），否则回到原角标
                var state = states.GetValueOrDefault(item.WorkId);
                if (state is "已下载" or "已品悦")
                {
                    item.DownText = state;
                    item.DownBrush = BrushOf(state == "已品悦" ? "#4ade80" : "#60a5fa");
                }
                else
                {
                    item.DownActive = false;
                }
            }
        }
        if (groups.Count == 0)
            _downSyncTimer.Stop();   // 无活动下载，最终状态已固化在卡片上
    }

    /// <summary>下载列表按番号聚合出一行显示状态（镜像下载页 DownloadPage.AggregateStatus）。</summary>
    private static (string Text, string Color) AggregateStatus(string workId, List<string> statuses)
    {
        var done = statuses.Count(s => s == "1");
        if (statuses.Contains("3"))
            return (I18n.Format(I18n.Tr("下载中 {done}/{total}"), ("done", done), ("total", statuses.Count)), "#60a5fa");
        if (statuses.Contains("0"))
            return (I18n.Format(I18n.Tr("等待下载 {done}/{total}"), ("done", done), ("total", statuses.Count)), "#facc15");
        if (statuses.Contains("4"))
            return (I18n.Format(I18n.Tr("已暂停 {done}/{total}"), ("done", done), ("total", statuses.Count)), "#9aa4b2");
        if (statuses.Contains("2"))
            return (I18n.Format(I18n.Tr("{n} 个解析失败"), ("n", statuses.Count(s => s == "2"))), "#f87171");
        if (DownloadEngine.UnzipProgress.TryGetValue(workId, out var unzip))
        {
            if (unzip.State == "pending")
                return (I18n.Tr("待解压"), "#facc15");
            if (unzip.State == "moving")
                return (I18n.Format(I18n.Tr("移动中 {pct}%"), ("pct", unzip.Pct)), "#a78bfa");
            return (I18n.Format(I18n.Tr("解压中 {pct}%"), ("pct", unzip.Pct)), "#60a5fa");
        }
        return (I18n.Tr("已完成"), "#4ade80");
    }

    // 角标颜色只有固定几种，冻结并按 hex 缓存复用，避免每秒每卡片重复分配画刷。
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

    /// <summary>批量查询作品号在 works 表中的状态（不存在则不在结果里），用于标记"已在库"。</summary>
    private static Dictionary<string, string> LookupWorkStates(IEnumerable<string> ids)
    {
        var list = ids.Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0)
            return result;
        var names = new List<string>();
        var args = new List<(string, object?)>();
        for (var i = 0; i < list.Count; i++)
        {
            names.Add($"@w{i}");
            args.Add(($"@w{i}", list[i]));
        }
        var rows = Db.Select(
            $"SELECT \"work_id\", \"state\" FROM \"works\" WHERE \"work_id\" IN ({string.Join(",", names)})",
            args.ToArray());
        foreach (var row in rows ?? [])
            result[row[0] as string ?? ""] = row[1] as string ?? "";
        return result;
    }

    /// <summary>
    /// 对未在库作品按 3 秒间隔逐个扫描 AS 论坛，把匹配帖子数写回卡片角标。
    /// 单实例运行（_makerScanning 守卫）；翻页追加的新作品会被同一队列消费。
    /// 守卫 _makerGeneration：点击作品进入帖子列表（递增 _searchGeneration）不会中断本扫描，
    /// 仅当发起新的社团搜索（递增 _makerGeneration）时停止。
    /// </summary>
    private async Task RunMakerScansAsync(int generation)
    {
        if (_makerScanning)
            return;
        _makerScanning = true;
        try
        {
            while (_scanQueue.Count > 0)
            {
                if (generation != _makerGeneration)
                    return;
                var item = _scanQueue.Dequeue();
                item.AsStatusText = I18n.Tr("扫描中…");
                item.AsStatusBrush = (Brush)FindResource("CaptionBrush");
                int count;
                try
                {
                    count = (await AnimeSharing.SearchWorkAsync(item.WorkId)).Count;
                }
                catch (Exception)
                {
                    count = -1;
                }
                if (generation != _makerGeneration)
                    return;
                if (count > 0)
                {
                    item.AsStatusText = I18n.Format(I18n.Tr("AS · {count} 帖"), ("count", count));
                    item.AsStatusBrush = (Brush)FindResource("GreenBrush");
                }
                else if (count == 0)
                {
                    item.AsStatusText = I18n.Tr("AS · 无");
                    item.AsStatusBrush = (Brush)FindResource("CaptionBrush");
                }
                else
                {
                    item.AsStatusText = I18n.Tr("AS · 失败");
                    item.AsStatusBrush = (Brush)FindResource("RedBrush");
                }
                if (_scanQueue.Count > 0)   // 仅在还有待扫描项时按 3 秒间隔
                    await Task.Delay(3000);
            }
        }
        finally
        {
            _makerScanning = false;
        }
    }

    private void MakerList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 下拉接近底部时自动加载下一页
        if (!_makerHasMore || _makerLoading || e.ExtentHeight <= 0)
            return;
        if (e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 300)
            _ = LoadMakerPageAsync();
    }

    private async void MakerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 点击社团作品 → 自动用其 RJ 号搜索 AS（社团页扫描在后台继续）
        if (MakerList.SelectedItem is not MakerWorkItem item)
            return;
        MakerList.SelectedItem = null;
        InputBox.Text = item.WorkId;
        _fromMaker = true;
        await RunWorkSearchAsync(item.WorkId);
    }

    /// <summary>后台加载社团作品缩略图。</summary>
    private async Task LoadMakerThumbnailsAsync(List<MakerWorkItem> items, int generation)
    {
        using var client = Http.CreateClient(TimeSpan.FromSeconds(15));
        foreach (var item in items)
        {
            if (generation != _makerGeneration)
                return;
            if (string.IsNullOrEmpty(item.ThumbUrl))
                continue;
            try
            {
                var bytes = await client.GetByteArrayAsync(item.ThumbUrl);
                if (generation != _makerGeneration)
                    return;
                var image = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    image.EndInit();
                }
                image.Freeze();
                item.Thumb = image;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
            {
                // 单张缩略图失败不影响其它
            }
        }
    }

    /// <summary>整理帖子摘要：去掉空行和多余空白，最多保留 6 行。</summary>
    private static string TrimSnippet(string snippet)
    {
        var lines = snippet.Split('\n')
            .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0)
            .Take(6);
        return string.Join('\n', lines);
    }

    /// <summary>后台下载搜索结果缩略图（对应 Python 版 ThumbLoader 线程）。</summary>
    private async Task LoadThumbnailsAsync(int generation)
    {
        using var client = Http.CreateClient(TimeSpan.FromSeconds(15));
        foreach (var item in _results.ToList())
        {
            if (generation != _searchGeneration)
                return;  // 新一轮搜索已开始，停止旧缩略图加载
            if (string.IsNullOrEmpty(item.ThumbUrl))
                continue;
            try
            {
                var bytes = await client.GetByteArrayAsync(item.ThumbUrl);
                if (generation != _searchGeneration)
                    return;
                var image = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    image.EndInit();
                }
                image.Freeze();
                item.Thumb = image;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or NotSupportedException)
            {
                // 单张缩略图失败不影响其它
            }
        }
    }

    /// <summary>点击未扫描的帖子可手动触发其扫描（自动扫描已命中并停止后，仍可补扫后续帖子）。</summary>
    private async void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is not SearchResultItem post)
            return;
        ResultList.SelectedItem = null;
        if (post.Scanning || post.Scanned)
            return;   // 已扫描/扫描中的帖子不重复处理（下载按钮点击也会落到这里，需放行）
        // 手动点击未扫描的帖子：用户主动要校验该帖，解除之前点击下载造成的暂停
        _checksPaused = false;
        var checkToken = RestartCheckCts();
        await ScanPostAsync(post, _searchGeneration, checkToken);
    }

    private static string HostOf(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return host.StartsWith("www.") ? host[4..] : host;
        }
        catch (UriFormatException)
        {
            return url;
        }
    }

    /// <summary>校验应否中止：发起了新搜索（代际变化），或用户点击下载后暂停了链接校验。</summary>
    private bool ChecksStopped(int generation) => generation != _searchGeneration || _checksPaused;

    /// <summary>
    /// 开启新一轮校验前调用：取消上一轮仍在进行的网络请求并换发新的取消令牌。
    /// 用于新搜索、用户手动点击帖子恢复扫描等"确实要继续检测"的场景。
    /// </summary>
    private CancellationToken RestartCheckCts()
    {
        _checkCts.Cancel();
        _checkCts = new CancellationTokenSource();
        return _checkCts.Token;
    }

    /// <summary>把仍处于"检测中"的网盘卡片标为已暂停校验（已判定有效/失效/已加入下载的卡片不动）。</summary>
    private void MarkCardPaused(HostCardItem card)
    {
        if (card.Status is not null)
            return;
        card.StatusText = I18n.Tr("已暂停校验");
        card.StatusBrush = (Brush)FindResource("CaptionBrush");
        // 未出结果（待检测/检测中…）的链接一并标为已暂停，让用户清楚哪些链接根本没检测完
        var pending = I18n.Tr("待检测");
        var checking = I18n.Tr("检测中…");
        foreach (var link in card.Links)
            if (link.StatusText.Length == 0 || link.StatusText == pending || link.StatusText == checking)
            {
                link.StatusText = I18n.Tr("已暂停");
                link.StatusBrush = (Brush)FindResource("CaptionBrush");
            }
    }

    /// <summary>
    /// 检测某网盘组下所有链接是否直连有效（不经过中转站）；全部有效则允许下载。
    /// 逐条更新 card.Links 中对应链接的状态，让用户在检测过程中就能明确看到具体是哪一条链接失效；
    /// 用户点击下载后 ct 会被取消，正在进行的网络请求随之立即中止（而非等到超时）。
    /// </summary>
    private async Task CheckHostAsync(HostCardItem card, int generation, CancellationToken ct)
    {
        using var client = LinkChecker.MakeClient();
        int valid = 0, checkedCount = 0;
        for (var i = 0; i < card.Urls.Count; i++)
        {
            var url = card.Urls[i];
            var link = i < card.Links.Count ? card.Links[i] : null;
            if (ChecksStopped(generation))
            {
                if (_checksPaused)
                    MarkCardPaused(card);
                return;
            }
            if (link != null)
            {
                link.StatusText = I18n.Tr("检测中…");
                link.StatusBrush = (Brush)FindResource("CaptionBrush");
            }
            bool ok;
            try
            {
                ok = await LinkChecker.CheckUrlAsync(url, client, ct);
            }
            catch (OperationCanceledException)
            {
                // 用户点击下载中断了本次检测：不判定该链接失效，仅标记为已暂停
                if (_checksPaused)
                    MarkCardPaused(card);
                return;
            }
            checkedCount++;
            if (ok)
                valid++;
            if (link != null)
            {
                link.StatusText = ok ? I18n.Tr("有效") : I18n.Tr("失效");
                link.StatusBrush = (Brush)FindResource(ok ? "GreenBrush" : "RedBrush");
            }
            if (ChecksStopped(generation))
            {
                if (_checksPaused)
                    MarkCardPaused(card);
                return;
            }
            if (checkedCount < card.Urls.Count)
                card.StatusText = I18n.Format(I18n.Tr("检测中… {checked}/{total}"),
                    ("checked", checkedCount), ("total", card.Urls.Count));
        }
        if (generation != _searchGeneration)
            return;
        if (valid == card.Urls.Count)
        {
            card.Status = true;
            card.StatusText = I18n.Tr("有效");
            card.StatusBrush = (Brush)FindResource("GreenBrush");
            card.CanDownload = true;
        }
        else if (valid == 0)
        {
            card.Status = false;
            card.StatusText = I18n.Tr("失效");
            card.StatusBrush = (Brush)FindResource("RedBrush");
        }
        else
        {
            // 分卷不全等于不可用，不允许加入下载
            card.Status = false;
            card.StatusText = I18n.Format(I18n.Tr("部分有效 {valid}/{total}"),
                ("valid", valid), ("total", card.Urls.Count));
            card.StatusBrush = (Brush)FindResource("YellowBrush");
        }
    }

    /// <summary>列表内点击"下载"：该网站全部链接通过 debrid-link 中转站下载。</summary>
    private void HostDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HostCardItem card || !card.CanDownload)
            return;

        // 用户已选定下载源：暂停其余网盘/帖子的链接校验，并立即中断所有正在进行中的检测请求
        _checksPaused = true;
        _checkCts.Cancel();

        // 有媒体库配置时弹窗选择下载目标
        string? targetFolder = null, targetLib = null;
        if (AppConfig.ReadMediaLibs().Count > 0)
        {
            var dialog = new DownTargetDialog();
            if (!dialog.Show(this))
                return;
            targetFolder = dialog.SelectedFolder;
            targetLib = dialog.SelectedLib;
        }

        foreach (var downUrl in card.Urls)
            // url 为主键：重复加入同一作品时覆盖旧记录并重置为待下载（用户已在搜索时确认过）
            Db.Execute(
                "INSERT OR REPLACE INTO \"download_list\" (\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\") " +
                "VALUES (@uuid, @w, @url, '0', '0', '1')",
                ("@uuid", Guid.NewGuid().ToString()), ("@w", _selectId), ("@url", downUrl));
        RecordWork();
        // 须在 RecordWork 建好 works 行之后落库目标目录，重启后仍可恢复
        if (!string.IsNullOrEmpty(targetFolder))
            DownloadEngine.SetWorkTargetPath(_selectId!, targetFolder, targetLib);
        DownloadEngine.Start();

        card.Status = "queued";
        card.CanDownload = false;
        card.StatusText = I18n.Tr("已加入下载");
        card.StatusBrush = (Brush)FindResource("AccentLightBrush");

        // 同步社团卡片：该作品（若在社团网格中）立即转为"待下载"，并启动与下载页的状态同步
        var makerItem = _makerWorks.FirstOrDefault(m => m.WorkId == _selectId);
        if (makerItem != null)
        {
            makerItem.DownText = I18n.Tr("待下载");
            makerItem.DownBrush = (Brush)FindResource("YellowBrush");
            makerItem.DownActive = true;
        }
        StartDownSync();
    }

    /// <summary>重置 asmr.one 横幅状态（每次新搜索时调用）：清空状态文案、恢复下载按钮。</summary>
    private void ResetAsmrBanner()
    {
        AsmrBannerStatus.Text = "";
        AsmrDownloadButton.IsEnabled = true;
    }

    /// <summary>横幅点击"asmr.one 下载"：用当前 RJ 号通过 asmr.one 直链下载该 SOU 作品。</summary>
    private async void AsmrDownload_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectId))
            return;
        if (string.IsNullOrEmpty(AppConfig.AsmrUsername) || string.IsNullOrEmpty(AppConfig.AsmrPassword))
        {
            InAppDialog.Warn(this, I18n.Tr("请先在设置中填写 ASMR.ONE 账号"), I18n.Tr("提示"));
            return;
        }

        // 用户已选定下载源：暂停并立即中断所有正在进行中的链接校验请求
        _checksPaused = true;
        _checkCts.Cancel();

        // 有媒体库配置时弹窗选择下载目标
        string? targetFolder = null, targetLib = null;
        if (AppConfig.ReadMediaLibs().Count > 0)
        {
            var dialog = new DownTargetDialog();
            if (!dialog.Show(Window.GetWindow(this)))
                return;
            targetFolder = dialog.SelectedFolder;
            targetLib = dialog.SelectedLib;
        }

        AsmrDownloadButton.IsEnabled = false;
        AsmrBannerStatus.Text = I18n.Tr("入队中…");
        AsmrBannerStatus.Foreground = (Brush)FindResource("CaptionBrush");

        var result = await AsmrService.EnqueueByRjAsync(
            _selectId, _workData?.WorkName ?? "", targetFolder, targetLib);

        if (result.Ok)
        {
            AsmrBannerStatus.Text = I18n.Format(I18n.Tr("已加入下载：{count} 个文件"), ("count", result.FileCount));
            AsmrBannerStatus.Foreground = (Brush)FindResource("AccentLightBrush");
            // 同步社团卡片（若来自社团网格）：立即转为"待下载"并启动与下载页的状态同步
            var makerItem = _makerWorks.FirstOrDefault(m => m.WorkId == _selectId);
            if (makerItem != null)
            {
                makerItem.DownText = I18n.Tr("待下载");
                makerItem.DownBrush = (Brush)FindResource("YellowBrush");
                makerItem.DownActive = true;
            }
            StartDownSync();
        }
        else
        {
            AsmrBannerStatus.Text = result.Error ?? I18n.Tr("入队失败");
            AsmrBannerStatus.Foreground = (Brush)FindResource("RedBrush");
            AsmrDownloadButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 把 DL API 返回的作品数据写入 works 表，状态为下载中。
    /// UPSERT：重复入队时只刷新本次下载相关的列，保留已有元数据；
    /// folder/target/target_lib/cover/meta_scanned 重置，重新下载后重新获取。
    /// </summary>
    private void RecordWork()
    {
        var work = _workData;
        Db.Execute(
            "INSERT INTO \"works\" (\"work_id\", \"work_name\", \"maker_id\", \"maker_name\", \"work_type\", " +
            "\"intro_s\", \"age_category\", \"is_ana\", \"state\", \"down_time\") VALUES " +
            "(@w, @n, @mi, @mn, @t, @s, @a, @ana, '下载中', @time) " +
            "ON CONFLICT(\"work_id\") DO UPDATE SET " +
            "\"work_name\" = excluded.\"work_name\", \"maker_id\" = excluded.\"maker_id\", " +
            "\"maker_name\" = excluded.\"maker_name\", \"work_type\" = excluded.\"work_type\", " +
            "\"intro_s\" = excluded.\"intro_s\", \"age_category\" = excluded.\"age_category\", " +
            "\"is_ana\" = excluded.\"is_ana\", \"state\" = excluded.\"state\", \"down_time\" = excluded.\"down_time\", " +
            "\"folder\" = NULL, \"target\" = NULL, \"target_lib\" = NULL, " +
            "\"cover\" = NULL, \"meta_scanned\" = NULL",
            ("@w", _selectId), ("@n", work?.WorkName ?? ""), ("@mi", work?.MakerId ?? ""),
            ("@mn", work?.MakerName ?? ""), ("@t", work?.WorkType ?? ""), ("@s", work?.IntroS ?? ""),
            ("@a", work?.AgeCategory ?? ""), ("@ana", work?.IsAna ?? ""),
            ("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff")));
    }
}
