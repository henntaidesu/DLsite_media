using System.Windows;
using DLsiteMedia.Core;
using DLsiteMedia.Services;

namespace DLsiteMedia;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Db.EnsureTables();
        I18n.InitLanguage();  // 创建窗口前加载已保存的语言
        // 设置中开启了自动下载时，随程序启动下载线程；否则由下载页"开始下载"按钮启动
        if (AppConfig.AutoDownload)
            DownloadEngine.Start();
        // 设置中开启了外部访问时，随程序启动内嵌 Web 服务
        if (AppConfig.WebEnabled)
            WebServer.StartFromConfig();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        WebServer.Stop();
        Db.Checkpoint();
        base.OnExit(e);
    }
}
