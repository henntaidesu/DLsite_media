using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DLsiteMedia.Core;

namespace DLsiteMedia.Services;

/// <summary>单个作品的入队结果。</summary>
public class AsmrEnqueueResult
{
    public string WorkId { get; init; } = "";   // RJ 号
    public int FileCount { get; init; }          // 实际入队的文件数（过滤后）
    public bool Ok => FileCount > 0;
    public string? Error { get; init; }
}

/// <summary>
/// asmr.one 下载编排：给定 RJ 号，拉详情、按文件类型过滤、写入 works / download_list
/// 队列，再交给 DownloadEngine 直链下载。
/// （AsmrApi 只负责调接口，本类负责落库与下载调度的桥接。）
/// </summary>
public static class AsmrService
{
    /// <summary>
    /// 通过 RJ 号把一个 SOU 作品加入 asmr.one 下载队列并启动下载引擎。
    /// title 为 DL API 查到的作品名（用作 work_name，可空，元数据补全会覆盖）。
    /// </summary>
    public static async Task<AsmrEnqueueResult> EnqueueByRjAsync(
        string rj, string title, string? targetFolder = null, string? targetLib = null)
    {
        var asmrId = AsmrApi.RjToId(rj);
        if (asmrId <= 0)
            return new AsmrEnqueueResult { WorkId = rj, Error = "无法从 RJ 号解析 asmr.one 作品 id" };

        var detail = await AsmrApi.GetWorkDetailAsync(asmrId);
        if (detail is null)
            return new AsmrEnqueueResult { WorkId = rj, Error = "asmr.one 未找到该作品或获取详情失败" };

        var files = detail.Files.Where(f => FileTypeAllowed(f.Title)).ToList();
        if (files.Count == 0)
            return new AsmrEnqueueResult { WorkId = rj, Error = "按当前文件类型过滤后无可下载文件" };

        // works 行：状态下载中，记录来源与 asmr 数字 id；work_name 优先用 DL API 名称，
        // 没有时回退到 asmr 详情标题，下载完成后的元数据补全会用 DL API 覆盖为正式字段
        var workName = string.IsNullOrEmpty(title) ? detail.Title : title;
        Db.Execute(
            "INSERT INTO \"works\" (\"work_id\", \"work_name\", \"state\", \"source\", \"asmr_id\", \"down_time\") " +
            "VALUES (@w, @n, '下载中', 'asmr', @aid, @time) " +
            "ON CONFLICT(\"work_id\") DO UPDATE SET " +
            "\"work_name\" = excluded.\"work_name\", \"state\" = excluded.\"state\", " +
            "\"source\" = excluded.\"source\", \"asmr_id\" = excluded.\"asmr_id\", " +
            "\"down_time\" = excluded.\"down_time\", " +
            "\"folder\" = NULL, \"target\" = NULL, \"target_lib\" = NULL, " +
            "\"cover\" = NULL, \"meta_scanned\" = NULL",
            ("@w", rj), ("@n", workName), ("@aid", asmrId.ToString()),
            ("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff")));
        var workId = rj;

        // 每个文件一条 download_list 记录：url 为直链（主键），sub_path 保留作品内目录结构
        // 同名子目录下重名文件去重（dedupe by sub_path）
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var subPath = BuildSubPath(file.FolderPath, file.Title, seen);
            Db.Execute(
                "INSERT OR REPLACE INTO \"download_list\" " +
                "(\"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"delete\", \"source\", \"sub_path\") " +
                "VALUES (@uuid, @w, @url, '0', '0', '1', 'asmr', @sub)",
                ("@uuid", Guid.NewGuid().ToString()), ("@w", workId),
                ("@url", file.DownloadUrl), ("@sub", subPath));
        }

        // 入队后落库目标媒体库目录（重启后仍可恢复）
        if (!string.IsNullOrEmpty(targetFolder))
            DownloadEngine.SetWorkTargetPath(workId, targetFolder, targetLib);

        Logger.Info($"{workId} asmr 已加入下载队列：{files.Count} 个文件");
        DownloadEngine.Start();
        return new AsmrEnqueueResult { WorkId = workId, FileCount = files.Count };
    }

    /// <summary>文件标题（含扩展名）是否允许下载：按扩展名查 asmr_filetype 配置，未知扩展名放行。</summary>
    private static bool FileTypeAllowed(string title)
    {
        var ext = Path.GetExtension(title).TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || !AppConfig.AsmrFileTypes.Contains(ext))
            return true;  // 无扩展名或不在受控类型列表内的，默认下载
        return AppConfig.AsmrFileTypeEnabled(ext);
    }

    /// <summary>构造作品内相对路径（含文件名），清理 Windows 非法字符，并对重名追加序号。</summary>
    private static string BuildSubPath(string folderPath, string title, HashSet<string> seen)
    {
        var segments = new List<string>();
        if (!string.IsNullOrEmpty(folderPath))
            segments.AddRange(folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(SanitizeSegment));
        segments.Add(SanitizeSegment(string.IsNullOrEmpty(title) ? "untitled" : title));
        var path = string.Join('/', segments);

        // 同一作品内的重名路径去重（极少见），追加 _2 / _3 …
        if (seen.Add(path))
            return path;
        var ext = Path.GetExtension(path);
        var stem = path[..^ext.Length];
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}_{i}{ext}";
            if (seen.Add(candidate))
                return candidate;
        }
    }

    private static string SanitizeSegment(string name)
    {
        foreach (var ch in "\\/:*?\"<>|")
            name = name.Replace(ch, ' ');
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
        return name.Length == 0 ? "_" : name;
    }
}
