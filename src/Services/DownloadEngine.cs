using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DLsiteMedia.Core;

namespace DLsiteMedia.Services;

/// <summary>正在下载任务的实时进度（UUID -> 进度），由下载线程写入、下载页 UI 读取。</summary>
public class DownloadProgressInfo
{
    public long Downloaded { get; init; }
    public long Total { get; init; }
    public double Speed { get; init; }   // B/s
}

/// <summary>正在解压/移动作品的实时进度（work_id -> 进度）。</summary>
public class UnzipProgressInfo
{
    public string State { get; init; } = "pending";  // pending / extracting / moving
    public int Pct { get; init; }
}

/// <summary>
/// debrid-link 中转下载引擎（对应 Python 版 debrid_link.py 的下载线程部分）：
/// 队列领取 → debrid-link 解析 → 断点续传下载 → 全部分卷完成后触发解压。
/// 支持暂停（停到断点）、低速重试、全局限速。
/// </summary>
public static class DownloadEngine
{
    private const int Chunk = 64 * 1024;

    public static readonly ConcurrentDictionary<string, DownloadProgressInfo> DownloadProgress = new();
    public static readonly ConcurrentDictionary<string, UnzipProgressInfo> UnzipProgress = new();

    // 暂停信号：置位后下载线程停止当前文件（保留断点）并退出
    private static volatile bool _stopRequested;
    private static Thread? _mainThread;

    // UI 指定的每作品媒体库目标根目录与所属媒体库名（内存缓存，同时持久化到 works 表）
    private static readonly ConcurrentDictionary<string, string> WorkTargetPaths = new();
    private static readonly ConcurrentDictionary<string, string?> WorkTargetLibs = new();
    // 作品名缓存：同一作品的多个分卷只调一次 DL API
    private static readonly ConcurrentDictionary<string, string> WorkNameCache = new();

    // 被用户单独"停止"的作品：下载线程检测到后停到断点并退出当前文件，且其分卷置为已暂停('4')不再领取
    private static readonly ConcurrentDictionary<string, byte> PausedWorks = new();

    // 因 debrid-link 流量用尽而暂停的网盘：host -> 预计流量重置的 UTC 时间；特殊键 "*" 表示账户总流量用尽
    // （暂停所有论坛源解析）。到期后由流量重置监控线程把对应的已暂停分卷（'2' maxData/maxDataHost）
    // 重新排队（'0'）自动续传。
    private static readonly ConcurrentDictionary<string, DateTime> ExhaustedHosts = new();
    private const string AccountKey = "*";
    // 序列化流量用尽登记：避免多个下载线程同时命中流量用尽时重复请求 limits API、重复批量标记
    private static readonly object ExhaustLock = new();

    // 领取任务与占位需原子进行，避免多个下载线程领到同一条记录
    private static readonly object ClaimLock = new();

    // 正在解压的番号，避免多个分卷同时完成时对同一目录重复解压
    private static readonly object UnzipLock = new();
    private static readonly HashSet<string> Unzipping = [];

    // 串行化"移动到媒体库"：一次只移动一个作品，避免多个跨盘移动同时抢占磁盘 IO 互相拖慢
    private static readonly object MoveLock = new();

    // ---------- 作品目录 ----------

    /// <summary>由 UI 在入队时设定作品解压完成后要移动到的媒体库目标目录及所属媒体库名（立即落库防重启丢失）。</summary>
    public static void SetWorkTargetPath(string workId, string path, string? libName = null)
    {
        WorkTargetPaths[workId] = path;
        WorkTargetLibs[workId] = libName;
        Db.Execute(
            "UPDATE \"works\" SET \"target\" = @t, \"target_lib\" = @l WHERE \"work_id\" = @w",
            ("@t", path), ("@l", libName ?? ""), ("@w", workId));
    }

    /// <summary>作品子文件夹名：按设置以 RJ号 或 DL API 返回的作品名称命名。</summary>
    private static string FolderLeafName(string workId)
    {
        if (AppConfig.FolderNameMode != "work_name")
            return workId;
        var name = WorkNameCache.GetOrAdd(workId, id =>
        {
            try
            {
                return DlsiteApi.GetWorkNameAsync(id).GetAwaiter().GetResult() ?? "";
            }
            catch (Exception e)
            {
                Logger.Error(e, "获取作品名");
                return "";
            }
        });
        // 去掉 Windows 文件夹名中的非法字符；未获取到作品名时回退到 RJ 号
        foreach (var ch in "\\/:*?\"<>|")
            name = name.Replace(ch, ' ');
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        if (name.Length == 0)
        {
            Logger.Error($"{workId} 未从 DL API 获取到作品名称，文件夹按 RJ 号命名");
            return workId;
        }
        return name;
    }

