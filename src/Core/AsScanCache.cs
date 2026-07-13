using System;

namespace DLsiteMedia.Core;

/// <summary>
/// AS 论坛扫描结果缓存：某作品扫描出"无结果"（count==0）时记录时间，7 天内不再重复扫 AS。
/// 只缓存"无结果"——有结果的作品帖子可能随时新增，不做缓存以免漏掉；无结果的重复扫描才是浪费。
/// </summary>
public static class AsScanCache
{
    private const int TtlDays = 7;

    /// <summary>是否存在 7 天内的"无结果"缓存——命中则应跳过本次 AS 扫描、直接按"AS·无"处理。</summary>
    public static bool HasFreshEmpty(string workId) => RemainSeconds(workId) > 0;

    /// <summary>距该作品"无结果"缓存到期（可再次扫描）的剩余秒数；无缓存 / 有结果 / 已过期均返回 0。</summary>
    public static long RemainSeconds(string workId)
    {
        if (string.IsNullOrEmpty(workId))
            return 0;
        var rows = Db.Select(
            "SELECT \"count\", \"time\" FROM \"as_scan_cache\" WHERE \"work_id\" = @w", ("@w", workId));
        if (rows is not { Count: > 0 })
            return 0;
        var count = rows[0][0] is { } c ? Convert.ToInt32(c) : -1;
        if (count != 0)
            return 0;
        if (rows[0][1] is not string t || !DateTime.TryParse(t, out var when))
            return 0;
        var remain = when.AddDays(TtlDays) - DateTime.Now;
        return remain.TotalSeconds > 0 ? (long)remain.TotalSeconds : 0;
    }

    /// <summary>清除某作品的"无结果"缓存，使其可立即重新扫描 AS（用户主动"全部重新解析"时调用）。</summary>
    public static void Clear(string workId)
    {
        if (string.IsNullOrEmpty(workId))
            return;
        Db.Execute("DELETE FROM \"as_scan_cache\" WHERE \"work_id\" = @w", ("@w", workId));
    }

    /// <summary>记录一次扫描结果：无结果（0）写入缓存并计时；有结果（&gt;0）清除旧缓存以便下次正常扫描。负数（失败）不记录。</summary>
    public static void Store(string workId, int count)
    {
        if (string.IsNullOrEmpty(workId) || count < 0)
            return;
        if (count == 0)
            Db.Execute(
                "INSERT OR REPLACE INTO \"as_scan_cache\" (\"work_id\", \"count\", \"time\") VALUES (@w, 0, @t)",
                ("@w", workId), ("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        else
            Db.Execute("DELETE FROM \"as_scan_cache\" WHERE \"work_id\" = @w", ("@w", workId));
    }
}
