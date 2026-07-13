using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using DLsiteMedia.Core;

namespace DLsiteMedia.Services;

/// <summary>
/// 把库内图片压缩成小缩略图（JPEG，默认 128KB 以内）供网页缩略图网格加载，避免直接传原图。
/// 先等比缩到不超过 720px，再逐级降质，仍超限则继续缩小尺寸；结果按 路径+修改时间 缓存复用。
/// </summary>
public static class ImageThumb
{
    private const int DefaultMaxBytes = 128 * 1024;   // 128KB
    private static readonly int[] Qualities = [82, 70, 58, 46, 35, 25];

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    // 简单内存缓存（键=路径|修改时刻|大小|上限）：避免同一图片被反复重新压缩；超量清空防无限增长
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();
    private const int CacheCap = 512;

    /// <summary>把图片压成 JPEG 缩略图（≤ maxBytes，尽力保证）；无法处理时返回 null（调用方回退原图）。</summary>
    public static byte[]? Compress(string path, int maxBytes = DefaultMaxBytes)
    {
        try
        {
            var fi = new FileInfo(path);
            var key = $"{path}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|{maxBytes}";
            if (Cache.TryGetValue(key, out var hit))
                return hit;

            using var src = Image.FromFile(path);
            byte[]? best = null;
            // 逐步缩小最长边：720 → 540 → 405 → 303 → 240，每档再逐级降质，命中 maxBytes 即返回
            for (var maxDim = 720; maxDim >= 240; maxDim = maxDim * 3 / 4)
            {
                using var bmp = Scale(src, maxDim);
                foreach (var q in Qualities)
                {
                    best = Encode(bmp, q);
                    if (best.Length <= maxBytes)
                    {
                        Store(key, best);
                        return best;
                    }
                }
            }
            // 极端大图：即便最小尺寸+最低质量仍超限，也返回这份（已远小于原图）
            if (best != null)
                Store(key, best);
            return best;
        }
        catch (Exception e)
        {
            Logger.Error($"缩略图压缩失败 {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>等比缩放到最长边不超过 maxDim（只缩不放）。</summary>
    private static Bitmap Scale(Image src, int maxDim)
    {
        var scale = Math.Min(1.0, (double)maxDim / Math.Max(src.Width, src.Height));
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var bmp = new Bitmap(w, h);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(src, 0, 0, w, h);
        }
        catch (Exception)
        {
            bmp.Dispose();
            throw;
        }
        return bmp;
    }

    /// <summary>把位图编码为指定质量的 JPEG 字节。</summary>
    private static byte[] Encode(Bitmap bmp, int quality)
    {
        using var ms = new MemoryStream();
        using var ep = new EncoderParameters(1);
        ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        bmp.Save(ms, JpegCodec, ep);
        return ms.ToArray();
    }

    private static void Store(string key, byte[] data)
    {
        if (Cache.Count >= CacheCap)
            Cache.Clear();   // 简单封顶：满则整清，随浏览重新填充，避免复杂淘汰逻辑
        Cache[key] = data;
    }
}
