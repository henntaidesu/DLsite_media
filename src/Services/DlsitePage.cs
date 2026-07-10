using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using DLsiteMedia.Core;
using HtmlAgilityPack;

namespace DLsiteMedia.Services;

/// <summary>DLsite 作品页抓取结果。</summary>
public class WorkPageData
{
    /// <summary>works 表列名 -> 值（sell_date / series / scenario / illust / voice_actor /
    /// age_category / work_type / genre / file_size / work_name / maker_name）。</summary>
    public Dictionary<string, string> Fields { get; } = new();

    /// <summary>ジャンル标签列表（供 work_genres 表使用）。</summary>
    public List<string> Genres { get; } = [];
}

/// <summary>DLsite 作品页抓取与数据源文件管理（对应 Python 版 DLsite_page.py）。</summary>
public static class DlsitePage
{
    public const string ImagesDir = "images";            // 无作品文件夹时的图片回退目录：images/<RJ号>/
    public const string DataSourceDir = "DataSource";    // 作品文件夹内存放 DLsite 图片的子文件夹名
    public const string DescriptionTxt = "description.txt";
    public static readonly string[] ImageExts = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    /// <summary>description.txt 中正文图片的占位标记行：[img:文件名]</summary>
    public static readonly System.Text.RegularExpressions.Regex BodyImageRe =
        new(@"^\[img:(.+)\]$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // 作品页 work_outline 表格字段 -> works 表列名
    private static readonly Dictionary<string, string> FieldMap = new()
    {
        ["販売日"] = "sell_date",
        ["シリーズ名"] = "series",
        ["シナリオ"] = "scenario",
        ["イラスト"] = "illust",
        ["声優"] = "voice_actor",
        ["年齢指定"] = "age_category",
        ["作品形式"] = "work_type",
        ["ジャンル"] = "genre",
        ["ファイル容量"] = "file_size",
    };

    private static HttpClient CreateClient()
    {
        var (enabled, proxy) = AppConfig.ReadProxy();
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
        };
        if (enabled)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        // R18 作品页需要已通过年龄确认
        handler.CookieContainer.Add(new Cookie("adultchecked", "1", "/", ".dlsite.com"));
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Http.UserAgent);
        return client;
    }

    /// <summary>
    /// 抓取 DLsite 作品页，返回 (字段, 图片 URL 列表, 正文文本)；
    /// 图片列表为轮播图在前、正文图片在后（封面取第一张轮播图）。
    /// 页面不存在或请求失败返回 (null, [], "")。
    /// </summary>
    public static async Task<(WorkPageData? Data, List<string> ImageUrls, string BodyText)>
        GetWorkPageAsync(string workId)
    {
        using var client = CreateClient();
        string? html = null;
        foreach (var shop in new[] { "home", "maniax" })
        {
            var url = $"https://www.dlsite.com/{shop}/work/=/product_id/{workId}.html";
            try
            {
                using var response = await client.GetAsync(url);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    html = await response.Content.ReadAsStringAsync();
                    break;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Logger.Error($"作品页请求失败 {workId}: {e.Message}");
                return (null, [], "");
            }
        }
        if (html == null)
        {
            Logger.Info($"作品页不存在 {workId}");
            return (null, [], "");
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var data = new WorkPageData();

        var outline = doc.GetElementbyId("work_outline");
        if (outline != null)
            foreach (var tr in outline.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
            {
                var th = tr.SelectSingleNode(".//th");
                var td = tr.SelectSingleNode(".//td");
                if (th == null || td == null)
                    continue;
                if (!FieldMap.TryGetValue(CleanText(th), out var column))
                    continue;
                data.Fields[column] = CleanText(td);
                if (column == "genre")
                {
                    // ジャンル标签逐个提取（每个标签是一个 <a>），供 work_genres 表使用
                    foreach (var a in td.SelectNodes(".//a") ?? Enumerable.Empty<HtmlNode>())
                    {
                        var tag = CleanText(a);
                        if (tag.Length > 0)
                            data.Genres.Add(tag);
                    }
                }
            }

        // 作品名 / 社团名（suggest API 检索不到的作品兜底）
        var nameNode = doc.GetElementbyId("work_name");
        if (nameNode != null)
            data.Fields["work_name"] = CleanText(nameNode);
        var makerNode = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'maker_name')]");
        if (makerNode != null)
            data.Fields["maker_name"] = CleanText(makerNode);

        var urls = new List<string>();
        var slider = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'product-slider-data')]");
        if (slider != null)
            foreach (var div in slider.SelectNodes(".//div") ?? Enumerable.Empty<HtmlNode>())
            {
                var src = div.GetAttributeValue("data-src", "");
                if (src.Length > 0)
                    urls.Add(src.StartsWith("//") ? "https:" + src : src);
            }
        if (urls.Count == 0)
        {
            var og = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
            var content = og?.GetAttributeValue("content", "");
            if (!string.IsNullOrEmpty(content))
                urls.Add(content);
        }

        // 正文（work_parts_container）：图片先替换成 [img:文件名] 占位标记再取文本，
        // 详情页据此把正文图片嵌回原文位置
        var bodyText = "";
        var description = doc.DocumentNode.SelectSingleNode("//div[@itemprop='description']")
                          ?? doc.DocumentNode.SelectSingleNode("//*[contains(@class,'work_parts_container')]");
        if (description != null)
        {
            foreach (var img in (description.SelectNodes(".//img") ?? Enumerable.Empty<HtmlNode>()).ToList())
            {
                var src = img.GetAttributeValue("data-src", "");
                if (src.Length == 0)
                    src = img.GetAttributeValue("src", "");
                var marker = "";
                if (src.Length > 0)
                {
                    var url = src.StartsWith("//") ? "https:" + src : src;
                    if (!urls.Contains(url))
                        urls.Add(url);
                    var filename = url.Split('/')[^1].Split('?')[0];
                    if (filename.Length > 0)
                        marker = $"\n[img:{filename}]\n";
                }
                img.ParentNode.ReplaceChild(HtmlNode.CreateNode(
                    HtmlDocument.HtmlEncode(marker.Length > 0 ? marker : " ")), img);
            }
            bodyText = ExtractTextLines(description);
        }
        return (data, urls, bodyText);
    }

    /// <summary>提取节点文本：各文本片段去空白后按行拼接（近似 BeautifulSoup get_text('\n', strip=True)）。</summary>
    private static string ExtractTextLines(HtmlNode node)
    {
        var lines = new List<string>();
        foreach (var text in node.DescendantsAndSelf()
                     .Where(n => n.NodeType == HtmlNodeType.Text))
        {
            var value = HtmlEntity.DeEntitize(text.InnerText);
            foreach (var line in value.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    lines.Add(trimmed);
            }
        }
        return string.Join('\n', lines);
    }

    private static string CleanText(HtmlNode node) =>
        string.Join(' ', HtmlEntity.DeEntitize(node.InnerText)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>作品数据源目录：<作品文件夹>/DataSource/，无作品文件夹时回退 images/<RJ号>/。</summary>
    public static string DataSourceFolder(string workId, string? workFolder = null)
    {
        if (!string.IsNullOrEmpty(workFolder) && Directory.Exists(workFolder))
            return Path.Combine(workFolder, DataSourceDir);
        return Path.Combine(ImagesDir, workId);
    }

    /// <summary>删除作品已下载的数据源文件夹（强制重扫确认作品页可访问后调用）。</summary>
    public static bool RemoveWorkDataSource(string workId, string? workFolder = null)
    {
        var folder = DataSourceFolder(workId, workFolder);
        if (!Directory.Exists(folder))
            return false;
        try
        {
            Directory.Delete(folder, true);
            return true;
        }
        catch (IOException e)
        {
            Logger.Error($"数据源文件夹删除失败 {workId}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 下载作品全部图片到数据源目录。已存在图片时不再下载，直接返回第一张作为封面；
    /// 否则逐张下载（已存在的文件跳过），返回封面路径，没有图片返回 ""。
    /// </summary>
    public static async Task<string> DownloadWorkImagesAsync(
        string workId, List<string> urls, string? workFolder = null)
    {
        var folder = DataSourceFolder(workId, workFolder);
        if (Directory.Exists(folder))
        {
            var existing = Directory.GetFiles(folder)
                .Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (existing.Count > 0)
            {
                // 优先用主图做封面（正文图片是哈希文件名，排序会排在主图前面）
                var main = existing.FirstOrDefault(f =>
                    Path.GetFileName(f).Contains("img_main", StringComparison.OrdinalIgnoreCase));
                return main ?? existing[0];
            }
        }
        if (urls.Count == 0)
            return "";
        Directory.CreateDirectory(folder);
        using var client = CreateClient();
        var cover = "";
        for (var i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            var filename = url.Split('/')[^1].Split('?')[0];
            if (filename.Length == 0)
                filename = $"{workId}_{i}";
            var path = Path.Combine(folder, filename);
            if (!File.Exists(path))
            {
                try
                {
                    using var response = await client.GetAsync(url);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        Logger.Error($"图片下载失败 {url}: HTTP {(int)response.StatusCode}");
                        continue;
                    }
                    await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
                {
                    Logger.Error($"图片下载失败 {url}: {e.Message}");
                    continue;
                }
            }
            if (cover.Length == 0)
                cover = path;
        }
        return cover;
    }

    /// <summary>把作品页正文保存为数据源目录的 description.txt（覆盖写入），失败返回 ""。</summary>
    public static string SaveWorkDescription(string workId, string text, string? workFolder = null)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var folder = DataSourceFolder(workId, workFolder);
        var path = Path.Combine(folder, DescriptionTxt);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch (IOException e)
        {
            Logger.Error($"正文保存失败 {workId}: {e.Message}");
            return "";
        }
        return path;
    }
}
