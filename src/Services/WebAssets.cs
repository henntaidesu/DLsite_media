using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace DLsiteMedia.Services;

/// <summary>外部访问网页的静态资源：单文件响应式 SPA，作为嵌入资源打包（Web/index.html）。</summary>
internal static class WebAssets
{
    private static byte[]? _index;
    private static readonly object Sync = new();

    public static byte[] IndexHtml()
    {
        if (_index != null)
            return _index;
        lock (Sync)
        {
            if (_index != null)
                return _index;
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
            if (name != null)
                using (var stream = asm.GetManifestResourceStream(name))
                using (var reader = new StreamReader(stream!, Encoding.UTF8))
                    _index = Encoding.UTF8.GetBytes(reader.ReadToEnd());
            else
                _index = Encoding.UTF8.GetBytes("<h1>index.html resource missing</h1>");
            return _index;
        }
    }
}
