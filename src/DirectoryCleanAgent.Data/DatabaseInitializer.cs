using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// SQLite 数据库初始化器。
///
/// 职责：
/// 1. 创建数据库文件及目录结构
/// 2. 设置 WAL 模式、synchronous=NORMAL 等性能 PRAGMA
/// 3. 创建所有业务表（FileDecisionCache / LocalTombstone / DeletionRecord / AuditLog）
/// 4. 创建查询索引
/// 5. 对外提供 ISqliteConnectionFactory 供仓储层创建连接
///
/// 所有建表语句使用 IF NOT EXISTS，支持幂等执行（应用重启时可安全重复调用）。
/// </summary>
public sealed class DatabaseInitializer : ISqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    /// <inheritdoc/>
    public string DatabasePath { get; }

    /// <summary>
    /// 初始化数据库。
    /// </summary>
    /// <param name="databasePath">SQLite 数据库文件完整路径（如 "%LocalAppData%\SpaceClear\data.db"）</param>
    /// <param name="logger">日志记录器</param>
    public DatabaseInitializer(string databasePath, ILogger logger)
    {
        DatabasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 构建连接字符串，启用外键约束
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,   // WAL 模式下推荐使用 Private 缓存
            ForeignKeys = true                   // 启用外键约束
        }.ToString();

        _logger.LogDebug("数据库连接字符串已构建: {Path}", DatabasePath);
    }

    /// <inheritdoc/>
    public async Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// 执行完整的数据库初始化流程。
    /// 此方法可在应用每次启动时安全调用（所有 DDL 使用 IF NOT EXISTS 幂等执行）。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[数据库初始化] 开始初始化 SQLite 数据库: {Path}", DatabasePath);

        try
        {
            // 步骤0：确保数据库目录存在
            EnsureDirectoryExists();

            // 步骤1：创建连接并设置 PRAGMA
            await using var connection = await CreateConnectionAsync(ct).ConfigureAwait(false);
            await SetPragmasAsync(connection).ConfigureAwait(false);

            // 步骤2：创建所有表（幂等）
            await CreateTablesAsync(connection, ct).ConfigureAwait(false);

            // 步骤3：创建索引（幂等）
            await CreateIndexesAsync(connection, ct).ConfigureAwait(false);

            _logger.LogInformation("[数据库初始化] 数据库初始化完成");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[数据库初始化] 操作被取消");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[数据库初始化] 初始化失败");
            throw;
        }
    }

    /// <summary>确保数据库文件所在的目录存在</summary>
    private void EnsureDirectoryExists()
    {
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            _logger.LogDebug("已创建数据库目录: {Dir}", dir);
        }
    }

    /// <summary>设置 WAL 模式及性能 PRAGMA</summary>
    private async Task SetPragmasAsync(SqliteConnection connection)
    {
        // WAL 模式：允许并发读写，写入不阻塞读取
        // synchronous=NORMAL：在 WAL 模式下安全，且比 FULL 快 2-3 倍
        var pragmas = new[]
        {
            "PRAGMA journal_mode=WAL;",
            "PRAGMA synchronous=NORMAL;",
            "PRAGMA cache_size=-8000;",        // 8MB 缓存
            "PRAGMA busy_timeout=5000;",       // 5 秒忙等超时
            "PRAGMA temp_store=MEMORY;",       // 临时表存储在内存中
        };

        foreach (var pragma in pragmas)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = pragma;
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            _logger.LogDebug("PRAGMA 执行: {Pragma} => {Result}", pragma, result ?? "(null)");
        }
    }

    /// <summary>创建所有业务表（IF NOT EXISTS 保证幂等）</summary>
    private async Task CreateTablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        // 表结构定义与设计文档 5.1 节完全一致

        const string createFileDecisionCache = """
            CREATE TABLE IF NOT EXISTS FileDecisionCache (
                file_path            TEXT PRIMARY KEY NOT NULL,
                size_bytes           INTEGER NOT NULL,
                last_write_time      TEXT NOT NULL,
                rule_verdict         INTEGER NOT NULL,
                semantic_category    TEXT NOT NULL,
                ai_label             TEXT,
                ai_confidence        REAL,
                ai_explanation       TEXT,
                final_action         INTEGER NOT NULL,
                user_decision        INTEGER,
                cache_version        INTEGER NOT NULL
            );
            """;

        const string createLocalTombstone = """
            CREATE TABLE IF NOT EXISTS LocalTombstone (
                file_path                TEXT NOT NULL,
                file_identity_key        TEXT PRIMARY KEY NOT NULL,
                operation_id             TEXT NOT NULL,
                original_size            INTEGER NOT NULL,
                original_last_write_time TEXT NOT NULL,
                deleted_at               TEXT NOT NULL
            );
            """;

        const string createDeletionRecord = """
            CREATE TABLE IF NOT EXISTS DeletionRecord (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                operation_id       TEXT NOT NULL,
                file_path          TEXT NOT NULL,
                file_hash          TEXT NOT NULL,
                file_size          INTEGER NOT NULL,
                deletion_method    TEXT NOT NULL,
                decision_snapshot  TEXT NOT NULL,
                created_at         TEXT NOT NULL
            );
            """;

        const string createAuditLog = """
            CREATE TABLE IF NOT EXISTS AuditLog (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp       TEXT NOT NULL,
                user_sid        TEXT NOT NULL,
                operation_type  TEXT NOT NULL,
                target          TEXT NOT NULL,
                result          TEXT NOT NULL,
                details         TEXT
            );
            """;

        var tables = new Dictionary<string, string>
        {
            ["FileDecisionCache"] = createFileDecisionCache,
            ["LocalTombstone"]   = createLocalTombstone,
            ["DeletionRecord"]   = createDeletionRecord,
            ["AuditLog"]         = createAuditLog,
        };

        foreach (var (tableName, ddl) in tables)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = ddl;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _logger.LogDebug("表已就绪: {Table}", tableName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建表失败: {Table}", tableName);
                throw;
            }
        }
    }

    /// <summary>创建查询索引（IF NOT EXISTS 保证幂等）</summary>
    private async Task CreateIndexesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var indexes = new[]
        {
            // FileDecisionCache 查询索引
            "CREATE INDEX IF NOT EXISTS idx_fdc_action ON FileDecisionCache(final_action);",
            "CREATE INDEX IF NOT EXISTS idx_fdc_semantic ON FileDecisionCache(semantic_category);",
            "CREATE INDEX IF NOT EXISTS idx_fdc_version ON FileDecisionCache(cache_version);",

            // LocalTombstone 查询索引
            "CREATE INDEX IF NOT EXISTS idx_tomb_op ON LocalTombstone(operation_id);",
            "CREATE INDEX IF NOT EXISTS idx_tomb_deleted ON LocalTombstone(deleted_at);",
            "CREATE INDEX IF NOT EXISTS idx_tomb_path ON LocalTombstone(file_path);",

            // DeletionRecord 查询索引
            "CREATE INDEX IF NOT EXISTS idx_dr_op ON DeletionRecord(operation_id);",
            "CREATE INDEX IF NOT EXISTS idx_dr_created ON DeletionRecord(created_at);",

            // AuditLog 查询索引
            "CREATE INDEX IF NOT EXISTS idx_al_timestamp ON AuditLog(timestamp);",
            "CREATE INDEX IF NOT EXISTS idx_al_usersid ON AuditLog(user_sid);",
            "CREATE INDEX IF NOT EXISTS idx_al_optype ON AuditLog(operation_type);",
        };

        foreach (var ddl in indexes)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = ddl;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _logger.LogDebug("索引已就绪: {Index}", ddl[..Math.Min(ddl.Length, 60)]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建索引失败: {Index}", ddl);
                throw;
            }
        }
    }
}
