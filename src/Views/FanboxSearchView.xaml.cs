using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>FANBOX 搜索结果里的一张卡片：作家卡（artist）或作家主页的作品卡（post）。</summary>
public class FanboxCardItem : ObservableBase
{
    /// <summary>作家号或作品号（按 Kind 解释）。</summary>
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string ThumbUrl { get; init; } = "";
    /// <summary>左上角小标：作家的 public_id。</summary>
    public string TagText { get; init; } = "";
    /// <summary>右上角小标：作品文件数 / 作家被收藏数。</summary>
    public string MetaText { get; init; } = "";
    /// <summary>本地状态（已品悦 / 下载中 / 已下载），空表示站上作品尚未下载过。</summary>
    public string State { get; init; } = "";
    /// <summary>可选中下载（未入库且不在下载队列中的作品）。</summary>
    public bool Selectable { get; init; }
    /// <summary>卡片来源：artist=作家卡（无操作按钮）/ post=作家主页作品。</summary>
    public string Kind { get; init; } = "post";

    public Visibility TagVisibility => TagText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MetaVisibility => MetaText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>状态文案：已品悦在界面上一律显示为「已下载」。</summary>
    public string StateText => State == "已品悦" ? I18n.Tr("已下载") : State;
    public Visibility StateVisibility => State.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // 卡内按钮行二选一：未下载的作品可 选择/下载；已下载或下载中则是禁用的状态按钮。作家卡不带按钮。
    public Visibility SelectVisibility =>
        Kind == "post" && Selectable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BusyVisibility =>
        Kind == "post" && !Selectable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>已入库/下载中的作品降亮，与 Web 的 .card.dim 等价。</summary>
    public double CardOpacity => Kind == "post" && !Selectable ? 0.45 : 1.0;

    private bool _isPicked;
    public bool IsPicked
    {
        get => _isPicked;
        set
        {
            if (!Set(ref _isPicked, value))
                return;
            Raise(nameof(PickBorderBrush));
            Raise(nameof(PickText));
            Raise(nameof(PickBackground));
            Raise(nameof(PickForeground));
        }
    }

    /// <summary>选中的卡片用强调色描边（等价 Web 的 .card.picked）。</summary>
    public Brush PickBorderBrush => IsPicked
        ? Application.Current?.TryFindResource("AccentLightBrush") as Brush ?? Brushes.DodgerBlue
        : Brushes.Transparent;

    /// <summary>选择按钮文案：未选「选择」，已选「已选」（对齐 Web 的 .mcard-actions 按钮）。</summary>
    public string PickText => IsPicked ? I18n.Tr("已选") : I18n.Tr("选择");

    /// <summary>已选时按钮转强调色底（等价 Web 的 .mcard-actions button.primary）。</summary>
    public Brush PickBackground => IsPicked
        ? Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue
        : Application.Current?.TryFindResource("CardBrush") as Brush ?? Brushes.DimGray;

    /// <summary>强调色底上用白字，未选时用常规文字色。</summary>
    public Brush PickForeground => IsPicked
        ? Brushes.White
        : Application.Current?.TryFindResource("TextBrush") as Brush ?? Brushes.White;

    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get => _thumb;
        set => Set(ref _thumb, value);
    }
}

/// <summary>
/// 「作品搜索」分区中 FANBOX 来源（pawchive）的结果区：搜作家 → 作家主页选作品 → 批量/单篇下载。
/// 只做搜索与下载，不含媒体库浏览——已下载的 fanbox 作品落在磁盘的媒体库目录里，程序内不再建卡片视图。
///
/// 交互与样式以 Web 端 src/Web/js/fanbox.js 为基准：单个搜索结果直接进主页、卡内按钮选择、
/// 下拉到底自动翻页、已下载/下载中的作品置灰且不可选。
/// 工具栏（来源下拉 + 输入框 + 查询 + 返回作家列表）由宿主 <see cref="SearchPage"/> 提供。
/// 层级（_level）：artists（作家结果）/ posts（作家主页）。
/// </summary>
public partial class FanboxSearchView : UserControl
{
    private readonly ObservableCollection<FanboxCardItem> _cards = [];

    private string _level = "artists";
    private int _generation;        // 请求代际：新一轮搜索/换作家即作废在途请求与缩略图加载

    // 当前作家
    private string _artistId = "";
    private string _artistName = "";
    private string _publicId = "";
    // 当前主页是否从作家列表点进来（决定「返回作家列表」是否可用）
    private bool _fromArtists;
    // 作家列表结果缓存：从主页返回时无需重新搜索
    private List<FanboxCardItem> _artistCards = [];