    /// <summary>
    /// 作品的缓存文件夹完整路径（下载与解压都在此进行）。
    /// 优先使用 works 表中已持久化的路径，保证同一作品的所有分卷、以及进程重启后的
    /// 续传与解压都落在同一目录；未持久化时再按缓存路径设置计算。
    /// </summary>
    public static string WorkFolderPath(string workId)
    {
        var persisted = Db.Scalar(
            "SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string;
        if (!string.IsNullOrEmpty(persisted))
            return persisted;
        return Path.Combine(AppConfig.DownloadPath, FolderLeafName(workId));
    }

    /// <summary>把作品的缓存文件夹路径写入 works 表（仅在尚未写入时）。</summary>
    public static void PersistWorkFolder(string workId, string path) =>
        Db.Execute(
            "UPDATE \"works\" SET \"folder\" = @p WHERE \"work_id\" = @w AND (\"folder\" IS NULL OR \"folder\" = '')",
            ("@p", path), ("@w", workId));

    /// <summary>作品所属媒体库名：优先本次会话的内存选择，其次 works.target_lib（重启后用）。</summary>
    public static string? ReadWorkTargetLib(string workId)
    {
        if (WorkTargetLibs.TryGetValue(workId, out var lib) && !string.IsNullOrEmpty(lib))
            return lib;
        var fromDb = Db.Scalar(
            "SELECT \"target_lib\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string;
        return string.IsNullOrEmpty(fromDb) ? null : fromDb;
    }

    private static string? ReadWorkTarget(string workId)
    {
        if (WorkTargetPaths.TryGetValue(workId, out var path) && !string.IsNullOrEmpty(path))
            return path;
        var fromDb = Db.Scalar(
            "SELECT \"target\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string;
        return string.IsNullOrEmpty(fromDb) ? null : fromDb;
    }

    private static long FolderSize(string folder)
    {
        long total = 0;
        if (!Directory.Exists(folder))
            return 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                try { total += new FileInfo(file).Length; } catch (IOException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>
    /// 解压完成后把作品从缓存目录移动到媒体库目标目录，并更新 works.folder，返回最终目录路径。
    /// 未设置目标、目标即缓存、或移动失败时，保持在缓存目录。
    /// 移动期间把进度写入 UnzipProgress（state='moving'）供下载页显示。
    /// </summary>
    public static string MoveToTargetFolder(string workId, string cacheFolder)
    {
        var targetRoot = ReadWorkTarget(workId);
        if (string.IsNullOrEmpty(targetRoot))
        {
            Logger.Warning($"{workId} 未设置媒体库目标目录，保留在缓存目录: {cacheFolder}");
            return cacheFolder;
        }
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(cacheFolder));
        var dest = Path.Combine(targetRoot, leaf);
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(cacheFolder),
                StringComparison.OrdinalIgnoreCase))
            return cacheFolder;  // 缓存路径就是媒体库目录，无需移动

        // 串行化：一次只移动一个作品。等锁期间先显示"等待移动"，避免多个跨盘移动同时抢磁盘 IO。
        UnzipProgress[workId] = new UnzipProgressInfo { State = "movewait", Pct = 0 };
        lock (MoveLock)
        {
            // 跨盘移动是复制+删除，用目标目录已写入字节数 / 源目录总字节数估算进度
            var total = Math.Max(1, FolderSize(cacheFolder));
            UnzipProgress[workId] = new UnzipProgressInfo { State = "moving", Pct = 0 };
            using var stop = new ManualResetEventSlim(false);
            var monitor = new Thread(() =>
            {
                while (!stop.Wait(1000))
                {
                    var pct = (int)Math.Min(99, FolderSize(dest) * 100 / total);
                    // 移动若已结束，绝不能再写回进度，否则进度条目被"复活"卡在 99%
                    if (stop.IsSet)
                        break;
                    UnzipProgress[workId] = new UnzipProgressInfo { State = "moving", Pct = pct };
                }
            }) { IsBackground = true, Name = $"move-mon-{workId}" };
            monitor.Start();
            try
            {
                Directory.CreateDirectory(targetRoot);
                if (Directory.Exists(dest))
                {
                    Logger.Warning($"{workId} 媒体库已存在同名目录，先删除再移动: {dest}");
                    Directory.Delete(dest, true);
                }
                Logger.Info($"{workId} 开始移动到媒体库: {dest}");
                MoveDirectory(cacheFolder, dest);
            }
            catch (Exception e)
            {
                Logger.Error(e, "移动到媒体库");
                Logger.Error($"{workId} 移动到媒体库失败，保留在缓存目录: {cacheFolder}");
                return cacheFolder;
            }
            finally
            {
                stop.Set();
                monitor.Join();  // 等监控线程退出，确保返回后不会再写 UnzipProgress
            }
            // cover 随文件夹一起被移动，数据库里的绝对路径必须同步改写，否则主图无法显示
            Db.Execute(
                "UPDATE \"works\" SET \"folder\" = @d, \"cover\" = REPLACE(\"cover\", @s, @d) WHERE \"work_id\" = @w",
                ("@d", dest), ("@s", cacheFolder), ("@w", workId));
            Logger.Info($"{workId} 解压完成，已移动到媒体库: {dest}");
            return dest;
        }
    }

    /// <summary>跨盘安全的目录移动：同盘直接 Move，跨盘复制后删除源。</summary>
    private static void MoveDirectory(string source, string dest)
    {
        try
        {
            Directory.Move(source, dest);
        }
        catch (IOException)
        {
            CopyDirectory(source, dest);
            Directory.Delete(source, true);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>
    /// 重新搜索前清理该作品：删除已下载的分卷文件与作品文件夹，并清空其
    /// download_list 记录与对应的 works 行（无论何种状态），使其可从头重新下载。
    /// </summary>
    public static bool PurgeWorkDownload(string workId)
    {
        // 先取文件夹路径再删 works 行，否则删除后无法定位
        var folder = WorkFolderPath(workId);
        var removed = false;
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
            removed = !Directory.Exists(folder);
            Logger.Info($"{workId} 重新搜索，删除已下载文件夹: {folder}");
        }
        Db.Execute("DELETE FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", workId));
        // 重新搜索要彻底清除该作品记录，含已完成/已品悦，避免重复或残留
        Db.Execute("DELETE FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId));
        Db.Execute("DELETE FROM \"work_genres\" WHERE \"work_id\" = @w", ("@w", workId));
        WorkTargetPaths.TryRemove(workId, out _);
        WorkTargetLibs.TryRemove(workId, out _);
        WorkNameCache.TryRemove(workId, out _);
        PausedWorks.TryRemove(workId, out _);
        return removed;
    }

    /// <summary>单独停止某作品：其待下载分卷置为已暂停('4')，正在下载的分卷由下载线程停到断点。</summary>
    public static void PauseWork(string workId)
    {
        PausedWorks[workId] = 0;
        Db.Execute(
            "UPDATE \"download_list\" SET \"status\" = '4' WHERE \"work_id\" = @w AND \"status\" = '0'",
            ("@w", workId));
        Logger.Info($"{workId} 已单独停止下载");
    }

    /// <summary>单独（继续）下载某作品：解除暂停，把已暂停分卷重新排队并启动下载引擎。</summary>
    public static void ResumeWork(string workId)
    {
        PausedWorks.TryRemove(workId, out _);
        Db.Execute(
            "UPDATE \"download_list\" SET \"status\" = '0' WHERE \"work_id\" = @w AND \"status\" = '4'",
            ("@w", workId));
        Start();
        Logger.Info($"{workId} 已继续下载");
    }

    /// <summary>
    /// 单独删除某作品：停止其下载，删除 download_list / works / work_genres 记录；
    /// 若文件仍在下载缓存目录则一并删除（不动已移入媒体库的文件）。
    /// </summary>
    public static void DeleteWork(string workId)
    {
        PausedWorks[workId] = 0;   // 让正在下载该作品的线程停下，避免边删边写
        var folder = Db.Scalar(
            "SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string;
        Db.Execute("DELETE FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", workId));
        Db.Execute("DELETE FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId));
        Db.Execute("DELETE FROM \"work_genres\" WHERE \"work_id\" = @w", ("@w", workId));
        if (!string.IsNullOrEmpty(folder) && IsUnderDownloadCache(folder) && Directory.Exists(folder))
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
            Logger.Info($"{workId} 已删除下载缓存目录: {folder}");
        }
        WorkTargetPaths.TryRemove(workId, out _);
        WorkTargetLibs.TryRemove(workId, out _);
        WorkNameCache.TryRemove(workId, out _);
        PausedWorks.TryRemove(workId, out _);
        Logger.Info($"{workId} 已从下载列表删除");
    }

    /// <summary>路径是否位于下载缓存目录下（避免误删已移入媒体库的文件）。</summary>
    private static bool IsUnderDownloadCache(string folder)
    {
        try
        {
            var cache = Path.GetFullPath(AppConfig.DownloadPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(folder);
            return full.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(full, cache, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            return false;
        }
    }

    // ---------- 全局限速（令牌桶）----------

    private sealed class RateLimiter
    {
        private readonly object _lock = new();
        private long _rate;            // 字节/秒，0=不限速
        private double _allowance;
        private DateTime? _last;

        public void SetRate(long bytesPerSec)
        {
            lock (_lock)
            {
                _rate = Math.Max(0, bytesPerSec);
                if (_rate == 0)
                {
                    _allowance = 0;
                    _last = null;
                }
            }
        }

        /// <summary>登记本次已下载 n 字节，返回需要 sleep 的毫秒数（计算在锁内，睡眠在锁外）。</summary>
        public int Consume(int nbytes)
        {
            if (_rate <= 0)
                return 0;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                _last ??= now;
                _allowance += (now - _last.Value).TotalSeconds * _rate;
                _last = now;
                if (_allowance > _rate)   // 突发上限：最多积累 1 秒额度
                    _allowance = _rate;
                _allowance -= nbytes;
                if (_allowance >= 0)
                    return 0;
                return (int)(-_allowance * 1000 / _rate);
            }
        }
    }

    private static readonly RateLimiter Limiter = new();

    // ---------- 下载 ----------

    private static void SetStatus(string key, string status, int? progress = null)
    {
        if (progress is { } p)
            Db.Execute(
                "UPDATE \"download_list\" SET \"status\" = @s, \"long\" = @p WHERE \"UUID\" = @k",
                ("@s", status), ("@p", p.ToString()), ("@k", key));
        else
            Db.Execute(
                "UPDATE \"download_list\" SET \"status\" = @s WHERE \"UUID\" = @k",
                ("@s", status), ("@k", key));
    }

    /// <summary>标记某分卷解析失败('2')，并记录失败原因（debrid-link 错误码）。</summary>
    private static void SetParseFailed(string key, string? error)
    {
        Db.Execute(
            "UPDATE \"download_list\" SET \"status\" = '2', \"error\" = @e WHERE \"UUID\" = @k",
            ("@e", error), ("@k", key));
    }

    // ---------- debrid-link 流量用尽自动暂停 / 重置后自动续传 ----------

    /// <summary>从下载链接取主机名（去 www. 前缀），失败返回空串。</summary>
    private static string HostOf(string url)
    {
        try
        {
            var h = new Uri(url).Host.ToLowerInvariant();
            return h.StartsWith("www.") ? h[4..] : h;
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// 登记某网盘（maxDataHost）或账户总流量（maxData）已用尽：记录预计重置时间，并把该网盘下
    /// （账户级则为所有论坛源）待下载的分卷一并标记为流量用尽('2')暂停，避免继续解析空耗流量。
    /// 重置时间到后由 <see cref="TrafficResetLoop"/> 自动重新排队续传。
    /// </summary>
    private static void RegisterTrafficExhausted(string url, string error)
    {
        var key = error == "maxData" ? AccountKey : HostOf(url);
        if (key.Length == 0)
            return;
        lock (ExhaustLock)
        {
            // 仅在尚未记录或已过期时请求一次 limits API 取重置时间，避免每条失败都打 API
            if (!ExhaustedHosts.TryGetValue(key, out var existing) || existing <= DateTime.UtcNow)
            {
                var seconds = FetchResetSeconds();
                var resetUtc = DateTime.UtcNow.AddSeconds(seconds > 0 ? seconds : 3600);
                ExhaustedHosts[key] = resetUtc;
                Logger.Warning(
                    $"debrid-link {(key == AccountKey ? "账户总流量" : key)} 流量用尽，暂停下载，" +
                    $"预计 {resetUtc.ToLocalTime():yyyy-MM-dd HH:mm} 重置后自动继续");
            }
            PauseHostDownloads(key, error);
        }
    }

    /// <summary>把某网盘（账户级则为所有论坛源）当前待下载('0')的分卷标记为流量用尽('2')暂停。</summary>
    private static void PauseHostDownloads(string key, string error)
    {
        var rows = Db.Select(
            "SELECT \"UUID\", \"url\" FROM \"download_list\" WHERE \"status\" = '0' AND (\"source\" IS NULL OR \"source\" != 'asmr')");
        foreach (var r in rows ?? [])
        {
            var url = r[1] as string ?? "";
            if (key == AccountKey || HostOf(url) == key)
                SetParseFailed(r[0] as string ?? "", error);
        }
    }

    /// <summary>查询 debrid-link 距离下载流量重置的秒数（失败或无数据返回 0）。</summary>
    private static double FetchResetSeconds()
    {
        try
        {
            JsonElement? value;
            using (var client = new DebridLinkClient())
                value = client.DownloadLimitsAsync().GetAwaiter().GetResult();
            if (value is { } v && v.ValueKind == JsonValueKind.Object &&
                v.TryGetProperty("nextResetSeconds", out var reset) &&
                reset.ValueKind == JsonValueKind.Object &&
                reset.TryGetProperty("value", out var rv) &&
                rv.ValueKind == JsonValueKind.Number)
                return rv.GetDouble();
        }
        catch (Exception e)
        {
            Logger.Error(e, "查询流量重置时间");
        }
        return 0;
    }

    /// <summary>
    /// 流量重置监控：每分钟检查因流量用尽而暂停的网盘，重置时间已到的把其暂停分卷（'2'）重新排队（'0'）
    /// 供下载线程重新解析续传；若仍未真正重置会再次失败并以新的重置时间重新登记，自我修正。
    /// </summary>
    private static void TrafficResetLoop()
    {
        while (!_stopRequested)
        {
            for (var i = 0; i < 60 && !_stopRequested; i++)
                Thread.Sleep(1000);
            if (_stopRequested)
                return;
            if (ExhaustedHosts.IsEmpty)
                continue;
            var now = DateTime.UtcNow;
            foreach (var kv in ExhaustedHosts)
            {
                if (kv.Value > now)
                    continue;
                if (ExhaustedHosts.TryRemove(kv.Key, out _))
                    RequeueExhausted(kv.Key);
            }
        }
    }

    /// <summary>把某网盘（账户级则为所有论坛源）因流量用尽而暂停('2')的分卷重新排队('0')续传。</summary>
    private static void RequeueExhausted(string key)
    {
        var err = key == AccountKey ? "maxData" : "maxDataHost";
        var rows = Db.Select(
            "SELECT \"UUID\", \"url\" FROM \"download_list\" WHERE \"status\" = '2' AND \"error\" = @e",
            ("@e", err));
        var count = 0;
        foreach (var r in rows ?? [])
        {
            var url = r[1] as string ?? "";
            if (key != AccountKey && HostOf(url) != key)
                continue;
            Db.Execute(
                "UPDATE \"download_list\" SET \"status\" = '0', \"error\" = NULL WHERE \"UUID\" = @k",
                ("@k", r[0] as string ?? ""));
            count++;
        }
        if (count > 0)
        {
            Logger.Info(
                $"debrid-link {(key == AccountKey ? "账户总流量" : key)} 流量已重置，" +
                $"重新排队 {count} 个分卷继续下载");
            Start();  // 引擎若已空闲退出，确保重新拉起下载线程
        }
    }

    /// <summary>从队列原子地领取一条待下载任务并立即标记为下载中；无任务返回 null。</summary>
    private static (string Key, string WorkId, string Url, string Source, string SubPath)? ClaimNext()
    {
        lock (ClaimLock)
        {
            var rows = Db.Select(
                "SELECT \"UUID\", \"work_id\", \"url\", \"source\", \"sub_path\" FROM \"download_list\" WHERE \"status\" = '0' LIMIT 1");
            if (rows is not { Count: > 0 })
                return null;
            var key = rows[0][0] as string ?? "";
            // 立即占位，其它线程不会重复领取；同时清除上次解析失败原因
            Db.Execute(
                "UPDATE \"download_list\" SET \"status\" = '3', \"long\" = '0', \"error\" = NULL WHERE \"UUID\" = @k",
                ("@k", key));
            return (key, rows[0][1] as string ?? "", rows[0][2] as string ?? "",
                rows[0][3] as string ?? "", rows[0][4] as string ?? "");
        }
    }

    /// <summary>探测文件总大小；返回 (总字节数, 是否支持 Range)。</summary>
    private static (long Total, bool Range) ProbeSize(HttpClient client, string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var len = response.Content.Headers.ContentRange?.Length;
                if (len is { } l)
                    return (l, true);
            }
            if (response.StatusCode == HttpStatusCode.OK)
                return (response.Content.Headers.ContentLength ?? 0, false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        return (0, false);
    }

    private static string MetaPath(string filePath) => filePath + ".dlmeta";

    /// <summary>单连接下载（断点续传 + 暂停 + 低速重试），返回 done/paused/slow/failed。</summary>
    private static string DownloadSingle(HttpClient client, string url, string filePath,
        string filename, string key, string workId)
    {
        long downloaded = 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (File.Exists(filePath))
        {
            downloaded = new FileInfo(filePath).Length;
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(downloaded, null);
        }

        HttpResponseMessage response;
        try
        {
            response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Logger.Error($"{filename} 下载请求失败: {e.Message}");
            SetStatus(key, "0");
            return "failed";
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                return "done";  // 文件已完整
            if (response.StatusCode != HttpStatusCode.OK &&
                response.StatusCode != HttpStatusCode.PartialContent)
            {
                Logger.Error($"{filename} 下载失败 HTTP {(int)response.StatusCode}");
                SetStatus(key, "0");
                return "failed";
            }
            var append = response.StatusCode == HttpStatusCode.PartialContent;
            if (!append)
                downloaded = 0;  // 服务器不支持续传，从头下载

            var totalSize = downloaded + (response.Content.Headers.ContentLength ?? 0);
            var minSpeedBytes = AppConfig.MinSpeedKb * 1024L;
            // 全局限速：所有下载线程共享同一总上限；限速时关闭低速重试，避免被限的速度误判为卡死
            var speedLimited = AppConfig.SpeedLimitKb > 0;
            Limiter.SetRate(AppConfig.SpeedLimitKb * 1024L);

            var result = "done";
            double speed = 0;
            var speedTime = DateTime.UtcNow;
            var speedBytes = downloaded;
            DateTime? lowSpeedStart = null;
            var lastDbWrite = DateTime.MinValue;

            using var stream = response.Content.ReadAsStream();
            using var file = new FileStream(filePath, append ? FileMode.Append : FileMode.Create,
                FileAccess.Write);
            var buffer = new byte[Chunk];
            while (true)
            {
                if (_stopRequested)
                {
                    result = "paused";
                    break;
                }
                if (PausedWorks.ContainsKey(workId))
                {
                    result = "workpaused";   // 用户单独停止该作品：停到断点，置为已暂停
                    break;
                }
                int read;
                try
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                catch (IOException e)
                {
                    Logger.Error($"{filename} 下载中断: {e.Message}");
                    SetStatus(key, "0");
                    return "failed";
                }
                if (read <= 0)
                    break;
                file.Write(buffer, 0, read);
                downloaded += read;
                var sleepMs = Limiter.Consume(read);
                if (sleepMs > 0)
                    Thread.Sleep(sleepMs);
                var now = DateTime.UtcNow;
                if ((now - speedTime).TotalSeconds >= 1)
                {
                    speed = (downloaded - speedBytes) / (now - speedTime).TotalSeconds;
                    speedTime = now;
                    speedBytes = downloaded;
                    if (minSpeedBytes > 0 && speed > 0 && !speedLimited)
                    {
                        if (speed < minSpeedBytes)
                        {
                            lowSpeedStart ??= now;
                            if ((now - lowSpeedStart.Value).TotalSeconds >= 30)
                            {
                                result = "slow";
                                break;
                            }
                        }
                        else
                        {
                            lowSpeedStart = null;
                        }
                    }
                }
                DownloadProgress[key] = new DownloadProgressInfo
                {
                    Downloaded = downloaded, Total = totalSize, Speed = speed,
                };
                if (totalSize > 0 && (now - lastDbWrite).TotalSeconds >= 2)
                {
                    SetStatus(key, "3", (int)(downloaded * 100 / totalSize));
                    lastDbWrite = now;
                }
            }
            if (result is "paused" or "slow" or "workpaused")
            {
                var pct = totalSize > 0 ? (int)(downloaded * 100 / totalSize) : 0;
                // 单独停止：置为已暂停('4')，不再被领取；全局暂停/低速：置为待下载('0')可续传
                SetStatus(key, result == "workpaused" ? "4" : "0", pct);
                if (result == "slow")
                    Logger.Warning($"{filename} 速度持续低于 {AppConfig.MinSpeedKb} KB/s，重新排队");
            }
            return result;
        }
    }

    /// <summary>下载单个文件：清理旧版分段下载元数据后单连接下载，返回 done/paused/slow/failed。</summary>
    private static string DownloadFile(HttpClient client, string directUrl, string filePath,
        string filename, string key, string workId)
    {
        var (totalSize, _) = ProbeSize(client, directUrl);

        // 旧版分段下载遗留的元数据：其预分配的整文件内容不可信，连同文件一起清掉后重下
        if (File.Exists(MetaPath(filePath)))
        {
            try { File.Delete(MetaPath(filePath)); } catch (IOException) { }
            if (File.Exists(filePath))
                try { File.Delete(filePath); } catch (IOException) { }
        }

        // 已完整下载
        if (totalSize > 0 && File.Exists(filePath) && new FileInfo(filePath).Length == totalSize)
            return "done";

        return DownloadSingle(client, directUrl, filePath, filename, key, workId);
    }

    /// <summary>单个下载线程：不断领取队列任务，通过 debrid-link 中转下载（支持断点续传）。</summary>
    private static void WorkerLoop()
    {
        using var client = Http.CreateClient(Timeout.InfiniteTimeSpan);
        while (true)
        {
            string? key = null;
            try
            {
                if (_stopRequested)
                    return;

                var claimed = ClaimNext();
                if (claimed is null)
                {
                    // 队列空，等待新任务；期间收到暂停信号立即退出
                    for (var i = 0; i < 100; i++)
                    {
                        if (_stopRequested)
                            return;
                        Thread.Sleep(100);
                    }
                    continue;
                }

                var (k, workId, url, source, subPath) = claimed.Value;
                key = k;
                var isAsmr = source == "asmr";

                string directUrl;
                string filename;
                if (isAsmr)
                {
                    // asmr.one：download_list.url 本身就是直链，无需 debrid 解析；
                    // sub_path 为作品内相对路径（含文件名），按其重建目录树落盘
                    directUrl = url;
                    filename = string.IsNullOrEmpty(subPath)
                        ? url.TrimEnd('/').Split('/')[^1].Split('?')[0]
                        : Path.GetFileName(subPath);
                }
                else
                {
                    Logger.Info($"通过 debrid-link 解析: {url}");
                    System.Text.Json.JsonElement? value;
                    string? parseError;
                    using (var debrid = new DebridLinkClient())
                        (value, parseError) = debrid.AddDownloadDetailedAsync(url).GetAwaiter().GetResult();
                    directUrl = value is { } v ? DlsiteApi.JStr(v, "downloadUrl") : "";
                    if (string.IsNullOrEmpty(directUrl))
                    {
                        Logger.Error($"{workId} debrid-link 解析失败: {url} ({parseError})");
                        // 流量用尽：暂停该网盘（或账户级全部论坛源）下载，等流量重置后自动继续
                        if (parseError is "maxData" or "maxDataHost")
                            RegisterTrafficExhausted(url, parseError);
                        SetParseFailed(key, parseError);
                        continue;
                    }
                    filename = value is { } v2 ? DlsiteApi.JStr(v2, "name") : "";
                    if (string.IsNullOrEmpty(filename))
                        filename = directUrl.TrimEnd('/').Split('/')[^1].Split('?')[0];
                }

                var downloadPath = WorkFolderPath(workId);
                // 首个分卷处理时落库缓存目录，保证后续分卷、重启续传后的解压/入库都用同一目录
                PersistWorkFolder(workId, downloadPath);
                // asmr 保留作品内子目录结构；其它源扁平落盘
                var filePath = isAsmr && !string.IsNullOrEmpty(subPath)
                    ? Path.Combine(downloadPath, subPath.Replace('/', Path.DirectorySeparatorChar))
                    : Path.Combine(downloadPath, filename);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? downloadPath);

                var result = DownloadFile(client, directUrl, filePath, filename, key, workId);
                DownloadProgress.TryRemove(key, out _);
                if (result == "paused")
                    return;  // 全局暂停：部分文件保留在磁盘上，下次从断点续传
                if (result == "workpaused")
                    continue;  // 单独停止该作品：保留断点，本线程继续领取其它作品的任务
                if (result is "slow" or "failed")
                {
                    Thread.Sleep(5000);
                    continue;
                }

                Logger.Info($"{workId}已完成下载");
                SetStatus(key, "1", 100);
                MarkWorkDownloaded(workId);
                // asmr 直链下载无压缩包，下完直接入库；其它源走自动解压
                if (isAsmr)
                    AsmrFinalizeIfDone(workId);
                else
                    AutoUnzipIfDone(workId);
            }
            catch (Exception e)
            {
                Logger.Error(e, "下载线程");
                if (key != null)
                {
                    DownloadProgress.TryRemove(key, out _);
                    SetStatus(key, "0");  // 重新排队，下次从断点继续
                }
                Thread.Sleep(5000);
            }
        }
    }

    /// <summary>下载调度：把遗留的"下载中"任务重新排队，再按设置启动 N 个下载线程。</summary>
    private static void Run()
    {
        // 上次运行中断时遗留的"下载中"任务重新排队，靠断点续传从已下载部分继续
        Db.Execute("UPDATE \"download_list\" SET \"status\" = '0' WHERE \"status\" = '3'");

        // 上次因流量用尽而暂停的分卷（'2' maxData/maxDataHost）：内存暂停状态随重启丢失，
        // 一律重新排队重试——流量若已重置则直接续传，否则再次失败并以新的重置时间重新暂停（自我修正）
        Db.Execute(
            "UPDATE \"download_list\" SET \"status\" = '0', \"error\" = NULL WHERE \"status\" = '2' AND \"error\" IN ('maxData', 'maxDataHost')");

        // 上次中途中断的作品：分卷已全部下载完但还未入库，重新触发解压/收尾 → 移动 → 入库
        var stuck = Db.Select("""
            SELECT w."work_id", w."source" FROM "works" w
            WHERE w."state" IN ('下载中', '已下载')
            AND EXISTS (SELECT 1 FROM "download_list" d WHERE d."work_id" = w."work_id")
            AND NOT EXISTS (SELECT 1 FROM "download_list" d
                            WHERE d."work_id" = w."work_id" AND d."status" != '1')
            """);
        if (stuck != null)
            foreach (var row in stuck)
            {
                var stuckId = row[0] as string ?? "";
                Logger.Info($"{stuckId} 分卷已全部下载但未完成入库，重新触发收尾");
                MarkWorkDownloaded(stuckId);
                if (row[1] as string == "asmr")
                    AsmrFinalizeIfDone(stuckId);
                else
                    AutoUnzipIfDone(stuckId);
            }

        // 流量重置监控线程：网盘流量用尽暂停后，到重置时间自动把暂停分卷重新排队续传
        new Thread(TrafficResetLoop) { IsBackground = true, Name = "traffic-reset-monitor" }.Start();

        var workers = new List<Thread>();
        for (var i = 0; i < AppConfig.DownloadProcesses; i++)
        {
            var thread = new Thread(WorkerLoop) { IsBackground = true, Name = $"download-{i}" };
            thread.Start();
            workers.Add(thread);
        }
        foreach (var thread in workers)
            thread.Join();
    }

    /// <summary>该番号的所有任务都下载完成后，works 表状态从 下载中 改为 已下载。</summary>
    private static void MarkWorkDownloaded(string workId)
    {
        var pending = Db.Scalar(
            "SELECT COUNT(*) FROM \"download_list\" WHERE \"work_id\" = @w AND \"status\" != '1'",
            ("@w", workId));
        if (pending is not long and not int || Convert.ToInt64(pending) != 0)
            return;  // 还有未完成的分卷
        Db.Execute(
            "UPDATE \"works\" SET \"state\" = '已下载' WHERE \"work_id\" = @w AND \"state\" = '下载中'",
            ("@w", workId));
    }

    /// <summary>开启自动解压时，该番号的所有任务都下载完成后在后台线程解压（每个番号只解压一次）。</summary>
    private static void AutoUnzipIfDone(string workId)
    {
        if (!AppConfig.AutoUnzip)
            return;
        var pending = Db.Scalar(
            "SELECT COUNT(*) FROM \"download_list\" WHERE \"work_id\" = @w AND \"status\" != '1'",
            ("@w", workId));
        if (pending is null || Convert.ToInt64(pending) != 0)
            return;  // 还有未完成的分卷，等全部下载完再解压

        lock (UnzipLock)
        {
            if (!Unzipping.Add(workId))
                return;  // 已有线程在解压该番号（并发完成时去重）
        }

        // 立即置为"待解压"，避免下载完成到解压线程启动之间 UI 短暂显示"已完成"
        UnzipProgress[workId] = new UnzipProgressInfo { State = "pending", Pct = 0 };
        Logger.Info($"{workId} 下载完成，开始自动解压");
        new Thread(() => RunUnzip(workId)) { IsBackground = true, Name = $"unzip-{workId}" }.Start();
    }

    /// <summary>
    /// asmr.one 作品下完后的收尾（无解压）：所有分卷完成后在后台移动到媒体库、入库、
    /// 补全元数据，并在 asmr.one 站上回标为「听完」。每个作品只执行一次。
    /// </summary>
    private static void AsmrFinalizeIfDone(string workId)
    {
        var pending = Db.Scalar(
            "SELECT COUNT(*) FROM \"download_list\" WHERE \"work_id\" = @w AND \"status\" != '1'",
            ("@w", workId));
        if (pending is null || Convert.ToInt64(pending) != 0)
            return;  // 还有未完成的文件

        lock (UnzipLock)
        {
            if (!Unzipping.Add(workId))
                return;  // 已有线程在收尾该作品（并发完成时去重）
        }

        UnzipProgress[workId] = new UnzipProgressInfo { State = "moving", Pct = 0 };
        Logger.Info($"{workId} asmr 下载完成，开始入库");
        new Thread(() => RunAsmrFinalize(workId)) { IsBackground = true, Name = $"asmr-finalize-{workId}" }.Start();
    }

    /// <summary>在后台把 asmr 作品移入媒体库并回标，进度由 MoveToTargetFolder 维护。</summary>
    private static void RunAsmrFinalize(string workId)
    {
        try
        {
            // 复用解压流程的入库收尾：移动到媒体库 + 标记已品悦 + 补全元数据
            UnzipService.FinalizeIntoLibrary(workId, WorkFolderPath(workId));
            // 在 asmr.one 站上回标为「听完」
            var asmrId = Db.Scalar(
                "SELECT \"asmr_id\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string;
            if (long.TryParse(asmrId, out var id) && id > 0)
                AsmrApi.ReviewAsync(id, listened: true).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Logger.Error(e, "asmr 入库收尾");
        }
        finally
        {
            UnzipProgress.TryRemove(workId, out _);
            lock (UnzipLock)
                Unzipping.Remove(workId);
        }
    }

    /// <summary>在后台解压一个作品，并用独立线程按解压产出量估算进度写入 UnzipProgress。</summary>
    private static void RunUnzip(string workId)
    {
        var folder = WorkFolderPath(workId);
        long total = 0;
        foreach (var f in UnzipService.GetAllArchiveFiles(folder))
            try { total += new FileInfo(f).Length; } catch (IOException) { }
        if (total == 0)
            total = 1;

        using var stop = new ManualResetEventSlim(false);
        var monitor = new Thread(() =>
        {
            while (!stop.Wait(1000))
            {
                if (UnzipProgress.TryGetValue(workId, out var current)
                    && current.State is "moving" or "movewait")
                    continue;  // 已进入等待移动/移动阶段，进度改由移动逻辑维护
                if (!Directory.Exists(folder))
                    continue;  // 解压完成后已移动到媒体库，保持上次进度直到解压线程收尾
                var pct = (int)Math.Min(99, UnzipService.ExtractedSize(folder) * 100 / total);
                // 解压流程若已收尾，不能把弹出的条目再写回去
                if (stop.IsSet)
                    break;
                UnzipProgress[workId] = new UnzipProgressInfo { State = "extracting", Pct = pct };
            }
        }) { IsBackground = true, Name = $"unzip-mon-{workId}" };

        UnzipProgress[workId] = new UnzipProgressInfo { State = "extracting", Pct = 0 };
        monitor.Start();
        try
        {
            UnzipService.Unzip(workId);
        }
        finally
        {
            stop.Set();
            monitor.Join();  // 先等监控线程退出再弹出条目，避免条目被"复活"卡在解压中
            UnzipProgress.TryRemove(workId, out _);
            lock (UnzipLock)
                Unzipping.Remove(workId);
        }
    }

    // ---------- 启停 ----------

    /// <summary>启动后台下载线程；已在运行时不重复启动，返回是否新启动。</summary>
    public static bool Start()
    {
        if (IsRunning)
            return false;
        _stopRequested = false;
        _mainThread = new Thread(Run) { IsBackground = true, Name = "download-main" };
        _mainThread.Start();
        return true;
    }

    /// <summary>请求暂停下载：当前文件停到断点后线程退出，再次开始时续传。</summary>
    public static void Stop() => _stopRequested = true;

    public static bool StopRequested => _stopRequested;

    public static bool IsRunning => _mainThread is { IsAlive: true };
}
