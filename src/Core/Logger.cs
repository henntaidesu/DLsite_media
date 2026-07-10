using System;
using System.IO;

namespace DLsiteMedia.Core;

/// <summary>
/// 文件 + 控制台日志（对应 Python 版 log.py）。
/// 写入 log/yyyy-MM-dd.log，按天切换文件；级别由 conf 表 loglevel.level 控制（info/error/debug）。
/// </summary>
public static class Logger
{
    private static readonly object Lock = new();
    private static readonly string LogDir = Path.Combine(Environment.CurrentDirectory, "log");

    public static void Info(string text) => Write(text, "INFO");
    public static void Warning(string text) => Write(text, "WARNING");
    public static void Error(string text) => Write(text, "ERROR");

    public static void Error(Exception e, string? context = null)
    {
        Write($"Err Message: {e.Message}", "ERROR");
        Write($"Err Type: {e.GetType().Name}" + (context is null ? "" : $" ({context})"), "ERROR");
        if (e.StackTrace is { } st)
            Write(st, "ERROR");
    }

    private static void Write(string text, string level)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {level} - {text}";
        lock (Lock)
        {
            // 日志级别为 error 时 info/warning 只打印控制台不落盘（与 Python 版一致）
            var logLevel = AppConfig.LogLevelCached;
            var toFile = level == "ERROR" || logLevel != "error";
            Console.WriteLine(line);
            if (!toFile) return;
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(
                    Path.Combine(LogDir, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    line + Environment.NewLine);
            }
            catch (IOException)
            {
                // 日志写入失败不影响主流程
            }
        }
    }
}
