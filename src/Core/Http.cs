using System;
using System.Net;
using System.Net.Http;

namespace DLsiteMedia.Core;

/// <summary>HttpClient 工厂：按当前代理设置创建客户端（对应 Python 各模块的 requests.Session）。</summary>
public static class Http
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var (enabled, proxy) = AppConfig.ReadProxy();
        // 像浏览器一样自动协商并解压 gzip/deflate/br，否则 Cloudflare 等可能返回压缩内容导致读到乱码
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (enabled)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
