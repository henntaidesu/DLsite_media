using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DLsiteMedia.Core;
using DLsiteMedia.Views;

namespace DLsiteMedia;

/// <summary>主窗口：左侧导航 + 右侧页面切换（对应 Python 版 index_UI.py）。</summary>
public partial class MainWindow : Window
{
    // 默认媒体库页预建（启动即显示）；其余页首次导航时才实例化，缩短首屏时间。
    private readonly MediaLibPage _mediaLibPage = new(MediaLibRoot.Library);
    // 下载页持有 search/downloaded 的事件闭包，故只需保留下载页字段即可根住三者。
    private DownloadPage? _downloadPageField;
    private MediaLibPage? _tagPage;
    private MediaLibPage? _typePage;
    private MediaLibPage? _favoritePage;
    private SettingsPage? _settingsPage;

    // 搜索/下载三视图相互跳转由事件驱动，首次访问其一即需三者就绪，故统一惰性创建。
    private DownloadPage DownloadPage => _downloadPageField ??= CreateDownloadTrio();

    private DownloadPage CreateDownloadTrio()
    {
        if (_downloadPageField != null)
            return _downloadPageField;

        var download = new DownloadPage();
        var search = new SearchPage();
        var downloaded = new DownloadedPage();
        _downloadPageField = download;

        // 搜索/下载合并为同一导航：下载页"搜索作品"切到搜索视图，搜索页返回切回下载视图
        download.ShowSearchRequested += () => PageHost.Content = search;
        search.BackToDownloadRequested += () => PageHost.Content = download;

        // 下载页解析失败点击"重新搜索"：切到搜索视图并自动以该番号重新搜索
        download.ResearchRequested += workId =>
        {
            PageHost.Content = search;
            search.SearchFor(workId);
        };

        // "已下载"已并入下载页：下载页按钮切到已下载视图，已下载页按钮切回下载视图（导航仍停留在"搜索/下载"）
        download.ShowDownloadedRequested += () => PageHost.Content = downloaded;
        downloaded.BackToDownloadRequested += () => PageHost.Content = download;
        return download;
    }

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => EnableDarkTitleBar();
        PageHost.Content = _mediaLibPage;

        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    private void RetranslateUi()
    {
        LogoLabel.Text = I18n.Tr("DLsite媒体库");
        Title = I18n.Tr("DLsite媒体库");
        NavMediaLib.Content = I18n.Tr("媒体库");
        NavSearchDownload.Content = I18n.Tr("搜索/下载");
        NavTag.Content = I18n.Tr("标签");
        NavType.Content = I18n.Tr("作品形式");
        NavFavorite.Content = I18n.Tr("收藏夹");
        NavSetting.Content = I18n.Tr("设置");
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHost == null)
            return;  // InitializeComponent 期间的首次 Checked
        PageHost.Content = (sender as RadioButton)?.Tag switch
        {
            "searchdownload" => DownloadPage,
            "tag" => _tagPage ??= new MediaLibPage(MediaLibRoot.Genre),
            "type" => _typePage ??= new MediaLibPage(MediaLibRoot.WorkType),
            "favorite" => _favoritePage ??= new MediaLibPage(MediaLibRoot.Favorite),
            "setting" => _settingsPage ??= new SettingsPage(),
            _ => (object)_mediaLibPage,
        };
    }

    /// <summary>Windows 下将窗口标题栏切换为深色（对应 Python 版 enable_dark_title_bar）。</summary>
    private void EnableDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var value = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 2004+），旧版本用 19
            foreach (var attr in new[] { 20, 19 })
                if (DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)) == 0)
                    break;
        }
        catch (Exception)
        {
            // 旧系统不支持时忽略
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
