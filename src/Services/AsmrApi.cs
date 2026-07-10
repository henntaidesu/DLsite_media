using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DLsiteMedia.Core;

namespace DLsiteMedia.Services;

/// <summary>asmr.one 作品详情中的单个文件（含直链与作品内相对目录）。</summary>
public class AsmrFile
{
    public string Title { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long Size { get; init; }
    public string Type { get; init; } = "";        // audio / image / text / ...
    public string FolderPath { get; init; } = "";   // 作品内相对子目录（可空），如 "01/intro"
}

/// <summary>asmr.one 作品详情：基本信息 + 扁平化后的文件列表。</summary>
public class AsmrWorkDetail
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public long TotalSize { get; set; }
    public List<AsmrFile> Files { get; } = [];
}

/// <summary>
/// asmr.one 官方 API 客户端（移植自 Python 版 asmr_api/*.py：login / get_down_list / get_work_detail / works_review）。
/// 直链下载，无需 debrid 中转、无需解压。镜像站与代理由 AppConfig / Http 统一处理。
/// </summary>
public static class AsmrApi
{
    /// <summary>非作品列表的特殊结果标识（与 Python 版保持一致，便于 UI 层分支处理）。</summary>
    public const string TokenExpired = "TOKEN_EXPIRED";

    private static string ApiBase => $"https://api.{AppConfig.AsmrApiHost}/api";

    /// <summary>
    /// 把 RJ 号转换为 asmr.one 的数字作品 id：asmr.one 直接用 DLsite RJ 编号的数字部分作主键
    /// （如 RJ01234567 → 1234567）。无法解析时返回 0。
    /// </summary>
    public static long RjToId(string rj)
    {
        var digits = new string((rj ?? "").Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var id) ? id : 0;
    }

    /// <summary>用账号密码登录，成功后保存 token / recommenderUuid，返回 (成功, 错误信息)。</summary>
    public static async Task<(bool Ok, string? Error)> LoginAsync()
    {
        var username = AppConfig.AsmrUsername;
        var password = AppConfig.AsmrPassword;
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return (false, "未配置 asmr.one 账号或密码");
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(30));
            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["name"] = username,
                ["password"] = password,
            });
            using var resp = await client.PostAsync($"{ApiBase}/auth/me", body);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("user", out var user) &&
                user.TryGetProperty("loggedIn", out var loggedIn) &&
                loggedIn.ValueKind == JsonValueKind.True)
            {
                AppConfig.WriteAsmrToken(DlsiteApi.JStr(user, "recommenderUuid"), DlsiteApi.JStr(root, "token"));
                Logger.Info("asmr.one 登录成功，token 已保存");
                return (true, null);
            }
            var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            Logger.Error($"asmr.one 登录失败: {err ?? text}");
            return (false, err ?? "登录失败：响应格式异常");
        }
        catch (Exception e)
        {
            Logger.Error(e, "asmr.one 登录");
            return (false, $"登录过程中发生错误：{e.Message}");
        }
    }

    /// <summary>获取作品详情（递归展开文件夹树为扁平文件列表，含直链与大小），失败返回 null。</summary>
    public static async Task<AsmrWorkDetail?> GetWorkDetailAsync(long workId)
    {
        var (text, _) = await GetAuthorizedAsync($"{ApiBase}/tracks/{workId}?v=1");
        if (text is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var detail = new AsmrWorkDetail { Id = workId };
            var first = true;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (first)
                {
                    detail = new AsmrWorkDetail { Id = workId, Title = DlsiteApi.JStr(item, "workTitle") };
                    first = false;
                }
                FlattenItem(item, "", detail);
            }
            Logger.Info($"asmr.one 作品 {workId}：{detail.Files.Count} 个文件，共 {detail.TotalSize / (1024.0 * 1024):F2} MB");
            return detail;
        }
        catch (JsonException e)
        {
            Logger.Error(e, "asmr.one 解析详情");
            return null;
        }
    }

    /// <summary>递归处理 tracks 树：文件夹下钻，文件取直链与大小后加入扁平列表。</summary>
    private static void FlattenItem(JsonElement item, string prefixPath, AsmrWorkDetail detail)
    {
        var type = DlsiteApi.JStr(item, "type");
        if (type == "folder" && item.TryGetProperty("children", out var children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            var title = DlsiteApi.JStr(item, "title");
            var folderPath = string.IsNullOrEmpty(prefixPath) ? title : $"{prefixPath}/{title}";
            foreach (var child in children.EnumerateArray())
                FlattenItem(child, folderPath, detail);
            return;
        }

        var url = DlsiteApi.JStr(item, "mediaDownloadUrl");
        if (string.IsNullOrEmpty(url))
            return;

        long size = 0;
        foreach (var key in new[] { "size", "fileSize", "streamSize", "contentLength" })
        {
            if (item.TryGetProperty(key, out var s) && s.TryGetInt64(out var v) && v > 0)
            {
                size = v;
                break;
            }
        }

        detail.Files.Add(new AsmrFile
        {
            Title = DlsiteApi.JStr(item, "title"),
            DownloadUrl = url,
            Size = size,
            Type = type,
            FolderPath = prefixPath,
        });
        detail.TotalSize += size;
    }

    /// <summary>更新作品收听状态：listened(已听完) / listening(在听)。返回是否成功。</summary>
    public static async Task<bool> ReviewAsync(long workId, bool listened)
    {
        var token = AppConfig.AsmrToken;
        if (string.IsNullOrEmpty(token))
            return false;
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(30));
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{ApiBase}/review")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["progress"] = listened ? "listened" : "listening",
                    ["work_id"] = workId.ToString(),
                }),
            };
            req.Headers.TryAddWithoutValidation("authorization", $"Bearer {token}");
            using var resp = await client.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            Logger.Info($"asmr.one 作品 {workId} 已标记为{(listened ? "听完" : "在听")}");
            return true;
        }
        catch (Exception e)
        {
            Logger.Error(e, "asmr.one 更新收听状态");
            return false;
        }
    }

    /// <summary>
    /// 带 Bearer token 的 GET：401 时自动重新登录并重试一次。
    /// 成功返回 (响应文本, null)；失败返回 (null, 错误标识)。
    /// </summary>
    private static async Task<(string? Text, string? Error)> GetAuthorizedAsync(string url)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = AppConfig.AsmrToken;
            if (string.IsNullOrEmpty(token))
            {
                var (ok, _) = await LoginAsync();
                if (!ok)
                    return (null, TokenExpired);
                token = AppConfig.AsmrToken;
            }
            try
            {
                using var client = Http.CreateClient(TimeSpan.FromSeconds(30));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("authorization", $"Bearer {token}");
                using var resp = await client.SendAsync(req);
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // token 失效：清空后重新登录再试一次
                    AppConfig.WriteAsmrToken(AppConfig.AsmrRecommenderUuid, "");
                    if (attempt == 0)
                    {
                        var (ok, _) = await LoginAsync();
                        if (ok)
                            continue;
                    }
                    return (null, TokenExpired);
                }
                if (!resp.IsSuccessStatusCode)
                {
                    Logger.Error($"asmr.one 请求失败 HTTP {(int)resp.StatusCode}: {url}");
                    return (null, "API_ERROR");
                }
                return (await resp.Content.ReadAsStringAsync(), null);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Logger.Error(e, "asmr.one 网络请求");
                return (null, "NETWORK_ERROR");
            }
        }
        return (null, TokenExpired);
    }
}
