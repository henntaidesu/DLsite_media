using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DLsiteMedia.Core;

namespace DLsiteMedia.Services;

/// <summary>直连检测下载链接是否有效，不经过中转站（对应 Python 版 doun_url_test.py）。</summary>
public static class LinkChecker
{
    // 个别网盘（如 rapidgator / katfile）对失效文件返回 200 + 错误页而非 404，
    // 需要用页面中明确的死链提示语兜底（统一小写匹配）
    private static readonly string[] DeadMarkers =
    [
        // 英文
        "file not found",
        "404 file",
        "404 not found",
        "file was deleted",
        "file has been deleted",
        "file has expired",
        "file does not exist",
        "does not exist or it has been removed",
        "file is no longer available",
        "no longer available",
        "file you requested",
        "file you were looking for",
        "file has been removed",   // katfile 404 页 <img alt="File has been removed">
        "has been removed",
        "was removed",
        "page not found",
        "no such file",
        "unknown file",
        "invalid file",
        "file not available",
        "404-remove.png",          // katfile 专属死链图片
        // depositfiles 专属（CSS class/id，与语言无关，不受编码影响）
        "page_download_no_file",   // <body class="... page_download_no_file">
        "html_download_api-not_exists",  // <span class="html_download_api-not_exists">
        // 中文（depositfiles 等按 IP/地区返回本地化错误页）
        "此文件不存在",
        "文件不存在",
        "文件已被删除",
        "文件已删除",
        "文件不可用",
        // 俄文（depositfiles 源站语言）
        "файл не найден",
        "файл не существует",
        "файл удален",
    ];

    // 页面 <title> 中出现这些词，几乎可断定是错误/未找到页（有效下载页标题不会含这些）
    private static readonly string[] TitleDeadWords =
    [
        "404", "not found", "file not found", "error", "deleted", "expired", "removed",
    ];