    // 作家主页分页（站点固定每页 50 条）
    private readonly List<PawchivePost> _posts = [];
    private int _offset;
    private bool _hasMore;
    private bool _loadingMore;

    private static readonly Regex ArtistUrlRe = new(@"/fanbox/user/(\d+)", RegexOptions.IgnoreCase);

    /// <summary>结果计数/提示文案变化（宿主显示在搜索框下方）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>「返回作家列表」按钮是否应该显示。</summary>
    public event Action<bool>? BackAvailabilityChanged;

    /// <summary>当前是否停在作家主页且可返回作家列表（宿主切回本来源时用它恢复返回按钮）。</summary>
    public bool CanGoBack => _level == "posts" && _fromArtists;

    public FanboxSearchView()
    {
        InitializeComponent();
        CardList.ItemsSource = _cards;
        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    private void RetranslateUi()
    {
        SelectAllButton.Content = I18n.Tr("全选");
        SelectNoneButton.Content = I18n.Tr("清空");
        DownloadSelectedButton.Content = I18n.Tr("下载选中");
        LoadingText.Text = I18n.Tr("正在查询…");
        UpdateSelectInfo();
    }

    /// <summary>输入框的占位提示（宿主切到本来源时取用）。</summary>
    public static string InputHint => I18n.Tr("作家名 / 作家 ID，或粘贴 pawchive 作家链接");

    /// <summary>当前结果计数/提示文案；宿主切回本来源时用它恢复计数行。</summary>
    public string CurrentStatus { get; private set; } = "";

    private void SetStatus(string text)
    {
        CurrentStatus = text;
        StatusChanged?.Invoke(text);
    }

    private void ShowLevel(string level)
    {
        _level = level;
        SelectBar.Visibility = level == "posts" ? Visibility.Visible : Visibility.Collapsed;
        BackAvailabilityChanged?.Invoke(level == "posts" && _fromArtists);
    }

    // ---------- 搜索 ----------

    /// <summary>
    /// 由宿主的「查询」触发。输入可以是作家名、纯数字作家号，或直接粘贴 pawchive 的作家/作品链接。
    /// </summary>
    public async Task RunSearchAsync(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
            return;

        var match = ArtistUrlRe.Match(raw);
        if (match.Success)
        {
            await OpenArtistAsync(match.Groups[1].Value, "", "", fromArtists: false);
            return;
        }
        if (raw.Length >= 4 && raw.All(char.IsAsciiDigit))
        {
            await OpenArtistAsync(raw, "", "", fromArtists: false);
            return;
        }
        await SearchArtistsAsync(raw);
    }

    private async Task SearchArtistsAsync(string keyword)
    {
        var generation = ++_generation;
        _fromArtists = false;
        ShowLevel("artists");
        _cards.Clear();
        SetStatus("");
        LoadingText.Text = PawchiveApi.CreatorsReady
            ? I18n.Tr("正在搜索作家…")
            : I18n.Tr("正在下载站点作家索引（约 15MB），首次搜索稍慢…");
        LoadingOverlay.Visibility = Visibility.Visible;

        List<PawchiveArtist> artists;
        try
        {
            artists = await PawchiveApi.SearchArtistsAsync(keyword);
        }
        finally
        {
            if (generation == _generation)
                LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        if (generation != _generation)
            return;

        // 只有一个结果 → 直接进入该作家主页（对齐 Web：免去多余一次点击）
        if (artists.Count == 1)
        {
            await OpenArtistAsync(artists[0].Id, artists[0].Name, artists[0].PublicId, fromArtists: false);
            return;
        }
        if (artists.Count == 0)
        {
            SetStatus(I18n.Tr("没有匹配的作家"));
            return;
        }

        _artistCards = artists.Select(a => new FanboxCardItem
        {
            Kind = "artist",
            Id = a.Id,
            Title = a.Name.Length > 0 ? a.Name : a.PublicId,
            ThumbUrl = PawchiveApi.IconUrl(a.Service, a.Id),
            TagText = a.PublicId,
            MetaText = a.Favorited > 0 ? $"♥ {a.Favorited}" : "",
        }).ToList();
        foreach (var card in _artistCards)
            _cards.Add(card);

        var cachedAt = PawchiveApi.CreatorsCachedAt;
        SetStatus(I18n.Format(I18n.Tr("搜索到 {n} 位作家"), ("n", artists.Count)) +
            (cachedAt is { } t ? "　" + I18n.Format(I18n.Tr("索引更新于 {time}"),
                ("time", t.ToString("yyyy-MM-dd HH:mm"))) : ""));
        _ = LoadThumbnailsAsync(generation);
    }

    /// <summary>宿主的「返回作家列表」：回到上一次的作家搜索结果（结果已缓存，不重新请求）。</summary>
    public void GoBackToArtists()
    {
        if (_artistCards.Count == 0)
            return;
        var generation = ++_generation;
        _cards.Clear();
        foreach (var card in _artistCards)
            _cards.Add(card);
        _fromArtists = false;
        ShowLevel("artists");
        SetStatus(I18n.Format(I18n.Tr("搜索到 {n} 位作家"), ("n", _artistCards.Count)));
        _ = LoadThumbnailsAsync(generation);
    }

    // ---------- 作家主页 ----------

    private async Task OpenArtistAsync(string artistId, string artistName, string publicId, bool fromArtists)
    {
        var generation = ++_generation;
        _artistId = artistId;
        _artistName = artistName;
        _publicId = publicId;
        _fromArtists = fromArtists;
        _posts.Clear();
        _cards.Clear();
        _offset = 0;
        _hasMore = true;
        _loadingMore = false;
        ShowLevel("posts");
        UpdateSelectInfo();
        SetStatus("");
        LoadingText.Text = I18n.Tr("正在获取作品…");
        LoadingOverlay.Visibility = Visibility.Visible;

        // 作家名未知（直接输入作家号/链接进来）时补一次作家信息
        if (_artistName.Length == 0 &&
            await PawchiveApi.GetArtistAsync(PawchiveApi.FanboxService, artistId) is { } profile)
        {
            if (generation != _generation)
                return;
            _artistName = profile.Name;
            _publicId = profile.PublicId;
        }

        await LoadMorePostsAsync(generation);
        if (generation == _generation)
            LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>取下一页作品并追加到网格；每页 50 条，取不满一页即到底。</summary>
    private async Task LoadMorePostsAsync(int generation)
    {
        if (_loadingMore || !_hasMore)
            return;
        _loadingMore = true;
        try
        {
            var batch = await PawchiveApi.GetPostsAsync(PawchiveApi.FanboxService, _artistId, _offset);
            if (generation != _generation)
                return;
            if (batch is null)
            {
                SetStatus(I18n.Tr("获取作品列表失败"));
                _hasMore = false;
                return;
            }
            _offset += batch.Count;
            _hasMore = batch.Count >= PawchiveApi.PageSize;
            _posts.AddRange(batch);

            var states = await Task.Run(() => FanboxService.PostStates(_artistId));
            if (generation != _generation)
                return;
            foreach (var p in batch)
            {
                var state = states.GetValueOrDefault(p.Id, "");
                _cards.Add(new FanboxCardItem
                {
                    Kind = "post",
                    Id = p.Id,
                    Title = p.Title.Length > 0 ? p.Title : p.Id,
                    ThumbUrl = p.CoverPath.Length > 0 ? PawchiveApi.ThumbUrl(p.CoverPath) : "",
                    MetaText = p.Files.Count > 0
                        ? I18n.Format(I18n.Tr("{n} 文件"), ("n", p.Files.Count)) : "",
                    State = state,
                    Selectable = state.Length == 0 && p.Files.Count > 0,
                });
            }
            SetStatus($"{(_artistName.Length > 0 ? _artistName : _artistId)}　" +
                I18n.Format(I18n.Tr("已加载 {n} 篇作品"), ("n", _posts.Count)) +
                (_hasMore ? I18n.Tr("（下拉加载更多）") : ""));
            _ = LoadThumbnailsAsync(generation);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    /// <summary>滚动到接近底部时自动加载下一页（与 DLsite 社团作品网格同一交互）。</summary>
    private void CardList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_level != "posts" || !_hasMore || _loadingMore)
            return;
        if (e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2)
            _ = LoadMorePostsAsync(_generation);
    }

    private void CardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardList.SelectedItem is not FanboxCardItem item)
            return;
        CardList.SelectedItem = null;
        if (item.Kind == "artist")
        {
            _ = OpenArtistAsync(item.Id, item.Title, item.TagText, fromArtists: true);
        }
        else if (item.Selectable)
        {
            // 点卡片本体等同于点「选择」（对齐 Web）
            item.IsPicked = !item.IsPicked;
            UpdateSelectInfo();
        }
    }

    // ---------- 选择与下载 ----------

    /// <summary>卡内「选择/已选」：翻转该卡片的选中态。</summary>
    private void PickButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;   // 不让点击冒泡成 ListBoxItem 选中，否则会被再翻一次
        if ((sender as FrameworkElement)?.DataContext is not FanboxCardItem card)
            return;
        card.IsPicked = !card.IsPicked;
        UpdateSelectInfo();
    }

