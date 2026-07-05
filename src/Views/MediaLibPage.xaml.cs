using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DASD.Core;
using DASD.Services;

namespace DASD.Views;

/// <summary>媒体库页的根视图模式。</summary>
public enum MediaLibRoot
{
    /// <summary>媒体库页：媒体库 → 社团 → 作品。</summary>
    Library,
    /// <summary>标签页：标签 → 社团 → 作品。</summary>
    Genre,
    /// <summary>作品形式页：作品形式 → 社团 → 作品。</summary>
    WorkType,
    /// <summary>收藏夹页：收藏的作品平铺 → 作品详情。</summary>
    Favorite,
}

/// <summary>
/// 媒体库页（对应 Python 版 media_lib_UI.py）：媒体库卡片 → 社团卡片 → 作品卡片 → 作品详情 四级浏览。
/// root=Genre 时作为"标签页"使用（标签 → 社团 → 作品）；root=WorkType 时作为"作品形式页"使用（作品形式 → 社团 → 作品）。
/// </summary>
public partial class MediaLibPage : UserControl
{
    private const int WorksPageSize = 30;   // 作品卡片懒加载：每批数量
    private const string UnknownMaker = ""; // maker_name 为空的作品归到"未知社团"
    private const double WorkCardW = 210, WorkCardH = 248;   // WorkCardW 作为卡片最小宽度
    private const double WorkCoverW = 186, WorkCoverH = 140;
    private const double CardGap = 10;                       // 卡片右/下外边距
    private const double CoverWidthDelta = WorkCardW - WorkCoverW; // 封面宽 = 卡片宽 - 内边距
    private double _cardWidth = WorkCardW;                   // 动态卡片宽度：按视口宽度撑满整行，消除右侧留白

    private static readonly string[] VideoExts =
        [".mp4", ".mkv", ".avi", ".wmv", ".mov", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg"];
    private static readonly string[] AudioExts =
        [".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".opus"];

    private static readonly Dictionary<string, string> AgeMap = new()
    {
        ["1"] = "全年龄", ["2"] = "R-15", ["3"] = "R-18",
    };

    // label → (db列名, 是否多值用 " / " 分割)
    private static readonly Dictionary<string, (string Col, bool Multi)> LinkCols = new()
    {
        ["社团"] = ("maker_name", false),
        ["系列"] = ("series", false),
        ["剧本"] = ("scenario", false),
        ["插画"] = ("illust", false),
        ["声优"] = ("voice_actor", true),
    };

    private readonly MediaLibRoot _root;
    private readonly WrapPanel _cardsPanel = new();

    private string _level = "libs";  // libs / makers / works / detail / genres / types / filtered_works
    private string? _currentLib;
    private string? _currentMaker;   // null=未选社团；UnknownMaker=未知社团
    private string? _currentGenre;
    private string? _currentType;    // 当前选中的作品形式（work_type）
    private string? _currentWork;
    private string? _currentWorkFolder;
    private bool _currentRead;   // 当前详情作品的"已读"状态
    private bool _currentFav;    // 当前详情作品的"收藏"状态
    private string? _filterCol;
    private string? _filterVal;
    private int _totalWorks;
    private double _worksScrollPos;
    private MediaLibSettingDialog? _settingDialog;

    // 搜索作用域：分组层级（媒体库首页/社团页）输入关键字时改为在当前作用域内按 RJ号/作品名 搜索作品
    private bool _searchMode;        // 当前是否处于作品搜索结果视图
    private bool _detailFromSearch;  // 当前详情是否由搜索结果打开（返回时回到搜索结果）
    private string _searchLevel = "libs";  // 进入详情前所处的搜索层级（libs/makers）
    private string _detailReturnLevel = "works";  // 进入详情前的作品列表层级（返回时复原）

    // 所有卡片层级统一的懒加载来源：每项携带一个延迟构建器与搜索过滤键
    private sealed record GridCard(Func<Border> Build, string FilterKey);
    private List<GridCard> _gridCards = [];   // 当前层级全部卡片
    private List<GridCard> _shownCards = [];  // 搜索过滤后的卡片
    private int _loadedCards;                 // 已渲染数量
    private bool _workLevelCount;             // 计数文案样式：true=作品卡，false=分组卡
    private string _countUnit = "";           // 分组卡层级的单位文案（个社团/个媒体库/…）

    // 排序状态：社团页按作品数，作品页按发售日（详见 MakerSortOptions / WorkSortOptions）
    private int _makerSort;   // 0=作品数↓ 1=作品数↑ 2=社团名
    private int _workSort;    // 0=发售日↓ 1=发售日↑ 2=RJ号↓ 3=RJ号↑
    private bool _suppressSort;  // 重填 SortBox 时抑制 SelectionChanged

    // 文件树作为独立页面显示，记录其所属作品文件夹（用于语言切换/重新可见时重建）
    private string? _treeFolder;
    private bool _treeThumbView = true;   // 查看作品默认采用缩略图视图（false=文件列表）

    // 整页预览状态
    private InlineMediaPlayer? _inlinePlayer;
    private List<string> _previewImages = [];
    private List<string> _previewVideos = [];   // 视频整页预览的播放队列（同文件夹视频，按名称排序）
    private bool _previewVideoMode;              // 当前整页预览是否为视频（决定 StepPreview 切换图片还是视频）
    private int _previewIndex;

    private static readonly Brush RowHoverBrush = MakeFrozenBrush(Color.FromArgb(18, 255, 255, 255));

    private static Brush MakeFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // 底部音频播放条：代码动态加入 RootGrid 第 2 行（在文件树中就地播放 wav/mp3 等）
    private readonly AudioPlayerBar AudioBar = new() { Margin = new Thickness(0, 10, 0, 0) };

    // 搜索输入防抖：分组层级的作用域搜索会查库，逐字触发既卡 UI 又打库，
    // 停顿 250ms 后再执行一次过滤（作品/标签/形式层级则是纯内存过滤）。
    private readonly DispatcherTimer _searchDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MediaLibPage(MediaLibRoot root)
    {
        InitializeComponent();
        Grid.SetRow(AudioBar, 2);
        RootGrid.Children.Add(AudioBar);
        _root = root;
        // 各根视图的初始层级；媒体库设置按钮仅在媒体库首页显示（由 ClearCards/ShowLibs 控制）
        _level = root switch
        {
            MediaLibRoot.Genre => "genres",
            MediaLibRoot.WorkType => "types",
            MediaLibRoot.Favorite => "favorites",
            _ => "libs",
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyCardFilter();
        };
        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    private void RetranslateUi()
    {
        BackButton.Content = I18n.Tr("← 返回");
        LibSettingButton.Content = I18n.Tr("媒体库设置");
        OpenFolderButton.Content = I18n.Tr("打开文件夹");
        ViewFilesButton.Content = I18n.Tr("查看作品");
        MoveLibButton.Content = I18n.Tr("移动媒体库");
        SearchBox.ToolTip = I18n.Tr("搜索 RJ号 / 作品名");
        PreviewCloseButton.Content = I18n.Tr("← 返回");
        PreviewPrevButton.Content = "‹ " + I18n.Tr("上一张");
        PreviewNextButton.Content = I18n.Tr("下一张") + " ›";
        Refresh();
    }

    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
        {
            AudioBar.Stop();  // 离开本页时停止音频播放
            return;
        }
        // 切换到本页时重新加载数据：先丢弃配置缓存，再重查数据库刷新当前视图
        AppConfig.Reload();
        Refresh();
    }

    // ---------- 导航 ----------

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_level)
        {
            case "filetree":
                ShowDetail();   // 文件树页返回作品详情
                break;
            case "filtered_works":
                ShowDetail();
                break;
            case "detail":
                _currentWork = null;
                if (_detailFromSearch)
                {
                    // 由搜索结果进入：恢复搜索层级并按搜索框内容重跑作用域搜索
                    _detailFromSearch = false;
                    _level = _searchLevel;
                    ApplyCardFilter();
                }
                else
                {
                    // 返回进入详情前的作品列表层级
                    switch (_detailReturnLevel)
                    {
                        case "lib_works": ShowLibWorks(); break;
                        case "favorites": ShowFavorites(); break;
                        case "filtered_works": ShowFilteredWorks(); break;
                        default: ShowWorks(); break;
                    }
                }
                RestoreWorksScroll();
                break;
            case "lib_works":
                ShowLibs();
                break;
            case "works":
                _currentMaker = null;
                ShowMakers();
                break;
            case "makers":
                if (_currentGenre != null)
                {
                    _currentGenre = null;
                    ShowGenres();
                }
                else if (_currentType != null)
                {
                    _currentType = null;
                    ShowTypes();
                }
                else
                {
                    ShowLibs();
                }
                break;
            case "genres" when _root != MediaLibRoot.Genre:
                ShowLibs();
                break;
            case "types" when _root != MediaLibRoot.WorkType:
                ShowLibs();
                break;
        }
    }