    private static readonly Regex TitleRegex =
        new("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static HttpClient MakeClient() => Http.CreateClient(TimeSpan.FromSeconds(20));

    /// <summary>
    /// 判定规则（任一命中即失效）：
    /// 1. HTTP 4xx/5xx 状态（网盘对已删除/过期文件通常返回 404，katfile 有时返回 500）；
    /// 2. 死链被重定向到站点首页 / 明显的错误页（katfile 等死链跳首页，状态仍是 200）；
    /// 3. 页面 &lt;title&gt; 含 404 / not found / error 等错误词；
    /// 4. 页面正文含明确的"文件不存在"提示语（rapidgator 等返回 200+错误页的网盘）。
    /// 其余视为有效。
    /// </summary>
    /// <param name="ct">
    /// 用户点击下载后由调用方触发取消：此时应立即中止正在进行的网络请求，而不是等到超时，
    /// 因此显式区分"用户主动取消"（向上抛出，不判定为失效）与"HttpClient 自身超时"（判定为失效）。
    /// </param>
    public static async Task<bool> CheckUrlAsync(string url, HttpClient client, CancellationToken ct = default)
    {
        // mega 是纯前端应用，死链同样返回 200 + JS 页面，必须走 mega API 判断文件是否存在
        if (IsMega(url))
            return await CheckMegaAsync(url, client, ct);

        string text;
        Uri? finalUri;
        try
        {
            using var response = await client.GetAsync(url, ct);
            if ((int)response.StatusCode >= 400)
                return false;
            finalUri = response.RequestMessage?.RequestUri;  // 跟随重定向后的最终地址
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;  // 用户主动中断校验，不判定为失效
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return false;
        }

        // 2. 死链跳首页 / 错误页：原链接指向具体文件，最终却落到站点根或错误页
        if (finalUri != null && RedirectedAway(url, finalUri))
            return false;

        var lower = text.ToLowerInvariant();

        // 3. 标题命中错误词
        var title = TitleRegex.Match(text);
        if (title.Success)
        {
            var titleText = title.Groups[1].Value.ToLowerInvariant();
            foreach (var word in TitleDeadWords)
                if (titleText.Contains(word))
                    return false;
        }

        // 4. 正文命中死链提示语
        foreach (var marker in DeadMarkers)
            if (lower.Contains(marker))
                return false;

        // 5. SPA 空壳（turbobit 等）或 katfile：HTML 内容因代理 IP 不同而异（死链页 / 付费页均可能
        //    出现），仅靠文本无法可靠判断，改用 debrid-link 解析确认。
        if (IsKatfile(url) || IsSpaShell(text))
            return await VerifyViaDebridAsync(url, ct);

        return true;
    }

    /// <summary>
    /// 页面是否为内容全靠 JS 渲染的空壳（turbobit / k2s 等现代网盘）：
    /// 体积很小，存在挂载点(app/root)，且要么挂载点直接闭合、要么用 ES module 打包入口。
    /// </summary>
    private static bool IsSpaShell(string html)
    {
        if (html.Length > 20000)
            return false;  // 有实际内容的页面不算空壳，避免误判
        var lower = html.ToLowerInvariant();
        var hasMount = lower.Contains("id=\"app\"") || lower.Contains("id=\"root\"")
                       || lower.Contains("id='app'") || lower.Contains("id='root'")
                       || lower.Contains("data-app=");
        if (!hasMount)
            return false;
        return lower.Contains("type=\"module\"")            // 现代 SPA 的 ES module 入口
               || lower.Contains("id=\"app\"></div>")        // 空挂载点
               || lower.Contains("id=\"root\"></div>")
               || lower.Contains("__nuxt__")
               || lower.Contains("__next_data__");
    }

    /// <summary>
    /// 用 debrid-link 解析链接来确认有效性（仅用于 HTML 无法判断的 SPA 网盘）：
    /// 成功拿到直链=有效；返回明确的"文件失效"错误=失效；其它情况(限流/不支持/未配置)不拦截。
    /// </summary>
    private static async Task<bool> VerifyViaDebridAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(AppConfig.DebridApiKey))
            return true;  // 未配置中转，无法验证则不误判为失效
        try
        {
            using var debrid = new DebridLinkClient();
            var (value, error) = await debrid.AddDownloadDetailedAsync(url, ct: ct);
            if (value is { } v && !string.IsNullOrEmpty(DlsiteApi.JStr(v, "downloadUrl")))
                return true;  // 中转成功解析 → 有效
            // 仅在明确的"文件不存在/已失效"错误时判失效；限流/会员/不支持等无法判定 → 不拦截
            return error is not ("fileNotFound" or "fileUnavailable" or "notFound"
                or "fileError" or "fileNotAvailable");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;  // 用户主动中断校验，不判定为失效
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return true;  // 网络异常不误判为失效
        }
    }

    private static bool IsKatfile(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return host == "katfile.com" || host.EndsWith(".katfile.com")
                   || host == "katfile.space" || host.EndsWith(".katfile.space")
                   || host == "katfile.cloud" || host.EndsWith(".katfile.cloud");
        }
        catch (UriFormatException) { return false; }
    }

    private static bool IsMega(string url)
    {
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return host == "mega.nz" || host.EndsWith(".mega.nz")
                   || host == "mega.co.nz" || host.EndsWith(".mega.co.nz");
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// 通过 mega API 判断公开文件是否存在：POST [{"a":"g","p":"&lt;id&gt;"}]，
    /// 返回含 "s"(大小) 视为有效；返回 -9(不存在)/-2(参数错误) 视为死链；其它(如 -3 限流)不误判。
    /// </summary>
    private static async Task<bool> CheckMegaAsync(string url, HttpClient client, CancellationToken ct)
    {
        var id = ExtractMegaFileId(url);
        if (id == null)
            return true;  // 文件夹链接或无法解析：不在此处拦截，交给后续中转判断
        try
        {
            var body = $"[{{\"a\":\"g\",\"p\":\"{id}\"}}]";
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("https://g.api.mega.co.nz/cs", content, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var first = root.ValueKind == JsonValueKind.Array
                ? (root.GetArrayLength() > 0 ? root[0] : default)
                : root;
            if (first.ValueKind == JsonValueKind.Number)
                return MegaCodeAlive(first.GetInt64());
            if (first.ValueKind == JsonValueKind.Object)
            {
                if (first.TryGetProperty("e", out var e) && e.ValueKind == JsonValueKind.Number)
                    return MegaCodeAlive(e.GetInt64());
                return first.TryGetProperty("s", out _);  // 含文件大小=有效
            }
            return true;  // 无法识别的响应不误判为失效
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;  // 用户主动中断校验，不判定为失效
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return true;  // 网络/解析异常不误判为失效，交由真正下载时再确认
        }
    }

    /// <summary>mega 错误码：&gt;=0 有效；-9(不存在)/-2(参数错误) 死链；其它负值(限流等)不判死。</summary>
    private static bool MegaCodeAlive(long code) => code >= 0 || (code != -9 && code != -2);

    /// <summary>从 mega 链接解析公开文件句柄（支持 /file/&lt;id&gt; 与旧版 /#!&lt;id&gt; 格式）。</summary>
    private static string? ExtractMegaFileId(string url)
    {
        var m = Regex.Match(url, @"mega\.(?:nz|co\.nz)/file/([^#?/]+)", RegexOptions.IgnoreCase);
        if (m.Success)
            return m.Groups[1].Value;
        m = Regex.Match(url, @"mega\.(?:nz|co\.nz)/#!([^!#?/]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>原链接指向具体文件，却被重定向到站点根目录或明显的错误页，判为死链。</summary>
    private static bool RedirectedAway(string originalUrl, Uri finalUri)
    {
        try
        {
            var original = new Uri(originalUrl);
            var origPath = original.AbsolutePath.Trim('/');
            var finalPath = finalUri.AbsolutePath.Trim('/');
            // 原本有文件路径，却被甩到首页（空路径）
            if (origPath.Length > 0 && finalPath.Length == 0)
                return true;
            var finalLower = finalUri.AbsoluteUri.ToLowerInvariant();
            return finalLower.Contains("/404")
                   || finalLower.Contains("notfound")
                   || finalLower.Contains("not-found")
                   || finalLower.Contains("/error");
        }
        catch (UriFormatException)
        {
            return false;
        }
    }
}
