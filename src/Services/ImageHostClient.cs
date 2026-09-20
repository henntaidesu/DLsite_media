using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace R18MediaLibrary.Services;

/// <summary>
/// 自建图床（D:\Project\Image_hosting，Flask）的 /api/v1 客户端：只认 Bearer 项目 Token，只回 JSON。
///
/// 图片公开地址 <c>{base}/images/{slug}/{stored_name}</c> 无需鉴权，且支持 <c>?w=</c> 出缩略图
/// （宽度只接受固定档位，见 <see cref="ThumbWidths"/>，请求值向上取整）——卡片封面因此可以让
/// 桌面端与手机浏览器直接取图，绕开本机 HDD。
/// </summary>
public static class ImageHostClient
{
    /// <summary>图床支持的缩略图档位（与图床 config.DERIVATIVE_WIDTHS 一致，请求值向上取整到最近档）。</summary>
    public static readonly int[] ThumbWidths = [100, 200, 300, 400, 560, 800, 1200];

    /// <summary>作品卡封面取图宽度：WPF 卡片封面 186px、Web 卡片最窄 150px，二倍图后都落在 400 这一档。</summary>
    public const int CardWidth = 400;

    /// <summary>
    /// 全局复用一个客户端：卡片封面会短时间内并发取几十张图，每次 new HttpClient 会耗尽端口。
    /// 明确不走代理——代理是给 DLsite / asmr.one 这些外网站点配的，图床是用户自己的机器
    /// （多为 127.0.0.1 或局域网 IP），套上代理反而连不通。
    /// </summary>
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(100),
    };

    /// <summary>供缩略图缓存取图复用（同一份连接池）。</summary>
    internal static HttpClient Shared => Client;

    public sealed record PingInfo(string Project, string Name, int ImageCount, long TotalSize, int MaxUploadMb);

    /// <param name="Stale">
    /// 图床上已存在同 external_key、内容却不一样的旧图（图床按 sha256 判定，返回 422）。
    /// 出现在"本地映射表丢了 / 换过封面但没来得及删旧图"时——幂等键把旧记录顶了回来，
    /// 光重试永远传不上去，调用方需先删掉旧图再传。
    /// </param>
    public sealed record UploadResult(
        bool Ok, string? StoredName, string? Path, bool Reused, string? Error, bool Stale = false);

    // ---------- 地址拼装 ----------

    /// <summary>把图床返回的 path（/images/slug/xxx.jpg）拼成带缩略图档位的公开地址。</summary>
    public static string BuildUrl(string baseUrl, string path, int width)
    {
        var root = NormalizeBase(baseUrl);
        return width > 0 ? $"{root}{path}?w={Normalize(width)}" : $"{root}{path}";
    }

    /// <summary>
    /// 规整服务地址：补协议、去尾部斜杠。设置页可能只填了 <c>192.168.1.5:9990</c>，
    /// 而"测试连接"要能直接用页面上还没保存的值，所以在客户端这一层统一兜住。
    /// </summary>
    public static string NormalizeBase(string? baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (url.Length > 0 && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                           && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        return url;
    }

    /// <summary>宽度向上取整到图床的固定档位（未命中档位的请求会被图床当原图处理，白白传大图）。</summary>
    public static int Normalize(int width)
    {
        foreach (var w in ThumbWidths)
            if (width <= w)
                return w;
        return ThumbWidths[^1];
    }

    private static string ApiRoot(string baseUrl, string project) =>
        $"{NormalizeBase(baseUrl)}/api/v1/projects/{Uri.EscapeDataString(project.Trim())}";

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    // ---------- 端点 ----------

    /// <summary>连接自检：地址/项目/Token 是否对得上，顺带拿回图床侧的限制与现有图片数。</summary>
    public static async Task<(bool Ok, PingInfo? Info, string? Error)> PingAsync(
        string baseUrl, string project, string token, CancellationToken ct = default)
    {
        if (baseUrl.Length == 0 || project.Length == 0 || token.Length == 0)
            return (false, null, "图床地址 / 项目 / Token 未填写完整");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = Authorized(HttpMethod.Get, $"{ApiRoot(baseUrl, project)}/ping", token);
            using var resp = await Client.SendAsync(request, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                return (false, null, ErrorText(resp.StatusCode, body));
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return (true, new PingInfo(
                Str(root, "project"), Str(root, "name"),
                Int(root, "image_count"), Long(root, "total_size"), Int(root, "max_upload_mb")), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, null, FriendlyNetworkError(e));
        }
    }

    /// <summary>
    /// 上传一张本地图片。<paramref name="externalKey"/> 是图床侧的幂等键：同一个 key 重复上传
    /// 不会产生第二份文件，直接返回已有记录（reused=true）——同步中断后重跑靠的就是它。
    /// </summary>
    public static async Task<UploadResult> UploadAsync(
        string baseUrl, string project, string token,
        string localPath, string externalKey, CancellationToken ct = default)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(localPath, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new UploadResult(false, null, null, false, e.Message);
        }
        return await UploadAsync(baseUrl, project, token, bytes, Sha256Of(bytes),
            Path.GetExtension(localPath).ToLowerInvariant(), externalKey, ct);
    }

    /// <summary>
    /// 上传已读入内存的图片。迁移时调用方为了比对内容指纹本就要读盘 + 算哈希，
    /// 让它把结果带过来，同一张封面就只读一次盘（源盘是 HDD，能少一次随机读是一次）。
    /// </summary>
    public static async Task<UploadResult> UploadAsync(
        string baseUrl, string project, string token,
        byte[] bytes, string sha256, string extension, string externalKey, CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue(MimeOf(extension));
            // 文件名自己造成纯 ASCII：图床对原文件名跑 secure_filename，日文/中文名会被剥成空串，
            // 连扩展名一起丢掉后会被判成"不允许的类型"而 400。图床侧真正的存储名是 uuid，
            // 这里的名字只进 original_name 一栏，造一个稳定可读的即可。
            content.Add(file, "file", SafeFileName(externalKey, extension));
            content.Add(new StringContent(externalKey), "external_key");
            content.Add(new StringContent(sha256), "sha256");

            using var request = Authorized(HttpMethod.Post, $"{ApiRoot(baseUrl, project)}/images", token);
            request.Content = content;
            using var resp = await Client.SendAsync(request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new UploadResult(false, null, null, false, ErrorText(resp.StatusCode, body),
                    Stale: resp.StatusCode == HttpStatusCode.UnprocessableContent);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new UploadResult(true, Str(root, "stored_name"), Str(root, "path"),
                root.TryGetProperty("reused", out var r) && r.ValueKind == JsonValueKind.True, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or IOException or UnauthorizedAccessException)
        {
            return new UploadResult(false, null, null, false, FriendlyNetworkError(e));
        }
    }

    /// <summary>图床上已有的一张图（lookup 结果）。</summary>
    public sealed record HostedImage(string StoredName, string Path, string Sha256);

    /// <summary>单次 lookup 的 external_key 上限（图床侧 MAX_LOOKUP_KEYS）。</summary>
    public const int LookupBatch = 500;

    /// <summary>
    /// 按 external_key 批量查图床上已有的图，返回 key -> 图片信息。
    ///
    /// 这是"迁移不重复"的关键一环：本地映射表丢了（重装、还原备份、换机器）也能一次问清楚
    /// 哪些封面已经在图床上，直接把映射捡回来，而不是整库重传一遍。
    /// </summary>
    public static async Task<Dictionary<string, HostedImage>> LookupAsync(
        string baseUrl, string project, string token, IEnumerable<string> keys, CancellationToken ct = default)
    {
        var found = new Dictionary<string, HostedImage>(StringComparer.Ordinal);
        try
        {
            using var request = Authorized(HttpMethod.Post, $"{ApiRoot(baseUrl, project)}/images/lookup", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { external_keys = keys }), Encoding.UTF8, "application/json");
            using var resp = await Client.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
                return found;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("found", out var hits) || hits.ValueKind != JsonValueKind.Object)
                return found;
            foreach (var hit in hits.EnumerateObject())
            {
                var stored = Str(hit.Value, "stored_name");
                if (stored.Length > 0)
                    found[hit.Name] = new HostedImage(stored, Str(hit.Value, "path"), Str(hit.Value, "sha256"));
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 查不到就当图床上没有，调用方照常按"新图"处理
        }
        return found;
    }

    /// <summary>算内容指纹（十六进制小写，与图床侧 sha256 字段同格式）。</summary>
    public static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>删除图床上的一张图（幂等：删不存在的也算成功）。换封面时必须先删，否则幂等键会命中旧图。</summary>
    public static async Task<bool> DeleteAsync(
        string baseUrl, string project, string token, string storedName, CancellationToken ct = default)
    {
        try
        {
            using var request = Authorized(
                HttpMethod.Delete, $"{ApiRoot(baseUrl, project)}/images/{Uri.EscapeDataString(storedName)}", token);
            using var resp = await Client.SendAsync(request, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // ---------- 工具 ----------

    private static string SafeFileName(string externalKey, string extension)
    {
        var sb = new StringBuilder();
        foreach (var ch in externalKey)
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        var stem = sb.ToString().Trim('_');
        return (stem.Length > 0 ? stem : "image") + (extension.Length > 0 ? extension : ".jpg");
    }

    private static string MimeOf(string extension) => extension switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".avif" => "image/avif",
        _ => "image/jpeg",
    };

    /// <summary>把图床返回的 {"error": "..."} 转成可直接显示的文案。</summary>
    private static string ErrorText(HttpStatusCode status, string body)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            detail = Str(doc.RootElement, "error");
        }
        catch (JsonException)
        {
            // 非 JSON 响应（如反向代理的错误页）按原样截断显示
            detail = body.Length > 120 ? body[..120] : body;
        }
        return status switch
        {
            HttpStatusCode.Unauthorized => "项目或 API Token 无效",
            // 图床填了公开访问基地址后默认只认该域名，按 IP 直连会被判成非法主机
            HttpStatusCode.BadRequest when detail.Length == 0 =>
                "图床拒绝了该访问地址，请在图床「系统设置 → 附加访问主机名」里加入本地址",
            HttpStatusCode.InsufficientStorage => "图床没有空间足够的存储位置",
            _ => detail.Length > 0 ? detail : $"HTTP {(int)status}",
        };
    }

    private static string FriendlyNetworkError(Exception e) => e switch
    {
        TaskCanceledException => "连接图床超时",
        HttpRequestException => $"无法连接图床：{e.Message}",
        _ => e.Message,
    };

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static long Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}
