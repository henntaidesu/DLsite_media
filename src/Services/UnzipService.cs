using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DLsiteMedia.Core;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace DLsiteMedia.Services;

/// <summary>
/// 解压作品压缩包（对应 Python 版 unzip.py）：
/// 优先用 Bandizip bz.exe（处理分卷/SFX 最稳），未安装时回退 SharpCompress（支持 RAR5）。
/// 解压后拍平嵌套目录、修复 Shift_JIS 文件名乱码，最后移动到媒体库并入库。
/// </summary>
public static class UnzipService
{
    // Bandizip 命令行工具
    private const string BandizipBz = @"C:\Program Files\Bandizip\bz.exe";

    private static readonly string[] ArchiveExts = [".zip", ".rar", ".exe"];

    static UnzipService()
    {
        // cp437 / Shift_JIS 等代码页编码需要显式注册
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>目录下（递归）所有压缩文件路径。</summary>
    public static List<string> GetAllArchiveFiles(string folder)
    {
        var result = new List<string>();
        if (!Directory.Exists(folder))
            return result;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            if (ArchiveExts.Contains(Path.GetExtension(file).ToLowerInvariant()))
                result.Add(file);
        return result;
    }

    /// <summary>目录下所有非压缩包文件的总字节数，作为解压产出量估算解压进度。</summary>
    public static long ExtractedSize(string folder)
    {
        long total = 0;
        if (!Directory.Exists(folder))
            return 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (ArchiveExts.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    continue;
                try { total += new FileInfo(file).Length; } catch (IOException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>解压一个压缩包到指定目录，返回是否成功。加密包按「解压密码库」里的密码逐个试。</summary>
    private static bool ExtractArchive(string filePath, string extractPath)
    {
        try
        {
            return File.Exists(BandizipBz)
                ? ExtractWithBandizip(filePath, extractPath)
                : ExtractWithSharpCompress(filePath, extractPath);
        }
        catch (Exception e)
        {
            Logger.Error(e, $"解压 {filePath}");
            return false;
        }
    }

    private static bool ExtractWithBandizip(string filePath, string extractPath)
    {
        // bz 返回非 0 多为输出文件被杀软/索引器临时占用（0x20 共享冲突），
        // -aoa 会覆盖已解出的部分，因此可安全重试，等占用释放后再来。
        // 但 .exe 多为作品自带可执行文件（仅极少数是自解压包），失败通常是"根本不是压缩包"
        // 而非占用冲突，没必要长等重试——单次尝试即可，让上层快速判定为普通文件跳过。
        var maxAttempts = Path.GetExtension(filePath).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? 1 : 3;
        var lastCode = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var (ok, exitCode, needsPassword) = RunBandizip(filePath, extractPath, null);
            if (ok)
                return true;
            // 加密包：等多久都没用，直接转去试密码库（只有占用冲突才值得等）
            if (needsPassword)
                return TryPasswords(filePath, pwd => RunBandizip(filePath, extractPath, pwd).Ok);
            lastCode = exitCode;
            if (attempt < maxAttempts)
            {
                Logger.Warning($"bz.exe 解压返回码 {lastCode}，可能文件被占用，{attempt}/{maxAttempts} 次后重试");
                Thread.Sleep(10000);
            }
        }
        throw new Exception($"bz.exe 解压失败，返回码 {lastCode}");
    }

    /// <summary>
    /// 跑一次 bz.exe。输出里的 0xa0000020（需要密码）/ 0xa0000021（密码错误）
    /// 是区分「加密包」与「文件被占用」的唯一依据——两者的返回码都是 2。
    /// </summary>
    private static (bool Ok, int ExitCode, bool NeedsPassword) RunBandizip(
        string filePath, string extractPath, string? password)
    {
        var psi = new ProcessStartInfo
        {
            FileName = BandizipBz,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("x");
        psi.ArgumentList.Add($"-o:{extractPath}");
        psi.ArgumentList.Add("-aoa");
        psi.ArgumentList.Add("-y");
        if (password != null)
            psi.ArgumentList.Add($"-p:{password}");
        psi.ArgumentList.Add(filePath);   // 压缩包必须放在最后（bz 的参数顺序要求）

        using var process = Process.Start(psi)!;
        var needsPassword = false;
        // 边跑边读：大包会逐个文件打印，输出攒满管道会把子进程卡死
        while (process.StandardOutput.ReadLine() is { } line)
            if (line.Contains("0xa0000020", StringComparison.Ordinal) ||
                line.Contains("0xa0000021", StringComparison.Ordinal))
                needsPassword = true;
        process.WaitForExit();
        return (process.ExitCode == 0, process.ExitCode, needsPassword);
    }

    private static bool ExtractWithSharpCompress(string filePath, string extractPath) =>
        IsEncryptedArchive(filePath)
            ? TryPasswords(filePath, pwd => SharpCompressExtract(filePath, extractPath, pwd))
            : SharpCompressExtract(filePath, extractPath, null);

    /// <summary>SharpCompress 回退：支持 zip / rar（含 RAR5 与分卷，需打开首卷）。</summary>
    private static bool SharpCompressExtract(string filePath, string extractPath, string? password)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(filePath, new ReaderOptions { Password = password });
            foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                entry.WriteToDirectory(extractPath, new ExtractionOptions
                {
                    ExtractFullPath = true,
                    Overwrite = true,
                });
            return true;
        }
        catch (Exception e)
        {
            if (password == null)
                Logger.Error(e, $"解压 {filePath}");   // 试密码过程中的失败由 TryPasswords 统一汇报
            return false;
        }
    }

    /// <summary>压缩包里是否有加密条目（bz.exe 缺席时用来判断该不该上密码库）。</summary>
    private static bool IsEncryptedArchive(string filePath)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(filePath, new ReaderOptions());
            return archive.Entries.Any(e => !e.IsDirectory && e.IsEncrypted);
        }
        catch (Exception)
        {
            return false;   // 打不开就当普通包走，失败由正常流程记日志
        }
    }

