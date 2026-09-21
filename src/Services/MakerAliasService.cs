using System;
using System.Collections.Generic;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 社团显示名映射：给社团起一个只用于显示的别名，<c>works.maker_name</c> 这个真名一律不动。
///
/// 站点给的社团名常常又长又带装饰（「〇〇の部屋【R18】」之类），但它同时是分组键、筛选值、
/// 入库目录名的来源，改真名会牵动一大片。所以这里只做一层显示映射：
/// 卡片、标题显示别名，查询与落盘继续用真名。本表丢了也只是回到显示真名，不影响任何数据。
///
/// 映射整表驻留内存：社团卡是成批渲染的，逐张查库等于把渲染又拖慢一遍（同 ImageHostService 的封面映射）。
/// </summary>
public static class MakerAliasService
{
    private static readonly object Sync = new();
    private static Dictionary<string, string>? _map;

    /// <summary>真名 -> 别名。</summary>
    public static Dictionary<string, string> Map()
    {
        lock (Sync)
        {
            if (_map != null)
                return _map;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var rows = Db.Select("SELECT \"maker_name\", \"alias\" FROM \"maker_alias\"");
            foreach (var row in rows ?? [])
                if (row[0] is string name && row[1] is string alias && alias.Length > 0)
                    map[name] = alias;
            return _map = map;
        }
    }

    /// <summary>该社团的显示名：设过别名就用别名，否则原样返回真名。</summary>
    public static string Display(string? makerName)
    {
        var name = makerName ?? "";
        return name.Length > 0 && Map().TryGetValue(name, out var alias) ? alias : name;
    }

    /// <summary>
    /// 设置/清除别名。<paramref name="alias"/> 为空或与真名相同即删除映射
    /// ——否则库里会留一堆等于真名的无用行，用户"改回原名"也就等于取消映射。
    /// </summary>
    public static void Set(string makerName, string? alias)
    {
        if (makerName.Length == 0)
            return;
        var value = (alias ?? "").Trim();
        if (value.Length == 0 || value == makerName)
            Db.Execute("DELETE FROM \"maker_alias\" WHERE \"maker_name\" = @m", ("@m", makerName));
        else
            Db.Execute(
                "INSERT OR REPLACE INTO \"maker_alias\" (\"maker_name\", \"alias\", \"up_time\") " +
                "VALUES (@m, @a, @t)",
                ("@m", makerName), ("@a", value),
                ("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        Invalidate();
    }

    /// <summary>丢弃内存映射，下次读取重新载入（改完别名、或外部改库后调用）。</summary>
    public static void Invalidate()
    {
        lock (Sync)
            _map = null;
    }
}
