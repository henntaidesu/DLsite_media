using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DLsiteMedia.Core;

namespace DLsiteMedia.Services;

/// <summary>
/// 无界面的媒体库扫描器：供 Web 端"媒体库设置"触发导入 + 元数据补全。
/// 复用 <see cref="MediaLibraryService"/> 的导入/剔除/补全逻辑（与桌面端
/// <c>MediaLibSettingDialog.RunScanAsync</c> 同一编排：单实例串行 + 队列），
/// 扫描进度以字符串形式暴露，供网页每秒轮询显示。进程级静态，跨请求共享。
/// </summary>
public static class MediaLibScanner
{
    private static readonly object Sync = new();
    private static readonly List<(string Lib, bool Force)> Pending = [];  // 扫描队列
    private static bool _scanning;
    private static string _status = "";

    /// <summary>当前扫描状态（是否在扫、状态文本），供 /api/lib/scanstatus 轮询。</summary>
    public static (bool Scanning, string Status) State()
    {
        lock (Sync)
            return (_scanning, _status);
    }

    /// <summary>触发某个媒体库扫描；正在扫描时排队（去重）。</summary>
    public static void Scan(string libName, bool force)
    {
        var lib = AppConfig.ReadMediaLibs().FirstOrDefault(l => l.Name == libName);
        if (lib == null || lib.Folders.Count == 0)
            return;
        lock (Sync)
        {
            if (_scanning)
            {
                if (!Pending.Contains((libName, force)))
                    Pending.Add((libName, force));
                return;
            }
            _scanning = true;
        }
        _ = RunAsync(libName, force);
    }

    /// <summary>对所有有文件夹的媒体库依次触发扫描。</summary>
    public static void ScanAll()
    {
        foreach (var lib in AppConfig.ReadMediaLibs())
            if (lib.Folders.Count > 0)
                Scan(lib.Name, force: false);
    }

    /// <summary>后台扫描：导入 RJ 号、剔除已缺失作品，再走 DL API + 作品页补全（镜像桌面端）。</summary>
    private static async Task RunAsync(string libName, bool force)
    {
        var lib = AppConfig.ReadMediaLibs().FirstOrDefault(l => l.Name == libName);
        if (lib == null || lib.Folders.Count == 0)
        {
            DequeueNext();
            return;
        }
        var folders = lib.Folders.ToList();
        SetStatus($"正在扫描媒体库\"{libName}\"…");
        try
        {
            var (added, total, removed) = await Task.Run(() =>
            {
                int addedSum = 0, totalSum = 0;
                foreach (var folder in folders)
                {
                    var (a, t) = MediaLibraryService.ImportMediaLib(folder, libName);
                    addedSum += a;
                    totalSum += t;
                }
                // 导入后剔除磁盘上已缺失的作品（同步删除 works / work_genres 记录）
                var pruned = MediaLibraryService.PruneMissingWorks(libName, folders);
                return (addedSum, totalSum, pruned.Count);
            });
            SetStatus($"已导入 {total} 个作品（新增 {added}，剔除 {removed}），正在从 DL API 补全数据…");

            var (filled, _, _) = await MediaLibraryService.BackfillWorksFromApiAsync(
                delaySeconds: 0.5,
                progress: (i, totalCount, _, _) =>
                {
                    if (i % 10 == 0 || i == totalCount)
                        SetStatus($"DL API 数据补全中… {i}/{totalCount}");
                },
                library: libName, force: force);

            SetStatus("正在抓取 DLsite 作品页元数据与图片…");
            var (pageFilled, pageMissed, _) = await MediaLibraryService.BackfillWorkPagesAsync(
                delaySeconds: 1.0,
                progress: (i, totalCount, rj, _) =>
                    SetStatus($"作品页抓取中… {i}/{totalCount}（{rj}）"),
                library: libName, force: force);

            SetStatus($"扫描完成：导入 {total} 个，剔除 {removed} 个，API 补全 {filled} 个，" +
                      $"作品页补全 {pageFilled} 个（失败 {pageMissed}）");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Web 媒体库扫描");
            SetStatus("扫描出错：" + e.Message);
        }
        finally
        {
            DequeueNext();
        }
    }

    private static void DequeueNext()
    {
        (string Lib, bool Force)? next = null;
        lock (Sync)
        {
            if (Pending.Count > 0)
            {
                next = Pending[0];
                Pending.RemoveAt(0);
            }
            else
            {
                _scanning = false;
            }
        }
        if (next is { } n)
            _ = RunAsync(n.Lib, n.Force);
    }

    private static void SetStatus(string text)
    {
        lock (Sync)
            _status = text;
        Logger.Info("[Web 媒体库扫描] " + text);
    }
}
