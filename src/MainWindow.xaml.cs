using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DASD.Core;
using DASD.Views;

namespace DASD;

/// <summary>主窗口：左侧导航 + 右侧页面切换（对应 Python 版 index_UI.py）。</summary>
public partial class MainWindow : Window
{
    private readonly SearchPage _searchPage = new();
    private readonly DownloadPage _downloadPage = new();
    private readonly DownloadedPage _downloadedPage = new();
    private readonly MediaLibPage _mediaLibPage = new(MediaLibRoot.Library);
    private readonly MediaLibPage _tagPage = new(MediaLibRoot.Genre);
    private readonly MediaLibPage _typePage = new(MediaLibRoot.WorkType);
    private readonly MediaLibPage _favoritePage = new(MediaLibRoot.Favorite);
    private readonly SettingsPage _settingsPage = new();

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => EnableDarkTitleBar();
        PageHost.Content = _mediaLibPage;

        // 搜索/下载合并为同一导航：下载页"搜索作品"切到搜索视图，搜索页返回切回下载视图
        _downloadPage.ShowSearchRequested += () => PageHost.Content = _searchPage;
        _searchPage.BackToDownloadRequested += () => PageHost.Content = _downloadPage;

        // 下载页解析失败点击"重新搜索"：切到搜索视图并自动以该番号重新搜索
        _downloadPage.ResearchRequested += workId =>
        {
            PageHost.Content = _searchPage;
            _searchPage.SearchFor(workId);
        };

        // "已下载"已并入下载页：下载页按钮切到已下载视图，已下载页按钮切回下载视图（导航仍停留在"搜索/下载"）
        _downloadPage.ShowDownloadedRequested += () => PageHost.Content = _downloadedPage;
        _downloadedPage.BackToDownloadRequested += () => PageHost.Content = _downloadPage;

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
            "searchdownload" => _downloadPage,
            "tag" => _tagPage,
            "type" => _typePage,
            "favorite" => _favoritePage,
            "setting" => _settingsPage,
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
