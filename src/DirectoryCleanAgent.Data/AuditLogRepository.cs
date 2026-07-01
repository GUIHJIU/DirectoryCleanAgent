using DirectoryCleanAgent.Core.DTOs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 审计日志仓储实现。
///
/// 不可修改性约束（设计文档 4.2/8 节）：
/// 本仓储仅提供 Insert（入队批量写入）和 Query（直接查询）方法。
/// 不暴露任何 Update / Delete 公开 API，从代码根源杜绝审计日志篡改可能。
///
/// 应用程序数据库连接仅拥有写入和查询权限。
/// </summary>
public sealed class AuditLogRepository : IAuditLogRepository, IDisposable
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IBatchWriteQueue<AuditLogEntry> _writeQueue;
    private readonly ILogger _logger;
    private bool _disposed;

    public AuditLogRepository(
        ISqliteConnectionFactory connectionFactory,
        ILogger logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _writeQueue = new BatchWriteQueue<AuditLogEntry>(
            flushHandler: InsertBatchAsync,
            logger: logger,
            name: "AuditLog",
            batchSize: 200,
            flushIntervalMs: 500);
    }

    /// <inheritdoc/>
    public void Insert(AuditLogEntry entry) => _writeQueue.Enqueue(entry);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AuditLogEntry>> QueryAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? userSid = null,
        string? operationType = null,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default)
    {
        // 动态构建 WHERE 条件，防止 SQL 注入：所有用户输入通过参数化传递
        var conditions = new List<string>();
        using var cmd = new SqliteCommand();

        if (from.HasValue)
        {
            conditions.Add("timestamp >= @from");
            cmd.Parameters.AddWithValue("@from", from.Value.ToString("O"));
        }
        if (to.HasValue)
        {
            conditions.Add("timestamp <= @to");
            cmd.Parameters.AddWithValue("@to", to.Value.ToString("O"));
        }
        if (!string.IsNullOrEmpty(userSid))
        {
            conditions.Add("user_sid = @sid");
            cmd.Parameters.AddWithValue("@sid", userSid);
        }
        if (!string.IsNullOrEmpty(operationType))
        {
            conditions.Add("operation_type = @opType");
            cmd.Parameters.AddWithValue("@opType", operationType);
        }

        var whereClause = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        var sql = $"""
            SELECT id, timestamp, user_sid, operation_type, target, result, details
            FROM AuditLog
            {whereClause}
            ORDER BY timestamp DESC
            LIMIT @limit OFFSET @offset
            """;

        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        cmd.Connection = connection;
        cmd.CommandText = sql;

        var results = new List<AuditLogEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new AuditLogEntry
            {
                Id = reader.GetInt64(0),
                Timestamp = DateTime.Parse(reader.GetString(1)),
                UserSid = reader.GetString(2),
                OperationType = reader.GetString(3),
                Target = reader.GetString(4),
                Result = reader.GetString(5),
                Details = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }

        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default) => _writeQueue.FlushAsync(ct);

    /// <summary>批量插入处理函数</summary>
    private async Task InsertBatchAsync(IReadOnlyList<AuditLogEntry> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        const string baseSql = """
            INSERT INTO AuditLog
            (timestamp, user_sid, operation_type, target, result, details)
            VALUES
            """;

        var valueClauses = new List<string>(batch.Count);
        using var cmd = connection.CreateCommand();

        for (var i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            valueClauses.Add($"(@t{i}, @sid{i}, @ot{i}, @tg{i}, @r{i}, @d{i})");

            cmd.Parameters.AddWithValue($"@t{i}", entry.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue($"@sid{i}", entry.UserSid);
            cmd.Parameters.AddWithValue($"@ot{i}", entry.OperationType);
            cmd.Parameters.AddWithValue($"@tg{i}", entry.Target);
            cmd.Parameters.AddWithValue($"@r{i}", entry.Result);
            cmd.Parameters.AddWithValue($"@d{i}", (object?)entry.Details ?? DBNull.Value);
        }

        cmd.CommandText = baseSql + string.Join(", ", valueClauses);

        try
        {
            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("[审计日志] 批量插入完成: {Count} 条", batch.Count);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeQueue.Dispose();
    }
}