    /// <summary>按「解压密码库」逐条试解，成功即止。日志只记第几条，不写出密码本身。</summary>
    private static bool TryPasswords(string filePath, Func<string, bool> attempt)
    {
        var name = Path.GetFileName(filePath);
        var passwords = AppConfig.UnzipPasswords;
        if (passwords.Count == 0)
        {
            Logger.Error($"{name} 需要解压密码，但密码库是空的（系统设置 → 下载 → 解压密码库）");
            return false;
        }
        Logger.Info($"{name} 需要解压密码，开始尝试密码库中的 {passwords.Count} 条");
        for (var i = 0; i < passwords.Count; i++)
            if (attempt(passwords[i]))
            {
                Logger.Info($"{name} 已用密码库第 {i + 1} 条密码解压");
                return true;
            }
        Logger.Error($"{name} 需要解压密码，但密码库里的 {passwords.Count} 条都不匹配");
        return false;
    }

    /// <summary>
    /// 修复目录下文件名乱码：按 系统编码(默认 cp437) 编码再按 Shift_JIS 解码。
    /// 返回 true=已转码；false=文件名包含编码外字符，无需转码。
    /// </summary>
    public static bool FixEncoding(string workPath)
    {
        try
        {
            var sysEncoding = Encoding.GetEncoding(
                NormalizeEncodingName(AppConfig.SysEncoding),
                EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var shiftJis = Encoding.GetEncoding(
                "shift_jis", EncoderFallback.ReplacementFallback,
                new DecoderReplacementFallback(""));  // 对应 Python 的 errors='ignore'
            return FixEncodingInner(workPath, sysEncoding, shiftJis);
        }
        catch (EncoderFallbackException)
        {
            return false;  // 文件名含编码外字符（如中文/日文），说明本来就没乱码
        }
        catch (Exception e)
        {
            Logger.Error(e, "文件名转码");
            return false;
        }
    }

    private static bool FixEncodingInner(string workPath, Encoding sysEncoding, Encoding shiftJis)
    {
        foreach (var entry in Directory.GetFileSystemEntries(workPath))
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
                FixEncodingInner(entry, sysEncoding, shiftJis);  // 先处理子目录内容，再重命名子目录本身
            var fixedName = shiftJis.GetString(sysEncoding.GetBytes(name));
            if (fixedName.Length > 0 && fixedName != name)
            {
                var dest = Path.Combine(workPath, fixedName);
                if (Directory.Exists(entry))
                    Directory.Move(entry, dest);
                else
                    File.Move(entry, dest);
            }
        }
        return true;
    }

    private static string NormalizeEncodingName(string name) =>
        name.Trim().ToLowerInvariant() switch
        {
            "cp437" => "IBM437",
            "cp932" => "shift_jis",
            var n => n,
        };

