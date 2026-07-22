using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 文件决策缓存仓储实现。
///
/// 写操作（Upsert/Clear）通过批量写入队列异步执行。
/// 读操作直接通过 SQLite 连接查询，利用 WAL 模式下的并发读能力。
///
/// Upsert 策略：
/// 使用 INSERT OR REPLACE，以 file_path 为主键。
/// 当文件被重新扫描时，自动覆盖旧的缓存记录，同时更新 cache_version。
/// </summary>
public sealed class FileDecisionCacheRepository : IFileDecisionCacheRepository, IDisposable
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IBatchWriteQueue<FileDecisionCache> _writeQueue;
    private readonly ILogger _logger;
    private bool _disposed;

    public FileDecisionCacheRepository(
        ISqliteConnectionFactory connectionFactory,
        ILogger logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 创建批量写入队列：使用 INSERT OR REPLACE 实现 Upsert
        _writeQueue = new BatchWriteQueue<FileDecisionCache>(
            flushHandler: UpsertBatchAsync,
            logger: logger,
            name: "FileDecisionCache",
            batchSize: 200,
            flushIntervalMs: 500);
    }

    /// <inheritdoc/>
    public void Upsert(FileDecisionCache entry) => _writeQueue.Enqueue(entry);

    /// <inheritdoc/>
    public void UpsertBatch(IEnumerable<FileDecisionCache> entries) => _writeQueue.EnqueueBatch(entries);

    /// <inheritdoc/>
    public async Task<FileDecisionCache?> GetByFilePathAsync(string filePath, CancellationToken ct = default)
    {
        const string sql = """
            SELECT file_path, size_bytes, last_write_time, rule_verdict, semantic_category,
                   ai_label, ai_confidence, ai_explanation, final_action, user_decision, cache_version
            FROM FileDecisionCache
            WHERE file_path = @path
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@path", filePath);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return MapFromReader(reader);
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FileDecisionCache>> GetByActionAsync(
        FinalAction action, CancellationToken ct = default)
    {
        const string sql = """
            SELECT file_path, size_bytes, last_write_time, rule_verdict, semantic_category,
                   ai_label, ai_confidence, ai_explanation, final_action, user_decision, cache_version
            FROM FileDecisionCache
            WHERE final_action = @action
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@action", (int)action);

        var results = new List<FileDecisionCache>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapFromReader(reader));
        }

        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FileDecisionCache>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT file_path, size_bytes, last_write_time, rule_verdict, semantic_category,
                   ai_label, ai_confidence, ai_explanation, final_action, user_decision, cache_version
            FROM FileDecisionCache
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        var results = new List<FileDecisionCache>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapFromReader(reader));
        }

        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FileDecisionCache>> GetBySemanticCategoryAsync(
        string category, CancellationToken ct = default)
    {
        const string sql = """
            SELECT file_path, size_bytes, last_write_time, rule_verdict, semantic_category,
                   ai_label, ai_confidence, ai_explanation, final_action, user_decision, cache_version
            FROM FileDecisionCache
            WHERE semantic_category = @category
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@category", category);

        var results = new List<FileDecisionCache>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapFromReader(reader));
        }

        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FileDecisionCache>> GetByVersionAsync(
        int cacheVersion, CancellationToken ct = default)
    {
        const string sql = """
            SELECT file_path, size_bytes, last_write_time, rule_verdict, semantic_category,
                   ai_label, ai_confidence, ai_explanation, final_action, user_decision, cache_version
            FROM FileDecisionCache
            WHERE cache_version = @version
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@version", cacheVersion);

        var results = new List<FileDecisionCache>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapFromReader(reader));
        }

        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FileDecisionCache>> GetByActionAndVersionAsync(
        FinalAction action, int cacheVersion, CancellationToken ct = default)
    {
        const string sql = """
            SELECT file_path, size_bytes, last_write_time, rule_verdict, semantic_category,
                   ai_label, ai_confidence, ai_explanation, final_action, user_decision, cache_version
            FROM FileDecisionCache
            WHERE final_action = @action AND cache_version = @version
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@action", (int)action);
        cmd.Parameters.AddWithValue("@version", cacheVersion);

        var results = new List<FileDecisionCache>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapFromReader(reader));
        }

        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        // 清空操作不进入队列：先刷新队列确保所有待写入数据落盘，再执行 DELETE
        await _writeQueue.FlushAsync(ct).ConfigureAwait(false);

        const string sql = "DELETE FROM FileDecisionCache";

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var deletedCount = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("[决策缓存] 已清空缓存表，删除 {Count} 条记录", deletedCount);
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default) => _writeQueue.FlushAsync(ct);

    /// <summary>批量 Upsert 处理函数：执行 INSERT OR REPLACE 事务</summary>
    private async Task UpsertBatchAsync(IReadOnlyList<FileDecisionCache> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        // 构建批量 INSERT OR REPLACE 语句
        const string baseSql = """
            INSERT OR REPLACE INTO FileDecisionCache
            (file_path, size_bytes, last_write_time, rule_verdict, semantic_category,
             ai_label, ai_confidence, ai_explanation, final_action, user_decision, cache_version)
            VALUES
            """;

        var valueClauses = new List<string>(batch.Count);
        using var cmd = connection.CreateCommand();

        for (var i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            valueClauses.Add($"(@p{i}, @s{i}, @l{i}, @r{i}, @sc{i}, @al{i}, @ac{i}, @ae{i}, @fa{i}, @ud{i}, @cv{i})");

            cmd.Parameters.AddWithValue($"@p{i}", entry.FilePath);
            cmd.Parameters.AddWithValue($"@s{i}", entry.SizeBytes);
            cmd.Parameters.AddWithValue($"@l{i}", entry.LastWriteTime.ToString("O"));
            cmd.Parameters.AddWithValue($"@r{i}", (int)entry.RuleVerdict);
            cmd.Parameters.AddWithValue($"@sc{i}", entry.SemanticCategory);
            cmd.Parameters.AddWithValue($"@al{i}", (object?)entry.AiLabel ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@ac{i}", (object?)entry.AiConfidence ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@ae{i}", (object?)entry.AiExplanation ?? DBNull.Value);
            cmd.Parameters.AddWithValue($"@fa{i}", (int)entry.FinalAction);
            cmd.Parameters.AddWithValue($"@ud{i}", entry.UserDecision.HasValue ? (int)entry.UserDecision.Value : DBNull.Value);
            cmd.Parameters.AddWithValue($"@cv{i}", entry.CacheVersion);
        }

        cmd.CommandText = baseSql + string.Join(", ", valueClauses);

        try
        {
            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("[决策缓存] 批量 Upsert 完成: {Count} 条", batch.Count);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>从 SqliteDataReader 当前行映射到 FileDecisionCache 实体</summary>
    private static FileDecisionCache MapFromReader(SqliteDataReader reader)
    {
        return new FileDecisionCache
        {
            FilePath = reader.GetString(0),
            SizeBytes = reader.GetInt64(1),
            LastWriteTime = DateTime.Parse(reader.GetString(2)),
            RuleVerdict = (RuleVerdict)reader.GetInt32(3),
            SemanticCategory = reader.GetString(4),
            AiLabel = reader.IsDBNull(5) ? null : reader.GetString(5),
            AiConfidence = reader.IsDBNull(6) ? null : reader.GetDouble(6),
            AiExplanation = reader.IsDBNull(7) ? null : reader.GetString(7),
            FinalAction = (FinalAction)reader.GetInt32(8),
            UserDecision = reader.IsDBNull(9) ? null : (FinalAction)reader.GetInt32(9),
            CacheVersion = reader.GetInt32(10),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeQueue.Dispose();
    }
}
