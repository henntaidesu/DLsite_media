using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DLsiteMedia.Core;

namespace DLsiteMedia.Services;

/// <summary>一次 fanbox 批量入队的结果。</summary>
public class FanboxEnqueueResult
{
    /// <summary>实际入队的作品数。</summary>
    public int PostCount { get; init; }
    /// <summary>实际入队的文件数。</summary>
    public int FileCount { get; init; }
    /// <summary>已在库/已在下载队列而跳过的作品数。</summary>
    public int Skipped { get; init; }
    public string? Error { get; init; }
    public bool Ok => Error is null && PostCount > 0;
}

/// <summary>媒体库里的一位 fanbox 作家（按已下载作品聚合）。</summary>
public class FanboxArtistRow
{
    public string Id { get; init; } = "";
    public string Service { get; init; } = PawchiveApi.FanboxService;
    public string Name { get; init; } = "";
    public string PublicId { get; init; } = "";
    /// <summary>本地已入库（已品悦）的作品数。</summary>
    public int Downloaded { get; init; }
    /// <summary>本地记录的作品总数（含下载中）。</summary>
    public int Total { get; init; }
}

/// <summary>媒体库里的一篇 fanbox 作品。</summary>
public class FanboxPostRow
{
    public string PostId { get; init; } = "";
    public string Service { get; init; } = PawchiveApi.FanboxService;
    public string ArtistId { get; init; } = "";
    public string ArtistName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public string Tags { get; init; } = "";
    public string Published { get; init; } = "";
    public string State { get; init; } = "";
    public string Folder { get; init; } = "";
    public string Library { get; init; } = "";
    public string Cover { get; init; } = "";
    public string CoverUrl { get; init; } = "";
    public int FileCount { get; init; }
    public bool Read { get; init; }
    public bool Favorite { get; init; }

    public string WorkId => FanboxService.WorkIdOf(PostId);
}

/// <summary>
/// fanbox（pawchive）下载编排与本地库访问层。
///
/// 与 DLsite 作品不同，fanbox 数据不进 works 表，而是独立的 fanbox_artists / fanbox_posts 两张表
/// （来源不同、字段语义不同，混进 works 会污染 DLsite 的元数据补全与媒体库聚合）。
/// 下载队列仍复用 download_list（source='fanbox'，url 即直链），沿用同一套断点续传/限速/暂停设施：
/// download_list.work_id 用 <see cref="WorkIdPrefix"/> + post_id 表示一篇 fanbox 作品，
/// DownloadEngine 据此前缀把"作品目录/目标库/状态"等读写切到 fanbox_posts 表。
/// </summary>
public static class FanboxService
{
    /// <summary>download_list.work_id 的 fanbox 前缀（RJ 号不会以此开头，故可安全区分来源）。</summary>
    public const string WorkIdPrefix = "fb_";

    /// <summary>缓存目录与媒体库目录下的 fanbox 一级目录名。</summary>
    public const string RootFolderName = "FANBOX";

    /// <summary>作品目录内保存正文的文本文件名。</summary>
    public const string PostTextFile = "post.txt";

    public static string WorkIdOf(string postId) => WorkIdPrefix + postId;

    public static string PostIdOf(string workId) =>
        workId.StartsWith(WorkIdPrefix, StringComparison.Ordinal) ? workId[WorkIdPrefix.Length..] : workId;

    public static bool IsFanboxWorkId(string workId) =>
        workId.StartsWith(WorkIdPrefix, StringComparison.Ordinal);

    // ---------- 入队 ----------

    /// <summary>
    /// 把选中的作品加入下载队列：写 fanbox_artists / fanbox_posts，再按文件逐条写 download_list，
    /// 最后启动下载引擎。已入库（已品悦）或已在队列中的作品会跳过。
    /// </summary>
    public static Task<FanboxEnqueueResult> EnqueuePostsAsync(
        PawchiveArtist artist, IReadOnlyList<PawchivePost> posts,
        string? targetFolder = null, string? targetLib = null) =>
        Task.Run(() => EnqueuePosts(artist, posts, targetFolder, targetLib));

