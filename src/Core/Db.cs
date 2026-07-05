using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DASD.Core;

/// <summary>
/// SQLite 数据库访问层（对应 Python 版 datebase_execution.py）。
/// 沿用项目根目录的 DASD.db：conf / download_list / works / work_genres 表，老数据无缝继承。
/// 每次操作独立连接（Sqlite 连接池），WAL 模式下 UI 刷新 + 下载线程 + 元数据补全并发安全。
/// </summary>
public static class Db
{
    private static readonly string DbPath = LocateDb();
    private static readonly string ConnString =
        new SqliteConnectionStringBuilder { DataSource = DbPath, DefaultTimeout = 30 }.ToString();

    private static bool _initialized;
    private static readonly object InitLock = new();

    /// <summary>
    /// 定位 DASD.db：优先当前工作目录，其次从 exe 目录向上逐级查找（开发期命中仓库根），
    /// 都没有时在 exe 目录新建。
    /// </summary>
    private static string LocateDb()
    {
        var cwd = Path.Combine(Environment.CurrentDirectory, "DASD.db");
        if (File.Exists(cwd))
            return cwd;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "DASD.db");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "DASD.db");
    }

    public static string DatabasePath => DbPath;

    public static SqliteConnection Open()
    {
        EnsureTables();
        var conn = new SqliteConnection(ConnString);
        conn.Open();
        // WAL 是数据库级持久属性、synchronous 由 EnsureTables 建库时设一次即可，
        // 不再每次开连接重复执行 PRAGMA（每查询固定开销）。
        return conn;
    }

    /// <summary>建表（不存在时）并为旧版 works 表补加缺失列，进程内只执行一次。</summary>
    public static void EnsureTables()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    CREATE TABLE IF NOT EXISTS "conf" (
                        "section" TEXT NOT NULL,
                        "key" TEXT NOT NULL,
                        "value" TEXT,
                        PRIMARY KEY ("section", "key")
                    );
                    CREATE TABLE IF NOT EXISTS "download_list" (
                        "UUID" text,
                        "work_id" text,
                        "url" TEXT NOT NULL,
                        "status" text,
                        "long" text,
                        "delete" text,
                        PRIMARY KEY ("url")
                    );
                    CREATE TABLE IF NOT EXISTS "works" (
                        "work_id" text,
                        "work_name" TEXT,
                        "maker_id" text,
                        "maker_name" TEXT,
                        "work_type" text,
                        "intro_s" TEXT,
                        "age_category" text,
                        "is_ana" text,
                        "state" text,
                        "library" text,
                        "sell_date" text,
                        "series" text,
                        "scenario" text,
                        "illust" text,
                        "voice_actor" text,
                        "genre" text,
                        "file_size" text,
                        "cover" text,
                        "down_time" text,
                        "meta_scanned" text,
                        "folder" text,
                        "target" text,
                        "target_lib" text,
                        PRIMARY KEY ("work_id")
                    );
                    CREATE TABLE IF NOT EXISTS "work_genres" (
                        "work_id" text NOT NULL,
                        "genre" text NOT NULL,
                        PRIMARY KEY ("work_id", "genre")
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // 旧版 works 表缺列时补加
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(\"works\")";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    columns.Add(reader.GetString(1));
            }
            string[] required =
            [
                "state", "library", "sell_date", "series", "scenario", "illust",
                "voice_actor", "genre", "file_size", "cover", "meta_scanned", "folder",
                "target", "target_lib", "read_flag", "favorite",
                // 数据源区分："anime-sharing"(默认/旧数据) 或 "asmr"；asmr_id 存 asmr.one 的数字作品 id（调 API 用）
                "source", "asmr_id"
            ];
            foreach (var col in required)
            {
                if (!columns.Contains(col))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"ALTER TABLE \"works\" ADD COLUMN \"{col}\" text";
                    cmd.ExecuteNonQuery();
                }
            }

            // 旧版 download_list 缺 error 列时补加（记录解析失败原因）
            var dlColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(\"download_list\")";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    dlColumns.Add(reader.GetString(1));
            }
            if (!dlColumns.Contains("error"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE \"download_list\" ADD COLUMN \"error\" text";
                cmd.ExecuteNonQuery();
            }
            // download_list 补加来源与作品内子目录列：
            // source="asmr" 时 url 即直链（跳过 debrid 解析）；sub_path 为该文件在作品内的相对子目录（保留 asmr 目录树）
            foreach (var dlCol in new[] { "source", "sub_path" })
            {
                if (!dlColumns.Contains(dlCol))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"ALTER TABLE \"download_list\" ADD COLUMN \"{dlCol}\" text";
                    cmd.ExecuteNonQuery();
                }
            }

            // 为高频过滤/排序列建二级索引（列必已由上方建表或补列保证存在），
            // 避免媒体库/标签/形式/已下载页的全表扫描。
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE INDEX IF NOT EXISTS "idx_works_library" ON "works" ("library");
                    CREATE INDEX IF NOT EXISTS "idx_works_state" ON "works" ("state");
                    CREATE INDEX IF NOT EXISTS "idx_works_maker_name" ON "works" ("maker_name");
                    CREATE INDEX IF NOT EXISTS "idx_works_work_type" ON "works" ("work_type");
                    CREATE INDEX IF NOT EXISTS "idx_works_down_time" ON "works" ("down_time");
                    CREATE INDEX IF NOT EXISTS "idx_work_genres_genre" ON "work_genres" ("genre");
                    CREATE INDEX IF NOT EXISTS "idx_download_list_work_id" ON "download_list" ("work_id");
                    CREATE INDEX IF NOT EXISTS "idx_download_list_status" ON "download_list" ("status");
                    """;
                cmd.ExecuteNonQuery();
            }
            _initialized = true;
        }
    }

    /// <summary>查询：返回行列表，每行为 object?[]（列序与 SQL 一致），失败时返回 null 并记日志。</summary>
    public static List<object?[]>? Select(string sql, params (string Name, object? Value)[] args)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        catch (SqliteException e)
        {
            Logger.Error($"数据库查询失败: {e.Message}");
            Logger.Error(sql);
            return null;
        }
    }

    /// <summary>写操作（INSERT/UPDATE/DELETE），返回是否成功。</summary>
    public static bool Execute(string sql, params (string Name, object? Value)[] args)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException e)
        {
            Logger.Error($"数据库操作失败: {e.Message}");
            Logger.Error(sql);
            return false;
        }
    }

    /// <summary>查询单值（第一行第一列），无结果返回 null。</summary>
    public static object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        var rows = Select(sql, args);
        return rows is { Count: > 0 } ? rows[0][0] : null;
    }
}
