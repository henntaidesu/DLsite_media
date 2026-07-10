using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;

namespace DLsiteMedia.Core;

/// <summary>一个媒体库：名称 + 文件夹列表。</summary>
public class MediaLib
{
    public string Name { get; set; } = "";
    public List<string> Folders { get; set; } = [];
}

/// <summary>
/// 配置读写（对应 Python 版 conf_operate.py），数据保存在 SQLite 的 conf 表中（section / key / value）。
/// 进程级缓存一次加载，section/key 全部小写；Reload() 丢弃缓存重新读库。
/// </summary>
public static class AppConfig
{
    // 默认配置：首次运行（conf 表中无对应项）时写入
    private static readonly Dictionary<string, Dictionary<string, string>> Defaults = new()
    {
        ["processes"] = new() { ["processes"] = "5" },
        ["downpath"] = new() { ["downpath"] = "" },
        ["debrid"] = new() { ["api_key"] = "" },
        ["proxy"] = new() { ["openproxy"] = "False", ["host"] = "127.0.0.1", ["port"] = "7890", ["type"] = "http" },
        ["loglevel"] = new() { ["level"] = "info" },
        ["encoding"] = new() { ["encoding"] = "cp437" },
        ["down_list"] = new()
        {
            ["auto_download"] = "False", ["auto_unzip"] = "False", ["download_processes"] = "5",
            ["folder_name"] = "rj", ["min_speed"] = "256", ["speed_limit"] = "0"
        },
        ["media_lib"] = new() { ["libs"] = "[]" },
        ["language"] = new() { ["lang"] = "zh_CN" },
        // 作品类型优先搜索来源：SOU(音声) 可选 asmr / anime-sharing，其他类型固定 anime-sharing
        ["search"] = new() { ["sou_source"] = "asmr" },
        ["web_server"] = new() { ["enabled"] = "False", ["port"] = "8080", ["password"] = "" },
        // asmr.one 数据源：账号 + 登录后保存的 token / recommenderUuid + 镜像站选择
        ["asmr"] = new()
        {
            ["username"] = "", ["password"] = "", ["token"] = "", ["recommender_uuid"] = "",
            ["mirror_site"] = "Original",
        },
        // asmr.one 下载的文件类型过滤（仅勾选的类型会入队下载）
        ["asmr_filetype"] = new()
        {
            ["mp3"] = "True", ["mp4"] = "True", ["flac"] = "True", ["wav"] = "True",
            ["jpg"] = "True", ["png"] = "True", ["pdf"] = "True", ["txt"] = "True",
            ["vtt"] = "True", ["lrc"] = "True",
        },
    };

    private static Dictionary<string, Dictionary<string, string>>? _cache;
    private static readonly object Lock = new();

    /// <summary>Logger 用的级别快捷缓存（避免日志路径反查数据库造成递归）。</summary>
    internal static string LogLevelCached { get; private set; } = "info";

    private static Dictionary<string, Dictionary<string, string>> Cache
    {
        get
        {
            if (_cache is null)
                lock (Lock)
                    _cache ??= Load();
            return _cache;
        }
    }