    private static FanboxEnqueueResult EnqueuePosts(
        PawchiveArtist artist, IReadOnlyList<PawchivePost> posts,
        string? targetFolder, string? targetLib)
    {
        if (posts.Count == 0)
            return new FanboxEnqueueResult { Error = "未选择任何作品" };
        if (string.IsNullOrEmpty(AppConfig.DownloadPath))
            return new FanboxEnqueueResult { Error = "尚未设置下载缓存目录（请在系统设置中填写）" };

        UpsertArtist(artist);

        var artistFolder = SanitizeSegment(
            artist.Name.Length > 0 ? artist.Name : artist.PublicId.Length > 0 ? artist.PublicId : artist.Id);
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
        int postCount = 0, fileCount = 0, skipped = 0;

        foreach (var post in posts)
        {
            if (post.Files.Count == 0)
            {
                skipped++;
                continue;   // 纯文字作品无附件可下载
            }
            var workId = WorkIdOf(post.Id);
            if (IsBusy(post.Id))
            {
                skipped++;
                continue;   // 已入库或队列里还有未完成的文件
            }

            var leaf = UniquePostFolderName(post, artist.Id);
            var cacheFolder = Path.Combine(
                AppConfig.DownloadPath, RootFolderName, artistFolder, leaf);

            Db.Execute(
                "INSERT INTO \"fanbox_posts\" (\"service\", \"post_id\", \"artist_id\", \"artist_name\", " +
                "\"title\", \"content\", \"tags\", \"published\", \"cover_url\", \"file_count\", " +
                "\"state\", \"folder\", \"target\", \"target_lib\", \"down_time\") VALUES " +
                "(@sv, @p, @a, @an, @t, @c, @tag, @pub, @cu, @fc, '下载中', @f, @tf, @tl, @now) " +
                "ON CONFLICT(\"service\", \"post_id\") DO UPDATE SET " +
                "\"artist_id\" = excluded.\"artist_id\", \"artist_name\" = excluded.\"artist_name\", " +
                "\"title\" = excluded.\"title\", \"content\" = excluded.\"content\", " +
                "\"tags\" = excluded.\"tags\", \"published\" = excluded.\"published\", " +
                "\"cover_url\" = excluded.\"cover_url\", \"file_count\" = excluded.\"file_count\", " +
                "\"state\" = excluded.\"state\", \"folder\" = excluded.\"folder\", " +
                "\"target\" = excluded.\"target\", \"target_lib\" = excluded.\"target_lib\", " +
                "\"down_time\" = excluded.\"down_time\", \"cover\" = NULL, \"library\" = NULL",
                ("@sv", post.Service), ("@p", post.Id), ("@a", post.ArtistId.Length > 0 ? post.ArtistId : artist.Id),
                ("@an", artist.Name), ("@t", post.Title), ("@c", post.Content),
                ("@tag", string.Join(", ", post.Tags)), ("@pub", post.Published),
                ("@cu", post.CoverPath), ("@fc", post.Files.Count), ("@f", cacheFolder),
                ("@tf", (object?)targetFolder ?? ""), ("@tl", (object?)targetLib ?? ""), ("@now", now));

            // 重新入队前先清掉该作品的旧队列记录，避免残留文件行导致永远"未完成"
            Db.Execute("DELETE FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", workId));

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var file in post.Files)
            {
                index++;
                var subPath = FileLeafName(index, file.Name, file.Path, names);
                Db.Execute(
                    "INSERT OR REPLACE INTO \"download_list\" " +
                    "(\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\", \"source\", \"sub_path\") " +
                    "VALUES (@uuid, @w, @url, '0', '0', '1', 'fanbox', @sub)",
                    ("@uuid", Guid.NewGuid().ToString()), ("@w", workId),
                    ("@url", DownloadUrl(file, post.Id)), ("@sub", subPath));
                fileCount++;
            }
            postCount++;
        }

        if (postCount == 0)
            return new FanboxEnqueueResult
            {
                Skipped = skipped,
                Error = skipped > 0 ? "选中的作品都已在库或已在下载队列中" : "选中的作品没有可下载的文件",
            };

        Logger.Info($"fanbox {artist.Name} 已加入下载队列：{postCount} 篇作品 / {fileCount} 个文件");
        DownloadEngine.Start();
        return new FanboxEnqueueResult { PostCount = postCount, FileCount = fileCount, Skipped = skipped };
    }

    /// <summary>
    /// 文件下载直链。站点按内容哈希存文件，同一张图可能同时出现在多篇作品里（路径相同 → URL 相同），
    /// 而 download_list 以 url 为主键，直接复用会让后入队的作品顶掉前一篇的记录；
    /// 故附加一个作品号参数保证每篇作品的每个文件都是独立记录（服务端忽略未知参数）。
    /// </summary>
    private static string DownloadUrl(PawchiveFile file, string postId)
    {
        var url = PawchiveApi.FileUrl(file.Path, file.Name);
        return url + (url.Contains('?') ? "&" : "?") + "pcid=" + postId;
    }

    /// <summary>该作品是否已入库或仍有未完成的下载任务（据此跳过重复入队）。</summary>
    private static bool IsBusy(string postId)
    {
        var state = Db.Scalar(
            "SELECT \"state\" FROM \"fanbox_posts\" WHERE \"service\" = @sv AND \"post_id\" = @p",
            ("@sv", PawchiveApi.FanboxService), ("@p", postId)) as string;
        if (state == "已品悦")
            return true;
        var pending = Db.Scalar(
            "SELECT COUNT(*) FROM \"download_list\" WHERE \"work_id\" = @w AND \"status\" != '1'",
            ("@w", WorkIdOf(postId)));
        return pending != null && Convert.ToInt64(pending) > 0;
    }

    /// <summary>
    /// 作品目录名：作品标题净化后截断；标题为空用作品号。
    /// 同一作家下已有别的作品占用同名目录时追加作品号，避免互相覆盖。
    /// </summary>
    private static string UniquePostFolderName(PawchivePost post, string artistId)
    {
        var title = SanitizeSegment(post.Title);
        if (title.Length == 0 || title == "_")
            return post.Id;
        if (title.Length > 80)
            title = title[..80].TrimEnd('.', ' ');
        var taken = Db.Scalar(
            "SELECT COUNT(*) FROM \"fanbox_posts\" WHERE \"artist_id\" = @a AND \"post_id\" != @p " +
            "AND (\"folder\" LIKE @like1 OR \"folder\" LIKE @like2)",
            ("@a", artistId), ("@p", post.Id),
            ("@like1", "%\\" + title), ("@like2", "%/" + title));
        return taken != null && Convert.ToInt64(taken) > 0 ? $"{title} [{post.Id}]" : title;
    }

    /// <summary>
    /// 作品内的文件名：站点的附件名多为随机串且不保证有序，统一加三位序号前缀保持展示顺序，
    /// 同时保留原名便于识别；重名时再追加序号。
    /// </summary>
    private static string FileLeafName(int index, string name, string path, HashSet<string> seen)
    {
        var clean = SanitizeSegment(name);
        if (clean.Length == 0 || clean == "_")
            clean = SanitizeSegment(Path.GetFileName(path));
        var leaf = $"{index:D3}_{clean}";
        if (leaf.Length > 120)
        {
            var ext = Path.GetExtension(leaf);
            leaf = leaf[..(120 - ext.Length)] + ext;
        }
        if (seen.Add(leaf))
            return leaf;
        var e = Path.GetExtension(leaf);
        var stem = leaf[..^e.Length];
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}_{i}{e}";
            if (seen.Add(candidate))
                return candidate;
        }
    }

    private static void UpsertArtist(PawchiveArtist artist)
    {
        Db.Execute(
            "INSERT INTO \"fanbox_artists\" (\"service\", \"artist_id\", \"name\", \"public_id\", \"add_time\") " +
            "VALUES (@sv, @a, @n, @pid, @now) " +
            "ON CONFLICT(\"service\", \"artist_id\") DO UPDATE SET " +
            "\"name\" = excluded.\"name\", \"public_id\" = excluded.\"public_id\"",
            ("@sv", artist.Service.Length > 0 ? artist.Service : PawchiveApi.FanboxService),
            ("@a", artist.Id), ("@n", artist.Name), ("@pid", artist.PublicId),
            ("@now", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff")));
    }

    /// <summary>净化成合法的 Windows 目录/文件名片段。</summary>
    public static string SanitizeSegment(string name)
    {
        name ??= "";
        foreach (var ch in "\\/:*?\"<>|")
            name = name.Replace(ch, ' ');
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        return name.Length == 0 ? "_" : name;
    }

    // ---------- DownloadEngine 用的作品行读写（对应 works 表的同名操作）----------

    /// <summary>作品的缓存/最终目录（入队时即写入，重启后续传与收尾都用同一目录）。</summary>
    public static string ReadFolder(string workId) =>
        Db.Scalar("SELECT \"folder\" FROM \"fanbox_posts\" WHERE \"post_id\" = @p", ("@p", PostIdOf(workId)))
            as string ?? "";

    public static void PersistFolder(string workId, string path) =>
        Db.Execute(
            "UPDATE \"fanbox_posts\" SET \"folder\" = @f WHERE \"post_id\" = @p " +
            "AND (\"folder\" IS NULL OR \"folder\" = '')",
            ("@f", path), ("@p", PostIdOf(workId)));

    public static void SetTarget(string workId, string path, string? libName) =>
        Db.Execute(
            "UPDATE \"fanbox_posts\" SET \"target\" = @t, \"target_lib\" = @l WHERE \"post_id\" = @p",
            ("@t", path), ("@l", libName ?? ""), ("@p", PostIdOf(workId)));

    public static string? ReadTarget(string workId)
    {
        var v = Db.Scalar("SELECT \"target\" FROM \"fanbox_posts\" WHERE \"post_id\" = @p",
            ("@p", PostIdOf(workId))) as string;
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static string? ReadTargetLib(string workId)
    {
        var v = Db.Scalar("SELECT \"target_lib\" FROM \"fanbox_posts\" WHERE \"post_id\" = @p",
            ("@p", PostIdOf(workId))) as string;
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>全部文件下载完成后把状态从 下载中 改为 已下载。</summary>
    public static void MarkDownloaded(string workId) =>
        Db.Execute(
            "UPDATE \"fanbox_posts\" SET \"state\" = '已下载' WHERE \"post_id\" = @p AND \"state\" = '下载中'",
            ("@p", PostIdOf(workId)));

    /// <summary>移动到媒体库后改写目录（及随目录一起移动的封面路径）。</summary>
    public static void UpdateFolderAfterMove(string workId, string oldFolder, string newFolder) =>
        Db.Execute(
            "UPDATE \"fanbox_posts\" SET \"folder\" = @d, \"cover\" = REPLACE(\"cover\", @s, @d) " +
            "WHERE \"post_id\" = @p",
            ("@d", newFolder), ("@s", oldFolder), ("@p", PostIdOf(workId)));

    /// <summary>入队时选定的媒体库目标目录：fanbox 统一落在 目标库/FANBOX/作家/作品 下。</summary>
    public static string? TargetFolderFor(string workId)
    {
        var root = ReadTarget(workId);
        if (string.IsNullOrEmpty(root))
            return null;
        var cache = ReadFolder(workId);
        if (cache.Length == 0)
            return null;
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(cache));
        var artist = Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(cache)) ?? "");
        return artist.Length > 0
            ? Path.Combine(root, RootFolderName, artist, leaf)
            : Path.Combine(root, RootFolderName, leaf);
    }

    /// <summary>
    /// 下载完成后的收尾：移动到媒体库目录、写入正文文本、定位封面并标记为已品悦。
    /// （fanbox 作品无压缩包，也不做 DLsite 元数据补全——元数据在入队时就已从 pawchive 取到。）
    /// </summary>
    internal static void FinalizeIntoLibrary(string workId)
    {
        var postId = PostIdOf(workId);
        var cacheFolder = ReadFolder(workId);
        var folder = DownloadEngine.MoveToTargetFolder(workId, cacheFolder, TargetFolderFor(workId));
        WritePostText(postId, folder);

        var cover = FindCover(folder);
        var lib = DownloadEngine.ReadWorkTargetLib(workId);
        Db.Execute(
            "UPDATE \"fanbox_posts\" SET \"state\" = '已品悦', \"folder\" = @f, \"cover\" = @c, " +
            "\"library\" = @l WHERE \"post_id\" = @p",
            ("@f", folder), ("@c", cover ?? ""), ("@l", (object?)lib ?? ""), ("@p", postId));
        Logger.Info($"fanbox 作品 {postId} 已入库，媒体库: {lib ?? "未关联"}");
    }

    /// <summary>把标题/发布时间/标签/正文写成作品目录下的文本文件，便于离线查看。</summary>
    private static void WritePostText(string postId, string folder)
    {
        if (!Directory.Exists(folder))
            return;
        var rows = Db.Select(
            "SELECT \"title\", \"published\", \"tags\", \"content\", \"artist_name\" " +
            "FROM \"fanbox_posts\" WHERE \"post_id\" = @p", ("@p", postId));
        if (rows is not { Count: > 0 })
            return;
        var r = rows[0];
        var lines = new List<string>
        {
            r[0] as string ?? "",
            $"作家：{r[4] as string ?? ""}",
            $"发布：{r[1] as string ?? ""}",
        };
        if (r[2] as string is { Length: > 0 } tags)
            lines.Add($"标签：{tags}");
        if (r[3] as string is { Length: > 0 } content)
        {
            lines.Add("");
            lines.Add(content);
        }
        try
        {
            File.WriteAllText(Path.Combine(folder, PostTextFile), string.Join(Environment.NewLine, lines));
        }
        catch (IOException e)
        {
            Logger.Error($"fanbox 写入作品正文失败 {postId}: {e.Message}");
        }
    }

    private static readonly string[] ImageExts = [".jpg", ".jpeg", ".png", ".gif", ".webp"];

    /// <summary>作品封面：目录内按名称排序的第一张图片（文件名的三位序号前缀保证与站上顺序一致）。</summary>
    private static string? FindCover(string folder)
    {
        if (!Directory.Exists(folder))
            return null;
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>彻底删除一篇作品的记录（下载列表 + 作品行），供"删除"操作使用。</summary>
    public static void PurgePost(string workId)
    {
        var postId = PostIdOf(workId);
        Db.Execute("DELETE FROM \"download_list\" WHERE \"work_id\" = @w", ("@w", workId));
        Db.Execute("DELETE FROM \"fanbox_posts\" WHERE \"post_id\" = @p", ("@p", postId));
    }

    /// <summary>下载页显示用的作品标题（取不到时回退作品号）。</summary>
    public static string DisplayName(string workId)
    {
        var postId = PostIdOf(workId);
        var rows = Db.Select(
            "SELECT \"artist_name\", \"title\" FROM \"fanbox_posts\" WHERE \"post_id\" = @p", ("@p", postId));
        if (rows is not { Count: > 0 })
            return workId;
        var artist = rows[0][0] as string ?? "";
        var title = rows[0][1] as string ?? "";
        if (title.Length == 0)
            return workId;
        return artist.Length > 0 ? $"{artist} · {title}" : title;
    }

    // ---------- 本地库查询（FANBOX 页与 Web 端共用）----------

    /// <summary>本地已记录的作家列表（按已入库作品数降序）。</summary>
    public static List<FanboxArtistRow> ListArtists()
    {
        var rows = Db.Select("""
            SELECT a."artist_id", a."service", a."name", a."public_id",
                   SUM(CASE WHEN p."state" = '已品悦' THEN 1 ELSE 0 END), COUNT(p."post_id")
            FROM "fanbox_artists" a
            LEFT JOIN "fanbox_posts" p ON p."artist_id" = a."artist_id"
            GROUP BY a."service", a."artist_id"
            HAVING COUNT(p."post_id") > 0
            ORDER BY 5 DESC, a."name"
            """);
        return (rows ?? []).Select(r => new FanboxArtistRow
        {
            Id = r[0] as string ?? "",
            Service = r[1] as string ?? PawchiveApi.FanboxService,
            Name = r[2] as string ?? "",
            PublicId = r[3] as string ?? "",
            Downloaded = r[4] is null ? 0 : Convert.ToInt32(r[4]),
            Total = r[5] is null ? 0 : Convert.ToInt32(r[5]),
        }).ToList();
    }

    private const string PostColumns =
        "\"post_id\", \"service\", \"artist_id\", \"artist_name\", \"title\", \"content\", \"tags\", " +
        "\"published\", \"state\", \"folder\", \"library\", \"cover\", \"cover_url\", \"file_count\", " +
        "\"read_flag\", \"favorite\"";

    private static FanboxPostRow MapPost(object?[] r) => new()
    {
        PostId = r[0] as string ?? "",
        Service = r[1] as string ?? PawchiveApi.FanboxService,
        ArtistId = r[2] as string ?? "",
        ArtistName = r[3] as string ?? "",
        Title = r[4] as string ?? "",
        Content = r[5] as string ?? "",
        Tags = r[6] as string ?? "",
        Published = r[7] as string ?? "",
        State = r[8] as string ?? "",
        Folder = r[9] as string ?? "",
        Library = r[10] as string ?? "",
        Cover = r[11] as string ?? "",
        CoverUrl = r[12] as string ?? "",
        FileCount = r[13] is null ? 0 : Convert.ToInt32(r[13]),
        Read = r[14] as string == "1",
        Favorite = r[15] as string == "1",
    };

    /// <summary>某作家本地已记录的作品（按发布时间倒序）。</summary>
    public static List<FanboxPostRow> ListPosts(string artistId)
    {
        var rows = Db.Select(
            $"SELECT {PostColumns} FROM \"fanbox_posts\" WHERE \"artist_id\" = @a " +
            "ORDER BY \"published\" DESC, \"post_id\" DESC", ("@a", artistId));
        return (rows ?? []).Select(MapPost).ToList();
    }

    /// <summary>全部已收藏的 fanbox 作品。</summary>
    public static List<FanboxPostRow> ListFavorites()
    {
        var rows = Db.Select(
            $"SELECT {PostColumns} FROM \"fanbox_posts\" WHERE \"favorite\" = '1' " +
            "ORDER BY \"published\" DESC, \"post_id\" DESC");
        return (rows ?? []).Select(MapPost).ToList();
    }

    public static FanboxPostRow? GetPost(string postId)
    {
        var rows = Db.Select(
            $"SELECT {PostColumns} FROM \"fanbox_posts\" WHERE \"post_id\" = @p", ("@p", postId));
        return rows is { Count: > 0 } ? MapPost(rows[0]) : null;
    }

    /// <summary>该作家下已入库或已在队列中的作品号集合（作家主页据此标记"已下载/下载中"）。</summary>
    public static Dictionary<string, string> PostStates(string artistId)
    {
        var map = new Dictionary<string, string>();
        var rows = Db.Select(
            "SELECT \"post_id\", \"state\" FROM \"fanbox_posts\" WHERE \"artist_id\" = @a", ("@a", artistId));
        foreach (var r in rows ?? [])
            map[r[0] as string ?? ""] = r[1] as string ?? "";
        return map;
    }

    /// <summary>切换已读/收藏标记，返回切换后的值。</summary>
    public static bool ToggleFlag(string postId, string field, bool value)
    {
        var column = field == "read" ? "read_flag" : "favorite";
        Db.Execute($"UPDATE \"fanbox_posts\" SET \"{column}\" = @v WHERE \"post_id\" = @p",
            ("@v", value ? "1" : "0"), ("@p", postId));
        return value;
    }
}
