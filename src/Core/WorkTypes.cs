namespace DLsiteMedia.Core;

/// <summary>
/// DLsite work_type 代码 ↔ 中文名映射（集中维护，供「作品类型优先搜索」设置与类型显示复用）。
/// 列表外的代码统一显示为「其他」。目前仅 SOU(音声/ASMR) 可走 asmr.one 直链，其余类型仅 anime-sharing。
/// </summary>
public static class WorkTypes
{
    /// <summary>已知作品类型（有序）：SOU 置首（唯一可切换来源者），其余按常用度排列。</summary>
    public static readonly (string Code, string Name)[] Known =
    [
        ("SOU", "ASMR"),        // 音声 / ASMR
        ("MOV", "动画"),
        ("RPG", "角色扮演"),
        ("MNG", "漫画"),
        ("ICG", "CG"),
        ("MUS", "音乐"),
        ("NRE", "小说"),
        ("TBL", "桌游"),
        ("ACN", "动作"),
        ("QIZ", "解密"),
    ];

    /// <summary>代码对应的中文名；未收录的代码（含空）返回「其他」。</summary>
    public static string DisplayName(string? code)
    {
        if (!string.IsNullOrEmpty(code))
            foreach (var (c, name) in Known)
                if (string.Equals(c, code, System.StringComparison.OrdinalIgnoreCase))
                    return name;
        return "其他";
    }

    /// <summary>该类型当前是否支持 asmr.one 直链（目前仅 SOU）。</summary>
    public static bool SupportsAsmr(string code) =>
        string.Equals(code, "SOU", System.StringComparison.OrdinalIgnoreCase);
}
