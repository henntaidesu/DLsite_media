using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DLsiteMedia.Core;
using DLsiteMedia.Views;
using WinForms = System.Windows.Forms;

namespace DLsiteMedia;

/// <summary>主窗口：左侧导航 + 右侧页面切换（对应 Python 版 index_UI.py）。</summary>
public partial class MainWindow : Window
{
    // 右下角托盘图标：关闭时可选择最小化到此处而非退出，后台仍继续下载/Web 服务
    private WinForms.NotifyIcon? _trayIcon;
    private WinForms.ToolStripMenuItem? _trayShowItem;
    private WinForms.ToolStripMenuItem? _trayExitItem;
    private bool _forceExit;   // true 时关闭直接退出（托盘菜单"退出"或已确认退出）
    // 默认媒体库页预建（启动即显示）；其余页首次导航时才实例化，缩短首屏时间。
    private readonly MediaLibPage _mediaLibPage = new(MediaLibRoot.Library);
    // 下载页持有 search/downloaded 的事件闭包，故只需保留下载页字段即可根住三者。
    private DownloadPage? _downloadPageField;
    private MediaLibPage? _tagPage;
    private MediaLibPage? _typePage;
    private MediaLibPage? _makerPage;
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

        SetupTray();
        Closing += MainWindow_Closing;

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
        NavMaker.Content = I18n.Tr("社团");
        NavFavorite.Content = I18n.Tr("收藏夹");
        NavSetting.Content = I18n.Tr("设置");
        if (_trayIcon != null)
            _trayIcon.Text = I18n.Tr("DLsite媒体库");
        if (_trayShowItem != null)
            _trayShowItem.Text = I18n.Tr("显示主界面");
        if (_trayExitItem != null)
            _trayExitItem.Text = I18n.Tr("退出");
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
            "maker" => _makerPage ??= new MediaLibPage(MediaLibRoot.Maker),
            "favorite" => _favoritePage ??= new MediaLibPage(MediaLibRoot.Favorite),
            "setting" => _settingsPage ??= new SettingsPage(),
            _ => (object)_mediaLibPage,
        };
    }

    // ---------- 系统托盘（右下角常驻）----------

    /// <summary>创建右下角托盘图标及其右键菜单：双击/菜单可还原窗口，菜单可退出程序。</summary>
    private void SetupTray()
    {
        _trayShowItem = new WinForms.ToolStripMenuItem(I18n.Tr("显示主界面"));
        _trayShowItem.Click += (_, _) => RestoreFromTray();
        _trayExitItem = new WinForms.ToolStripMenuItem(I18n.Tr("退出"));
        _trayExitItem.Click += (_, _) => ExitApp();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_trayShowItem);
        menu.Items.Add(_trayExitItem);

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = I18n.Tr("DLsite媒体库"),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    /// <summary>取应用自身的图标用于托盘；失败时回退到系统默认应用图标。</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } path &&
                System.Drawing.Icon.ExtractAssociatedIcon(path) is { } icon)
                return icon;
        }
        catch (Exception)
        {
            // 提取失败时用系统默认图标兜底
        }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>从托盘还原并激活主窗口。</summary>
    private void RestoreFromTray() =>
        // 经 Dispatcher 异步执行：托盘菜单/双击回调运行在 WinForms 菜单自身的消息循环内，
        // 同步操作窗口会与之重入（抛"窗口关闭期间无法调用 Show/Close"等异常），先让菜单循环退栈。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }));

    /// <summary>隐藏到托盘（后台下载与 Web 服务继续运行）。</summary>
    private void MinimizeToTray() => Hide();

    /// <summary>真正退出程序（跳过关闭确认）。</summary>
    private void ExitApp()
    {
        _forceExit = true;
        // 同 RestoreFromTray：从 WinForms 托盘菜单回调中同步 Close() 会在其消息循环内重入
        // （关闭时又会 Dispose 掉当前正在处理点击的 ContextMenuStrip），故延后到 Dispatcher 再关。
        Dispatcher.BeginInvoke(new Action(Close));
    }

    /// <summary>点击关闭按钮：按配置直接执行，或弹窗询问退出还是最小化到托盘。</summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_forceExit)
            return;   // 托盘"退出"或已确认退出：放行

        switch (AppConfig.CloseAction)
        {
            case "exit":
                return;             // 记住的选择：直接退出
            case "tray":
                e.Cancel = true;
                MinimizeToTray();   // 记住的选择：直接最小化
                return;
        }

        // 未记住选择：弹窗询问
        var choice = InAppDialog.Choose(this,
            I18n.Tr("您想要退出程序，还是最小化到托盘继续在后台运行？"),
            I18n.Tr("关闭窗口"),
            [I18n.Tr("最小化到托盘"), I18n.Tr("退出程序"), I18n.Tr("取消")],
            out var remember,
            I18n.Tr("记住我的选择，不再询问"));

        switch (choice)
        {
            case 0:   // 最小化到托盘
                e.Cancel = true;
                if (remember)
                    AppConfig.CloseAction = "tray";
                MinimizeToTray();
                break;
            case 1:   // 退出程序
                if (remember)
                    AppConfig.CloseAction = "exit";
                _forceExit = true;   // 放行本次关闭
                break;
            default:  // 取消 / Esc
                e.Cancel = true;
                break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        base.OnClosed(e);
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
