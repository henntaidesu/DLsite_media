using System;
using System.Collections.Generic;
using System.Linq;

namespace DLsiteMedia.Core;

/// <summary>
/// 用户"不喜欢"的作品（RJ 号）。命中的作品在下载搜索中跳过 AS 论坛扫描、直接置灰，可随时取消不喜欢。
/// 独立于 works 表：不喜欢的作品通常并未在库，故单独用 dislikes 表按 work_id 记录。
/// </summary>
public static class Dislikes
{
    public static bool IsDisliked(string workId) =>
        Db.Scalar("SELECT 1 FROM \"dislikes\" WHERE \"work_id\" = @w", ("@w", workId)) != null;

    /// <summary>设置/取消某作品的不喜欢标记。</summary>
    public static void Set(string workId, bool disliked)
    {
        if (string.IsNullOrEmpty(workId))
            return;
        if (disliked)
            Db.Execute("INSERT OR IGNORE INTO \"dislikes\" (\"work_id\", \"time\") VALUES (@w, @t)",
                ("@w", workId), ("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        else
            Db.Execute("DELETE FROM \"dislikes\" WHERE \"work_id\" = @w", ("@w", workId));
    }

    /// <summary>批量查询：返回入参中处于"不喜欢"的作品号集合（供列表页一次性标记）。</summary>
    public static HashSet<string> Lookup(IEnumerable<string> ids)
    {
        var list = ids.Where(s => !string.IsNullOrEmpty(s))
                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (list.Count == 0)
            return result;
        var names = new List<string>();
        var args = new List<(string, object?)>();
        for (var i = 0; i < list.Count; i++)
        {
            names.Add($"@w{i}");
            args.Add(($"@w{i}", list[i]));
        }
        var rows = Db.Select(
            $"SELECT \"work_id\" FROM \"dislikes\" WHERE \"work_id\" IN ({string.Join(",", names)})",
            args.ToArray());
        foreach (var r in rows ?? [])
            if (r[0] is string s)
                result.Add(s);
        return result;
    }
}
