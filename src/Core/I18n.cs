using System;
using System.Collections.Generic;

namespace DLsiteMedia.Core;

/// <summary>
/// 界面多语言支持（对应 Python 版 i18n.py）。
///
/// 以简体中文原文作为翻译键（key），运行时按当前语言返回对应译文：
/// - 当前语言为 zh_CN 时，Tr() 直接返回原文；
/// - 其他语言在 Translations 中查表，缺失时回退到原文。
///
/// 含占位符的文案（如 '共 {n} 个作品'）由调用方用 Format() 替换，
/// 各语言译文需保留相同的占位符名称。
///
/// 注意：仅用于"显示"的中文才走 Tr()。数据库中存取的中文值
/// （works.state 的 '已品悦' / '已下载' / '下载中' 等）必须保持原样，不要翻译。
/// </summary>
public static class I18n
{
    /// <summary>语言代码 -> 该语言自我标注的名称（用于设置页下拉框）。</summary>
    public static readonly (string Code, string Name)[] Languages =
    [
        ("zh_CN", "简体中文"),
        ("zh_TW", "繁體中文"),
        ("ja", "日本語"),
        ("en", "English"),
    ];

    private static readonly HashSet<string> Valid = ["zh_CN", "zh_TW", "ja", "en"];

    private static string _current = "zh_CN";

    /// <summary>语言切换广播：各窗口订阅后实时重译界面。</summary>
    public static event Action? LanguageChanged;

    public static string CurrentLanguage => _current;

    /// <summary>仅更新内存中的当前语言（不写库、不发事件）。</summary>
    public static void SetLanguage(string lang) =>
        _current = Valid.Contains(lang) ? lang : "zh_CN";

    /// <summary>从配置读取已保存的语言；在创建任何窗口之前调用。</summary>
    public static void InitLanguage() => SetLanguage(AppConfig.Language);

    /// <summary>切换语言：写入配置、更新当前语言并广播给所有窗口实时重译。</summary>
    public static void ApplyLanguage(string lang)
    {
        SetLanguage(lang);
        AppConfig.Write("language", "lang", _current);
        LanguageChanged?.Invoke();
    }

    /// <summary>按当前语言返回译文；zh_CN 或查不到时返回原文。</summary>
    public static string Tr(string text)
    {
        if (_current == "zh_CN")
            return text;
        return Translations.TryGetValue(text, out var entry) &&
               entry.TryGetValue(_current, out var translated)
            ? translated
            : text;
    }

