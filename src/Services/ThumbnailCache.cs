using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace DLsiteMedia.Services;

/// <summary>
/// 进程级封面/缩略图缓存：后台线程解码 BitmapImage 并 Freeze，按 path|size|mtime|解码宽 缓存，
/// 详情↔返回、滚动来回不再重复读盘解码。LRU 上限控制内存，键式思路同 AudioPlayerBar 的增益缓存。
/// </summary>
public static class ThumbnailCache
{
    private const int MaxEntries = 400;
    private static readonly object Lock = new();
    private static readonly Dictionary<string, BitmapSource> Cache = new();
    private static readonly LinkedList<string> Lru = new();   // 头=最近使用，尾=最久未用

    private static string? KeyFor(string path, int decodePixelWidth)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
                return null;
            return $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{decodePixelWidth}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static BitmapSource? Get(string key)
    {
        lock (Lock)
        {
            if (!Cache.TryGetValue(key, out var bmp))
                return null;
            Lru.Remove(key);
            Lru.AddFirst(key);
            return bmp;
        }
    }

    private static void Store(string key, BitmapSource bmp)
    {
        lock (Lock)
        {
            if (Cache.ContainsKey(key))
            {
                Cache[key] = bmp;
                Lru.Remove(key);
                Lru.AddFirst(key);
                return;
            }
            Cache[key] = bmp;
            Lru.AddFirst(key);
            while (Lru.Count > MaxEntries && Lru.Last != null)
            {
                Cache.Remove(Lru.Last.Value);
                Lru.RemoveLast();
            }
        }
    }

    /// <summary>
    /// 取得（或后台解码）指定图片的冻结位图。decodePixelWidth&gt;0 时限制解码尺寸防内存膨胀（传 0 = 原尺寸）。
    /// 命中缓存时同步返回（不阻塞），否则在线程池线程解码后回到调用方上下文。
    /// </summary>
    public static async Task<BitmapSource?> LoadAsync(string path, int decodePixelWidth)
    {
        var key = KeyFor(path, decodePixelWidth);
        if (key == null)
            return null;
        var cached = Get(key);
        if (cached != null)
            return cached;
        var bmp = await Task.Run(() => Decode(path, decodePixelWidth)).ConfigureAwait(true);
        if (bmp != null)
            Store(key, bmp);
        return bmp;
    }

    private static BitmapSource? Decode(string path, int decodePixelWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            if (decodePixelWidth > 0)
                bmp.DecodePixelWidth = decodePixelWidth;
            bmp.EndInit();
            bmp.Freeze();   // 跨线程可用
            return bmp;
        }
        catch (Exception)
        {
            return null;   // 单张损坏/占用不影响整页
        }
    }
}