    /// <summary>卡内「下载」：只入队这一篇。</summary>
    private void DownloadOne_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is FanboxCardItem card)
            _ = EnqueueAsync([card.Id]);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllPicked(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllPicked(false);

    private void SetAllPicked(bool picked)
    {
        foreach (var card in _cards.Where(c => c.Selectable))
            card.IsPicked = picked;
        UpdateSelectInfo();
    }

    private void UpdateSelectInfo()
    {
        var count = _cards.Count(c => c.Selectable && c.IsPicked);
        SelectInfo.Text = I18n.Format(I18n.Tr("已选 {n} 篇"), ("n", count));
        DownloadSelectedButton.IsEnabled = count > 0;
    }

    /// <summary>操作条「下载选中」：批量入队当前选中的作品。</summary>
    private void DownloadSelected_Click(object sender, RoutedEventArgs e) =>
        _ = EnqueueAsync(_cards.Where(c => c.Selectable && c.IsPicked).Select(c => c.Id).ToList());

    /// <summary>
    /// 选媒体库 → 入队 → 提示 → 把入队成功的作品就地转为"下载中"（对齐 Web：无需重新拉列表）。
    /// 操作条的批量下载与卡内单篇下载共用此流程。
    /// </summary>
    private async Task EnqueueAsync(IReadOnlyList<string> ids)
    {
        var wanted = ids.ToHashSet();
        var posts = _posts.Where(p => wanted.Contains(p.Id)).ToList();
        if (posts.Count == 0)
            return;

        // 有媒体库配置时先选下载目标（与 DLsite 搜索的入队流程一致）
        string? targetFolder = null, targetLib = null;
        if (AppConfig.ReadMediaLibs().Count > 0)
        {
            var dialog = new DownTargetDialog();
            if (!dialog.Show(Window.GetWindow(this)))
                return;
            targetFolder = dialog.SelectedFolder;
            targetLib = dialog.SelectedLib;
        }

        DownloadSelectedButton.IsEnabled = false;
        LoadingText.Text = I18n.Tr("正在加入下载队列…");
        LoadingOverlay.Visibility = Visibility.Visible;

        var artist = new PawchiveArtist
        {
            Id = _artistId, Service = PawchiveApi.FanboxService,
            Name = _artistName, PublicId = _publicId,
        };
        var result = await FanboxService.EnqueuePostsAsync(artist, posts, targetFolder, targetLib);
        LoadingOverlay.Visibility = Visibility.Collapsed;

        if (!result.Ok)
        {
            InAppDialog.Warn(this, result.Error ?? I18n.Tr("加入下载失败"), I18n.Tr("提示"));
            UpdateSelectInfo();
            return;
        }

        for (var i = 0; i < _cards.Count; i++)
        {
            if (!wanted.Contains(_cards[i].Id))
                continue;
            _cards[i] = new FanboxCardItem
            {
                Kind = _cards[i].Kind,
                Id = _cards[i].Id, Title = _cards[i].Title, ThumbUrl = _cards[i].ThumbUrl,
                MetaText = _cards[i].MetaText, State = "下载中", Selectable = false,
                Thumb = _cards[i].Thumb,
            };
        }
        UpdateSelectInfo();
        InAppDialog.Info(this,
            I18n.Format(I18n.Tr("已加入下载：{posts} 篇作品 / {files} 个文件"),
                ("posts", result.PostCount), ("files", result.FileCount)) +
            (result.Skipped > 0
                ? I18n.Format(I18n.Tr("，跳过 {n} 篇"), ("n", result.Skipped))
                : ""),
            I18n.Tr("提示"));
    }

    // ---------- 站上缩略图 ----------

    /// <summary>后台下载卡片缩略图（作家头像 / 作品封面），新一轮请求即作废旧加载。</summary>
    private async Task LoadThumbnailsAsync(int generation)
    {
        // pawchive 的防护网关拦浏览器 UA，取头像/缩略图同样要用中性 UA
        using var client = Http.CreateClient(TimeSpan.FromSeconds(20), PawchiveApi.UserAgent);
        foreach (var card in _cards.ToList())
        {
            if (generation != _generation)
                return;
            if (card.Thumb != null || card.ThumbUrl.Length == 0)
                continue;
            try
            {
                var bytes = await client.GetByteArrayAsync(card.ThumbUrl);
                if (generation != _generation)
                    return;
                var image = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    // 卡片宽随窗口在 150~310 之间浮动，解码宽取固定一档 400（同媒体库作品卡的封面档位）
                    image.DecodePixelWidth = 400;
                    image.StreamSource = ms;
                    image.EndInit();
                }
                image.Freeze();
                card.Thumb = image;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                          or NotSupportedException or ArgumentException)
            {
                // 单张缩略图失败不影响其它
            }
        }
    }
}
