using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DLsiteMedia.Core;

namespace DLsiteMedia.Services;

/// <summary>
/// 内嵌的外部访问 HTTP 服务：把媒体库（已品悦作品）以响应式网页的形式暴露给手机/电脑浏览器。
///
/// 不用 <see cref="HttpListener"/>：Windows 下监听非 localhost 地址需要管理员级 URL-ACL 预留，
/// 普通用户无法直接对外开放。这里用裸 <see cref="TcpListener"/> 自行实现极简 HTTP/1.1，
/// 可无特权绑定 0.0.0.0，零额外依赖。所有 DB 读写沿用 <see cref="Db"/>（WAL，并发读安全）。
///
/// 路由全部镜像 <c>Views/MediaLibPage</c> 的查询：媒体库→社团→作品→详情，外加标签/作品形式/收藏/搜索/排序/筛选。
/// </summary>
public static class WebServer
{
    private static TcpListener? _listener;
    private static Thread? _acceptThread;
    private static volatile bool _running;
    private static string _expectedToken = "";   // SHA256(password) 的十六进制；密码为空时不鉴权
    private static bool _authRequired;
    private static readonly object Sync = new();
    private static readonly object LibSync = new();   // 媒体库配置读改写串行化（并发请求安全）

    public static bool IsRunning => _running;
    public static int RunningPort { get; private set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Dictionary<string, string> AgeMap = new()
    {
        ["1"] = "全年龄", ["2"] = "R-15", ["3"] = "R-18",
    };

    // 详情页可点击筛选字段：label 对应的 works 列（白名单，防 SQL 注入）。
    private static readonly HashSet<string> FilterCols =
        ["maker_name", "series", "scenario", "illust", "voice_actor", "age_category"];

    private static readonly string[] ImageExts = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    // 搜索页：番号 → DL API 作品数据缓存，供加入下载时写 works 表（避免重复请求）。
    private static readonly ConcurrentDictionary<string, DlWork?> SearchCache = new();

    private static readonly Regex WorkIdRe = new(@"^(?:RJ|BJ|VJ)\d+$", RegexOptions.Compiled);

    // download_list.status → (显示文本, 颜色)
    private static readonly Dictionary<string, (string Text, string Color)> StatusMap = new()
    {
        ["0"] = ("等待下载", "#facc15"),
        ["3"] = ("下载中", "#60a5fa"),
        ["1"] = ("已完成", "#4ade80"),
        ["2"] = ("解析失败", "#f87171"),
        ["4"] = ("已暂停", "#9aa4b2"),
    };

    // 设置页可写入的 section/key 白名单（web_server 段不允许从网页改，避免自我锁死）。
    private static readonly HashSet<string> SettingsWhitelist =
    [
        "downpath/downpath",
        "down_list/auto_download", "down_list/auto_unzip", "down_list/download_processes",
        "down_list/min_speed", "down_list/speed_limit",
        "proxy/openproxy", "proxy/host", "proxy/port", "proxy/type",
        "debrid/api_key", "language/lang", "loglevel/level", "encoding/encoding",
    ];

    // ---------- 生命周期 ----------

    /// <summary>按指定端口/密码启动服务；已在运行则先停止。返回是否成功及错误信息。</summary>
    public static (bool Ok, string? Error) Start(int port, string password)
    {
        lock (Sync)
        {
            Stop();
            _authRequired = password.Length > 0;
            _expectedToken = _authRequired ? Sha256Hex(password) : "";
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                _listener = null;
                Logger.Error($"外部访问启动失败（端口 {port}）：{e.Message}");
                return (false, e.Message);
            }
            _running = true;
            RunningPort = port;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "WebServerAccept" };
            _acceptThread.Start();
            Logger.Info($"外部访问已开启：端口 {port}，鉴权 {(_authRequired ? "开" : "关")}");
            return (true, null);
        }
    }

    /// <summary>按当前配置启动（设置页/启动时调用）。</summary>
    public static (bool Ok, string? Error) StartFromConfig() =>
        Start(AppConfig.WebPort, AppConfig.WebPassword);

    public static void Stop()
    {
        lock (Sync)
        {
            if (!_running && _listener == null)
                return;
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // 关闭监听器时的异常忽略
            }
            _listener = null;
            RunningPort = 0;
        }
    }

    private static void AcceptLoop()
    {
        var listener = _listener;
        while (_running && listener != null)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                break;  // 监听器已关闭
            }
            // 交给线程池起步（同步读请求头不占用 accept 线程），进入外部网络调用后 await 又会让出线程
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    // ---------- 请求处理 ----------

    private static async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 30000;
                using var stream = client.GetStream();
                var request = ReadRequest(stream);
                if (request == null)
                    return;
                // 路由异步到底：处理链路中的外部网络调用（debrid/dlsite/asmr）await 让出线程池线程，
                // 多客户端并发访问时线程不被阻塞、响应不排队。
                await RouteAsync(stream, request);
            }
        }
        catch (Exception e)
        {
            if (e is not (IOException or SocketException or ObjectDisposedException))
                Logger.Error($"外部访问请求异常：{e.Message}");
        }
    }

    private sealed class Request
    {
        public string Method = "GET";
        public string Path = "/";
        public Dictionary<string, string> Query = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = [];
    }

    private static Request? ReadRequest(NetworkStream stream)
    {
        // 读到 \r\n\r\n 为止即为请求头
        var header = new List<byte>(1024);
        int b;
        while ((b = stream.ReadByte()) != -1)
        {
            header.Add((byte)b);
            var n = header.Count;
            if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 &&
                header[n - 2] == 13 && header[n - 1] == 10)
                break;
            if (n > 65536)
                return null;  // 请求头过大
        }
        if (header.Count == 0)
            return null;

        var text = Encoding.ASCII.GetString(header.ToArray());
        var lines = text.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
            return null;

        var req = new Request { Method = requestLine[0].ToUpperInvariant() };
        var rawUrl = requestLine[1];
        var qIndex = rawUrl.IndexOf('?');
        if (qIndex >= 0)
        {
            req.Path = UrlDecode(rawUrl[..qIndex]);
            ParseQuery(rawUrl[(qIndex + 1)..], req.Query);
        }
        else
        {
            req.Path = UrlDecode(rawUrl);
        }

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            req.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (req.Headers.TryGetValue("Content-Length", out var clv) &&
            int.TryParse(clv, out var len) && len is > 0 and <= 1_048_576)
        {
            var body = new byte[len];
            var read = 0;
            while (read < len)
            {
                var got = stream.Read(body, read, len - read);
                if (got <= 0)
                    break;
                read += got;
            }
            req.Body = body;
        }
        return req;
    }

    private static void ParseQuery(string query, Dictionary<string, string> into)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
                into[UrlDecode(pair)] = "";
            else
                into[UrlDecode(pair[..eq])] = UrlDecode(pair[(eq + 1)..]);
        }
    }

    private static string UrlDecode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    // ---------- 路由 ----------

    private static async Task RouteAsync(NetworkStream stream, Request req)
    {
        var path = req.Path;
        if (path is "/" or "/index.html")
        {
            WriteBytes(stream, 200, "OK", "text/html; charset=utf-8", WebAssets.IndexHtml());
            return;
        }
        if (path == "/api/state")
        {
            WriteJson(stream, 200, new { needLogin = _authRequired && !Authed(req) });
            return;
        }
        if (path == "/api/login")
        {
            HandleLogin(stream, req);
            return;
        }

        // 其余 /api 与资源端点需鉴权
        if (!Authed(req))
        {
            WriteJson(stream, 401, new { error = "unauthorized" });
            return;
        }

        try
        {
            switch (path)
            {
                case "/api/libs": ApiLibs(stream); break;
                case "/api/makers": ApiMakers(stream, req); break;
                case "/api/works": ApiWorks(stream, req); break;
                case "/api/genreworks": ApiGenreWorks(stream, req); break;
                case "/api/libworks": ApiLibWorks(stream, req); break;
                case "/api/genres": ApiGenres(stream); break;
                case "/api/types": ApiTypes(stream); break;
                case "/api/favorites": ApiFavorites(stream, req); break;
                case "/api/filter": ApiFilter(stream, req); break;
                case "/api/detail": ApiDetail(stream, req); break;
                case "/api/toggle": ApiToggle(stream, req); break;
                case "/api/searchworks": ApiSearchWorks(stream, req); break;
                case "/api/movework": await ApiMoveWorkAsync(stream, req); break;
                case "/api/cover": ApiCover(stream, req); break;
                case "/api/asset": ApiAsset(stream, req); break;
                case "/api/files": ApiFiles(stream, req); break;
                case "/api/file": ApiFile(stream, req); break;
                // 搜索
                case "/api/search": await ApiSearchAsync(stream, req); break;
                case "/api/maker": await ApiMakerAsync(stream, req); break;
                case "/api/asscan": await ApiAsScanAsync(stream, req); break;
                case "/api/thumb": await ApiThumbAsync(stream, req); break;
                case "/api/posturls": await ApiPostUrlsAsync(stream, req); break;
                case "/api/checkhost": await ApiCheckHostAsync(stream, req); break;
                case "/api/downtargets": ApiDownTargets(stream); break;
                case "/api/enqueue": ApiEnqueue(stream, req); break;
                // 下载 / 已下载
                case "/api/downloads": ApiDownloads(stream); break;
                case "/api/usage": await ApiUsageAsync(stream); break;
                case "/api/engine": ApiEngine(stream, req); break;
                case "/api/reparse": ApiReparse(stream, req); break;
                case "/api/pausework": ApiPauseWork(stream, req); break;
                case "/api/resumework": ApiResumeWork(stream, req); break;
                case "/api/deletework": ApiDeleteWork(stream, req); break;
                case "/api/research": ApiResearch(stream, req); break;
                case "/api/cleardone": ApiClearDone(stream); break;
                case "/api/clearall": ApiClearAll(stream); break;
                case "/api/downloaded": ApiDownloaded(stream); break;
                case "/api/mark": ApiMark(stream, req); break;
                // 设置
                case "/api/settings": if (req.Method == "POST") ApiSettingsWrite(stream, req); else ApiSettings(stream); break;
                case "/api/debridtest": await ApiDebridTestAsync(stream, req); break;
                // 媒体库管理
                case "/api/medialibs": ApiMediaLibs(stream); break;
                case "/api/lib/create": ApiLibCreate(stream, req); break;
                case "/api/lib/delete": ApiLibDelete(stream, req); break;
                case "/api/lib/addfolder": ApiLibAddFolder(stream, req); break;
                case "/api/lib/removefolder": ApiLibRemoveFolder(stream, req); break;
                case "/api/lib/scan": ApiLibScan(stream, req); break;
                case "/api/lib/scanall": ApiLibScanAll(stream); break;
                case "/api/lib/scanstatus": ApiLibScanStatus(stream); break;
                default: WriteJson(stream, 404, new { error = "not found" }); break;
            }
        }
        catch (Exception e)
        {
            Logger.Error($"外部访问处理 {path} 出错：{e.Message}");
            WriteJson(stream, 500, new { error = "server error" });
        }
    }

    private static bool Authed(Request req)
    {
        if (!_authRequired)
            return true;
        if (!req.Headers.TryGetValue("Cookie", out var cookie))
            return false;
        foreach (var part in cookie.Split(';'))
        {
            var kv = part.Trim();
            if (kv.StartsWith("dasd_auth=", StringComparison.Ordinal))
                return string.Equals(kv[10..], _expectedToken, StringComparison.Ordinal);
        }
        return false;
    }

    private static void HandleLogin(NetworkStream stream, Request req)
    {
        var password = "";
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            if (doc.RootElement.TryGetProperty("password", out var p))
                password = p.GetString() ?? "";
        }
        catch (Exception)
        {
            password = "";
        }
        if (!_authRequired || Sha256Hex(password) == _expectedToken)
        {
            // 令牌写入 Cookie（HttpOnly，1 天）
            WriteJson(stream, 200, new { ok = true },
                ("Set-Cookie", $"dasd_auth={_expectedToken}; Path=/; Max-Age=86400; HttpOnly; SameSite=Lax"));
        }
        else
        {
            WriteJson(stream, 401, new { ok = false });
        }
    }

    // ---------- API：卡片层级 ----------

    private static void ApiLibs(NetworkStream stream)
    {
        var counts = new Dictionary<string, long>();
        var rows = Db.Select(
            "SELECT \"library\", COUNT(*) FROM \"works\" WHERE \"state\" = '已品悦' GROUP BY \"library\"");
        if (rows != null)
            foreach (var row in rows)
                counts[row[0] as string ?? ""] = Convert.ToInt64(row[1]);
        var libs = AppConfig.ReadMediaLibs().Select(lib => new
        {
            name = lib.Name,
            works = counts.GetValueOrDefault(lib.Name),
            folders = lib.Folders.Count,
        });
        WriteJson(stream, 200, new { libs });
    }

    private static void ApiMakers(NetworkStream stream, Request req)
    {
        var lib = req.Query.GetValueOrDefault("lib");
        var genre = req.Query.GetValueOrDefault("genre");
        var type = req.Query.GetValueOrDefault("type");
        var order = MakerOrderClause(GetInt(req, "sort"));
        List<object?[]>? rows;
        if (!string.IsNullOrEmpty(genre))
            rows = Db.Select(
                "SELECT w.\"maker_name\", COUNT(*) FROM \"works\" w " +
                "JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
                "WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g " +
                "GROUP BY w.\"maker_name\" " + order.Replace("{p}", "w."),
                ("@g", genre));
        else if (!string.IsNullOrEmpty(type))
            rows = Db.Select(
                "SELECT \"maker_name\", COUNT(*) FROM \"works\" " +
                "WHERE \"state\" = '已品悦' AND \"work_type\" = @t " +
                "GROUP BY \"maker_name\" " + order.Replace("{p}", ""),
                ("@t", type));
        else
            rows = Db.Select(
                "SELECT \"maker_name\", COUNT(*) FROM \"works\" " +
                "WHERE \"state\" = '已品悦' AND \"library\" = @lib " +
                "GROUP BY \"maker_name\" " + order.Replace("{p}", ""),
                ("@lib", lib ?? ""));
        var makers = (rows ?? []).Select(r => new
        {
            maker = r[0] as string ?? "",
            count = Convert.ToInt64(r[1]),
        });
        WriteJson(stream, 200, new { makers });
    }

    private static void ApiWorks(NetworkStream stream, Request req)
    {
        var lib = req.Query.GetValueOrDefault("lib");
        var maker = req.Query.GetValueOrDefault("maker") ?? "";
        var genre = req.Query.GetValueOrDefault("genre");
        var type = req.Query.GetValueOrDefault("type");
        var order = WorkOrderClause(GetInt(req, "sort"));
        // maker 为空串表示"未知社团"
        var makerCond = maker.Length == 0
            ? "({p}\"maker_name\" IS NULL OR {p}\"maker_name\" = '')"
            : "{p}\"maker_name\" = @maker";
        List<object?[]>? rows;
        if (!string.IsNullOrEmpty(genre))
            rows = Db.Select(
                "SELECT w.\"work_id\", w.\"work_name\", w.\"maker_name\", w.\"work_type\", " +
                "w.\"age_category\", w.\"cover\" FROM \"works\" w " +
                "JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
                "WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g AND " + makerCond.Replace("{p}", "w.") + " " +
                order.Replace("{p}", "w."),
                ("@g", genre), ("@maker", maker));
        else if (!string.IsNullOrEmpty(type))
            rows = Db.Select(
                "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
                "FROM \"works\" WHERE \"state\" = '已品悦' AND \"work_type\" = @t AND " + makerCond.Replace("{p}", "") + " " +
                order.Replace("{p}", ""),
                ("@t", type), ("@maker", maker));
        else
            rows = Db.Select(
                "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
                "FROM \"works\" WHERE \"state\" = '已品悦' AND \"library\" = @lib AND " + makerCond.Replace("{p}", "") + " " +
                order.Replace("{p}", ""),
                ("@lib", lib ?? ""), ("@maker", maker));
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    /// <summary>某标签下全部已品悦作品（不分社团）——作品详情页点击类型标签直接列出作品。</summary>
    private static void ApiGenreWorks(NetworkStream stream, Request req)
    {
        var genre = req.Query.GetValueOrDefault("genre") ?? "";
        var rows = Db.Select(
            "SELECT w.\"work_id\", w.\"work_name\", w.\"maker_name\", w.\"work_type\", " +
            "w.\"age_category\", w.\"cover\" FROM \"works\" w " +
            "JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
            "WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g " +
            WorkOrderClause(GetInt(req, "sort")).Replace("{p}", "w."),
            ("@g", genre));
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    /// <summary>某媒体库内全部已品悦作品（媒体库默认"显示作品"视图）。</summary>
    private static void ApiLibWorks(NetworkStream stream, Request req)
    {
        var lib = req.Query.GetValueOrDefault("lib") ?? "";
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            "FROM \"works\" WHERE \"state\" = '已品悦' AND \"library\" = @lib " +
            WorkOrderClause(GetInt(req, "sort")).Replace("{p}", ""),
            ("@lib", lib));
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    private static void ApiGenres(NetworkStream stream)
    {
        var rows = Db.Select(
            "SELECT g.\"genre\", COUNT(*) FROM \"work_genres\" g " +
            "JOIN \"works\" w ON w.\"work_id\" = g.\"work_id\" " +
            "WHERE w.\"state\" = '已品悦' GROUP BY g.\"genre\" ORDER BY COUNT(*) DESC");
        var genres = (rows ?? []).Select(r => new
        {
            genre = r[0] as string ?? "",
            count = Convert.ToInt64(r[1]),
        });
        WriteJson(stream, 200, new { genres });
    }

    private static void ApiTypes(NetworkStream stream)
    {
        var rows = Db.Select(
            "SELECT \"work_type\", COUNT(*) FROM \"works\" " +
            "WHERE \"state\" = '已品悦' AND \"work_type\" IS NOT NULL AND \"work_type\" <> '' " +
            "GROUP BY \"work_type\" ORDER BY COUNT(*) DESC");
        var types = (rows ?? []).Select(r => new
        {
            type = r[0] as string ?? "",
            count = Convert.ToInt64(r[1]),
        });
        WriteJson(stream, 200, new { types });
    }

    private static void ApiFavorites(NetworkStream stream, Request req)
    {
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            "FROM \"works\" WHERE \"favorite\" = '1' " + WorkOrderClause(GetInt(req, "sort")).Replace("{p}", ""));
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    private static void ApiFilter(NetworkStream stream, Request req)
    {
        var col = req.Query.GetValueOrDefault("col") ?? "";
        var val = req.Query.GetValueOrDefault("val") ?? "";
        if (!FilterCols.Contains(col))
        {
            WriteJson(stream, 400, new { error = "bad column" });
            return;
        }
        var cond = col == "voice_actor" ? "\"voice_actor\" LIKE @val" : $"\"{col}\" = @val";
        var value = col == "voice_actor" ? $"%{val}%" : val;
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            "FROM \"works\" WHERE \"state\" = '已品悦' AND " + cond + " " +
            WorkOrderClause(GetInt(req, "sort")).Replace("{p}", ""),
            ("@val", value));
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    private static IEnumerable<object> WorksJson(List<object?[]>? rows) =>
        (rows ?? []).Select(r => new
        {
            id = r[0] as string ?? "",
            name = r[1] as string ?? "",
            maker = r[2] as string ?? "",
            type = r[3] as string ?? "",
            age = r[4]?.ToString() ?? "",
            cover = r[5] != null && File.Exists(r[5] as string),
        });

    // ---------- API：详情 ----------

    private static void ApiDetail(NetworkStream stream, Request req)
    {
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"sell_date\", \"series\", " +
            "\"scenario\", \"illust\", \"voice_actor\", \"age_category\", \"work_type\", " +
            "\"genre\", \"file_size\", \"intro_s\", \"folder\", \"read_flag\", \"favorite\", \"cover\" " +
            "FROM \"works\" WHERE \"work_id\" = @w", ("@w", id));
        if (rows is not { Count: > 0 })
        {
            WriteJson(stream, 404, new { error = "not found" });
            return;
        }
        var r = rows[0];
        var folder = ResolveAssetFolder(id, r[13] as string);

        // 正文：按 [img:文件名] 占位标记拆成 文本/图片 块
        var body = new List<object>();
        var bodyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var txtPath = Path.Combine(folder, DlsitePage.DescriptionTxt);
        if (File.Exists(txtPath))
        {
            string raw;
            try { raw = File.ReadAllText(txtPath).Trim(); }
            catch (IOException) { raw = ""; }
            var buf = new List<string>();
            foreach (var line in raw.Split('\n'))
            {
                var match = DlsitePage.BodyImageRe.Match(line.Trim());
                if (match.Success)
                {
                    if (buf.Count > 0) { body.Add(new { kind = "text", value = string.Join('\n', buf) }); buf.Clear(); }
                    var fn = match.Groups[1].Value;
                    body.Add(new { kind = "image", value = fn });
                    bodyFiles.Add(fn);
                }
                else
                {
                    buf.Add(line);
                }
            }
            if (buf.Count > 0)
                body.Add(new { kind = "text", value = string.Join('\n', buf) });
        }

        // 轮播图：数据源中除正文图片外的图片，主图排最前
        var slider = new List<string>();
        if (Directory.Exists(folder))
        {
            slider = Directory.GetFiles(folder)
                .Select(Path.GetFileName)
                .Where(f => f != null &&
                            ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()) &&
                            !bodyFiles.Contains(f))
                .Select(f => f!)
                .OrderBy(f => f.Contains("img_main", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var tags = (Db.Select("SELECT \"genre\" FROM \"work_genres\" WHERE \"work_id\" = @w", ("@w", id)) ?? [])
            .Select(t => t[0] as string ?? "").Where(t => t.Length > 0).ToList();

        var ageRaw = r[8]?.ToString() ?? "";
        WriteJson(stream, 200, new
        {
            id = r[0] as string ?? "",
            name = r[1] as string ?? "",
            maker = r[2] as string ?? "",
            sellDate = r[3] as string ?? "",
            series = r[4] as string ?? "",
            scenario = r[5] as string ?? "",
            illust = r[6] as string ?? "",
            voiceActor = r[7] as string ?? "",
            age = ageRaw,
            ageText = AgeMap.GetValueOrDefault(ageRaw, ageRaw),
            workType = r[9] as string ?? "",
            fileSize = r[11] as string ?? "",
            intro = r[12] as string ?? "",
            read = r[14] as string == "1",
            favorite = r[15] as string == "1",
            hasCover = r[16] != null && File.Exists(r[16] as string),
            hasFiles = r[13] is string wf && Directory.Exists(wf),
            tags,
            slider,
            body,
        });
    }

    private static void ApiToggle(NetworkStream stream, Request req)
    {
        string id = "", field = "";
        var value = false;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            field = root.TryGetProperty("field", out var f) ? f.GetString() ?? "" : "";
            value = root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        // 字段白名单
        var col = field switch { "read" => "read_flag", "favorite" => "favorite", _ => null };
        if (col == null || id.Length == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        Db.Execute($"UPDATE \"works\" SET \"{col}\" = @v WHERE \"work_id\" = @w",
            ("@v", value ? "1" : null), ("@w", id));
        WriteJson(stream, 200, new { ok = true, value });
    }

    // ---------- API：图片 ----------

    private static void ApiCover(NetworkStream stream, Request req)
    {
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var cover = Db.Scalar("SELECT \"cover\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id)) as string;
        ServeImageFile(stream, cover);
    }

    private static void ApiAsset(NetworkStream stream, Request req)
    {
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var name = req.Query.GetValueOrDefault("name") ?? "";
        // 文件名净化：禁止路径分隔符与上跳，仅允许图片扩展名
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..") ||
            !ImageExts.Contains(Path.GetExtension(name).ToLowerInvariant()))
        {
            WriteBytes(stream, 400, "Bad Request", "text/plain", Encoding.ASCII.GetBytes("bad name"));
            return;
        }
        var folder = ResolveAssetFolder(id, Db.Scalar(
            "SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id)) as string);
        ServeImageFile(stream, Path.Combine(folder, name));
    }

    /// <summary>详情资源文件夹：作品文件夹/DataSource 优先，否则回退 images/&lt;RJ&gt;。</summary>
    private static string ResolveAssetFolder(string id, string? workFolder)
    {
        if (workFolder != null)
        {
            var ds = Path.Combine(workFolder, DlsitePage.DataSourceDir);
            if (Directory.Exists(ds))
                return ds;
        }
        return Path.Combine(DlsitePage.ImagesDir, id);
    }

    private static void ServeImageFile(NetworkStream stream, string? path)
    {
        if (path == null || !File.Exists(path) ||
            !ImageExts.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", Encoding.ASCII.GetBytes("not found"));
            return;
        }
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (IOException) { WriteBytes(stream, 404, "Not Found", "text/plain", []); return; }
        WriteBytes(stream, 200, "OK", ContentType(path), data,
            ("Cache-Control", "max-age=86400"));
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "image/jpeg",
    };

    // ---------- API：作品文件树（“查看作品”） ----------

    /// <summary>列出作品文件夹的文件树（递归，根目录排除 DataSource，镜像桌面端 ShowFileTree）。</summary>
    private static void ApiFiles(NetworkStream stream, Request req)
    {
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var folder = Db.Scalar("SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id)) as string;
        if (folder == null || !Directory.Exists(folder))
        {
            WriteJson(stream, 404, new { error = "作品文件夹不存在" });
            return;
        }
        var root = Path.GetFullPath(folder);
        WriteJson(stream, 200, new { id, nodes = BuildFileTree(root, root, isRoot: true) });
    }

    private static List<object> BuildFileTree(string dir, string root, bool isRoot)
    {
        var nodes = new List<object>();
        string[] subDirs = [], files = [];
        try
        {
            subDirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return nodes;
        }
        foreach (var sub in subDirs
                     .Where(d => !(isRoot && string.Equals(Path.GetFileName(d), DlsitePage.DataSourceDir,
                         StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            nodes.Add(new
            {
                name = Path.GetFileName(sub), dir = true, rel = RelPath(root, sub),
                children = BuildFileTree(sub, root, false),
            });
        foreach (var f in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            long size = 0;
            try { size = new FileInfo(f).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            nodes.Add(new
            {
                name = Path.GetFileName(f), dir = false, rel = RelPath(root, f),
                ext = Path.GetExtension(f).ToLowerInvariant(), size,
            });
        }
        return nodes;
    }

    private static string RelPath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>流式发送作品文件夹内的单个文件，支持 HTTP Range（视频/音频拖动、断点续传）。</summary>
    private static void ApiFile(NetworkStream stream, Request req)
    {
        var id = req.Query.GetValueOrDefault("id") ?? "";
        var rel = req.Query.GetValueOrDefault("path") ?? "";
        var folder = Db.Scalar("SELECT \"folder\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id)) as string;
        if (folder == null || !Directory.Exists(folder))
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", []);
            return;
        }
        var root = Path.GetFullPath(folder);
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, rel)); }
        catch (Exception) { WriteBytes(stream, 400, "Bad Request", "text/plain", []); return; }
        // 路径穿越防护：必须落在作品文件夹内
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(full))
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", []);
            return;
        }
        WriteFileRange(stream, req, full);
    }

    /// <summary>按 Range 头流式写出文件（206 Partial Content）或整文件（200），分块拷贝不占内存。</summary>
    private static void WriteFileRange(NetworkStream stream, Request req, string path)
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception) { WriteBytes(stream, 404, "Not Found", "text/plain", []); return; }
        using (fs)
        {
            var length = fs.Length;
            long start = 0, end = length - 1;
            var partial = false;
            if (req.Headers.TryGetValue("Range", out var range) &&
                range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && length > 0)
            {
                var spec = range["bytes=".Length..].Split('-');
                if (spec.Length == 2)
                {
                    if (spec[0].Length == 0)
                    {
                        // 后缀形式 bytes=-N：取最后 N 字节
                        if (long.TryParse(spec[1], out var suffix))
                            start = Math.Max(0, length - suffix);
                    }
                    else
                    {
                        long.TryParse(spec[0], out start);
                        if (spec[1].Length > 0 && long.TryParse(spec[1], out var e))
                            end = e;
                    }
                }
                if (start < 0) start = 0;
                if (end >= length) end = length - 1;
                if (start > end) { start = 0; end = length - 1; }
                partial = true;
            }
            var count = end - start + 1;
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(partial ? "206 Partial Content" : "200 OK").Append("\r\n");
            sb.Append("Content-Type: ").Append(MediaContentType(path)).Append("\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append("Content-Length: ").Append(count).Append("\r\n");
            if (partial)
                sb.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);

            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[81920];
            var remaining = count;
            int read;
            while (remaining > 0 &&
                   (read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining))) > 0)
            {
                stream.Write(buffer, 0, read);
                remaining -= read;
            }
            stream.Flush();
        }
    }

    /// <summary>按扩展名给出媒体 MIME 类型（供浏览器原生播放/预览）。</summary>
    private static string MediaContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".ogv" => "video/ogg",
        ".mov" => "video/quicktime",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".flv" => "video/x-flv",
        ".wmv" => "video/x-ms-wmv",
        ".ts" => "video/mp2t",
        ".mpg" or ".mpeg" => "video/mpeg",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".flac" => "audio/flac",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ogg" or ".opus" => "audio/ogg",
        ".wma" => "audio/x-ms-wma",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".txt" => "text/plain; charset=utf-8",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    // ---------- API：搜索 ----------

    private static async Task ApiSearchAsync(NetworkStream stream, Request req)
    {
        var id = (req.Query.GetValueOrDefault("id") ?? "").Trim().ToUpperInvariant();
        if (!WorkIdRe.IsMatch(id))
        {
            WriteJson(stream, 400, new { error = "番号格式错误（RJ/BJ/VJ + 数字）" });
            return;
        }
        // 已下载过的作品：返回提示信息供前端确认
        var existed = Db.Select(
            "SELECT \"work_name\", \"down_time\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", id));
        string? exName = null, exTime = null;
        if (existed is { Count: > 0 })
        {
            exName = existed[0][0] as string ?? "";
            var t = existed[0][1]?.ToString() ?? "";
            exTime = t.Length > 19 ? t[..19] : t;
        }
        var workTask = DlsiteApi.GetWorkDataAsync(id);
        var searchTask = AnimeSharing.SearchWorkAsync(id);
        try
        {
            await Task.WhenAll(workTask, searchTask);
        }
        catch (Exception e)
        {
            WriteJson(stream, 200, new { id, error = "查询失败：" + e.Message, results = Array.Empty<object>() });
            return;
        }
        var work = workTask.Result;
        SearchCache[id] = work;
        var results = searchTask.Result.Select(r => new
        {
            title = r.Title, minor = r.Minor, snippet = TrimSnippet(r.Snippet), url = r.Url, thumb = r.Thumb,
        });
        WriteJson(stream, 200, new
        {
            id,
            work = new { name = work?.WorkName ?? "", maker = work?.MakerName ?? "", type = work?.WorkType ?? "" },
            existed = exName != null, existedName = exName, existedTime = exTime,
            results,
        });
    }

    /// <summary>按社团号（RG）返回该社团作品列表的某一页。</summary>
    private static async Task ApiMakerAsync(NetworkStream stream, Request req)
    {
        var id = (req.Query.GetValueOrDefault("id") ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(id, @"^RG\d+$"))
        {
            WriteJson(stream, 400, new { error = "社团号格式错误（RG + 数字）" });
            return;
        }
        var page = GetInt(req, "page");
        if (page < 1)
            page = 1;
        var (works, hasMore) = await DlsiteApi.GetMakerWorksAsync(id, page);
        // 校验后台：本页作品号哪些已在库（works 表已有记录），供前端置灰标记
        var states = LookupWorkStates(works.Select(w => w.WorkId));
        WriteJson(stream, 200, new
        {
            works = works.Select(w => new
            {
                id = w.WorkId, title = w.Title, thumb = w.Thumb,
                inLib = states.ContainsKey(w.WorkId),
                state = states.GetValueOrDefault(w.WorkId, ""),
            }),
            hasMore,
        });
    }

    /// <summary>批量查询作品号在 works 表中的状态（不存在则不在结果里），用于搜索时标记"已在库"。</summary>
    private static Dictionary<string, string> LookupWorkStates(IEnumerable<string> ids)
    {
        var list = ids.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

    /// <summary>对单个作品号做一次 AS 论坛扫描，返回匹配帖子数（-1 表示扫描出错）。
    /// 供 RG 搜索时前端按 3 秒间隔逐个探测未在库作品的可下载性。</summary>
    private static async Task ApiAsScanAsync(NetworkStream stream, Request req)
    {
        var id = (req.Query.GetValueOrDefault("id") ?? "").Trim().ToUpperInvariant();
        if (!WorkIdRe.IsMatch(id))
        {
            WriteJson(stream, 400, new { error = "番号格式错误（RJ/BJ/VJ + 数字）" });
            return;
        }
        int count;
        try
        {
            count = (await AnimeSharing.SearchWorkAsync(id)).Count;
        }
        catch (Exception)
        {
            WriteJson(stream, 200, new { id, count = -1 });
            return;
        }
        WriteJson(stream, 200, new { id, count });
    }

    private static string TrimSnippet(string snippet) =>
        string.Join('\n', snippet.Split('\n')
            .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0).Take(6));

    private static async Task ApiThumbAsync(NetworkStream stream, Request req)
    {
        var url = req.Query.GetValueOrDefault("url") ?? "";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            WriteBytes(stream, 400, "Bad Request", "text/plain", []);
            return;
        }
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(15));
            var bytes = await client.GetByteArrayAsync(url);
            WriteBytes(stream, 200, "OK", ContentType(url), bytes, ("Cache-Control", "max-age=86400"));
        }
        catch (Exception)
        {
            WriteBytes(stream, 404, "Not Found", "text/plain", []);
        }
    }

    private static async Task ApiPostUrlsAsync(NetworkStream stream, Request req)
    {
        var threadPath = req.Query.GetValueOrDefault("url") ?? "";
        List<string> urls;
        try
        {
            (urls, _) = await AnimeSharing.GetWorkDownUrlsAsync(threadPath);
        }
        catch (Exception e)
        {
            WriteJson(stream, 200, new { error = "解析失败：" + e.Message, hosts = Array.Empty<object>() });
            return;
        }
        // 按域名分组，保持出现顺序
        var groups = new Dictionary<string, List<string>>();
        foreach (var u in urls)
        {
            var host = HostOf(u);
            if (!groups.TryGetValue(host, out var list))
                groups[host] = list = [];
            list.Add(u);
        }
        WriteJson(stream, 200, new
        {
            hosts = groups.Select(g => new { host = g.Key, count = g.Value.Count, urls = g.Value }),
        });
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

    private static async Task ApiCheckHostAsync(NetworkStream stream, Request req)
    {
        var urls = ReadStringArray(req.Body, "urls");
        if (urls.Count == 0)
        {
            WriteJson(stream, 400, new { error = "no urls" });
            return;
        }
        var valid = 0;
        using (var client = LinkChecker.MakeClient())
            foreach (var u in urls)
                if (await LinkChecker.CheckUrlAsync(u, client))
                    valid++;
        var status = valid == urls.Count ? "valid" : valid == 0 ? "invalid" : "partial";
        WriteJson(stream, 200, new { valid, total = urls.Count, status });
    }

    private static void ApiDownTargets(NetworkStream stream)
    {
        var libs = AppConfig.ReadMediaLibs()
            .Select(l => new { name = l.Name, folders = l.Folders.Where(f => f.Length > 0).ToList() })
            .Where(l => l.folders.Count > 0);
        WriteJson(stream, 200, new { libs });
    }

    private static void ApiEnqueue(NetworkStream stream, Request req)
    {
        string id = "", lib = "", folder = "";
        var urls = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            id = (root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "").ToUpperInvariant();
            lib = root.TryGetProperty("lib", out var l) ? l.GetString() ?? "" : "";
            folder = root.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
            if (root.TryGetProperty("urls", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var u in arr.EnumerateArray())
                    if (u.GetString() is { Length: > 0 } s)
                        urls.Add(s);
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        if (!WorkIdRe.IsMatch(id) || urls.Count == 0)
        {
            WriteJson(stream, 400, new { error = "bad request" });
            return;
        }
        foreach (var downUrl in urls)
            Db.Execute(
                "INSERT OR REPLACE INTO \"download_list\" (\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\") " +
                "VALUES (@uuid, @w, @url, '0', '0', '1')",
                ("@uuid", Guid.NewGuid().ToString()), ("@w", id), ("@url", downUrl));
        RecordWork(id, SearchCache.GetValueOrDefault(id));
        if (folder.Length > 0)
            DownloadEngine.SetWorkTargetPath(id, folder, lib.Length > 0 ? lib : null);
        DownloadEngine.Start();
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>把 DL API 作品数据写入 works 表（镜像 SearchPage.RecordWork）。</summary>
    private static void RecordWork(string id, DlWork? work)
    {
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
            ("@w", id), ("@n", work?.WorkName ?? ""), ("@mi", work?.MakerId ?? ""),
            ("@mn", work?.MakerName ?? ""), ("@t", work?.WorkType ?? ""), ("@s", work?.IntroS ?? ""),
            ("@a", work?.AgeCategory ?? ""), ("@ana", work?.IsAna ?? ""),
            ("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff")));
    }

    // ---------- API：下载 / 已下载 ----------

    private static void ApiDownloads(NetworkStream stream)
    {
        var rows = Db.Select(
            "SELECT \"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"error\", \"sub_path\" FROM \"download_list\" ORDER BY rowid");
        var order = new List<string>();
        var grouped = new Dictionary<string, List<(string Uuid, string Url, string Status, string Long, string? Error, string? SubPath)>>();
        foreach (var row in rows ?? [])
        {
            var workId = row[1] as string ?? "";
            if (!grouped.TryGetValue(workId, out var list))
            {
                grouped[workId] = list = [];
                order.Add(workId);
            }
            list.Add((row[0] as string ?? "", row[2] as string ?? "",
                row[3] as string ?? "", row[4]?.ToString() ?? "", row[5] as string, row[6] as string));
        }
        var groups = order.Select(workId =>
        {
            var items = grouped[workId];
            var statuses = items.Select(it => it.Status).ToList();
            var (aggText, aggColor) = AggregateStatus(workId, statuses);
            var totalPct = 0;
            double totalSpeed = 0;
            var children = items.Select(it =>
            {
                var (pct, speed) = FileProgress(it.Uuid, it.Status, it.Long);
                totalPct += pct;
                if (speed is { } s) totalSpeed += s;
                var (text, color) = StatusMap.TryGetValue(it.Status, out var m)
                    ? m : ($"未知({it.Status})", "#cdd3de");
                // asmr 直链带作品内目录层级（sub_path 含子目录），用于前端构建目录树；
                // 论坛源无 sub_path，回退到 URL 解码后的文件名（位于根层级），对齐 WPF 下载页
                var rel = string.IsNullOrEmpty(it.SubPath) ? FileNameOf(it.Url) : it.SubPath!;
                var slash = rel.LastIndexOf('/');
                return new
                {
                    fileName = slash >= 0 ? rel[(slash + 1)..] : rel, relPath = rel,
                    url = it.Url, pct,
                    speed = speed is { } sp ? FormatSpeed(sp) : "",
                    statusText = text, color,
                    // 解析失败的分卷附带可读错误原因
                    errorReason = it.Status == "2" ? MapParseError(it.Error) : "",
                };
            }).ToList();
            var groupPct = DownloadEngine.UnzipProgress.TryGetValue(workId, out var unzip)
                ? unzip.Pct : (double)totalPct / Math.Max(1, items.Count);
            return new
            {
                id = workId, pct = (int)groupPct, statusText = aggText, color = aggColor,
                speed = totalSpeed > 0 ? FormatSpeed(totalSpeed) : "",
                canReparse = statuses.Contains("2") && workId.Length > 0,
                // 有下载中/等待分卷可"停止"；有已暂停分卷可"下载"（继续）；非空番号始终可"删除"
                canPause = statuses.Any(s => s is "0" or "3") && workId.Length > 0,
                canResume = statuses.Contains("4") && workId.Length > 0,
                canDelete = workId.Length > 0,
                children,
            };
        });
        WriteJson(stream, 200, new
        {
            engine = new { running = DownloadEngine.IsRunning, stopRequested = DownloadEngine.StopRequested },
            groups,
        });
    }

    private static (string Text, string Color) AggregateStatus(string workId, List<string> statuses)
    {
        var done = statuses.Count(s => s == "1");
        if (statuses.Contains("3"))
            return ($"下载中 {done}/{statuses.Count}", "#60a5fa");
        if (statuses.Contains("0"))
            return ($"等待下载 {done}/{statuses.Count}", "#facc15");
        if (statuses.Contains("4"))
            return ($"已暂停 {done}/{statuses.Count}", "#9aa4b2");
        if (statuses.Contains("2"))
            return ($"{statuses.Count(s => s == "2")} 个解析失败", "#f87171");
        if (DownloadEngine.UnzipProgress.TryGetValue(workId, out var unzip))
        {
            if (unzip.State == "pending") return ("待解压", "#facc15");
            if (unzip.State == "movewait") return ("等待移动", "#facc15");
            if (unzip.State == "moving") return ($"移动中 {unzip.Pct}%", "#a78bfa");
            return ($"解压中 {unzip.Pct}%", "#60a5fa");
        }
        return ("已完成", "#4ade80");
    }

    private static (int Pct, double? Speed) FileProgress(string uuid, string status, string dbLong)
    {
        if (status == "1")
            return (100, null);
        if (status == "3" && DownloadEngine.DownloadProgress.TryGetValue(uuid, out var info) && info.Total > 0)
            return ((int)(info.Downloaded * 100 / info.Total), info.Speed);
        var pct = int.TryParse(dbLong, out var p) ? p : 0;
        return (Math.Min(pct, 100), null);
    }

    private static string FormatSpeed(double speed) => speed switch
    {
        >= 1024 * 1024 => $"{speed / 1024 / 1024:F1} MB/s",
        >= 1024 => $"{speed / 1024:F0} KB/s",
        _ => $"{speed:F0} B/s",
    };

    private static string FileNameOf(string url)
    {
        var raw = url.TrimEnd('/').Split('/')[^1].Split('?')[0];
        // 直链文件名常为 URL 百分号编码（asmr.one 的日文名尤其如此），解码为明文显示（对齐 WPF）
        try { return Uri.UnescapeDataString(raw); }
        catch (Exception) { return raw; }
    }

    private static async Task ApiUsageAsync(NetworkStream stream)
    {
        try
        {
            JsonElement? value;
            using (var client = new DebridLinkClient())
                value = await client.DownloadLimitsAsync();
            double? current = null;
            double resetSeconds = 0;
            if (value is { } v && v.ValueKind == JsonValueKind.Object)
            {
                if (v.TryGetProperty("usagePercent", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                    usage.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.Number)
                    current = cur.GetDouble();
                if (v.TryGetProperty("nextResetSeconds", out var reset) && reset.ValueKind == JsonValueKind.Object &&
                    reset.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.Number)
                    resetSeconds = rv.GetDouble();
            }
            WriteJson(stream, 200, new
            {
                percent = current is { } c ? (int)Math.Min(100, Math.Round(c)) : (int?)null,
                resetText = FormatReset(resetSeconds),
            });
        }
        catch (Exception)
        {
            WriteJson(stream, 200, new { percent = (int?)null, resetText = "" });
        }
    }

    private static string FormatReset(double seconds)
    {
        var s = (long)seconds;
        if (s <= 0) return "";
        var hours = s / 3600;
        var minutes = s % 3600 / 60;
        if (hours > 0) return $"{hours}h{minutes}m";
        return minutes > 0 ? $"{minutes}m" : "<1m";
    }

    private static void ApiEngine(NetworkStream stream, Request req)
    {
        var action = ReadStringField(req.Body, "action");
        if (action == "start") DownloadEngine.Start();
        else if (action == "stop") DownloadEngine.Stop();
        WriteJson(stream, 200, new { running = DownloadEngine.IsRunning, stopRequested = DownloadEngine.StopRequested });
    }

    private static void ApiReparse(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
        {
            Db.Execute("UPDATE \"download_list\" SET \"status\" = '0', \"error\" = NULL WHERE \"work_id\" = @w AND \"status\" = '2'",
                ("@w", id));
            DownloadEngine.Start();
        }
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>单独停止某作品下载（停到断点，镜像下载页"停止"）。</summary>
    private static void ApiPauseWork(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
            DownloadEngine.PauseWork(id);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>单独继续某作品下载（解除暂停并启动引擎，镜像下载页"下载"）。</summary>
    private static void ApiResumeWork(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
            DownloadEngine.ResumeWork(id);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>单独删除某作品（移除下载列表与作品记录、删下载缓存，镜像下载页"删除"）。</summary>
    private static void ApiDeleteWork(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
            DownloadEngine.DeleteWork(id);
        WriteJson(stream, 200, new { ok = true });
    }

    /// <summary>把 debrid-link 解析失败错误码翻译为可读说明（镜像 DownloadPage.MapParseError）。</summary>
    private static string MapParseError(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "解析失败（原因未知，可能是链接失效或网盘不支持）";
        return raw switch
        {
            "badToken" => "debrid-link API Key 无效或已过期",
            "maxData" or "maxDataHost" => "debrid-link 流量额度已用尽",
            "maxLink" or "maxLinkHost" => "链接数超过 debrid-link 限制",
            "hostUnsupported" or "notDebrid" or "hostNotValid" or "noServer"
                => "该网盘不被 debrid-link 支持",
            "notFreeHost" or "hostNotFree" or "disabledHost" or "disabledServerHost"
                => "该网盘需要会员或已停用",
            "fileNotFound" or "fileUnavailable" or "notFound" or "fileError"
                => "文件已失效或被删除",
            "floodDetected" => "请求过于频繁，请稍后再试",
            "badFileType" => "不支持的文件类型",
            _ => $"解析失败（{raw}）",
        };
    }

    /// <summary>把作品移动到另一个媒体库（镜像详情页"移动媒体库"）。</summary>
    private static async Task ApiMoveWorkAsync(NetworkStream stream, Request req)
    {
        string id = "", lib = "", folder = "";
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            id = (root.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "").ToUpperInvariant();
            lib = root.TryGetProperty("lib", out var l) ? l.GetString() ?? "" : "";
            folder = root.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "";
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        if (id.Length == 0 || lib.Length == 0 || folder.Length == 0)
        {
            WriteJson(stream, 400, new { ok = false, message = "参数缺失" });
            return;
        }
        var (ok, message) = await MediaLibraryService.MoveWorkToLibraryAsync(id, lib, folder);
        WriteJson(stream, 200, new { ok, message });
    }

    /// <summary>媒体库作用域作品搜索（镜像 MediaLibPage.ShowScopedSearch）：
    /// 媒体库根搜全部库；指定 lib/genre/type 时限定该范围；按 RJ号/作品名 LIKE 匹配已品悦作品。</summary>
    private static void ApiSearchWorks(NetworkStream stream, Request req)
    {
        var kw = (req.Query.GetValueOrDefault("kw") ?? "").Trim();
        if (kw.Length == 0)
        {
            WriteJson(stream, 200, new { works = Array.Empty<object>() });
            return;
        }
        var lib = req.Query.GetValueOrDefault("lib");
        var genre = req.Query.GetValueOrDefault("genre");
        var type = req.Query.GetValueOrDefault("type");
        var conds = new List<string> { "\"works\".\"state\" = '已品悦'", "(\"works\".\"work_id\" LIKE @kw OR \"works\".\"work_name\" LIKE @kw)" };
        var pars = new List<(string, object?)> { ("@kw", $"%{kw}%") };
        var join = "";
        if (!string.IsNullOrEmpty(genre))
        {
            join = "JOIN \"work_genres\" g ON g.\"work_id\" = \"works\".\"work_id\" ";
            conds.Add("g.\"genre\" = @g");
            pars.Add(("@g", genre));
        }
        else if (!string.IsNullOrEmpty(type))
        {
            conds.Add("\"works\".\"work_type\" = @t");
            pars.Add(("@t", type));
        }
        else if (!string.IsNullOrEmpty(lib))
        {
            conds.Add("\"works\".\"library\" = @lib");
            pars.Add(("@lib", lib));
        }
        var rows = Db.Select(
            "SELECT \"works\".\"work_id\", \"works\".\"work_name\", \"works\".\"maker_name\", " +
            "\"works\".\"work_type\", \"works\".\"age_category\", \"works\".\"cover\" FROM \"works\" " +
            join + "WHERE " + string.Join(" AND ", conds) + " " +
            WorkOrderClause(GetInt(req, "sort")).Replace("{p}", "\"works\"."),
            pars.ToArray());
        WriteJson(stream, 200, new { works = WorksJson(rows) });
    }

    private static void ApiResearch(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
            DownloadEngine.PurgeWorkDownload(id);
        WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiClearDone(NetworkStream stream)
    {
        var unzipping = DownloadEngine.UnzipProgress.Keys.ToList();
        if (unzipping.Count > 0)
        {
            var names = new List<string>();
            var args = new List<(string, object?)>();
            for (var i = 0; i < unzipping.Count; i++)
            {
                names.Add($"@u{i}");
                args.Add(($"@u{i}", unzipping[i]));
            }
            Db.Execute(
                $"DELETE FROM \"download_list\" WHERE \"status\" = '1' AND \"work_id\" NOT IN ({string.Join(",", names)})",
                args.ToArray());
        }
        else
        {
            Db.Execute("DELETE FROM \"download_list\" WHERE \"status\" = '1'");
        }
        WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiClearAll(NetworkStream stream)
    {
        var rows = Db.Select("SELECT DISTINCT \"work_id\" FROM \"download_list\" WHERE \"status\" != '1'");
        foreach (var row in rows ?? [])
            Db.Execute("DELETE FROM \"works\" WHERE \"work_id\" = @w AND \"state\" = '下载中'",
                ("@w", row[0] as string ?? ""));
        Db.Execute("DELETE FROM \"download_list\"");
        WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiDownloaded(NetworkStream stream)
    {
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"state\", \"down_time\" " +
            "FROM \"works\" ORDER BY \"down_time\" DESC, \"work_id\" DESC");
        var works = (rows ?? []).Select(r =>
        {
            var t = r[5]?.ToString() ?? "";
            return new
            {
                id = r[0] as string ?? "", name = r[1] as string ?? "", maker = r[2] as string ?? "",
                type = r[3] as string ?? "", state = r[4] as string ?? "",
                downTime = t.Length > 19 ? t[..19] : t,
            };
        });
        WriteJson(stream, 200, new { works });
    }

    private static void ApiMark(NetworkStream stream, Request req)
    {
        var id = ReadStringField(req.Body, "id");
        if (id.Length > 0)
            Db.Execute("UPDATE \"works\" SET \"state\" = '已品悦' WHERE \"work_id\" = @w AND \"state\" = '已下载'",
                ("@w", id));
        WriteJson(stream, 200, new { ok = true });
    }

    // ---------- API：设置 ----------

    private static void ApiSettings(NetworkStream stream)
    {
        var (proxyOpen, proxyHost, proxyPort, proxyType) = AppConfig.ReadProxySetting();
        var libs = AppConfig.ReadMediaLibs().Select(l => new { name = l.Name, folders = l.Folders });
        WriteJson(stream, 200, new
        {
            downpath = AppConfig.DownloadPath,
            autoDownload = AppConfig.AutoDownload,
            autoUnzip = AppConfig.AutoUnzip,
            proxy = new { open = proxyOpen == "True", host = proxyHost, port = proxyPort, type = proxyType },
            debridKey = AppConfig.DebridApiKey,
            downProc = AppConfig.DownloadProcesses,
            minSpeed = AppConfig.MinSpeedKb,
            speedLimit = AppConfig.SpeedLimitKb,
            language = AppConfig.Language,
            languages = I18n.Languages.Select(l => new { code = l.Code, name = l.Name }),
            logLevel = AppConfig.Read("loglevel", "level", "info"),
            encoding = AppConfig.SysEncoding,
            mediaLibs = libs,                       // 只读：文件夹管理需桌面端原生选择器
            web = new { enabled = AppConfig.WebEnabled, port = AppConfig.WebPort, password = AppConfig.WebPassword },
        });
    }

    private static void ApiSettingsWrite(NetworkStream stream, Request req)
    {
        string section = "", key = "", value = "";
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(req.Body));
            var root = doc.RootElement;
            section = (root.TryGetProperty("section", out var s) ? s.GetString() ?? "" : "").ToLowerInvariant();
            key = (root.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "").ToLowerInvariant();
            value = root.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
        }
        catch (Exception)
        {
            // 解析失败按非法请求处理
        }
        if (!SettingsWhitelist.Contains($"{section}/{key}"))
        {
            WriteJson(stream, 400, new { error = "该项不可从网页修改" });
            return;
        }
        // 语言变更需在 UI 线程应用（会触发各页面实时重译）
        if (section == "language" && key == "lang")
            System.Windows.Application.Current?.Dispatcher.Invoke(() => I18n.ApplyLanguage(value));
        else
            AppConfig.Write(section, key, value);
        WriteJson(stream, 200, new { ok = true });
    }

    // ---------- API：媒体库管理 ----------

    /// <summary>媒体库列表（含文件夹），供设置页管理区渲染/刷新。</summary>
    private static void ApiMediaLibs(NetworkStream stream)
    {
        var libs = AppConfig.ReadMediaLibs().Select(l => new { name = l.Name, folders = l.Folders });
        WriteJson(stream, 200, new { libs });
    }

    private static void ApiLibCreate(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        if (name.Length == 0)
        {
            WriteJson(stream, 400, new { error = "名称不能为空" });
            return;
        }
        string? err = null;
        lock (LibSync)
        {
            var libs = AppConfig.ReadMediaLibs();
            if (libs.Any(l => l.Name == name))
                err = "已存在同名媒体库";
            else
            {
                libs.Add(new MediaLib { Name = name });
                AppConfig.WriteMediaLibs(libs);
            }
        }
        if (err != null) WriteJson(stream, 400, new { error = err });
        else WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiLibDelete(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        bool removed;
        lock (LibSync)
        {
            var libs = AppConfig.ReadMediaLibs();
            removed = libs.RemoveAll(l => l.Name == name) > 0;
            if (removed) AppConfig.WriteMediaLibs(libs);
        }
        // 不删本地文件，仅解除作品与该库的关联（镜像桌面端 DeleteLib）
        if (removed)
            Db.Execute("UPDATE \"works\" SET \"library\" = NULL WHERE \"library\" = @l", ("@l", name));
        WriteJson(stream, 200, new { ok = removed });
    }

    private static void ApiLibAddFolder(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        var folderRaw = ReadStringField(req.Body, "folder").Trim();
        if (folderRaw.Length == 0)
        {
            WriteJson(stream, 400, new { error = "路径不能为空" });
            return;
        }
        string folder;
        try { folder = Path.GetFullPath(folderRaw); }
        catch (Exception) { WriteJson(stream, 400, new { error = "路径格式错误" }); return; }
        if (!Directory.Exists(folder))
        {
            WriteJson(stream, 400, new { error = "文件夹不存在或无法访问（需为运行本程序的电脑上的路径）" });
            return;
        }
        string? err = null;
        lock (LibSync)
        {
            var libs = AppConfig.ReadMediaLibs();
            var lib = libs.FirstOrDefault(l => l.Name == name);
            if (lib == null)
                err = "媒体库不存在";
            else if (libs.Any(l => l.Folders.Contains(folder)))
                err = "该文件夹已在某个媒体库中";
            else
            {
                lib.Folders.Add(folder);
                AppConfig.WriteMediaLibs(libs);
            }
        }
        if (err != null) WriteJson(stream, 400, new { error = err });
        else WriteJson(stream, 200, new { ok = true, folder });
    }

    private static void ApiLibRemoveFolder(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        var folder = ReadStringField(req.Body, "folder");
        var removed = false;
        lock (LibSync)
        {
            var libs = AppConfig.ReadMediaLibs();
            var lib = libs.FirstOrDefault(l => l.Name == name);
            if (lib != null && lib.Folders.Remove(folder))
            {
                removed = true;
                AppConfig.WriteMediaLibs(libs);
            }
        }
        WriteJson(stream, 200, new { ok = removed });
    }

    private static void ApiLibScan(NetworkStream stream, Request req)
    {
        var name = ReadStringField(req.Body, "name").Trim();
        var force = ReadBoolField(req.Body, "force");
        MediaLibScanner.Scan(name, force);
        WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiLibScanAll(NetworkStream stream)
    {
        MediaLibScanner.ScanAll();
        WriteJson(stream, 200, new { ok = true });
    }

    private static void ApiLibScanStatus(NetworkStream stream)
    {
        var (scanning, status) = MediaLibScanner.State();
        WriteJson(stream, 200, new { scanning, status });
    }

    private static async Task ApiDebridTestAsync(NetworkStream stream, Request req)
    {
        var key = ReadStringField(req.Body, "key");
        try
        {
            using var client = new DebridLinkClient(key.Length > 0 ? key : null);
            var info = await client.AccountInfosAsync();
            var ok = info is { } v && v.ValueKind == JsonValueKind.Object;
            WriteJson(stream, 200, new { ok });
        }
        catch (Exception)
        {
            WriteJson(stream, 200, new { ok = false });
        }
    }

    // ---------- 请求体解析 ----------

    private static string ReadStringField(byte[] body, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() ?? "" : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static bool ReadBoolField(byte[] body, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            return doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<string> ReadStringArray(byte[] body, string field)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(body));
            if (doc.RootElement.TryGetProperty(field, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray())
                    if (item.GetString() is { Length: > 0 } s)
                        list.Add(s);
        }
        catch (Exception)
        {
            // 解析失败返回空列表
        }
        return list;
    }

    // ---------- 排序子句（{p} 为表别名占位） ----------

    private static string MakerOrderClause(int sort) => sort switch
    {
        1 => "ORDER BY COUNT(*) ASC",
        2 => "ORDER BY {p}\"maker_name\" COLLATE NOCASE ASC",
        _ => "ORDER BY COUNT(*) DESC",
    };

    private static string WorkOrderClause(int sort) => sort switch
    {
        1 => "ORDER BY {p}\"sell_date\" ASC",
        2 => "ORDER BY {p}\"work_id\" DESC",
        3 => "ORDER BY {p}\"work_id\" ASC",
        _ => "ORDER BY {p}\"sell_date\" DESC",
    };

    private static int GetInt(Request req, string key) =>
        int.TryParse(req.Query.GetValueOrDefault(key), out var v) ? v : 0;

    // ---------- HTTP 响应写出 ----------

    private static void WriteJson(NetworkStream stream, int status, object payload,
        params (string Key, string Value)[] extra)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        WriteBytes(stream, status, StatusText(status), "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(json), extra);
    }

    private static void WriteBytes(NetworkStream stream, int status, string statusText,
        string contentType, byte[] body, params (string Key, string Value)[] extra)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(statusText).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");
        foreach (var (key, value) in extra)
            sb.Append(key).Append(": ").Append(value).Append("\r\n");
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(head, 0, head.Length);
        if (body.Length > 0)
            stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 401 => "Unauthorized",
        404 => "Not Found", 500 => "Internal Server Error", _ => "OK",
    };

    // ---------- 工具 ----------

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>取本机首个 IPv4 局域网地址（设置页展示访问地址用）；取不到返回 localhost。</summary>
    public static string LocalIPv4()
    {
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip))
                    return ip.ToString();
        }
        catch (Exception)
        {
            // 取本机地址失败时回退
        }
        return "127.0.0.1";
    }
}