    /// <summary>替换 {name} 形式的占位符：I18n.Format(Tr("共 {n} 个作品"), ("n", 5))。</summary>
    public static string Format(string template, params (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
            template = template.Replace("{" + name + "}", value?.ToString() ?? "");
        return template;
    }

    // 简体中文原文 -> {语言代码: 译文}
    private static readonly Dictionary<string, Dictionary<string, string>> Translations = new()
    {
        // ---------- 导航 / 通用 ----------
        ["DLsite媒体库"] = new() { ["zh_TW"] = "DLsite媒體庫", ["ja"] = "DLsiteメディアライブラリ", ["en"] = "DLsite Media Library" },
        ["搜索"] = new() { ["zh_TW"] = "搜尋", ["ja"] = "検索", ["en"] = "Search" },
        ["下载"] = new() { ["zh_TW"] = "下載", ["ja"] = "ダウンロード", ["en"] = "Download" },
        ["搜索/下载"] = new() { ["zh_TW"] = "搜尋/下載", ["ja"] = "検索/ダウンロード", ["en"] = "Search / Download" },
        // 左导航文案对齐 Web 端 SECTIONS (src/Web/js/core.js)
        ["下载搜索"] = new() { ["zh_TW"] = "下載搜尋", ["ja"] = "ダウンロード/検索", ["en"] = "Download / Search" },
        ["作品社团"] = new() { ["zh_TW"] = "作品社團", ["ja"] = "サークル", ["en"] = "Makers" },
        ["我的收藏"] = new() { ["zh_TW"] = "我的收藏", ["ja"] = "お気に入り", ["en"] = "Favorites" },
        ["系统设置"] = new() { ["zh_TW"] = "系統設定", ["ja"] = "システム設定", ["en"] = "Settings" },
        ["已下载"] = new() { ["zh_TW"] = "已下載", ["ja"] = "ダウンロード済み", ["en"] = "Downloaded" },
        ["媒体库"] = new() { ["zh_TW"] = "媒體庫", ["ja"] = "ライブラリ", ["en"] = "Library" },
        ["标签"] = new() { ["zh_TW"] = "標籤", ["ja"] = "タグ", ["en"] = "Tags" },
        ["收藏夹"] = new() { ["zh_TW"] = "收藏夾", ["ja"] = "お気に入り", ["en"] = "Favorites" },
        ["已读"] = new() { ["zh_TW"] = "已讀", ["ja"] = "既読", ["en"] = "Read" },
        ["收藏"] = new() { ["zh_TW"] = "收藏", ["ja"] = "お気に入り", ["en"] = "Favorite" },
        ["设置"] = new() { ["zh_TW"] = "設定", ["ja"] = "設定", ["en"] = "Settings" },
        ["保存"] = new() { ["zh_TW"] = "儲存", ["ja"] = "保存", ["en"] = "Save" },
        ["取消"] = new() { ["zh_TW"] = "取消", ["ja"] = "キャンセル", ["en"] = "Cancel" },
        ["删除"] = new() { ["zh_TW"] = "刪除", ["ja"] = "削除", ["en"] = "Delete" },
        ["移除"] = new() { ["zh_TW"] = "移除", ["ja"] = "削除", ["en"] = "Remove" },
        ["刷新"] = new() { ["zh_TW"] = "重新整理", ["ja"] = "更新", ["en"] = "Refresh" },
        ["测试"] = new() { ["zh_TW"] = "測試", ["ja"] = "テスト", ["en"] = "Test" },
        ["提示"] = new() { ["zh_TW"] = "提示", ["ja"] = "お知らせ", ["en"] = "Notice" },
        ["← 返回"] = new() { ["zh_TW"] = "← 返回", ["ja"] = "← 戻る", ["en"] = "← Back" },

        // ---------- 搜索页 ----------
        ["输入作品番号，例如 RJ01234567"] = new()
        {
            ["zh_TW"] = "輸入作品番號，例如 RJ01234567",
            ["ja"] = "作品番号を入力（例：RJ01234567）",
            ["en"] = "Enter work ID, e.g. RJ01234567"
        },
        ["查询"] = new() { ["zh_TW"] = "查詢", ["ja"] = "検索", ["en"] = "Search" },
        ["← 返回结果"] = new() { ["zh_TW"] = "← 返回結果", ["ja"] = "← 結果に戻る", ["en"] = "← Back to results" },
        ["← 返回作品列表"] = new() { ["zh_TW"] = "← 返回作品列表", ["ja"] = "← 作品一覧に戻る", ["en"] = "← Back to works" },
        ["未找到匹配的作品"] = new() { ["zh_TW"] = "未找到相符的作品", ["ja"] = "一致する作品が見つかりません", ["en"] = "No matching works found" },
        ["← 下载列表"] = new() { ["zh_TW"] = "← 下載列表", ["ja"] = "← ダウンロード一覧", ["en"] = "← Downloads" },
        ["选择下载位置"] = new() { ["zh_TW"] = "選擇下載位置", ["ja"] = "ダウンロード先を選択", ["en"] = "Choose download location" },
        ["选择下载到哪个媒体库"] = new()
        {
            ["zh_TW"] = "選擇下載到哪個媒體庫",
            ["ja"] = "ダウンロードするライブラリを選択",
            ["en"] = "Choose which library to download to"
        },
        ["选择下载文件夹"] = new() { ["zh_TW"] = "選擇下載資料夾", ["ja"] = "ダウンロードフォルダを選択", ["en"] = "Choose download folder" },
        ["正在查询…"] = new() { ["zh_TW"] = "正在查詢…", ["ja"] = "検索中…", ["en"] = "Searching…" },
        ["{count} 个文件"] = new() { ["zh_TW"] = "{count} 個檔案", ["ja"] = "{count} 個のファイル", ["en"] = "{count} files" },
        ["检测中…"] = new() { ["zh_TW"] = "檢測中…", ["ja"] = "確認中…", ["en"] = "Checking…" },
        ["检测中… {checked}/{total}"] = new()
        {
            ["zh_TW"] = "檢測中… {checked}/{total}",
            ["ja"] = "確認中… {checked}/{total}",
            ["en"] = "Checking… {checked}/{total}"
        },
        ["有效"] = new() { ["zh_TW"] = "有效", ["ja"] = "有効", ["en"] = "Valid" },
        ["已暂停校验"] = new() { ["zh_TW"] = "已暫停校驗", ["ja"] = "検証を一時停止", ["en"] = "Check paused" },
        ["失效"] = new() { ["zh_TW"] = "失效", ["ja"] = "無効", ["en"] = "Invalid" },
        ["待检测"] = new() { ["zh_TW"] = "待檢測", ["ja"] = "確認待ち", ["en"] = "Pending" },
        ["已暂停"] = new() { ["zh_TW"] = "已暫停", ["ja"] = "一時停止", ["en"] = "Paused" },
        ["部分有效 {valid}/{total}"] = new()
        {
            ["zh_TW"] = "部分有效 {valid}/{total}",
            ["ja"] = "一部有効 {valid}/{total}",
            ["en"] = "Partly valid {valid}/{total}"
        },
        ["已加入下载"] = new() { ["zh_TW"] = "已加入下載", ["ja"] = "ダウンロードに追加", ["en"] = "Added to downloads" },
        ["番号错误"] = new() { ["zh_TW"] = "番號錯誤", ["ja"] = "番号エラー", ["en"] = "Invalid number" },
        ["{id} 不是有效的番号（格式：RJ/BJ/VJ + 数字）"] = new()
        {
            ["zh_TW"] = "{id} 不是有效的番號（格式：RJ/BJ/VJ + 數字）",
            ["ja"] = "{id} は有効な番号ではありません（形式：RJ/BJ/VJ + 数字）",
            ["en"] = "{id} is not a valid number (format: RJ/BJ/VJ + digits)"
        },
        ["{id} {name}\n该作品已于 {time} 加入过下载，是否继续搜索？"] = new()
        {
            ["zh_TW"] = "{id} {name}\n該作品已於 {time} 加入過下載，是否繼續搜尋？",
            ["ja"] = "{id} {name}\nこの作品は {time} にダウンロードへ追加済みです。検索を続けますか？",
            ["en"] = "{id} {name}\nThis work was added to downloads on {time}. Continue searching?"
        },
        ["无匹配数据"] = new() { ["zh_TW"] = "無相符資料", ["ja"] = "一致するデータがありません", ["en"] = "No matching data" },
        ["该帖子中没有找到下载链接"] = new()
        {
            ["zh_TW"] = "該帖子中沒有找到下載連結",
            ["ja"] = "このスレッドにダウンロードリンクが見つかりません",
            ["en"] = "No download links found in this thread"
        },

        // ---------- 下载页 ----------
        ["debrid-link 使用量"] = new() { ["zh_TW"] = "debrid-link 使用量", ["ja"] = "debrid-link 使用量", ["en"] = "debrid-link usage" },
        ["debrid-link 使用量 --"] = new() { ["zh_TW"] = "debrid-link 使用量 --", ["ja"] = "debrid-link 使用量 --", ["en"] = "debrid-link usage --" },
        [" · {reset} 后重置"] = new() { ["zh_TW"] = " · {reset} 後重置", ["ja"] = " · {reset} 後にリセット", ["en"] = " · resets in {reset}" },
        ["搜索作品"] = new() { ["zh_TW"] = "搜尋作品", ["ja"] = "作品を検索", ["en"] = "Search works" },
        ["开始下载"] = new() { ["zh_TW"] = "開始下載", ["ja"] = "ダウンロード開始", ["en"] = "Start" },
        ["暂停下载"] = new() { ["zh_TW"] = "暫停下載", ["ja"] = "ダウンロード停止", ["en"] = "Pause" },
        ["暂停中…"] = new() { ["zh_TW"] = "暫停中…", ["ja"] = "停止中…", ["en"] = "Pausing…" },
        ["清除已完成"] = new() { ["zh_TW"] = "清除已完成", ["ja"] = "完了分を消去", ["en"] = "Clear completed" },
        ["清空列表"] = new() { ["zh_TW"] = "清空列表", ["ja"] = "リストをクリア", ["en"] = "Clear all" },
        ["等待下载"] = new() { ["zh_TW"] = "等待下載", ["ja"] = "待機中", ["en"] = "Waiting" },
        ["下载中"] = new() { ["zh_TW"] = "下載中", ["ja"] = "ダウンロード中", ["en"] = "Downloading" },
        ["已完成"] = new() { ["zh_TW"] = "已完成", ["ja"] = "完了", ["en"] = "Completed" },
        ["待解压"] = new() { ["zh_TW"] = "待解壓", ["ja"] = "解凍待ち", ["en"] = "Pending extract" },
        ["等待移动"] = new() { ["zh_TW"] = "等待移動", ["ja"] = "移動待ち", ["en"] = "Pending move" },
        ["解压中 {pct}%"] = new() { ["zh_TW"] = "解壓中 {pct}%", ["ja"] = "解凍中 {pct}%", ["en"] = "Extracting {pct}%" },
        ["移动中 {pct}%"] = new() { ["zh_TW"] = "移動中 {pct}%", ["ja"] = "移動中 {pct}%", ["en"] = "Moving {pct}%" },
        ["解析失败"] = new() { ["zh_TW"] = "解析失敗", ["ja"] = "解析失敗", ["en"] = "Parse failed" },
        ["下载中 {done}/{total}"] = new()
        {
            ["zh_TW"] = "下載中 {done}/{total}", ["ja"] = "ダウンロード中 {done}/{total}", ["en"] = "Downloading {done}/{total}"
        },
        ["等待下载 {done}/{total}"] = new()
        {
            ["zh_TW"] = "等待下載 {done}/{total}", ["ja"] = "待機中 {done}/{total}", ["en"] = "Waiting {done}/{total}"
        },
        ["{n} 个解析失败"] = new() { ["zh_TW"] = "{n} 個解析失敗", ["ja"] = "{n} 件解析失敗", ["en"] = "{n} parse failed" },
        ["流量用尽"] = new() { ["zh_TW"] = "流量用盡", ["ja"] = "通信量超過", ["en"] = "Data limit reached" },
        ["文件失效"] = new() { ["zh_TW"] = "檔案失效", ["ja"] = "ファイル失効", ["en"] = "File dead" },
        ["网盘不支持"] = new() { ["zh_TW"] = "網盤不支援", ["ja"] = "非対応ホスト", ["en"] = "Host unsupported" },
        ["需会员"] = new() { ["zh_TW"] = "需會員", ["ja"] = "会員限定", ["en"] = "Premium only" },
        ["Key 无效"] = new() { ["zh_TW"] = "Key 無效", ["ja"] = "Key 無効", ["en"] = "Bad API key" },
        ["请求频繁"] = new() { ["zh_TW"] = "請求頻繁", ["ja"] = "リクエスト過多", ["en"] = "Rate limited" },
        ["类型不支持"] = new() { ["zh_TW"] = "類型不支援", ["ja"] = "非対応形式", ["en"] = "Bad file type" },
        ["超链接数"] = new() { ["zh_TW"] = "超連結數", ["ja"] = "リンク数超過", ["en"] = "Too many links" },
        ["点击查看各网盘流量详情"] = new() { ["zh_TW"] = "點擊查看各網盤流量詳情", ["ja"] = "各ホストの通信量詳細を表示", ["en"] = "Click for per-host data usage" },
        ["debrid-link 流量详情"] = new() { ["zh_TW"] = "debrid-link 流量詳情", ["ja"] = "debrid-link 通信量詳細", ["en"] = "debrid-link data usage" },
        ["总用量 {pct}%"] = new() { ["zh_TW"] = "總用量 {pct}%", ["ja"] = "合計 {pct}%", ["en"] = "Total {pct}%" },
        ["（{reset} 后重置）"] = new() { ["zh_TW"] = "（{reset} 後重置）", ["ja"] = "（{reset} 後にリセット）", ["en"] = " (resets in {reset})" },
        ["各网盘用量："] = new() { ["zh_TW"] = "各網盤用量：", ["ja"] = "ホスト別使用量：", ["en"] = "Per-host usage:" },
        ["流量已用尽的网盘："] = new() { ["zh_TW"] = "流量已用盡的網盤：", ["ja"] = "通信量を使い切ったホスト：", ["en"] = "Hosts out of data:" },
        ["账户总流量（所有网盘）"] = new() { ["zh_TW"] = "帳戶總流量（所有網盤）", ["ja"] = "アカウント全体（全ホスト）", ["en"] = "Account total (all hosts)" },
        ["暂无网盘流量用尽"] = new() { ["zh_TW"] = "暫無網盤流量用盡", ["ja"] = "通信量を使い切ったホストはありません", ["en"] = "No hosts are out of data" },
        ["重新解析"] = new() { ["zh_TW"] = "重新解析", ["ja"] = "再解析", ["en"] = "Re-parse" },
        ["重新搜索"] = new() { ["zh_TW"] = "重新搜尋", ["ja"] = "再検索", ["en"] = "Re-search" },
        ["将删除 {id} 已下载的分卷与文件夹，并重新搜索。是否继续？"] = new()
        {
            ["zh_TW"] = "將刪除 {id} 已下載的分卷與資料夾，並重新搜尋。是否繼續？",
            ["ja"] = "{id} のダウンロード済み分割ファイルとフォルダを削除して再検索します。続行しますか？",
            ["en"] = "This will delete the downloaded parts and folder of {id}, then search again. Continue?"
        },
        ["未知({status})"] = new() { ["zh_TW"] = "未知({status})", ["ja"] = "不明({status})", ["en"] = "Unknown({status})" },
        ["番号 / 文件"] = new() { ["zh_TW"] = "番號 / 檔案", ["ja"] = "番号 / ファイル", ["en"] = "ID / File" },
        ["下载进度"] = new() { ["zh_TW"] = "下載進度", ["ja"] = "進捗", ["en"] = "Progress" },
        ["速度"] = new() { ["zh_TW"] = "速度", ["ja"] = "速度", ["en"] = "Speed" },
        ["状态"] = new() { ["zh_TW"] = "狀態", ["ja"] = "状態", ["en"] = "Status" },
        ["清空下载列表"] = new() { ["zh_TW"] = "清空下載列表", ["ja"] = "ダウンロードリストをクリア", ["en"] = "Clear download list" },
        ["确定要清空整个下载列表吗？等待中的任务也会被删除。"] = new()
        {
            ["zh_TW"] = "確定要清空整個下載列表嗎？等待中的任務也會被刪除。",
            ["ja"] = "ダウンロードリスト全体をクリアしますか？待機中のタスクも削除されます。",
            ["en"] = "Clear the entire download list? Pending tasks will also be removed."
        },

        // ---------- 已下载页 ----------
        ["RJ号"] = new() { ["zh_TW"] = "RJ號", ["ja"] = "RJ番号", ["en"] = "RJ number" },
        ["作品名称"] = new() { ["zh_TW"] = "作品名稱", ["ja"] = "作品名", ["en"] = "Title" },
        ["类型"] = new() { ["zh_TW"] = "類型", ["ja"] = "ジャンル", ["en"] = "Genre" },
        ["下载时间"] = new() { ["zh_TW"] = "下載時間", ["ja"] = "ダウンロード日時", ["en"] = "Download time" },
        ["标记为已品悦"] = new() { ["zh_TW"] = "標記為已品悦", ["ja"] = "鑑賞済みにする", ["en"] = "Mark as enjoyed" },
        ["全部"] = new() { ["zh_TW"] = "全部", ["ja"] = "すべて", ["en"] = "All" },
        ["已品悦"] = new() { ["zh_TW"] = "已品悦", ["ja"] = "鑑賞済み", ["en"] = "Enjoyed" },
        ["共 {n} 个作品"] = new() { ["zh_TW"] = "共 {n} 個作品", ["ja"] = "作品 {n} 件", ["en"] = "{n} works total" },

        // ---------- 组合下载（对齐 Web 端 combineDownload / /api/combine）----------
        ["组合下载"] = new() { ["zh_TW"] = "組合下載", ["ja"] = "分卷を組み合わせDL", ["en"] = "Combine download" },
        ["组合中…"] = new() { ["zh_TW"] = "組合中…", ["ja"] = "組み合わせ中…", ["en"] = "Combining…" },
        ["该帖未找到可用网盘链接"] = new()
        {
            ["zh_TW"] = "該帖未找到可用網盤連結",
            ["ja"] = "この投稿に利用可能なアップローダーのリンクがありません",
            ["en"] = "No usable host links found in this post"
        },
        ["所有网盘分卷均失效，无可用下载"] = new()
        {
            ["zh_TW"] = "所有網盤分卷均失效，無可用下載",
            ["ja"] = "すべての分卷が無効で、ダウンロードできません",
            ["en"] = "All volumes are dead on every host — nothing to download"
        },
        ["仅凑齐 {have}/{total} 个分卷（部分分卷在所有网盘均失效），无法组合完整下载"] = new()
        {
            ["zh_TW"] = "僅湊齊 {have}/{total} 個分卷（部分分卷在所有網盤均失效），無法組合完整下載",
            ["ja"] = "分卷 {have}/{total} のみ確保（一部が全ホストで無効）— 完全な組み合わせができません",
            ["en"] = "Only {have}/{total} volumes available (some dead on every host) — cannot assemble a complete download"
        },

        // ---------- 媒体库页 ----------
        ["搜索 RJ号 / 作品名 / 社团"] = new()
        {
            ["zh_TW"] = "搜尋 RJ號 / 作品名 / 社團",
            ["ja"] = "RJ番号 / 作品名 / サークルで検索",
            ["en"] = "Search RJ / title / maker"
        },
        ["作品标签"] = new() { ["zh_TW"] = "作品標籤", ["ja"] = "作品タグ", ["en"] = "Tags" },
        ["媒体库设置"] = new() { ["zh_TW"] = "媒體庫設定", ["ja"] = "ライブラリ設定", ["en"] = "Library settings" },
        ["打开文件夹"] = new() { ["zh_TW"] = "開啟資料夾", ["ja"] = "フォルダを開く", ["en"] = "Open folder" },
        ["移动媒体库"] = new() { ["zh_TW"] = "移動媒體庫", ["ja"] = "ライブラリを移動", ["en"] = "Move library" },
        ["查看作品"] = new() { ["zh_TW"] = "查看作品", ["ja"] = "作品を見る", ["en"] = "Browse files" },
        ["作品信息"] = new() { ["zh_TW"] = "作品資訊", ["ja"] = "作品情報", ["en"] = "Work info" },
        ["查看图片"] = new() { ["zh_TW"] = "查看圖片", ["ja"] = "画像を表示", ["en"] = "View image" },
        ["{count} 项"] = new() { ["zh_TW"] = "{count} 項", ["ja"] = "{count} 件", ["en"] = "{count} items" },
        ["上一张"] = new() { ["zh_TW"] = "上一張", ["ja"] = "前へ", ["en"] = "Previous" },
        ["下一张"] = new() { ["zh_TW"] = "下一張", ["ja"] = "次へ", ["en"] = "Next" },
        ["无法显示该图片"] = new() { ["zh_TW"] = "無法顯示該圖片", ["ja"] = "この画像を表示できません", ["en"] = "Cannot display this image" },
        ["播放"] = new() { ["zh_TW"] = "播放", ["ja"] = "再生", ["en"] = "Play" },
        ["暂停"] = new() { ["zh_TW"] = "暫停", ["ja"] = "一時停止", ["en"] = "Pause" },
        ["移动中…"] = new() { ["zh_TW"] = "移動中…", ["ja"] = "移動中…", ["en"] = "Moving…" },
        ["选择要移动到的媒体库"] = new()
        {
            ["zh_TW"] = "選擇要移動到的媒體庫",
            ["ja"] = "移動先のライブラリを選択",
            ["en"] = "Choose the library to move to"
        },
        ["选择目标文件夹"] = new() { ["zh_TW"] = "選擇目標資料夾", ["ja"] = "移動先フォルダを選択", ["en"] = "Choose target folder" },
        ["没有可移动到的其它媒体库"] = new()
        {
            ["zh_TW"] = "沒有可移動到的其它媒體庫",
            ["ja"] = "移動できる他のライブラリがありません",
            ["en"] = "No other library to move to"
        },
        ["确定将 {id} 移动到媒体库“{lib}”吗？"] = new()
        {
            ["zh_TW"] = "確定將 {id} 移動到媒體庫「{lib}」嗎？",
            ["ja"] = "{id} をライブラリ「{lib}」へ移動しますか？",
            ["en"] = "Move {id} to library \"{lib}\"?"
        },
        ["已移动到新媒体库"] = new() { ["zh_TW"] = "已移動到新媒體庫", ["ja"] = "新しいライブラリへ移動しました", ["en"] = "Moved to the new library" },
        ["作品文件夹不存在，无法移动"] = new()
        {
            ["zh_TW"] = "作品資料夾不存在，無法移動",
            ["ja"] = "作品フォルダが存在しないため移動できません",
            ["en"] = "Work folder does not exist; cannot move"
        },
        ["全年龄"] = new() { ["zh_TW"] = "全年齡", ["ja"] = "全年齢", ["en"] = "All ages" },
        ["社团"] = new() { ["zh_TW"] = "社團", ["ja"] = "サークル", ["en"] = "Maker" },
        ["系列"] = new() { ["zh_TW"] = "系列", ["ja"] = "シリーズ", ["en"] = "Series" },
        ["剧本"] = new() { ["zh_TW"] = "劇本", ["ja"] = "シナリオ", ["en"] = "Scenario" },
        ["插画"] = new() { ["zh_TW"] = "插畫", ["ja"] = "イラスト", ["en"] = "Illustration" },
        ["声优"] = new() { ["zh_TW"] = "聲優", ["ja"] = "声優", ["en"] = "Voice actor" },
        ["販売日"] = new() { ["zh_TW"] = "販售日", ["ja"] = "販売日", ["en"] = "Release date" },
        ["年龄分级"] = new() { ["zh_TW"] = "年齡分級", ["ja"] = "年齢区分", ["en"] = "Age rating" },
        ["作品形式"] = new() { ["zh_TW"] = "作品形式", ["ja"] = "作品形式", ["en"] = "Work type" },
        ["文件容量"] = new() { ["zh_TW"] = "檔案容量", ["ja"] = "ファイル容量", ["en"] = "File size" },
        ["简介"] = new() { ["zh_TW"] = "簡介", ["ja"] = "紹介", ["en"] = "Description" },
        ["未知社团"] = new() { ["zh_TW"] = "未知社團", ["ja"] = "不明なサークル", ["en"] = "Unknown maker" },
        ["{count} 个作品"] = new() { ["zh_TW"] = "{count} 個作品", ["ja"] = "作品 {count} 件", ["en"] = "{count} works" },
        ["{works} 个作品 · {folders} 个文件夹"] = new()
        {
            ["zh_TW"] = "{works} 個作品 · {folders} 個資料夾",
            ["ja"] = "作品 {works} 件 · フォルダ {folders} 個",
            ["en"] = "{works} works · {folders} folders"
        },
        ["个媒体库"] = new() { ["zh_TW"] = "個媒體庫", ["ja"] = "ライブラリ", ["en"] = "libraries" },
        ["作品数 多→少"] = new() { ["zh_TW"] = "作品數 多→少", ["ja"] = "作品数 多→少", ["en"] = "Works ↓" },
        ["作品数 少→多"] = new() { ["zh_TW"] = "作品數 少→多", ["ja"] = "作品数 少→多", ["en"] = "Works ↑" },
        ["社团名 A→Z"] = new() { ["zh_TW"] = "社團名 A→Z", ["ja"] = "サークル名 A→Z", ["en"] = "Maker A→Z" },
        ["发售日 新→旧"] = new() { ["zh_TW"] = "發售日 新→舊", ["ja"] = "販売日 新→旧", ["en"] = "Date ↓" },
        ["发售日 旧→新"] = new() { ["zh_TW"] = "發售日 舊→新", ["ja"] = "販売日 旧→新", ["en"] = "Date ↑" },
        ["RJ号 新→旧"] = new() { ["zh_TW"] = "RJ號 新→舊", ["ja"] = "RJ番号 新→旧", ["en"] = "RJ ↓" },
        ["RJ号 旧→新"] = new() { ["zh_TW"] = "RJ號 舊→新", ["ja"] = "RJ番号 旧→新", ["en"] = "RJ ↑" },
        ["个社团"] = new() { ["zh_TW"] = "個社團", ["ja"] = "サークル", ["en"] = "makers" },
        ["个标签"] = new() { ["zh_TW"] = "個標籤", ["ja"] = "タグ", ["en"] = "tags" },
        ["个形式"] = new() { ["zh_TW"] = "個形式", ["ja"] = "形式", ["en"] = "types" },
        ["共 {total} {unit}，匹配 {shown} 个"] = new()
        {
            ["zh_TW"] = "共 {total} {unit}，相符 {shown} 個",
            ["ja"] = "{unit} {total} 件、一致 {shown} 件",
            ["en"] = "{total} {unit}, {shown} matched"
        },
        ["共 {total} {unit}，{works} 个作品"] = new()
        {
            ["zh_TW"] = "共 {total} {unit}，{works} 個作品",
            ["ja"] = "{unit} {total} 件、作品 {works} 件",
            ["en"] = "{total} {unit}, {works} works"
        },
        ["共 {total} 个作品，匹配 {matched} 个"] = new()
        {
            ["zh_TW"] = "共 {total} 個作品，相符 {matched} 個",
            ["ja"] = "作品 {total} 件、一致 {matched} 件",
            ["en"] = "{total} works, {matched} matched"
        },
        ["共 {total} 个作品"] = new() { ["zh_TW"] = "共 {total} 個作品", ["ja"] = "作品 {total} 件", ["en"] = "{total} works" },
        ["（已加载 {loaded}）"] = new() { ["zh_TW"] = "（已載入 {loaded}）", ["ja"] = "（読み込み済み {loaded}）", ["en"] = " (loaded {loaded})" },

        // ---------- 媒体库管理（内联于系统设置页）----------
        ["新建媒体库"] = new() { ["zh_TW"] = "新增媒體庫", ["ja"] = "ライブラリを新規作成", ["en"] = "New library" },
        ["文件夹路径为本机本地路径（如 D:\\ASMR）；添加后点\"扫描元数据\"导入作品与元数据。删除媒体库不会删除本地文件、已导入记录保留。"] = new()
        {
            ["zh_TW"] = "資料夾路徑為本機本地路徑（如 D:\\ASMR）；新增後點「掃描中繼資料」匯入作品與中繼資料。刪除媒體庫不會刪除本地檔案、已匯入記錄保留。",
            ["ja"] = "フォルダパスは本機のローカルパス（例 D:\\ASMR）です。追加後「メタデータをスキャン」で作品とメタデータを取り込みます。ライブラリを削除してもローカルファイルは削除されず、取り込み済みの記録は保持されます。",
            ["en"] = "Folder paths are local paths on this machine (e.g. D:\\ASMR). After adding, click \"Scan metadata\" to import works and metadata. Deleting a library does not delete local files; imported records are kept."
        },
        ["还没有媒体库，点击\"新建媒体库\"创建。"] = new()
        {
            ["zh_TW"] = "還沒有媒體庫，點擊「新增媒體庫」建立。",
            ["ja"] = "ライブラリがまだありません。「ライブラリを新規作成」をクリックしてください。",
            ["en"] = "No libraries yet. Click \"New library\" to create one."
        },
        ["{n} 个文件夹"] = new() { ["zh_TW"] = "{n} 個資料夾", ["ja"] = "{n} 個のフォルダ", ["en"] = "{n} folders" },
        ["添加文件夹"] = new() { ["zh_TW"] = "新增資料夾", ["ja"] = "フォルダを追加", ["en"] = "Add folder" },
        ["扫描元数据"] = new() { ["zh_TW"] = "掃描中繼資料", ["ja"] = "メタデータをスキャン", ["en"] = "Scan metadata" },
        ["重新扫描元数据"] = new() { ["zh_TW"] = "重新掃描中繼資料", ["ja"] = "メタデータを再スキャン", ["en"] = "Rescan metadata" },
        ["媒体库名称："] = new() { ["zh_TW"] = "媒體庫名稱：", ["ja"] = "ライブラリ名：", ["en"] = "Library name:" },
        ["已存在同名媒体库。"] = new() { ["zh_TW"] = "已存在同名媒體庫。", ["ja"] = "同じ名前のライブラリが既に存在します。", ["en"] = "A library with this name already exists." },
        ["选择媒体库文件夹"] = new() { ["zh_TW"] = "選擇媒體庫資料夾", ["ja"] = "ライブラリフォルダを選択", ["en"] = "Choose library folder" },
        ["该文件夹已在媒体库\"{name}\"中。"] = new()
        {
            ["zh_TW"] = "該資料夾已在媒體庫「{name}」中。",
            ["ja"] = "このフォルダは既にライブラリ「{name}」に含まれています。",
            ["en"] = "This folder is already in library \"{name}\"."
        },
        ["删除媒体库"] = new() { ["zh_TW"] = "刪除媒體庫", ["ja"] = "ライブラリを削除", ["en"] = "Delete library" },
        ["确定删除媒体库\"{name}\"吗？\n不会删除本地文件，已导入的作品记录保留。"] = new()
        {
            ["zh_TW"] = "確定刪除媒體庫「{name}」嗎？\n不會刪除本機檔案，已匯入的作品記錄會保留。",
            ["ja"] = "ライブラリ「{name}」を削除しますか？\nローカルファイルは削除されず、取り込み済みの作品記録は保持されます。",
            ["en"] = "Delete library \"{name}\"?\nLocal files will not be deleted; imported work records are kept."
        },
        ["正在扫描媒体库\"{name}\"…"] = new()
        {
            ["zh_TW"] = "正在掃描媒體庫「{name}」…",
            ["ja"] = "ライブラリ「{name}」をスキャン中…",
            ["en"] = "Scanning library \"{name}\"…"
        },
        ["已导入 {total} 个作品（新增 {added}），正在从 DL API 补全数据…"] = new()
        {
            ["zh_TW"] = "已匯入 {total} 個作品（新增 {added}），正在從 DL API 補全資料…",
            ["ja"] = "{total} 件の作品を取り込みました（新規 {added}）。DL API からデータを補完中…",
            ["en"] = "Imported {total} works ({added} new), filling data from DL API…"
        },
        ["正在抓取 DLsite 作品页元数据与图片…"] = new()
        {
            ["zh_TW"] = "正在抓取 DLsite 作品頁中繼資料與圖片…",
            ["ja"] = "DLsite 作品ページのメタデータと画像を取得中…",
            ["en"] = "Fetching DLsite work page metadata and images…"
        },
        ["扫描完成：导入 {total} 个，API 补全 {filled} 个，作品页补全 {page_filled} 个（失败 {page_missed}）"] = new()
        {
            ["zh_TW"] = "掃描完成：匯入 {total} 個，API 補全 {filled} 個，作品頁補全 {page_filled} 個（失敗 {page_missed}）",
            ["ja"] = "スキャン完了：取り込み {total} 件、API 補完 {filled} 件、作品ページ補完 {page_filled} 件（失敗 {page_missed}）",
            ["en"] = "Scan complete: imported {total}, API filled {filled}, page filled {page_filled} ({page_missed} failed)"
        },
        ["DL API 数据补全中… {i}/{total}"] = new()
        {
            ["zh_TW"] = "DL API 資料補全中… {i}/{total}",
            ["ja"] = "DL API データ補完中… {i}/{total}",
            ["en"] = "Filling data from DL API… {i}/{total}"
        },
        ["作品页抓取中… {i}/{total}（{rj}）"] = new()
        {
            ["zh_TW"] = "作品頁抓取中… {i}/{total}（{rj}）",
            ["ja"] = "作品ページ取得中… {i}/{total}（{rj}）",
            ["en"] = "Fetching work pages… {i}/{total} ({rj})"
        },

        // ---------- 设置页 ----------
        ["下载路径"] = new() { ["zh_TW"] = "下載路徑", ["ja"] = "ダウンロード先", ["en"] = "Download path" },
        ["缓存路径"] = new() { ["zh_TW"] = "快取路徑", ["ja"] = "キャッシュ先", ["en"] = "Cache path" },
        ["代理"] = new() { ["zh_TW"] = "代理", ["ja"] = "プロキシ", ["en"] = "Proxy" },
        ["Debrid-Link 下载中转站"] = new() { ["zh_TW"] = "Debrid-Link 下載中轉站", ["ja"] = "Debrid-Link 中継", ["en"] = "Debrid-Link relay" },
        ["下载选项"] = new() { ["zh_TW"] = "下載選項", ["ja"] = "ダウンロード設定", ["en"] = "Download options" },
        ["自动下载"] = new() { ["zh_TW"] = "自動下載", ["ja"] = "自動ダウンロード", ["en"] = "Auto download" },
        ["自动解压"] = new() { ["zh_TW"] = "自動解壓", ["ja"] = "自動解凍", ["en"] = "Auto extract" },
        ["文件夹命名"] = new() { ["zh_TW"] = "資料夾命名", ["ja"] = "フォルダ命名", ["en"] = "Folder naming" },
        ["单文件线程数"] = new() { ["zh_TW"] = "單檔執行緒數", ["ja"] = "ファイルごとのスレッド数", ["en"] = "Threads per file" },
        ["单个文件分成多少段并发下载（多线程下载），1 为不分段"] = new()
        {
            ["zh_TW"] = "單個檔案分成多少段並發下載（多執行緒下載），1 為不分段",
            ["ja"] = "1 つのファイルを何分割して並列ダウンロードするか（マルチスレッド）。1 で分割なし",
            ["en"] = "How many segments to split each file into for concurrent download; 1 means no split"
        },
        ["单个文件分成多少段并发下载（多线程下载），1 为不分段，最大 16"] = new()
        {
            ["zh_TW"] = "單個檔案分成多少段並發下載（多執行緒下載），1 為不分段，最大 16",
            ["ja"] = "1 つのファイルを何分割して並列ダウンロードするか（マルチスレッド）。1 で分割なし、最大 16",
            ["en"] = "How many segments to split each file into for concurrent download; 1 means no split, max 16"
        },
        ["查询线程数"] = new() { ["zh_TW"] = "查詢執行緒數", ["ja"] = "検索スレッド数", ["en"] = "Query threads" },
        ["最低速度 (KB/s)"] = new() { ["zh_TW"] = "最低速度 (KB/s)", ["ja"] = "最低速度 (KB/s)", ["en"] = "Min speed (KB/s)" },
        ["持续低于该速度 30 秒后自动重试，0 表示不限制"] = new()
        {
            ["zh_TW"] = "持續低於該速度 30 秒後自動重試，0 表示不限制",
            ["ja"] = "この速度を 30 秒下回ると自動で再試行。0 は無制限",
            ["en"] = "Auto-retry after staying below this speed for 30s; 0 means no limit"
        },
        ["速度限制 (KB/s)"] = new() { ["zh_TW"] = "速度限制 (KB/s)", ["ja"] = "速度制限 (KB/s)", ["en"] = "Speed limit (KB/s)" },
        ["下载总速度上限，0 表示不限速"] = new()
        {
            ["zh_TW"] = "下載總速度上限，0 表示不限速",
            ["ja"] = "ダウンロード総速度の上限。0 は無制限",
            ["en"] = "Total download speed cap; 0 means unlimited"
        },
        ["系统"] = new() { ["zh_TW"] = "系統", ["ja"] = "システム", ["en"] = "System" },
        ["日志级别"] = new() { ["zh_TW"] = "日誌等級", ["ja"] = "ログレベル", ["en"] = "Log level" },
        ["解压编码"] = new() { ["zh_TW"] = "解壓編碼", ["ja"] = "解凍エンコード", ["en"] = "Extract encoding" },
        ["API 地址"] = new() { ["zh_TW"] = "API 位址", ["ja"] = "API アドレス", ["en"] = "API address" },
        ["语言"] = new() { ["zh_TW"] = "語言", ["ja"] = "言語", ["en"] = "Language" },
        ["开启"] = new() { ["zh_TW"] = "開啟", ["ja"] = "オン", ["en"] = "On" },
        ["关闭"] = new() { ["zh_TW"] = "關閉", ["ja"] = "オフ", ["en"] = "Off" },
        ["测试成功"] = new() { ["zh_TW"] = "測試成功", ["ja"] = "テスト成功", ["en"] = "Test succeeded" },
        ["测试失败"] = new() { ["zh_TW"] = "測試失敗", ["ja"] = "テスト失敗", ["en"] = "Test failed" },
        ["已连接 debrid-link\n账户: {account}"] = new()
        {
            ["zh_TW"] = "已連接 debrid-link\n帳戶: {account}",
            ["ja"] = "debrid-link に接続しました\nアカウント: {account}",
            ["en"] = "Connected to debrid-link\nAccount: {account}"
        },
        ["API Key 无效或网络不可用"] = new()
        {
            ["zh_TW"] = "API Key 無效或網路不可用",
            ["ja"] = "API Key が無効か、ネットワークが利用できません",
            ["en"] = "Invalid API Key or network unavailable"
        },
        ["选择下载路径"] = new() { ["zh_TW"] = "選擇下載路徑", ["ja"] = "ダウンロード先を選択", ["en"] = "Choose download path" },
        ["选择缓存路径"] = new() { ["zh_TW"] = "選擇快取路徑", ["ja"] = "キャッシュ先を選択", ["en"] = "Choose cache path" },
    };
}
