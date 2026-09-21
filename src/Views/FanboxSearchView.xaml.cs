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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>作品详情图集里的一张图：站上预览图 + 落到临时目录的本地副本（供看图窗口翻页）。</summary>
public class FanboxImageItem : ObservableBase
{
    public string Name { get; init; } = "";
    public string ThumbUrl { get; init; } = "";
    /// <summary>原图直链；源站未归档原图时会 404，详情只展示预览图，下载入库才取原图。</summary>
    public string FullUrl { get; init; } = "";
    /// <summary>预览图在临时目录里的本地副本路径，空表示还没下下来。</summary>
    public string LocalPath { get; set; } = "";

    private BitmapImage? _thumb;
    public BitmapImage? Thumb { get => _thumb; set => Set(ref _thumb, value); }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value))
                return;
            Raise(nameof(SelBorderBrush));
            Raise(nameof(SelOpacity));
        }
    }

    /// <summary>当前大图对应的缩略图高亮描边（等价 Web 的 .thumbs img.sel）。</summary>
    public Brush SelBorderBrush => IsSelected
        ? Application.Current?.TryFindResource("AccentLightBrush") as Brush ?? Brushes.DodgerBlue
        : Brushes.Transparent;

    public double SelOpacity => IsSelected ? 1.0 : 0.65;
}

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
/// 层级（_level）：artists（作家结果）/ posts（作家主页）/ detail（单篇作品内容）/ watch（作家监控）。
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

    // 作品详情
    private readonly ObservableCollection<FanboxImageItem> _detailImages = [];
    private string _detailPostId = "";
    private string _detailState = "";
    private int _detailGen;     // 详情请求代际：与 _generation 分开，看详情不该作废主页的分页

    // 作家监控
    private bool _watched;                      // 当前作家是否已在监控中
    private string _watchReturnLevel = "artists";  // 进监控面板前所处的层级，返回时回到那里
    private bool _fillingWatchUi;               // 正在填充监控面板控件（抑制 SelectionChanged 回写）
    private System.Windows.Threading.DispatcherTimer? _watchTimer;
    private bool _watchTimerHooked;

    /// <summary>预览图的本地临时副本目录：看图窗口按路径翻页，故要先落盘。</summary>
    private static string PreviewCacheDir =>
        Path.Combine(Path.GetTempPath(), "R-18MediaLibrary", "fanbox-preview");

    private static readonly Regex ArtistUrlRe = new(@"/fanbox/user/(\d+)", RegexOptions.IgnoreCase);

    /// <summary>结果计数/提示文案变化（宿主显示在搜索框下方）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>「返回作家列表」按钮是否应该显示。</summary>
    public event Action<bool>? BackAvailabilityChanged;

    /// <summary>当前是否可返回上一层（宿主切回本来源时用它恢复返回按钮）。</summary>
    public bool CanGoBack =>
        _level is "watch" or "detail" || (_level == "posts" && _fromArtists);

    /// <summary>返回按钮文案：按当前层级切换（对齐 Web fbUpdateBackBtn）。</summary>
    public string BackLabel => _level switch
    {
        "watch" => I18n.Tr("← 返回搜索结果"),
        "detail" => I18n.Tr("← 返回作品列表"),
        _ => I18n.Tr("← 返回作家列表"),
    };

    /// <summary>宿主的返回按钮：按当前层级回上一层。</summary>
    public void GoBack()
    {
        if (_level == "watch")
            GoBackFromWatch();
        else if (_level == "detail")
            GoBackToPosts();
        else
            GoBackToArtists();
    }

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
        WatchCheckAllButton.Content = I18n.Tr("立即检查全部");
        WatchDefaultLabel.Text = I18n.Tr("默认间隔");
        UpdateWatchButton();
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
        CardList.Visibility = level is "artists" or "posts" ? Visibility.Visible : Visibility.Collapsed;
        DetailPane.Visibility = level == "detail" ? Visibility.Visible : Visibility.Collapsed;
        WatchPane.Visibility = level == "watch" ? Visibility.Visible : Visibility.Collapsed;
        // 操作条在滚动区之外（第 0 行），随层级与各自的面板同步显隐
        DetailBar.Visibility = DetailPane.Visibility;
        WatchBar.Visibility = WatchPane.Visibility;
        if (level != "watch")
            StopWatchTimer();
        BackAvailabilityChanged?.Invoke(CanGoBack);
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
        _watched = FanboxWatchService.IsWatched(artistId);
        UpdateWatchButton();
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
                // 作品本体常常只放在网盘上（站上只归档一张封面图）：这类作品照样可选可下
                var drive = GoogleDriveClient.LinksIn(p.Links).Count;
                _cards.Add(new FanboxCardItem
                {
                    Kind = "post",
                    Id = p.Id,
                    Title = p.Title.Length > 0 ? p.Title : p.Id,
                    ThumbUrl = p.CoverPath.Length > 0 ? PawchiveApi.ThumbUrl(p.CoverPath) : "",
                    TagText = drive > 0 ? I18n.Tr("☁ 网盘") : "",
                    MetaText = p.Files.Count > 0
                        ? I18n.Format(I18n.Tr("{n} 文件"), ("n", p.Files.Count)) : "",
                    State = state,
                    Selectable = state.Length == 0 && (p.Files.Count > 0 || drive > 0),
                });
            }
            SetStatus(PostCountText());
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
        else
        {
            // 点卡片本体进入作品详情查看内容（对齐 Web：选择仍由卡内「选择」按钮负责）。
            // 已下载/下载中的作品同样能看，只是不能再选。
            _ = OpenPostAsync(item);
        }
    }

    // ---------- 作品详情 ----------

    /// <summary>从详情返回作家主页：网格与已翻页数据都还在，恢复计数行即可。</summary>
    private void GoBackToPosts()
    {
        ShowLevel("posts");
        SetStatus(PostCountText());
    }

    /// <summary>作家主页的计数行文案（加载完与从详情返回时共用同一处）。</summary>
    private string PostCountText() =>
        $"{(_artistName.Length > 0 ? _artistName : _artistId)}　" +
        I18n.Format(I18n.Tr("已加载 {n} 篇作品"), ("n", _posts.Count)) +
        (_hasMore ? I18n.Tr("（下拉加载更多）") : "");

    /// <summary>点作品卡进入：拉取正文与附件清单并渲染。</summary>
    private async Task OpenPostAsync(FanboxCardItem card)
    {
        var generation = ++_detailGen;
        _detailPostId = card.Id;
        _detailState = card.State;
        ShowLevel("detail");
        SetStatus(card.Title);

        // 先清空上一篇的内容，免得新旧混显
        DetailTitle.Text = card.Title;
        DetailMainImage.Source = null;
        _detailImages.Clear();
        DetailThumbs.ItemsSource = _detailImages;
        DetailFields.Children.Clear();
        DetailOthers.ItemsSource = null;
        DetailLinks.Children.Clear();
        DetailBodyHeader.Visibility = DetailBody.Visibility = Visibility.Collapsed;
        DetailOthersHeader.Visibility = Visibility.Collapsed;
        DetailLinksHeader.Visibility = DetailLinksNote.Visibility = Visibility.Collapsed;
        DetailNoImage.Visibility = Visibility.Collapsed;
        DetailPane.ScrollToTop();
        LoadingOverlay.Visibility = Visibility.Visible;

        var post = await PawchiveApi.GetPostAsync(PawchiveApi.FanboxService, _artistId, card.Id);
        if (generation != _detailGen || _level != "detail")
            return;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        if (post is null)
        {
            DetailNoImage.Text = I18n.Tr("获取作品详情失败");
            DetailNoImage.Visibility = Visibility.Visible;
            return;
        }
        RenderDetail(post);
        _ = LoadDetailImagesAsync(generation);
    }

    /// <summary>渲染详情：标题 + 字段 + 正文 + 其他附件 + 图集占位（图片随后异步填充）。</summary>
    private void RenderDetail(PawchivePost post)
    {
        var done = _detailState == "已品悦";
        var busy = _detailState is "下载中" or "已下载";

        DetailTitle.Text = post.Title.Length > 0 ? post.Title : post.Id;
        DetailDownloadButton.Content = done ? I18n.Tr("已下载")
            : busy ? _detailState : I18n.Tr("下载本篇");
        DetailDownloadButton.IsEnabled = !done && !busy;

        var images = post.Files.Where(f => PawchiveApi.IsImageName(f.Name)).ToList();
        var others = post.Files.Where(f => !PawchiveApi.IsImageName(f.Name)).ToList();
        var links = GoogleDriveClient.LinksIn(post.Links);

        AddDetailField(I18n.Tr("作家"), _artistName.Length > 0 ? _artistName : _artistId);
        AddDetailField(I18n.Tr("发布"), FormatPublished(post.Published));
        if (post.Tags.Count > 0)
            AddDetailField(I18n.Tr("标签"), string.Join(" / ", post.Tags));
        AddDetailField(I18n.Tr("文件"),
            I18n.Format(I18n.Tr("{n} 个（图片 {m}）"), ("n", post.Files.Count), ("m", images.Count)) +
            (links.Count > 0 ? "　" + I18n.Format(I18n.Tr("网盘 {n} 条"), ("n", links.Count)) : ""));
        AddDetailField(I18n.Tr("状态"), done ? I18n.Tr("已下载")
            : _detailState.Length > 0 ? _detailState : I18n.Tr("未下载"));

        if (post.Content.Length > 0)
        {
            DetailBody.Text = post.Content;
            DetailBodyHeader.Text = I18n.Tr("正文");
            DetailBodyHeader.Visibility = DetailBody.Visibility = Visibility.Visible;
        }
        if (others.Count > 0)
        {
            DetailOthers.ItemsSource = others.Select(f => f.Name).ToList();
            DetailOthersHeader.Text = I18n.Format(I18n.Tr("其他附件（{n}）"), ("n", others.Count));
            DetailOthersHeader.Visibility = Visibility.Visible;
        }
        RenderDriveLinks(links);

        foreach (var f in images)
            _detailImages.Add(new FanboxImageItem
            {
                Name = f.Name,
                ThumbUrl = PawchiveApi.ThumbUrl(f.Path),
                FullUrl = PawchiveApi.FileUrl(f.Path, f.Name),
            });
        if (images.Count == 0)
        {
            DetailNoImage.Text = I18n.Tr("这篇没有图片");
            DetailNoImage.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 渲染「网盘链接」区块（对齐 Web 的同名清单）：一行一条，可点开浏览器看原页面。
    ///
    /// 作者把作品本体（多为压缩包）放在谷歌网盘、站上只归档一张封面图时才有内容；
    /// 下载本篇会把这些文件一并入队（共享文件夹先展开成逐个文件），下完按设置自动解压。
    /// </summary>
    private void RenderDriveLinks(List<DriveLink> links)
    {
        DetailLinks.Children.Clear();
        if (links.Count == 0)
        {
            DetailLinksHeader.Visibility = DetailLinksNote.Visibility = Visibility.Collapsed;
            return;
        }
        DetailLinksHeader.Text = I18n.Format(I18n.Tr("网盘链接（{n}）"), ("n", links.Count));
        DetailLinksHeader.Visibility = Visibility.Visible;
        foreach (var link in links)
        {
            var url = link.Url;
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            text.Inlines.Add(new System.Windows.Documents.Run(
                link.Kind == DriveLinkKind.Folder ? "📁 " : "📦 "));
            var hyperlink = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(url))
            {
                Foreground = (Brush)FindResource("AccentLightBrush"),
                TextDecorations = null,
            };
            hyperlink.Click += (_, _) => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            text.Inlines.Add(hyperlink);
            DetailLinks.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x22)),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Child = text,
            });
        }
        DetailLinksNote.Text = I18n.Tr("下载本篇时会一并下载网盘里的文件，完成后按设置自动解压");
        DetailLinksNote.Visibility = Visibility.Visible;
    }

    private void AddDetailField(string key, string value)
    {
        if (value.Length == 0)
            return;
        DetailFields.Children.Add(new TextBlock
        {
            Text = key,
            Margin = new Thickness(0, 0, 0, 2),
            Style = TryFindResource("CaptionText") as Style,
        });
        DetailFields.Children.Add(new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            Margin = new Thickness(0, 0, 0, 12),
        });
    }

    /// <summary>站点给的是 2026-01-23T08:00:00 这种，去掉中间的 T。</summary>
    private static string FormatPublished(string raw) =>
        raw.Length == 0 ? "" : raw.Replace('T', ' ');

    /// <summary>
    /// 后台下载图集的预览图。
    ///
    /// 详情只展示 800px 预览图（img.&lt;host&gt;），不取原图：原图动辄数 MB，而且源站未归档时
    /// 一律 404（见 DownloadEngine 的预览图回退）。要原图请下载入库。
    /// 预览图同时在临时目录存一份本地副本，供看图窗口按路径翻页。
    /// </summary>
    private async Task LoadDetailImagesAsync(int generation)
    {
        var dir = Path.Combine(PreviewCacheDir, _detailPostId);
        try { Directory.CreateDirectory(dir); }
        catch (IOException) { dir = ""; }
        catch (UnauthorizedAccessException) { dir = ""; }

        using var client = Http.CreateClient(TimeSpan.FromSeconds(20), PawchiveApi.UserAgent);
        for (var i = 0; i < _detailImages.Count; i++)
        {
            if (generation != _detailGen)
                return;
            var item = _detailImages[i];
            try
            {
                var bytes = await client.GetByteArrayAsync(item.ThumbUrl);
                if (generation != _detailGen)
                    return;
                if (dir.Length > 0)
                {
                    // 站点预览图一律重编码为 jpeg（URL 后缀仍是原扩展名），本地副本按序号命名
                    var local = Path.Combine(dir, (i + 1).ToString("D3") + ".jpg");
                    try
                    {
                        await File.WriteAllBytesAsync(local, bytes);
                        item.LocalPath = local;
                    }
                    catch (IOException) { }
                }
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
                if (i == 0)
                    SelectDetailImage(item);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                          or NotSupportedException or ArgumentException)
            {
                // 单张失败不影响其它
            }
        }
    }

    /// <summary>把某张图设为大图，并把缩略图条的高亮挪过去。</summary>
    private void SelectDetailImage(FanboxImageItem item)
    {
        foreach (var x in _detailImages)
            x.IsSelected = ReferenceEquals(x, item);
        DetailMainImage.Source = item.Thumb;
    }

    private void DetailThumb_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FanboxImageItem item)
            SelectDetailImage(item);
    }

    /// <summary>点大图：用程序内看图窗口翻看这篇的全部预览图（对齐 Web 的灯箱）。</summary>
    private void DetailMainImage_Click(object sender, MouseButtonEventArgs e)
    {
        var paths = _detailImages.Where(x => x.LocalPath.Length > 0).Select(x => x.LocalPath).ToList();
        if (paths.Count == 0)
            return;
        var current = _detailImages.FirstOrDefault(x => x.IsSelected);
        var index = current is null ? 0 : Math.Max(0, paths.IndexOf(current.LocalPath));
        new ImageViewerDialog(paths, index) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    /// <summary>详情页「下载本篇」。</summary>
    private void DetailDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_detailPostId.Length > 0)
            _ = EnqueueAsync([_detailPostId]);
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
        // 正停在被入队作品的详情页时，「下载本篇」同步转为禁用的状态按钮
        if (_level == "detail" && wanted.Contains(_detailPostId))
        {
            _detailState = "下载中";
            DetailDownloadButton.Content = _detailState;
            DetailDownloadButton.IsEnabled = false;
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

    // ---------- 作家监控（有新作品自动下载） ----------
    //
    // 对齐 Web 端 fanbox.js 的 fbWatchPane：总开关 + 立即检查全部 + 默认间隔，
    // 下面一位作家一张卡（间隔下拉 / 立即检查 / 暂停 / 打开主页 / 删除）。
    // 数据与轮询都在 FanboxWatchService，本视图只负责显示与操作。

    /// <summary>宿主工具栏的「⏱ 监控」：进入监控面板（记住来时的层级，返回时回到那里）。</summary>
    public void OpenWatchList()
    {
        if (_level != "watch")
            _watchReturnLevel = _level;
        ShowLevel("watch");
        SetStatus("");
        WatchPane.ScrollToTop();
        RefreshWatchList();
    }

    private void GoBackFromWatch()
    {
        var back = _watchReturnLevel == "watch" ? "artists" : _watchReturnLevel;
        ShowLevel(back);
        SetStatus(back switch
        {
            "posts" => PostCountText(),
            "detail" => DetailTitle.Text,
            _ => _artistCards.Count > 0
                ? I18n.Format(I18n.Tr("搜索到 {n} 位作家"), ("n", _artistCards.Count))
                : "",
        });
    }

    /// <summary>重新拉取监控列表并重建卡片（检查进行中时由 _watchTimer 每 1.5 秒调一次）。</summary>
    private void RefreshWatchList()
    {
        var watches = FanboxWatchService.All();
        var (busy, status) = FanboxWatchService.State();

        _fillingWatchUi = true;
        WatchSwitchButton.Content = AppConfig.FanboxWatchEnabled
            ? I18n.Tr("自动监控：开") : I18n.Tr("自动监控：关");
        ApplyToggleState(WatchSwitchButton, AppConfig.FanboxWatchEnabled);
        WatchSwitchButton.ToolTip = I18n.Tr("关闭后仅停止后台自动轮询，「立即检查」仍可用");
        WatchCheckAllButton.IsEnabled = watches.Count > 0;
        FillIntervalBox(WatchDefaultIntervalBox, AppConfig.FanboxWatchInterval);
        _fillingWatchUi = false;

        SetStatus(FanboxWatchService.IdleSummary());
        WatchStatusText.Text = busy
            ? "⏳ " + (status.Length > 0 ? status : I18n.Tr("正在检查…")) : "";
        WatchStatusText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        WatchList.Children.Clear();
        foreach (var w in watches)
            WatchList.Children.Add(BuildWatchCard(w));

        WatchEmptyText.Text = I18n.Tr("还没有监控任何作家。搜到作家后进入作家主页，点「+ 监控作家」即可添加。");
        WatchEmptyText.Visibility = watches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WatchNoteText.Text = I18n.Tr(
            "监控在本程序运行期间后台轮询，发现新作品即按该作家设定的媒体库自动加入下载队列；程序关闭时暂停，下次启动继续。");
        WatchNoteText.Visibility = watches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (busy)
            StartWatchTimer();
        else
            StopWatchTimer();
    }

    /// <summary>一位作家一张卡（版式对齐 Web 的 .libcard：标题行 + 间隔行 + 上次结果）。</summary>
    private Border BuildWatchCard(FanboxWatch w)
    {
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++)
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = w.Display, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        };
        head.Children.Add(name);

        var sub = new List<string>();
        if (!w.Enabled)
            sub.Add(I18n.Tr("已暂停"));
        sub.Add(w.LastCheck.Length > 0
            ? I18n.Format(I18n.Tr("上次检查 {time}"), ("time", w.LastCheck))
            : I18n.Tr("尚未检查过"));
        if (FanboxWatchService.NextCheckText(w) is { Length: > 0 } next)
            sub.Add(I18n.Format(I18n.Tr("下次 {time}"), ("time", next)));
        if (w.Downloaded > 0)
            sub.Add(I18n.Format(I18n.Tr("累计自动下载 {n} 篇"), ("n", w.Downloaded)));
        var meta = new TextBlock
        {
            Text = string.Join(" · ", sub),
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = TryFindResource("CaptionText") as Style,
        };
        Grid.SetColumn(meta, 1);
        head.Children.Add(meta);

        AddWatchAction(head, 2, I18n.Tr("立即检查"), () =>
        {
            _ = FanboxWatchService.CheckNowAsync(w.ArtistId);
            StartWatchTimer();
        });
        AddWatchAction(head, 3, w.Enabled ? I18n.Tr("暂停") : I18n.Tr("启用"), () =>
        {
            FanboxWatchService.SetEnabled(w.ArtistId, !w.Enabled);
            RefreshWatchList();
        });
        AddWatchAction(head, 4, I18n.Tr("打开主页"), () =>
        {
            _watchReturnLevel = "artists";
            _ = OpenArtistAsync(w.ArtistId, w.ArtistName, "", fromArtists: false);
        });
        AddWatchAction(head, 5, I18n.Tr("删除"), () =>
        {
            if (!InAppDialog.Confirm(this,
                    I18n.Format(I18n.Tr("不再监控「{name}」？已经下载的作品不受影响。"), ("name", w.Display)),
                    I18n.Tr("确认")))
                return;
            FanboxWatchService.Remove(w.ArtistId);
            if (_artistId == w.ArtistId)
            {
                _watched = false;
                UpdateWatchButton();
            }
            RefreshWatchList();
        }, danger: true);

        // 间隔行：轮询间隔下拉 + 自动下载去向
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = I18n.Tr("轮询间隔"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0), Style = TryFindResource("CaptionText") as Style,
        });
        var box = new ComboBox { Width = 110, VerticalContentAlignment = VerticalAlignment.Center };
        FillIntervalBox(box, w.IntervalMin);
        box.SelectionChanged += (_, _) =>
        {
            if (_fillingWatchUi || box.SelectedItem is not ComboBoxItem { Tag: int minutes })
                return;
            if (minutes == w.IntervalMin)
                return;
            FanboxWatchService.SetInterval(w.ArtistId, minutes);
            RefreshWatchList();
        };
        row.Children.Add(box);
        row.Children.Add(new TextBlock
        {
            Text = w.TargetLib.Length > 0
                ? I18n.Format(I18n.Tr("自动下载到媒体库「{lib}」"), ("lib", w.TargetLib))
                : I18n.Tr("自动下载到缓存目录"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = TryFindResource("CaptionText") as Style,
        });

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(row);
        if (w.LastResult.Length > 0)
            body.Children.Add(new TextBlock
            {
                Text = I18n.Format(I18n.Tr("上次结果：{text}"), ("text", w.LastResult)),
                Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
                Style = TryFindResource("CaptionText") as Style,
            });

        return new Border
        {
            Style = TryFindResource("Card") as Style,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = body,
        };
    }

    private void AddWatchAction(Grid host, int column, string text, Action onClick, bool danger = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 76,
            Margin = new Thickness(8, 0, 0, 0),
            Style = danger ? TryFindResource("DangerButton") as Style : null,
        };
        button.Click += (_, _) => onClick();
        Grid.SetColumn(button, column);
        host.Children.Add(button);
    }

    /// <summary>把间隔档位填进下拉并选中当前值；库里存着非档位值时补一档，免得下拉把它悄悄改掉。</summary>
    private void FillIntervalBox(ComboBox box, int minutes)
    {
        var previous = _fillingWatchUi;
        _fillingWatchUi = true;
        box.Items.Clear();
        var choices = FanboxWatchService.IntervalChoices.ToList();
        if (!choices.Contains(minutes))
            choices.Insert(0, minutes);
        foreach (var value in choices)
        {
            var item = new ComboBoxItem { Content = IntervalLabel(value), Tag = value };
            box.Items.Add(item);
            if (value == minutes)
                box.SelectedItem = item;
        }
        _fillingWatchUi = previous;
    }

    private static string IntervalLabel(int minutes) => minutes switch
    {
        < 60 => I18n.Format(I18n.Tr("{n} 分钟"), ("n", minutes)),
        < 1440 when minutes % 60 == 0 => I18n.Format(I18n.Tr("{n} 小时"), ("n", minutes / 60)),
        _ when minutes % 1440 == 0 => I18n.Format(I18n.Tr("{n} 天"), ("n", minutes / 1440)),
        _ => I18n.Format(I18n.Tr("{n} 分钟"), ("n", minutes)),
    };

    /// <summary>切换按钮的激活态外观：on=强调色底+白字（等价 Web 的 .icon-btn.on，同媒体库的 ★/♥ 切换）。</summary>
    private void ApplyToggleState(Button btn, bool on)
    {
        btn.Background = TryFindResource(on ? "AccentBrush" : "ButtonBrush") as Brush
                         ?? (on ? Brushes.DodgerBlue : Brushes.DimGray);
        btn.Foreground = on
            ? Brushes.White
            : TryFindResource("TextBrush") as Brush ?? Brushes.White;
    }

    private void StartWatchTimer()
    {
        _watchTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        if (!_watchTimerHooked)
        {
            _watchTimer.Tick += (_, _) =>
            {
                if (_level != "watch")
                {
                    StopWatchTimer();
                    return;
                }
                var (busy, status) = FanboxWatchService.State();
                WatchStatusText.Text = busy
                    ? "⏳ " + (status.Length > 0 ? status : I18n.Tr("正在检查…")) : "";
                WatchStatusText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                // 检查结束才整表重绘：每 1.5 秒重建一次会把用户正在操作的下拉框踢掉
                if (!busy)
                {
                    StopWatchTimer();
                    RefreshWatchList();
                }
            };
            _watchTimerHooked = true;
        }
        _watchTimer.Start();
    }

    private void StopWatchTimer() => _watchTimer?.Stop();

    // ---------- 监控面板的操作 ----------

    private void WatchSwitch_Click(object sender, RoutedEventArgs e)
    {
        AppConfig.FanboxWatchEnabled = !AppConfig.FanboxWatchEnabled;
        if (AppConfig.FanboxWatchEnabled)
            FanboxWatchService.Kick();
        RefreshWatchList();
    }

    private void WatchCheckAll_Click(object sender, RoutedEventArgs e)
    {
        _ = FanboxWatchService.CheckAllAsync();
        StartWatchTimer();
    }

    private void WatchDefaultInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingWatchUi || WatchDefaultIntervalBox.SelectedItem is not ComboBoxItem { Tag: int minutes })
            return;
        AppConfig.FanboxWatchInterval = minutes;
    }

    // ---------- 作家主页的监控开关 ----------

    private void UpdateWatchButton()
    {
        WatchToggleButton.Content = _watched ? I18n.Tr("✓ 已监控") : I18n.Tr("+ 监控作家");
        ApplyToggleState(WatchToggleButton, _watched);
        WatchToggleButton.ToolTip = _watched
            ? I18n.Tr("点击取消监控这位作家")
            : I18n.Tr("添加后按设定的间隔轮询，这位作家发新作品时自动下载");
    }

    private void WatchToggle_Click(object sender, RoutedEventArgs e) => _ = ToggleWatchAsync();

    private async Task ToggleWatchAsync()
    {
        if (_artistId.Length == 0)
            return;
        var who = _artistName.Length > 0 ? _artistName : _artistId;

        if (_watched)
        {
            if (!InAppDialog.Confirm(this,
                    I18n.Format(I18n.Tr("不再监控「{name}」？已经下载的作品不受影响。"), ("name", who)),
                    I18n.Tr("确认")))
                return;
            FanboxWatchService.Remove(_artistId);
            _watched = false;
            UpdateWatchButton();
            return;
        }

        // 自动下载要有个落点：和手动下载一样先选媒体库，之后每次自动入队都往这里放
        string? targetFolder = null, targetLib = null;
        if (AppConfig.ReadMediaLibs().Count > 0)
        {
            var dialog = new DownTargetDialog();
            if (!dialog.Show(Window.GetWindow(this)))
                return;
            targetFolder = dialog.SelectedFolder;
            targetLib = dialog.SelectedLib;
        }

        // 现有作品下不下。Web 端是两选一的 confirm（取消即「只要新作品」），
        // 桌面端多一个 Esc 关闭态，按本程序的一贯约定当作取消整个操作。
        var choice = InAppDialog.Choose(this,
            I18n.Format(I18n.Tr("监控「{name}」。这位作家站上已有的作品要一并下载吗？"), ("name", who)) + "\n\n" +
            I18n.Tr("「全部下载」＝把现有作品也加入下载队列（数量可能很多）") + "\n" +
            I18n.Tr("「只要新作品」＝从现在起，只下载今后新发布的作品"),
            I18n.Tr("添加监控"),
            [I18n.Tr("全部下载"), I18n.Tr("只要新作品")], out _);
        if (choice < 0)
            return;

        WatchToggleButton.IsEnabled = false;
        LoadingText.Text = I18n.Tr("正在添加监控…");
        LoadingOverlay.Visibility = Visibility.Visible;
        var artist = new PawchiveArtist
        {
            Id = _artistId, Service = PawchiveApi.FanboxService,
            Name = _artistName, PublicId = _publicId,
        };
        var (ok, message) = await FanboxWatchService.AddAsync(
            artist, intervalMin: 0, includeExisting: choice == 0, targetLib, targetFolder);
        LoadingOverlay.Visibility = Visibility.Collapsed;
        WatchToggleButton.IsEnabled = true;

        if (!ok)
        {
            InAppDialog.Warn(this, message, I18n.Tr("提示"));
            return;
        }
        _watched = true;
        UpdateWatchButton();
        InAppDialog.Info(this, message, I18n.Tr("提示"));
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