    /// <summary>丢弃缓存重新从数据库加载（页面切换时调用，保证读到最新值）。</summary>
    public static void Reload()
    {
        lock (Lock)
            _cache = Load();
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        Db.EnsureTables();

        var conf = new Dictionary<string, Dictionary<string, string>>();
        var rows = Db.Select("SELECT \"section\", \"key\", \"value\" FROM \"conf\"");
        if (rows != null)
            foreach (var row in rows)
            {
                var section = (string)row[0]!;
                var key = (string)row[1]!;
                var value = row[2] as string ?? "";
                if (!conf.TryGetValue(section, out var dict))
                    conf[section] = dict = new Dictionary<string, string>();
                dict[key] = value;
            }

        // 仅补齐缺失的默认项（首次运行或新增配置项时才写库），
        // 避免每次 Load/Reload 都盲发数十条 INSERT——切页触发 Reload 时是纯读。
        foreach (var (section, items) in Defaults)
        {
            if (!conf.TryGetValue(section, out var dict))
                conf[section] = dict = new Dictionary<string, string>();
            foreach (var (key, value) in items)
            {
                if (dict.ContainsKey(key))
                    continue;
                Db.Execute(
                    "INSERT OR IGNORE INTO \"conf\" (\"section\", \"key\", \"value\") VALUES (@s, @k, @v)",
                    ("@s", section), ("@k", key), ("@v", value));
                dict[key] = value;   // 同步进本次缓存，免二次查询
            }
        }

        LogLevelCached = conf.GetValueOrDefault("loglevel")?.GetValueOrDefault("level") ?? "info";
        return conf;
    }

    // ---------- 通用读写 ----------

    public static string? Read(string section, string key, string? fallback = null) =>
        Cache.GetValueOrDefault(section.ToLowerInvariant())?.GetValueOrDefault(key.ToLowerInvariant())
        ?? fallback;

    public static void Write(string section, string key, string value)
    {
        section = section.ToLowerInvariant();
        key = key.ToLowerInvariant();
        Db.Execute(
            "INSERT OR REPLACE INTO \"conf\" (\"section\", \"key\", \"value\") VALUES (@s, @k, @v)",
            ("@s", section), ("@k", key), ("@v", value));
        lock (Lock)
        {
            if (!Cache.TryGetValue(section, out var dict))
                Cache[section] = dict = new Dictionary<string, string>();
            dict[key] = value;
            if (section == "loglevel" && key == "level")
                LogLevelCached = value;
        }
    }

    private static int ReadInt(string section, string key, int fallback) =>
        int.TryParse(Read(section, key), out var v) ? Math.Max(0, v) : fallback;

    // ---------- 读取 ----------

    public static string DownloadPath => Read("downpath", "downpath", "") ?? "";

    /// <summary>代理设置：开关 + WebProxy（供 HttpClient 使用）。</summary>
    public static (bool Enabled, WebProxy Proxy) ReadProxy()
    {
        var enabled = Read("proxy", "openproxy") == "True";
        var host = Read("proxy", "host", "127.0.0.1");
        var port = Read("proxy", "port", "7890");
        return (enabled, new WebProxy($"http://{host}:{port}"));
    }

    public static (string Enabled, string Host, string Port, string Type) ReadProxySetting() =>
        (Read("proxy", "openproxy", "False")!, Read("proxy", "host", "")!,
         Read("proxy", "port", "")!, Read("proxy", "type", "http")!);

    public static string DebridApiKey => Read("debrid", "api_key", "") ?? "";

    public static bool AutoDownload => Read("down_list", "auto_download") == "True";

    public static bool AutoUnzip => Read("down_list", "auto_unzip") == "True";

    /// <summary>单文件并发分段数（1 = 不分段，最大 16）。</summary>
    public static int DownloadProcesses => Math.Clamp(ReadInt("down_list", "download_processes", 1), 1, 16);

    /// <summary>下载文件夹命名方式："rj"=按RJ号，"work_name"=按作品名称。</summary>
    public static string FolderNameMode => Read("down_list", "folder_name", "rj") ?? "rj";

    /// <summary>最低下载速度阈值（KB/s），持续低于此值 30 秒后重试；0 表示不限制。</summary>
    public static int MinSpeedKb => ReadInt("down_list", "min_speed", 256);

    /// <summary>下载总速度上限（KB/s），所有并发下载共享；0 表示不限速。</summary>
    public static int SpeedLimitKb => ReadInt("down_list", "speed_limit", 0);

    public static string SysEncoding => Read("encoding", "encoding", "cp437") ?? "cp437";

    public static string Language => Read("language", "lang", "zh_CN") ?? "zh_CN";