    /// <summary>
    /// 解压后内容常被多包一层或多层目录（如 RJxxx\RJxxx\RJxxx），
    /// 沿"只有一个子目录"的链找到最终内容目录，把其内容移到根目录，并删除嵌套路径文件夹。
    /// </summary>
    public static void MoveToRoot(string workId, string folderPath)
    {
        try
        {
            var finalPath = folderPath;
            while (true)
            {
                var dirs = Directory.GetDirectories(finalPath);
                if (dirs.Length != 1)
                    break;
                finalPath = dirs[0];
            }
            if (string.Equals(Path.GetFullPath(finalPath), Path.GetFullPath(folderPath),
                    StringComparison.OrdinalIgnoreCase))
                return;
            var relative = Path.GetRelativePath(folderPath, finalPath);
            var topName = relative.Split(Path.DirectorySeparatorChar)[0];
            var topPath = Path.Combine(folderPath, topName);
            var pending = new List<(string Tmp, string Dst)>();  // 与嵌套目录重名的内容先用临时名
            foreach (var entry in Directory.GetFileSystemEntries(finalPath))
            {
                var name = Path.GetFileName(entry);
                var dst = Path.Combine(folderPath, name);
                if (File.Exists(dst) || Directory.Exists(dst))
                {
                    var tmp = dst + "_moving_tmp";
                    MoveEntry(entry, tmp);
                    pending.Add((tmp, dst));
                }
                else
                {
                    MoveEntry(entry, dst);
                }
            }
            Directory.Delete(topPath, true);
            foreach (var (tmp, dst) in pending)
                MoveEntry(tmp, dst);
            Logger.Info($"{workId} 已将最终目录内容移动到根目录");
        }
        catch (Exception e)
        {
            Logger.Error(e, "拍平嵌套目录");
        }
    }

    private static void MoveEntry(string source, string dest)
    {
        if (Directory.Exists(source))
            Directory.Move(source, dest);
        else
            File.Move(source, dest);
    }