    private void ReadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWork == null)
            return;
        _currentRead = !_currentRead;
        Db.Execute("UPDATE \"works\" SET \"read_flag\" = @v WHERE \"work_id\" = @w",
            ("@v", _currentRead ? "1" : null), ("@w", _currentWork));
        UpdateReadFavButtons();
    }

    private void FavButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWork == null)
            return;
        _currentFav = !_currentFav;
        Db.Execute("UPDATE \"works\" SET \"favorite\" = @v WHERE \"work_id\" = @w",
            ("@v", _currentFav ? "1" : null), ("@w", _currentWork));
        UpdateReadFavButtons();
    }

    private void UpdateReadFavButtons()
    {
        // 收藏/已读状态用 emoji 表示：已读 ⭐ / 未读 ☆（白色的星）；已收藏 ❤️ / 未收藏 🤍
        ReadButton.Content = (_currentRead ? "⭐ " : "☆ ") + I18n.Tr("已读");
        FavButton.Content = (_currentFav ? "❤️ " : "🤍 ") + I18n.Tr("收藏");
    }

    private void LibSettingButton_Click(object sender, RoutedEventArgs e)
    {
        // 媒体库设置（程序内覆盖层，常驻实例，扫描线程随其存活）
        if (_settingDialog == null)
        {
            _settingDialog = new MediaLibSettingDialog();
            _settingDialog.LibsChanged += Refresh;
            _settingDialog.ScanDone += Refresh;
        }
        _settingDialog.Show(this);  // 程序内模态覆盖层，阻塞到关闭
    }

    private void LibToggleButton_Click(object sender, RoutedEventArgs e)
    {
        // 在"显示作品"（媒体库全部作品）与"显示社团"（社团卡片）之间切换
        if (_level == "lib_works")
            ShowMakers();
        else
            ShowLibWorks();
    }

    /// <summary>仅进入某个媒体库（非标签/形式流程）时显示"显示作品/显示社团"切换按钮。</summary>
    private void UpdateLibToggleButton()
    {
        var inLib = _currentLib != null && _currentGenre == null && _currentType == null
                    && _level is "lib_works" or "makers";
        LibToggleButton.Visibility = inLib ? Visibility.Visible : Visibility.Collapsed;
        if (inLib)
            LibToggleButton.Content = _level == "makers" ? I18n.Tr("显示作品") : I18n.Tr("显示社团");
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWorkFolder != null && Directory.Exists(_currentWorkFolder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_currentWorkFolder}\""));
    }

    private void MoveLibButton_Click(object sender, RoutedEventArgs e)
    {
        // 把当前作品移动到另一个媒体库：移动文件夹 + 改库 + 同步元数据
        var workId = _currentWork;
        if (workId == null)
            return;
        if (_currentWorkFolder == null || !Directory.Exists(_currentWorkFolder))
        {
            InAppDialog.Warn(this, I18n.Tr("作品文件夹不存在，无法移动"), I18n.Tr("移动媒体库"));
            return;
        }
        var currentLib = Db.Scalar(
            "SELECT \"library\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string;

        var dialog = new DownTargetDialog(currentLib, moveMode: true);
        if (!dialog.Show(this) || dialog.SelectedFolder == null)
            return;
        var targetLib = dialog.SelectedLib!;
        var targetFolder = dialog.SelectedFolder;
        if (!InAppDialog.Confirm(this,
                I18n.Format(I18n.Tr("确定将 {id} 移动到媒体库“{lib}”吗？"), ("id", workId), ("lib", targetLib)),
                I18n.Tr("移动媒体库")))
            return;

        MoveLibButton.IsEnabled = false;
        MoveLibButton.Content = I18n.Tr("移动中…");
        _ = MoveWorkAsync(workId, targetLib, targetFolder);
    }

    private async Task MoveWorkAsync(string workId, string targetLib, string targetFolder)
    {
        var (ok, message) = await Task.Run(() =>
            MediaLibraryService.MoveWorkToLibraryAsync(workId, targetLib, targetFolder));
        MoveLibButton.IsEnabled = true;
        MoveLibButton.Content = I18n.Tr("移动媒体库");
        if (ok)
        {
            InAppDialog.Info(this, I18n.Tr("已移动到新媒体库"), I18n.Tr("移动媒体库"));
            Refresh();  // 重新载入详情，反映新的媒体库与文件夹
        }
        else
        {
            InAppDialog.Warn(this, message, I18n.Tr("移动媒体库"));
        }
    }

    private void ViewFilesButton_Click(object sender, RoutedEventArgs e)
    {
        // "查看作品"：从详情页再跳转到独立的文件树页面（等同卡片跳详情的二次页面跳转）
        if (_currentWorkFolder == null)
            return;
        _treeFolder = _currentWorkFolder;
        ShowFileTree();
    }

    public void Refresh()
    {
        // 重新加载当前层级的视图
        var names = AppConfig.ReadMediaLibs().Select(l => l.Name).ToHashSet();
        if (_currentLib != null && !names.Contains(_currentLib))
            ShowRoot();  // 当前库已被删除/改名，回到根视图
        else if (_level == "filetree" && _treeFolder != null && Directory.Exists(_treeFolder))
            ShowFileTree();
        else if (_level == "filtered_works" && _filterCol != null)
            ShowFilteredWorks();
        else if (_level == "detail" && _currentWork != null)
            ShowDetail();
        else if (_level == "favorites")
            ShowFavorites();
        else if (_level == "lib_works" && _currentLib != null)
            ShowLibWorks();
        else if (_level is "works" && _currentMaker != null)
            ShowWorks();
        else if (_level == "makers" && (_currentLib != null || _currentGenre != null || _currentType != null))
            ShowMakers();
        else if (_level == "genres")
            ShowGenres();
        else if (_level == "types")
            ShowTypes();
        else
            ShowRoot();
    }

    private void ShowRoot()
    {
        switch (_root)
        {
            case MediaLibRoot.Genre:
                ShowGenres();
                break;
            case MediaLibRoot.WorkType:
                ShowTypes();
                break;
            case MediaLibRoot.Favorite:
                ShowFavorites();
                break;
            default:
                ShowLibs();
                break;
        }
    }

    // ---------- 视图构建 ----------

    private void ClearCards()
    {
        _cardWidth = ComputeCardWidth();  // 重建前按当前视口宽度算好动态卡片宽度
        _cardsPanel.Children.Clear();
        ContentHost.Content = _cardsPanel;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        ViewModeBar.Visibility = Visibility.Collapsed;   // 仅文件树页显示视图切换
        ViewFilesButton.Visibility = Visibility.Collapsed;
        ViewFilesButton.Content = I18n.Tr("查看作品");
        MoveLibButton.Visibility = Visibility.Collapsed;
        ReadButton.Visibility = Visibility.Collapsed;
        FavButton.Visibility = Visibility.Collapsed;
        LibSettingButton.Visibility = Visibility.Collapsed;  // 仅媒体库首页显示，由 ApplyCardFilter 重新打开
        LibToggleButton.Visibility = Visibility.Collapsed;   // 仅媒体库内作品/社团视图显示
        SortBox.Visibility = Visibility.Collapsed;            // 仅社团页/作品页显示，由 ApplyCardFilter 重新打开
        SearchBox.Visibility = Visibility.Visible;           // 默认显示搜索框，详情页由 ShowDetail 隐藏
        RjLabel.Visibility = Visibility.Collapsed;           // RJ 号仅详情页显示
        RjLabel.Inlines.Clear();
        _currentWorkFolder = null;
        ClosePreview();
    }

    private Border MakeClickCard(string title, string caption, Action onClick)
    {
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Width = _cardWidth,
            Height = 110,
            Margin = new Thickness(0, 0, CardGap, CardGap),
            Cursor = Cursors.Hand,
            Tag = title.ToLowerInvariant(),  // 搜索过滤用
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = caption, Style = (Style)FindResource("CaptionText"), Margin = new Thickness(0, 6, 0, 0),
        });
        card.Child = panel;
        card.MouseLeftButtonUp += (_, _) => onClick();
        return card;
    }

    /// <summary>一级：媒体库卡片。</summary>
    private void ShowLibs()
    {
        _level = "libs";
        _currentLib = null;
        _currentMaker = null;
        var counts = new Dictionary<string, long>();
        var rows = Db.Select(
            "SELECT \"library\", COUNT(*) FROM \"works\" WHERE \"state\" = '已品悦' GROUP BY \"library\"");
        if (rows != null)
            foreach (var row in rows)
                counts[row[0] as string ?? ""] = Convert.ToInt64(row[1]);
        _totalWorks = (int)counts.Values.Sum();
        _workLevelCount = false;
        _countUnit = I18n.Tr("个媒体库");
        _gridCards = AppConfig.ReadMediaLibs().Select(lib =>
        {
            var name = lib.Name;
            var caption = I18n.Format(I18n.Tr("{works} 个作品 · {folders} 个文件夹"),
                ("works", counts.GetValueOrDefault(name)), ("folders", lib.Folders.Count));
            return new GridCard(() => MakeClickCard(name, caption, () =>
            {
                _currentLib = name;
                _currentMaker = null;
                _currentGenre = null;
                _currentType = null;
                ShowLibWorks();   // 进入媒体库默认显示作品（可切换到社团视图）
            }), name.ToLowerInvariant());
        }).ToList();
        ApplyCardFilter();
    }

    /// <summary>二级：当前媒体库（或当前标签）下的社团卡片，懒加载，可按作品数/社团名排序。</summary>
    private void ShowMakers()
    {
        _level = "makers";
        _currentMaker = null;
        List<object?[]>? rows;
        if (_currentGenre != null)
            rows = Db.Select(
                "SELECT w.\"maker_name\", COUNT(*) FROM \"works\" w " +
                "JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
                "WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g " +
                "GROUP BY w.\"maker_name\" " + MakerOrderClause("w."),
                ("@g", _currentGenre));
        else if (_currentType != null)
            rows = Db.Select(
                "SELECT \"maker_name\", COUNT(*) FROM \"works\" " +
                "WHERE \"state\" = '已品悦' AND \"work_type\" = @t " +
                "GROUP BY \"maker_name\" " + MakerOrderClause(),
                ("@t", _currentType));
        else
            rows = Db.Select(
                "SELECT \"maker_name\", COUNT(*) FROM \"works\" " +
                "WHERE \"state\" = '已品悦' AND \"library\" = @lib " +
                "GROUP BY \"maker_name\" " + MakerOrderClause(),
                ("@lib", _currentLib ?? ""));
        if (rows == null)
            return;
        _totalWorks = (int)rows.Sum(r => Convert.ToInt64(r[1]));
        _workLevelCount = false;
        _countUnit = I18n.Tr("个社团");
        _gridCards = rows.Select(row =>
        {
            var maker = row[0] as string ?? "";
            var count = Convert.ToInt64(row[1]);
            var title = maker.Length > 0 ? maker : I18n.Tr("未知社团");
            return new GridCard(() => MakeClickCard(
                title, I18n.Format(I18n.Tr("{count} 个作品"), ("count", count)),
                () =>
                {
                    _currentMaker = maker.Length > 0 ? maker : UnknownMaker;
                    ShowWorks();
                }), title.ToLowerInvariant());
        }).ToList();
        PopulateSortBox(MakerSortOptions, _makerSort);
        ApplyCardFilter();
    }

    /// <summary>标签视图：全部已品悦作品的ジャンル标签卡片。</summary>
    private void ShowGenres()
    {
        _level = "genres";
        var rows = Db.Select(
            "SELECT g.\"genre\", COUNT(*) FROM \"work_genres\" g " +
            "JOIN \"works\" w ON w.\"work_id\" = g.\"work_id\" " +
            "WHERE w.\"state\" = '已品悦' GROUP BY g.\"genre\" ORDER BY COUNT(*) DESC");
        if (rows == null)
            return;
        var total = Db.Scalar(
            "SELECT COUNT(DISTINCT g.\"work_id\") FROM \"work_genres\" g " +
            "JOIN \"works\" w ON w.\"work_id\" = g.\"work_id\" WHERE w.\"state\" = '已品悦'");
        _totalWorks = total != null ? (int)Convert.ToInt64(total) : 0;
        _workLevelCount = false;
        _countUnit = I18n.Tr("个标签");
        _gridCards = rows.Select(row =>
        {
            var genre = row[0] as string ?? "";
            var count = Convert.ToInt64(row[1]);
            return new GridCard(() => MakeClickCard(
                genre, I18n.Format(I18n.Tr("{count} 个作品"), ("count", count)),
                () => OpenGenre(genre)), genre.ToLowerInvariant());
        }).ToList();
        ApplyCardFilter();
    }

    private void OpenGenre(string genre)
    {
        _currentGenre = genre;
        ShowMakers();
    }

    /// <summary>作品形式视图：全部已品悦作品按 work_type 分组的形式卡片。</summary>
    private void ShowTypes()
    {
        _level = "types";
        var rows = Db.Select(
            "SELECT \"work_type\", COUNT(*) FROM \"works\" " +
            "WHERE \"state\" = '已品悦' AND \"work_type\" IS NOT NULL AND \"work_type\" <> '' " +
            "GROUP BY \"work_type\" ORDER BY COUNT(*) DESC");
        if (rows == null)
            return;
        _totalWorks = (int)rows.Sum(r => Convert.ToInt64(r[1]));
        _workLevelCount = false;
        _countUnit = I18n.Tr("个形式");
        _gridCards = rows.Select(row =>
        {
            var type = row[0] as string ?? "";
            var count = Convert.ToInt64(row[1]);
            return new GridCard(() => MakeClickCard(
                type, I18n.Format(I18n.Tr("{count} 个作品"), ("count", count)),
                () => OpenType(type)), type.ToLowerInvariant());
        }).ToList();
        ApplyCardFilter();
    }

    private void OpenType(string type)
    {
        _currentType = type;
        _currentGenre = null;
        _currentLib = null;
        _currentMaker = null;
        ShowMakers();
    }

    /// <summary>三级：当前社团的作品卡片（标签流程下为 标签+社团），默认按发售日由新到旧。</summary>
    private void ShowWorks()
    {
        _level = "works";
        List<object?[]>? rows;
        var makerCond = _currentMaker == UnknownMaker
            ? "(\"maker_name\" IS NULL OR \"maker_name\" = '')"
            : "\"maker_name\" = @maker";
        if (_currentGenre != null)
            rows = Db.Select(
                "SELECT w.\"work_id\", w.\"work_name\", w.\"maker_name\", w.\"work_type\", " +
                "w.\"age_category\", w.\"cover\" FROM \"works\" w " +
                "JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
                $"WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g AND {makerCond.Replace("\"maker_name\"", "w.\"maker_name\"")} " +
                WorkOrderClause("w."),
                ("@g", _currentGenre), ("@maker", _currentMaker ?? ""));
        else if (_currentType != null)
            rows = Db.Select(
                "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
                $"FROM \"works\" WHERE \"state\" = '已品悦' AND \"work_type\" = @t AND {makerCond} " +
                WorkOrderClause(),
                ("@t", _currentType), ("@maker", _currentMaker ?? ""));
        else
            rows = Db.Select(
                "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
                $"FROM \"works\" WHERE \"state\" = '已品悦' AND \"library\" = @lib AND {makerCond} " +
                WorkOrderClause(),
                ("@lib", _currentLib ?? ""), ("@maker", _currentMaker ?? ""));
        if (rows != null)
            SetWorkCards(rows);
    }

    /// <summary>按单列值过滤的作品视图（跨所有媒体库），由详情页可点击字段触发。</summary>
    private void ShowFilteredWorks()
    {
        _level = "filtered_works";
        var cond = _filterCol == "voice_actor"
            ? "\"voice_actor\" LIKE @val"
            : $"\"{_filterCol}\" = @val";
        var value = _filterCol == "voice_actor" ? $"%{_filterVal}%" : _filterVal;
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            $"FROM \"works\" WHERE \"state\" = '已品悦' AND {cond} " + WorkOrderClause(),
            ("@val", value));
        if (rows != null)
            SetWorkCards(rows);
    }

    private void OpenFilter(string column, string value)
    {
        _filterCol = column;
        _filterVal = value;
        ShowFilteredWorks();
    }

    /// <summary>收藏夹视图：平铺所有已收藏的作品（懒加载，复用作品卡片流）。</summary>
    private void ShowFavorites()
    {
        _level = "favorites";
        _currentMaker = null;
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            "FROM \"works\" WHERE \"favorite\" = '1' " + WorkOrderClause());
        if (rows != null)
            SetWorkCards(rows);
    }

    /// <summary>进入媒体库后的默认视图：平铺当前媒体库下全部已品悦作品（可切换到社团视图）。</summary>
    private void ShowLibWorks()
    {
        _level = "lib_works";
        _currentMaker = null;
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            "FROM \"works\" WHERE \"state\" = '已品悦' AND \"library\" = @lib " + WorkOrderClause(),
            ("@lib", _currentLib ?? ""));
        if (rows != null)
            SetWorkCards(rows);
    }

    /// <summary>作品卡层级共用：把查询结果转成懒加载卡片项并刷新 SortBox。</summary>
    private void SetWorkCards(List<object?[]> rows)
    {
        _workLevelCount = true;
        _gridCards = rows.Select(row => new GridCard(
            () => MakeWorkCard(row),
            $"{row[0]} {row[1]} {row[2]}".ToLowerInvariant())).ToList();
        PopulateSortBox(WorkSortOptions, _workSort);
        ApplyCardFilter();
    }

    // ---------- 排序 ----------

    private string[] MakerSortOptions =>
    [
        I18n.Tr("作品数 多→少"), I18n.Tr("作品数 少→多"), I18n.Tr("社团名 A→Z"),
    ];

    private string[] WorkSortOptions =>
    [
        I18n.Tr("发售日 新→旧"), I18n.Tr("发售日 旧→新"), I18n.Tr("RJ号 新→旧"), I18n.Tr("RJ号 旧→新"),
    ];

    /// <summary>社团排序的 ORDER BY 子句；prefix 为表别名（标签流程下为 "w."）。</summary>
    private string MakerOrderClause(string prefix = "") => _makerSort switch
    {
        1 => "ORDER BY COUNT(*) ASC",
        2 => $"ORDER BY {prefix}\"maker_name\" COLLATE NOCASE ASC",
        _ => "ORDER BY COUNT(*) DESC",
    };

    /// <summary>作品排序的 ORDER BY 子句；prefix 为表别名（标签流程下为 "w."）。</summary>
    private string WorkOrderClause(string prefix = "") => _workSort switch
    {
        1 => $"ORDER BY {prefix}\"sell_date\" ASC",
        2 => $"ORDER BY {prefix}\"work_id\" DESC",
        3 => $"ORDER BY {prefix}\"work_id\" ASC",
        _ => $"ORDER BY {prefix}\"sell_date\" DESC",
    };

    /// <summary>用指定选项重填 SortBox 并选中当前排序，抑制由此触发的事件。</summary>
    private void PopulateSortBox(string[] options, int selected)
    {
        _suppressSort = true;
        SortBox.Items.Clear();
        foreach (var option in options)
            SortBox.Items.Add(new ComboBoxItem { Content = option });
        SortBox.SelectedIndex = Math.Min(Math.Max(selected, 0), options.Length - 1);
        _suppressSort = false;
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSort || SortBox.SelectedIndex < 0)
            return;
        var idx = SortBox.SelectedIndex;
        if (_searchMode)
        {
            // 搜索结果按作品排序重排：直接重跑作用域搜索
            _workSort = idx;
            ApplyCardFilter();
            return;
        }
        switch (_level)
        {
            case "makers":
                _makerSort = idx;
                ShowMakers();
                break;
            case "works":
                _workSort = idx;
                ShowWorks();
                break;
            case "filtered_works":
                _workSort = idx;
                ShowFilteredWorks();
                break;
            case "favorites":
                _workSort = idx;
                ShowFavorites();
                break;
            case "lib_works":
                _workSort = idx;
                ShowLibWorks();
                break;
        }
    }

    // ---------- 过滤与懒加载 ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void ApplyCardFilter()
    {
        if (_level == "detail")
            return;
        var keyword = SearchBox.Text.Trim();
        // 分组层级（媒体库首页 / 社团页）输入关键字时，改为在当前作用域内按 RJ号/作品名 搜索作品
        if (keyword.Length > 0 && _level is "libs" or "makers")
        {
            ShowScopedSearch(keyword);
            return;
        }
        _searchMode = false;
        // 其余层级（作品卡 / 标签 / 形式）：在全量卡片项上按关键字过滤，再分批懒加载
        var key = keyword.ToLowerInvariant();
        _shownCards = key.Length == 0
            ? _gridCards.ToList()
            : _gridCards.Where(c => c.FilterKey.Contains(key)).ToList();
        ClearCards();   // 统一关闭详情/设置按钮并清空面板
        BackButton.Visibility = ShouldShowBack() ? Visibility.Visible : Visibility.Collapsed;
        if (_level == "libs")
            LibSettingButton.Visibility = Visibility.Visible;  // 媒体库设置仅在媒体库首页显示
        SortBox.Visibility = _level is "makers" or "works" or "filtered_works" or "favorites" or "lib_works"
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateLibToggleButton();
        _loadedCards = 0;
        LoadMoreCards();
    }

    /// <summary>
    /// 作用域搜索：在当前层级对应的范围内按 RJ号/作品名 搜索作品并平铺为作品卡。
    /// 媒体库首页搜全部库；进入某媒体库后限当前库；标签/形式下的社团页限对应分组。
    /// 不改动 _gridCards，清空搜索框后即可回到原分组视图。
    /// </summary>
    private void ShowScopedSearch(string keyword)
    {
        _searchMode = true;
        var conds = new List<string> { "\"state\" = '已品悦'", "(\"work_id\" LIKE @kw OR \"work_name\" LIKE @kw)" };
        var pars = new List<(string, object?)> { ("@kw", $"%{keyword}%") };
        var join = "";
        if (_level == "makers")
        {
            if (_currentGenre != null)
            {
                join = "JOIN \"work_genres\" g ON g.\"work_id\" = \"works\".\"work_id\" ";
                conds.Add("g.\"genre\" = @g");
                pars.Add(("@g", _currentGenre));
            }
            else if (_currentType != null)
            {
                conds.Add("\"work_type\" = @t");
                pars.Add(("@t", _currentType));
            }
            else
            {
                conds.Add("\"library\" = @lib");
                pars.Add(("@lib", _currentLib ?? ""));
            }
        }
        var rows = Db.Select(
            "SELECT \"works\".\"work_id\", \"works\".\"work_name\", \"works\".\"maker_name\", " +
            "\"works\".\"work_type\", \"works\".\"age_category\", \"works\".\"cover\" FROM \"works\" " +
            join + "WHERE " + string.Join(" AND ", conds) + " " + WorkOrderClause("\"works\"."),
            pars.ToArray()) ?? [];

        _workLevelCount = true;
        _shownCards = rows.Select(row => new GridCard(
            () => MakeWorkCard(row),
            $"{row[0]} {row[1]} {row[2]}".ToLowerInvariant())).ToList();
        PopulateSortBox(WorkSortOptions, _workSort);
        ClearCards();
        BackButton.Visibility = ShouldShowBack() ? Visibility.Visible : Visibility.Collapsed;
        SortBox.Visibility = Visibility.Visible;
        UpdateLibToggleButton();
        _loadedCards = 0;
        LoadMoreCards();
    }

    /// <summary>返回按钮在当前层级是否显示（根视图层级不显示）。</summary>
    private bool ShouldShowBack() => _level switch
    {
        "libs" => false,
        "favorites" => false,
        "genres" => _root != MediaLibRoot.Genre,
        "types" => _root != MediaLibRoot.WorkType,
        _ => true,
    };

    /// <summary>追加渲染下一批卡片（构建器在此刻才真正生成可视化元素）。</summary>
    private void LoadMoreCards()
    {
        var batch = _shownCards.Skip(_loadedCards).Take(WorksPageSize).ToList();
        foreach (var card in batch)
            _cardsPanel.Children.Add(card.Build());
        _loadedCards += batch.Count;
        UpdateGridCountLabel();
    }

    /// <summary>刷新计数文案：作品卡显示作品总数+已加载，分组卡显示分组数+作品数。</summary>
    private void UpdateGridCountLabel()
    {
        var keyword = SearchBox.Text.Trim();
        if (_searchMode)
        {
            // 作用域搜索：仅统计命中的作品数
            var matched = _shownCards.Count;
            var text = I18n.Format(I18n.Tr("搜索到 {count} 个作品"), ("count", matched));
            if (_loadedCards < matched)
                text += I18n.Format(I18n.Tr("（已加载 {loaded}）"), ("loaded", _loadedCards));
            CountLabel.Text = text;
            return;
        }
        var total = _gridCards.Count;
        var shown = _shownCards.Count;
        if (_workLevelCount)
        {
            var text = keyword.Length > 0
                ? I18n.Format(I18n.Tr("共 {total} 个作品，匹配 {matched} 个"), ("total", total), ("matched", shown))
                : I18n.Format(I18n.Tr("共 {total} 个作品"), ("total", total));
            if (_loadedCards < shown)
                text += I18n.Format(I18n.Tr("（已加载 {loaded}）"), ("loaded", _loadedCards));
            CountLabel.Text = text;
        }
        else
        {
            CountLabel.Text = keyword.Length > 0
                ? I18n.Format(I18n.Tr("共 {total} {unit}，匹配 {shown} 个"),
                    ("total", total), ("unit", _countUnit), ("shown", shown))
                : I18n.Format(I18n.Tr("共 {total} {unit}，{works} 个作品"),
                    ("total", total), ("unit", _countUnit), ("works", _totalWorks));
        }
    }

    private void CardsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 视口宽度变化（窗口缩放 / 滚动条出现）时，按新宽度重排卡片以撑满整行
        if (e.ViewportWidthChange != 0)
            RelayoutCards();
        // 滚动接近底部时加载下一批卡片（所有卡片层级通用）
        if (_level == "detail" || _loadedCards >= _shownCards.Count)
            return;
        if (e.VerticalOffset >= CardsScroll.ScrollableHeight - 300)
            LoadMoreCards();
    }

    /// <summary>按视口宽度计算卡片宽度：先求每行能放下的列数（按最小宽度），再让卡片均分整行宽度。</summary>
    private double ComputeCardWidth()
    {
        var avail = CardsScroll.ViewportWidth;
        if (avail <= 0)
            return WorkCardW;  // 尚未布局时退回最小宽度，布局后由重排修正
        var columns = Math.Max(1, (int)(avail / (WorkCardW + CardGap)));
        return Math.Floor(avail / columns - CardGap);
    }

    /// <summary>视口宽度变化后，把新宽度套用到已生成的所有卡片（及作品卡封面）。</summary>
    private void RelayoutCards()
    {
        if (ContentHost.Content != _cardsPanel)
            return;  // 详情视图不是卡片流，跳过
        var width = ComputeCardWidth();
        if (Math.Abs(width - _cardWidth) < 0.5)
            return;
        _cardWidth = width;
        foreach (var card in _cardsPanel.Children.OfType<Border>())
        {
            card.Width = width;
            // 作品卡：StackPanel 首个子元素是固定高、宽随卡片变化的封面区
            if (card.Child is StackPanel { Children: [Grid cover, ..] })
                cover.Width = width - CoverWidthDelta;
        }
    }

    private void RestoreWorksScroll()
    {
        var target = _worksScrollPos;
        if (target <= 0)
            return;
        // 预加载直到内容高度能容纳目标滚动位置
        while (_loadedCards < _shownCards.Count &&
               _cardsPanel.ActualHeight < target + CardsScroll.ViewportHeight)
        {
            LoadMoreCards();
            _cardsPanel.UpdateLayout();
        }
        Dispatcher.BeginInvoke(() => CardsScroll.ScrollToVerticalOffset(target));
    }

    /// <summary>单个作品卡片：封面 + 角标（RJ号/形式）+ 标题。</summary>
    private Border MakeWorkCard(object?[] row)
    {
        var workId = row[0] as string ?? "";
        var workName = row[1] as string ?? "";
        var makerName = row[2] as string ?? "";
        var workType = row[3] as string ?? "";
        var cover = row[5] as string;

        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Width = _cardWidth,
            Height = WorkCardH,
            Margin = new Thickness(0, 0, CardGap, CardGap),
            Cursor = Cursors.Hand,
            ToolTip = workName.Length > 0 ? workName : workId,
        };
        var panel = new StackPanel();

        // 封面区：宽度随卡片动态变化、高度固定（图片按比例铺满裁切），无封面时显示纯色底
        var coverGrid = new Grid { Width = _cardWidth - CoverWidthDelta, Height = WorkCoverH, ClipToBounds = true };
        coverGrid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(12, 255, 255, 255)),
            CornerRadius = new CornerRadius(4),
        });
        if (cover != null && File.Exists(cover))
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(cover);
                bmp.DecodePixelWidth = (int)WorkCoverW * 2;  // 控制解码尺寸防内存膨胀
                bmp.EndInit();
                bmp.Freeze();
                image.Source = bmp;
                coverGrid.Children.Add(image);
            }
            catch (Exception)
            {
                // 单张封面读取失败不影响整页
            }
        }
        // RJ 号：封面左上角；作品形式：右上角
        var badgeStyleBg = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0));
        var rjBadge = new Border
        {
            Background = badgeStyleBg, CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Child = new TextBlock { Text = workId, FontSize = 11, Foreground = Brushes.WhiteSmoke },
        };
        coverGrid.Children.Add(rjBadge);
        if (workType.Length > 0)
            coverGrid.Children.Add(new Border
            {
                Background = badgeStyleBg, CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4),
                MaxWidth = _cardWidth - CoverWidthDelta - 80,
                Child = new TextBlock
                {
                    Text = workType, FontSize = 11, Foreground = Brushes.WhiteSmoke,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            });
        panel.Children.Add(coverGrid);

        panel.Children.Add(new TextBlock
        {
            Text = workName.Length > 0 ? workName : workId,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 80,
            Margin = new Thickness(0, 6, 0, 0),
        });
        card.Child = panel;
        card.MouseLeftButtonUp += (_, _) =>
        {
            _worksScrollPos = CardsScroll.VerticalOffset;
            _currentWork = workId;
            // 记录进入详情前的层级，以便返回时回到原作品列表 / 搜索视图
            _detailFromSearch = _searchMode;
            _searchLevel = _level;
            _detailReturnLevel = _level;
            ShowDetail();
        };
        return card;
    }

    // ---------- 作品详情 ----------

    private void ShowDetail()
    {
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"sell_date\", \"series\", " +
            "\"scenario\", \"illust\", \"voice_actor\", \"age_category\", \"work_type\", " +
            "\"genre\", \"file_size\", \"intro_s\", \"folder\", \"read_flag\", \"favorite\" " +
            "FROM \"works\" WHERE \"work_id\" = @w",
            ("@w", _currentWork));
        if (rows is not { Count: > 0 })
            return;
        var r = rows[0];
        var workId = r[0] as string ?? "";
        var workName = r[1] as string ?? "";
        var workFolder = r[13] as string;

        _level = "detail";
        BackButton.Visibility = Visibility.Visible;
        ClearCards();
        CountLabel.Text = "";
        // 详情页不显示搜索框；RJ 号居左显示为可点击跳转 DLsite 作品页的超链接
        SearchBox.Visibility = Visibility.Collapsed;
        RjLabel.Inlines.Clear();
        var rjLink = new Hyperlink(new Run(workId))
        {
            Foreground = (Brush)FindResource("AccentLightBrush"),
            TextDecorations = null,
        };
        rjLink.Click += (_, _) => Process.Start(new ProcessStartInfo(
            $"https://www.dlsite.com/maniax/work/=/product_id/{workId}.html") { UseShellExecute = true });
        RjLabel.Inlines.Add(rjLink);
        RjLabel.Visibility = Visibility.Visible;
        if (workFolder != null && Directory.Exists(workFolder))
        {
            _currentWorkFolder = workFolder;
            OpenFolderButton.Visibility = Visibility.Visible;
            ViewFilesButton.Visibility = Visibility.Visible;
            MoveLibButton.Visibility = Visibility.Visible;
        }
        // 已读 / 收藏 切换按钮
        _currentRead = r[14] as string == "1";
        _currentFav = r[15] as string == "1";
        ReadButton.Visibility = Visibility.Visible;
        FavButton.Visibility = Visibility.Visible;
        UpdateReadFavButtons();

        var root = new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(20, 16, 20, 16),
        };
        var layout = new StackPanel();
        root.Child = layout;

        layout.Children.Add(new TextBlock
        {
            Text = workName.Length > 0 ? workName : workId,
            FontWeight = FontWeights.SemiBold, FontSize = 18, TextWrapping = TextWrapping.Wrap,
        });

        var folder = workFolder != null ? Path.Combine(workFolder, DlsitePage.DataSourceDir) : "";
        if (!Directory.Exists(folder))
            folder = Path.Combine(DlsitePage.ImagesDir, workId);

        // 正文：按 [img:文件名] 占位标记拆成 文本/图片 块
        var bodyBlocks = new List<(string Kind, string Value)>();
        var bodyFiles = new HashSet<string>();
        var txtPath = Path.Combine(folder, DlsitePage.DescriptionTxt);
        if (File.Exists(txtPath))
        {
            string bodyText;
            try
            {
                bodyText = File.ReadAllText(txtPath).Trim();
            }
            catch (IOException)
            {
                bodyText = "";
            }
            var buf = new List<string>();
            foreach (var line in bodyText.Split('\n'))
            {
                var match = DlsitePage.BodyImageRe.Match(line.Trim());
                if (match.Success)
                {
                    if (buf.Count > 0)
                    {
                        bodyBlocks.Add(("text", string.Join('\n', buf)));
                        buf.Clear();
                    }
                    bodyBlocks.Add(("image", match.Groups[1].Value));
                    bodyFiles.Add(match.Groups[1].Value);
                }
                else
                {
                    buf.Add(line);
                }
            }
            if (buf.Count > 0)
                bodyBlocks.Add(("text", string.Join('\n', buf)));
        }

        // 轮播图：数据源中除正文图片外的图片，主图排最前
        var sliderPaths = new List<string>();
        if (Directory.Exists(folder))
        {
            var names = Directory.GetFiles(folder)
                .Select(Path.GetFileName)
                .Where(f => f != null &&
                            DlsitePage.ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()) &&
                            !bodyFiles.Contains(f))
                .Select(f => f!)
                .OrderBy(f => f.Contains("img_main", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            sliderPaths = names.Select(f => Path.Combine(folder, f)).ToList();
        }

        // 字段网格（带可点击链接）
        var infoPanel = BuildDetailFields(workId, r);

        // 上半部分：左边轮播图（1/3），右边字段详情（2/3）
        var content = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        if (sliderPaths.Count > 0)
        {
            var slider = new ImageSliderControl(sliderPaths) { VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(slider, 0);
            content.Children.Add(slider);
        }
        var rightHost = new Grid { Margin = new Thickness(20, 0, 0, 0) };
        rightHost.Children.Add(infoPanel);
        Grid.SetColumn(rightHost, 1);
        content.Children.Add(rightHost);
        layout.Children.Add(content);

        // 正文：文本与图片按原文顺序嵌入
        var maxWidth = Math.Max(360, CardsScroll.ViewportWidth - 100);
        foreach (var (kind, value) in bodyBlocks)
        {
            if (kind == "text")
            {
                if (value.Trim().Length == 0)
                    continue;
                layout.Children.Add(new TextBox
                {
                    Text = value,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Margin = new Thickness(0, 10, 0, 0),
                });
            }
            else
            {
                var path = Path.Combine(folder, value);
                if (!File.Exists(path))
                    continue;
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path);
                    bmp.EndInit();
                    bmp.Freeze();
                    layout.Children.Add(new Image
                    {
                        Source = bmp,
                        MaxWidth = maxWidth,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 10, 0, 0),
                    });
                }
                catch (Exception)
                {
                    // 单张正文图加载失败跳过
                }
            }
        }

        ContentHost.Content = root;
        CardsScroll.ScrollToVerticalOffset(0);
    }

    /// <summary>详情字段网格：社团/声优等渲染为可点击链接，标签来自 work_genres 表。</summary>
    private FrameworkElement BuildDetailFields(string workId, object?[] r)
    {
        var fields = new (string Label, string? Value)[]
        {
            ("社团", r[2] as string), ("販売日", r[3] as string),
            ("系列", r[4] as string), ("剧本", r[5] as string), ("插画", r[6] as string),
            ("声优", r[7] as string), ("年龄分级", r[8]?.ToString()), ("作品形式", r[9] as string),
            ("类型", r[10] as string), ("文件容量", r[11] as string), ("简介", r[12] as string),
        };
        var tags = Db.Select(
            "SELECT \"genre\" FROM \"work_genres\" WHERE \"work_id\" = @w", ("@w", workId));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var (label, value) in fields)
        {
            FrameworkElement valueElement;
            if (label == "类型" && tags is { Count: > 0 })
            {
                // 标签渲染为可点击链接
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
                foreach (var tagRow in tags)
                {
                    var tag = tagRow[0] as string ?? "";
                    var link = new Hyperlink(new Run(tag))
                    {
                        Foreground = (Brush)FindResource("AccentLightBrush"),
                        TextDecorations = null,
                    };
                    var captured = tag;
                    link.Click += (_, _) =>
                    {
                        _currentGenre = captured;
                        _currentLib = null;
                        _currentMaker = null;
                        _currentType = null;
                        ShowMakers();
                    };
                    tb.Inlines.Add(link);
                    tb.Inlines.Add(new Run("　"));
                }
                valueElement = tb;
            }
            else if (string.IsNullOrEmpty(value))
            {
                continue;
            }
            else if (label == "年龄分级")
            {
                var display = I18n.Tr(AgeMap.GetValueOrDefault(value, value));
                valueElement = MakeLinkBlock(display, () => OpenFilter("age_category", value));
            }
            else if (LinkCols.TryGetValue(label, out var linkInfo))
            {
                var parts = linkInfo.Multi
                    ? value.Split(" / ").Select(p => p.Trim()).Where(p => p.Length > 0).ToList()
                    : [value.Trim()];
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
                for (var i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    var link = new Hyperlink(new Run(part))
                    {
                        Foreground = (Brush)FindResource("AccentLightBrush"),
                        TextDecorations = null,
                    };
                    link.Click += (_, _) => OpenFilter(linkInfo.Col, part);
                    tb.Inlines.Add(link);
                    if (i < parts.Count - 1)
                        tb.Inlines.Add(new Run(" / "));
                }
                valueElement = tb;
            }
            else
            {
                valueElement = new TextBox
                {
                    Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                    Padding = new Thickness(0),
                };
            }

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keyLabel = new TextBlock
            {
                Text = I18n.Tr(label),
                Style = (Style)FindResource("CaptionText"),
                Margin = new Thickness(0, 3, 16, 3),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(keyLabel, row);
            Grid.SetColumn(keyLabel, 0);
            grid.Children.Add(keyLabel);
            valueElement.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(valueElement, row);
            Grid.SetColumn(valueElement, 1);
            grid.Children.Add(valueElement);
            row++;
        }
        return grid;
    }

    private TextBlock MakeLinkBlock(string text, Action onClick)
    {
        var link = new Hyperlink(new Run(text))
        {
            Foreground = (Brush)FindResource("AccentLightBrush"),
            TextDecorations = null,
        };
        link.Click += (_, _) => onClick();
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(link);
        return tb;
    }

    // ---------- 文件树（独立页面，多级折叠列表） ----------

    private const double TreeIndentStep = 22;    // 每深一级的左缩进
    private const double TreeRowBaseIndent = 8;  // 行基础左缩进
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji");

    /// <summary>"查看作品"独立页面：以多级折叠列表展示作品文件树（默认仅显示根目录文件，文件夹折叠）。</summary>
    private void ShowFileTree()
    {
        var folder = _treeFolder;
        if (folder == null || !Directory.Exists(folder))
        {
            ShowDetail();
            return;
        }
        _level = "filetree";
        ClearCards();
        BackButton.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Collapsed;   // 文件树页不需要搜索框
        CountLabel.Text = "";
        // 顶部居左显示当前作品 RJ 号
        RjLabel.Inlines.Clear();
        RjLabel.Inlines.Add(new Run(_currentWork ?? Path.GetFileName(folder)));
        RjLabel.Visibility = Visibility.Visible;
        // 恢复"打开文件夹"按钮（ClearCards 已置空 _currentWorkFolder）
        _currentWorkFolder = folder;
        OpenFolderButton.Visibility = Visibility.Visible;
        ViewModeBar.Visibility = Visibility.Visible;
        UpdateViewModeButtons();

        if (_treeThumbView)
            ContentHost.Content = BuildThumbView(folder);
        else
        {
            var list = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            AddTreeLevel(list, folder, depth: 0, isRoot: true);
            if (list.Children.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = I18n.Tr("此作品文件夹为空"),
                    Style = (Style)FindResource("CaptionText"),
                    Margin = new Thickness(TreeRowBaseIndent, 8, 0, 0),
                });
            ContentHost.Content = list;
        }
        CardsScroll.ScrollToVerticalOffset(0);
    }

    /// <summary>把缩略图/文件列表按钮按当前视图高亮（选中态用主色按钮样式）。</summary>
    private void UpdateViewModeButtons()
    {
        ThumbViewButton.Style = _treeThumbView
            ? (Style)FindResource("AccentButton") : (Style)FindResource(typeof(Button));
        ListViewButton.Style = _treeThumbView
            ? (Style)FindResource(typeof(Button)) : (Style)FindResource("AccentButton");
    }

    private void ThumbViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_treeThumbView)
            return;
        _treeThumbView = true;
        ShowFileTree();
    }

    private void ListViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_treeThumbView)
            return;
        _treeThumbView = false;
        ShowFileTree();
    }

    private const double ThumbTileW = 160, ThumbTileH = 160, ThumbGap = 10;

    /// <summary>缩略图视图：递归收集作品内所有图片（排除 DataSource），平铺成缩略图网格，单击整页预览。</summary>
    private FrameworkElement BuildThumbView(string folder)
    {
        List<string> images = [];
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                // 排除根目录下的 DataSource（封面/描述图等元数据资源）
                var rel = Path.GetRelativePath(folder, f);
                if (rel.StartsWith(DlsitePage.DataSourceDir + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (DlsitePage.ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    images.Add(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        images.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));

        if (images.Count == 0)
            return new TextBlock
            {
                Text = I18n.Tr("此作品没有可预览的图片"),
                Style = (Style)FindResource("CaptionText"),
                Margin = new Thickness(TreeRowBaseIndent, 8, 0, 0),
            };

        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var path in images)
            wrap.Children.Add(MakeThumbTile(path));
        return wrap;
    }

    /// <summary>单张缩略图：图片填充裁切 + 文件名，单击整页预览。</summary>
    private Border MakeThumbTile(string path)
    {
        var tile = new Border
        {
            Width = ThumbTileW,
            Margin = new Thickness(0, 0, ThumbGap, ThumbGap),
            Cursor = Cursors.Hand,
            ToolTip = Path.GetFileName(path),
        };
        var panel = new StackPanel();
        var coverGrid = new Grid { Width = ThumbTileW, Height = ThumbTileH, ClipToBounds = true };
        coverGrid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(12, 255, 255, 255)),
            CornerRadius = new CornerRadius(4),
        });
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = (int)ThumbTileW * 2;  // 控制解码尺寸防内存膨胀
            bmp.EndInit();
            bmp.Freeze();
            coverGrid.Children.Add(new Image
            {
                Source = bmp, Stretch = Stretch.UniformToFill,
                ClipToBounds = true,
            });
        }
        catch (Exception)
        {
            // 单张缩略图读取失败不影响整页
        }
        panel.Children.Add(coverGrid);
        panel.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path),
            Style = (Style)FindResource("CaptionText"),
            Margin = new Thickness(2, 4, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        tile.Child = panel;
        tile.MouseLeftButtonUp += (_, _) => ShowPreview(path);
        return tile;
    }

    /// <summary>递归填充某一层级：子文件夹（默认折叠）在前、文件在后；根目录排除 DataSource。</summary>
    private void AddTreeLevel(StackPanel panel, string dir, int depth, bool isRoot)
    {
        string[] subDirs = [], files = [];
        try
        {
            subDirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var dirs = subDirs
            .Where(d => !(isRoot && string.Equals(Path.GetFileName(d), DlsitePage.DataSourceDir,
                StringComparison.OrdinalIgnoreCase)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        foreach (var sub in dirs)
        {
            var children = new StackPanel { Visibility = Visibility.Collapsed };  // 默认折叠
            AddTreeLevel(children, sub, depth + 1, false);
            panel.Children.Add(MakeTreeFolderRow(sub, children, depth));
            panel.Children.Add(children);
        }
        foreach (var file in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            panel.Children.Add(MakeTreeFileRow(file, depth));
    }

    /// <summary>文件夹行：箭头 + 📁 + 名称 + 项数，点击整行折叠/展开子级。</summary>
    private Border MakeTreeFolderRow(string dir, StackPanel children, int depth)
    {
        var row = new Border
        {
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(TreeRowBaseIndent + depth * TreeIndentStep, 7, 12, 7),
            Margin = new Thickness(0, 1, 0, 1),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var arrow = new TextBlock
        {
            Text = "▸",
            Foreground = (Brush)FindResource("CaptionBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(arrow);
        var icon = new TextBlock
        {
            Text = "📁", FontFamily = EmojiFont,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);
        var name = new TextBlock
        {
            Text = Path.GetFileName(dir),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);
        var count = 0;
        try
        {
            count = Directory.GetFileSystemEntries(dir).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        var countText = new TextBlock
        {
            Text = I18n.Format(I18n.Tr("{count} 项"), ("count", count)),
            Style = (Style)FindResource("CaptionText"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countText, 3);
        grid.Children.Add(countText);
        row.Child = grid;

        row.MouseLeftButtonUp += (_, _) =>
        {
            var show = children.Visibility != Visibility.Visible;
            children.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = show ? "▾" : "▸";
        };
        row.MouseEnter += (_, _) => row.Background = RowHoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    /// <summary>文件行：类型图标 + 名称 + 大小。图片/视频单击整页预览；音频单击底部播放条播放；其它双击打开。</summary>
    private Border MakeTreeFileRow(string path, int depth)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var previewable = DlsitePage.ImageExts.Contains(ext) || VideoExts.Contains(ext);
        var isAudio = AudioExts.Contains(ext);
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            // 文件对齐到同级文件夹名（让出箭头列宽度）
            Padding = new Thickness(TreeRowBaseIndent + depth * TreeIndentStep + 20, 5, 12, 5),
            Margin = new Thickness(0, 1, 0, 1),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = FileIcon(ext), FontFamily = EmojiFont,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(icon);
        var name = new TextBlock
        {
            Text = Path.GetFileName(path),
            Foreground = (Brush)FindResource(previewable || isAudio ? "TextBrush" : "CaptionBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        var sizeText = new TextBlock
        {
            Text = FormatSize(size),
            Style = (Style)FindResource("CaptionText"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(sizeText, 2);
        grid.Children.Add(sizeText);
        row.Child = grid;

        if (previewable)
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => ShowPreview(path);
        }
        else if (isAudio)
        {
            // 音频单击：在底部播放条就地播放（wav/mp3 等），并以同文件夹音频组成播放队列
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => PlayAudioFromTree(path);
        }
        else
        {
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2)
                    return;
                e.Handled = true;
                OpenTreeFile(path);
            };
        }
        row.MouseEnter += (_, _) => row.Background = RowHoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    /// <summary>点击音频：把同文件夹内的所有音频按名称排序组成播放队列，从点击的这首开始播放。</summary>
    private void PlayAudioFromTree(string path)
    {
        List<(string Path, string Title)> queue = [];
        var index = 0;
        var dir = Path.GetDirectoryName(path);
        if (dir != null)
        {
            try
            {
                var files = Directory.GetFiles(dir)
                    .Where(f => AudioExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                index = files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    index = 0;
                queue = files.Select(f => (f, Path.GetFileName(f))).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 枚举失败时退回单文件播放
            }
        }
        if (queue.Count == 0)
            queue = [(path, Path.GetFileName(path))];
        AudioBar.PlayQueue(queue, index);
    }

    /// <summary>按扩展名选择文件类型图标。</summary>
    private static string FileIcon(string ext)
    {
        if (DlsitePage.ImageExts.Contains(ext)) return "🖼️";
        if (VideoExts.Contains(ext)) return "🎬";
        if (AudioExts.Contains(ext)) return "🎵";
        return "📄";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>双击文件：音频→播放器弹窗，其它→系统默认程序（图片/视频由单击整页预览）。</summary>
    private void OpenTreeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (AudioExts.Contains(ext))
            new MediaPlayerDialog(path, isVideo: false) { Owner = Window.GetWindow(this) }
                .ShowDialog();
        else
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ---------- 整页预览 ----------

    /// <summary>整页覆盖预览：图片支持同文件夹左右切换，视频用内嵌播放器。</summary>
    private void ShowPreview(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (_inlinePlayer != null)
        {
            _inlinePlayer.Ended -= OnPreviewVideoEnded;
            _inlinePlayer.Shutdown();
            _inlinePlayer = null;
        }
        // 先显示整页遮罩，使随后挂入的视频播放器在“已加载且可见”的状态下开始播放（否则画面黑屏）
        PreviewOverlay.Visibility = Visibility.Visible;
        if (DlsitePage.ImageExts.Contains(ext))
        {
            _previewVideoMode = false;
            _previewVideos = [];
            var dir = Path.GetDirectoryName(path)!;
            try
            {
                _previewImages = Directory.GetFiles(dir)
                    .Where(f => DlsitePage.ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _previewImages = [path];
            }
            _previewIndex = Math.Max(0, _previewImages.IndexOf(path));
            PreviewPrevButton.Content = "‹ " + I18n.Tr("上一张");
            PreviewNextButton.Content = I18n.Tr("下一张") + " ›";
            SetPreviewNavVisible(_previewImages.Count > 1);
            LoadPreviewImage();
        }
        else if (VideoExts.Contains(ext))
        {
            AudioBar.Stop();   // 视频自带声音：开始播视频前停掉底部音频，避免两路声音重叠
            _previewImages = [];
            _previewVideoMode = true;
            // 同文件夹视频按名称排序组成播放队列，从点击的这首开始，支持上一/下一个与自动续播
            var dir = Path.GetDirectoryName(path)!;
            try
            {
                _previewVideos = Directory.GetFiles(dir)
                    .Where(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _previewVideos = [path];
            }
            _previewIndex = Math.Max(0, _previewVideos.IndexOf(path));
            PreviewPrevButton.Content = "‹ " + I18n.Tr("上一个");
            PreviewNextButton.Content = I18n.Tr("下一个") + " ›";
            SetPreviewNavVisible(_previewVideos.Count > 1);
            LoadPreviewVideo();
        }
        else
        {
            PreviewOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewOverlay.Focus();
    }

    /// <summary>统一切换顶部"上一张/下一张"与图片两侧覆盖箭头的显示。</summary>
    private void SetPreviewNavVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewNavBar.Visibility = v;
        PreviewPrevArrow.Visibility = v;
        PreviewNextArrow.Visibility = v;
    }

    private void LoadPreviewImage()
    {
        if (_previewImages.Count == 0)
            return;
        var path = _previewImages[_previewIndex];
        PreviewTitle.Text = _previewImages.Count > 1
            ? $"{Path.GetFileName(path)}　{_previewIndex + 1} / {_previewImages.Count}"
            : Path.GetFileName(path);
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            PreviewContent.Child = new Image { Source = bmp, Stretch = Stretch.Uniform };
        }
        catch (Exception)
        {
            PreviewContent.Child = null;
        }
    }

    /// <summary>加载视频播放队列中当前索引的视频；订阅自然结束以自动续播下一个。</summary>
    private void LoadPreviewVideo()
    {
        if (_previewVideos.Count == 0)
            return;
        var path = _previewVideos[_previewIndex];
        PreviewTitle.Text = _previewVideos.Count > 1
            ? $"{Path.GetFileName(path)}　{_previewIndex + 1} / {_previewVideos.Count}"
            : Path.GetFileName(path);
        if (_inlinePlayer != null)
        {
            _inlinePlayer.Ended -= OnPreviewVideoEnded;
            _inlinePlayer.Shutdown();
        }
        _inlinePlayer = new InlineMediaPlayer(path);
        _inlinePlayer.Ended += OnPreviewVideoEnded;
        PreviewContent.Child = _inlinePlayer;
    }

    /// <summary>视频自然播放结束：未到队尾则自动续播下一个。</summary>
    private void OnPreviewVideoEnded()
    {
        if (_previewVideoMode && _previewIndex < _previewVideos.Count - 1)
            StepPreview(1);
    }

    private void StepPreview(int delta)
    {
        if (_previewVideoMode)
        {
            if (_previewVideos.Count == 0)
                return;
            _previewIndex = ((_previewIndex + delta) % _previewVideos.Count + _previewVideos.Count)
                            % _previewVideos.Count;
            LoadPreviewVideo();
            return;
        }
        if (_previewImages.Count == 0)
            return;
        _previewIndex = ((_previewIndex + delta) % _previewImages.Count + _previewImages.Count)
                        % _previewImages.Count;
        LoadPreviewImage();
    }

    /// <summary>关闭整页预览：停止播放、释放图片，回到详情页。</summary>
    private void ClosePreview()
    {
        if (_inlinePlayer != null)
        {
            _inlinePlayer.Ended -= OnPreviewVideoEnded;
            _inlinePlayer.Shutdown();
            _inlinePlayer = null;
        }
        _previewImages = [];
        _previewVideos = [];
        _previewVideoMode = false;
        PreviewContent.Child = null;
        PreviewOverlay.Visibility = Visibility.Collapsed;
    }

    private void PreviewClose_Click(object sender, RoutedEventArgs e) => ClosePreview();

    private void PreviewPrev_Click(object sender, RoutedEventArgs e) => StepPreview(-1);

    private void PreviewNext_Click(object sender, RoutedEventArgs e) => StepPreview(1);

    private void PreviewOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ClosePreview();
                e.Handled = true;
                break;
            case Key.Left:
                StepPreview(-1);
                e.Handled = true;
                break;
            case Key.Right:
                StepPreview(1);
                e.Handled = true;
                break;
        }
    }
}