    /// <summary>SOU(音声) 作品优先搜索来源："asmr" = asmr.one 直链；"anime-sharing" = 论坛+debrid。</summary>
    public static string SouSearchSource => Read("search", "sou_source", "asmr") ?? "asmr";

    /// <summary>SOU 作品是否走 asmr.one 直链下载（由优先搜索设置决定）。</summary>
    public static bool SouUsesAsmr => SouSearchSource == "asmr";

    // ---------- 外部访问（内嵌 Web 服务）----------

    /// <summary>是否开启外部访问（内嵌 HTTP 服务，手机/电脑浏览器可访问媒体库）。</summary>
    public static bool WebEnabled => Read("web_server", "enabled") == "True";

    /// <summary>外部访问端口（1-65535）。</summary>
    public static int WebPort
    {
        get
        {
            var port = ReadInt("web_server", "port", 8080);
            return port is >= 1 and <= 65535 ? port : 8080;
        }
    }

    /// <summary>外部访问密码；为空表示不鉴权。</summary>
    public static string WebPassword => Read("web_server", "password", "") ?? "";

    /// <summary>媒体库列表（media_lib.libs，JSON）。</summary>
    public static List<MediaLib> ReadMediaLibs()
    {
        List<MediaLib> libs = [];
        try
        {
            var raw = Read("media_lib", "libs", "[]") ?? "[]";
            using var doc = JsonDocument.Parse(raw);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name) || !item.TryGetProperty("folders", out var f) ||
                    f.ValueKind != JsonValueKind.Array)
                    continue;
                var lib = new MediaLib { Name = name };
                foreach (var folder in f.EnumerateArray())
                    if (folder.GetString() is { Length: > 0 } path)
                        lib.Folders.Add(path);
                libs.Add(lib);
            }
        }
        catch (JsonException)
        {
            libs = [];
        }
        return libs;
    }

    public static void WriteMediaLibs(List<MediaLib> libs)
    {
        var json = JsonSerializer.Serialize(
            libs.ConvertAll(l => new { name = l.Name, folders = l.Folders }),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Write("media_lib", "libs", json);
    }

    // ---------- asmr.one 数据源 ----------

    public static string AsmrUsername => Read("asmr", "username", "") ?? "";
    public static string AsmrPassword => Read("asmr", "password", "") ?? "";
    public static string AsmrToken => Read("asmr", "token", "") ?? "";
    public static string AsmrRecommenderUuid => Read("asmr", "recommender_uuid", "") ?? "";

    /// <summary>登录成功后保存 token 与 recommenderUuid。</summary>
    public static void WriteAsmrToken(string recommenderUuid, string token)
    {
        Write("asmr", "recommender_uuid", recommenderUuid);
        Write("asmr", "token", token);
    }

    /// <summary>镜像站选择（Original / Mirror-1 / Mirror-2 / Mirror-3）。</summary>
    public static string AsmrMirrorSite => Read("asmr", "mirror_site", "Original") ?? "Original";

    /// <summary>当前镜像站对应的 API 域名（asmr.one / asmr-100/200/300.com）。</summary>
    public static string AsmrApiHost => AsmrMirrorSite switch
    {
        "Mirror-1" => "asmr-100.com",
        "Mirror-2" => "asmr-200.com",
        "Mirror-3" => "asmr-300.com",
        _ => "asmr.one",
    };

    /// <summary>某文件扩展名（不含点，小写）是否启用下载。</summary>
    public static bool AsmrFileTypeEnabled(string ext) =>
        Read("asmr_filetype", ext.ToLowerInvariant()) != "False";

    /// <summary>全部受支持的 asmr 文件类型（用于设置页勾选 + 默认放行未知扩展名）。</summary>
    public static readonly string[] AsmrFileTypes =
        ["mp3", "mp4", "flac", "wav", "jpg", "png", "pdf", "txt", "vtt", "lrc"];
}