    // 分卷包只解首卷，后续分卷由解压器自己带上（否则会逐个尝试后续分卷、刷一堆失败日志）
    private static readonly Regex NonFirstVolume =
        new(@"\.part0*([2-9]|\d{2,})\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 就地解压目录里的压缩包（fanbox 投稿的附件常是压缩包，且常带密码）：
    /// 每个包解到与包同名的子目录，避免包内文件与已下载的图片重名互相覆盖；
    /// 成功则删包，失败则原样保留。返回是否全部解开——
    /// 调用方无论成败都应继续入库，别让一个解不开的包把整篇作品挡在缓存目录里。
    /// </summary>
    public static bool ExtractArchivesInPlace(string workId, string folderPath)
    {
        // 先取快照：解出来的内容里若还套着压缩包，本轮不再深挖
        var archives = GetAllArchiveFiles(folderPath)
            .Where(a => !Path.GetExtension(a).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(a => !NonFirstVolume.IsMatch(a))
            .ToList();
        var allOk = true;
        foreach (var archive in archives)
        {
            if (!File.Exists(archive))
                continue;   // 已被上一个包当作分卷一并解掉
            var dest = Path.Combine(folderPath, Path.GetFileNameWithoutExtension(archive));
            var destExisted = Directory.Exists(dest);
            Logger.Info($"{workId} 解压附件 {Path.GetFileName(archive)}");
            if (ExtractArchive(archive, dest))
            {
                MoveToRoot(workId, dest);   // 包里常再套一层同名目录，拍平
                FixEncoding(dest);          // SharpCompress 回退时的 Shift_JIS 乱码
                try
                {
                    File.Delete(archive);
                }
                catch (Exception e)
                {
                    Logger.Error(e, $"删除压缩包 {archive}");
                }
                continue;
            }
            allOk = false;
            if (destExisted)
                continue;
            try
            {
                if (Directory.Exists(dest))
                    Directory.Delete(dest, true);   // 清掉解了一半的残留，别污染作品目录
            }
            catch (Exception e)
            {
                Logger.Error(e, $"清理解压残留 {dest}");
            }
        }
        return allOk;
    }

    /// <summary>
    /// 解压一个作品：循环解出所有压缩包（解一轮删一轮，处理包中包），
    /// 完成后拍平目录、修复乱码，然后移动到媒体库并入库补全元数据。
    /// </summary>
    public static void Unzip(string workId)
    {
        try
        {
            // 与下载逻辑保持同一文件夹命名方式（RJ号 / 作品名称）
            var folderPath = DownloadEngine.WorkFolderPath(workId);
            Logger.Info($"{workId} 正在解压");
            string? lastSignature = null;   // 上一轮压缩包集合签名，用于检测"无进展"避免死循环
            var rounds = 0;
            // 已确认"解压失败、不是自解压包"的 exe（即作品自带的可执行文件）：
            // 不再当压缩包反复尝试，也绝不删除，否则会卡死整个收尾流程或破坏作品。
            var skipExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var archives = GetAllArchiveFiles(folderPath)
                    .Where(a => !skipExe.Contains(a)).ToList();
                if (archives.Count == 0)
                {
                    MoveToRoot(workId, folderPath);
                    var transcoded = FixEncoding(folderPath);
                    Logger.Info($"{workId} 解压成功");
                    Logger.Info(transcoded ? $"{workId} 转码成功" : $"{workId} 无需转码");
                    FinalizeIntoLibrary(workId, folderPath);
                    return;
                }
                // 防止无限循环：本轮压缩包集合与上一轮完全相同，说明解压/删除均未推进，中止
                var signature = string.Join("|",
                    archives.OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
                if (signature == lastSignature)
                {
                    Logger.Error($"{workId} 解压无进展（压缩包无法删除或反复重现），已中止：{archives[0]}");
                    return;
                }
                lastSignature = signature;
                // 硬上限兜底：嵌套压缩包异常增殖时也能跳出
                if (++rounds > 100)
                {
                    Logger.Error($"{workId} 解压轮次超过上限（100），已中止");
                    return;
                }

                // 优先解真正的压缩包（zip/rar，含分卷）；只剩 exe 时才逐个试解。
                var realArchives = archives
                    .Where(a => !Path.GetExtension(a).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (realArchives.Count > 0)
                {
                    if (!ExtractArchive(realArchives[0], folderPath))
                        return;
                    Thread.Sleep(3000);
                    // 只删真正的压缩包/分卷，绝不删除解压出来的 exe（作品本体）
                    foreach (var archive in realArchives)
                        try
                        {
                            if (File.Exists(archive))
                                File.Delete(archive);
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e, $"删除压缩包 {archive}");
                        }
                }
                else
                {
                    // 只剩 exe：可能是自解压包，也可能是作品自带 exe。试解一次区分：
                    // 成功 → 是自解压包，删掉它继续；失败 → 是作品文件，跳过保留、继续收尾。
                    var exe = archives[0];
                    if (ExtractArchive(exe, folderPath))
                    {
                        Thread.Sleep(3000);
                        try
                        {
                            if (File.Exists(exe))
                                File.Delete(exe);
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e, $"删除自解压包 {exe}");
                        }
                    }
                    else
                    {
                        Logger.Warning($"{workId} {Path.GetFileName(exe)} 不是自解压压缩包，按作品文件保留");
                        skipExe.Add(exe);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Logger.Error(e, "解压作品");
        }
    }

    /// <summary>
    /// 下载/解压成功后入库：从缓存目录移动到媒体库目标目录，标记为已品悦，
    /// 关联所属媒体库和文件夹，然后后台补全详细元数据。
    /// asmr.one 直链下载（无压缩包）也复用此收尾逻辑。
    /// </summary>
    internal static void FinalizeIntoLibrary(string workId, string folderPath)
    {
        // 解压完成后把作品从缓存目录移动到媒体库目标目录，后续都用最终目录
        folderPath = DownloadEngine.MoveToTargetFolder(workId, folderPath);
        // 优先用入队时记录的所属媒体库名；没有时再通过父目录匹配媒体库文件夹
        var libName = DownloadEngine.ReadWorkTargetLib(workId);
        if (libName == null)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(folderPath));
            foreach (var lib in AppConfig.ReadMediaLibs())
            {
                if (lib.Folders.Any(f => string.Equals(
                        Path.GetFullPath(f), parent, StringComparison.OrdinalIgnoreCase)))
                {
                    libName = lib.Name;
                    break;
                }
            }
        }

        MediaLibraryService.ImportRjList(
            [workId], "已品悦", libName,
            new Dictionary<string, string> { [workId] = Path.GetFullPath(folderPath) });
        Logger.Info($"{workId} 已标记为已品悦，媒体库: {libName ?? "未关联"}");

        // 移动到媒体库后只补全本作品的元数据（DL API 字段 + 作品页正文/标签/图片）
        new Thread(() =>
        {
            try
            {
                Logger.Info($"{workId} 开始获取元数据");
                MediaLibraryService.BackfillWorksFromApiAsync(
                    delaySeconds: 0.5, workIds: [workId]).GetAwaiter().GetResult();
                MediaLibraryService.BackfillWorkPagesAsync(
                    delaySeconds: 1.0, workIds: [workId]).GetAwaiter().GetResult();
                Logger.Info($"{workId} 元数据获取完成");
            }
            catch (Exception e)
            {
                Logger.Error(e, "补全元数据");
            }
        }) { IsBackground = true, Name = $"backfill-{workId}" }.Start();
    }
}
