using DirectoryCleanAgent.Core.DTOs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 本地墓碑仓储实现。
///
/// 墓碑记录已删除文件的物理标识，防止后续扫描中重复列出。
/// 使用 INSERT OR IGNORE 策略：若 file_identity_key 已存在（同一物理文件被再次删除），
/// 静默跳过而非报错。
///
/// 回滚时通过 operation_id 批量清除关联墓碑。
/// 定期清理过期墓碑，路径复用场景下自动移除失效记录。
/// </summary>
public sealed class LocalTombstoneRepository : ILocalTombstoneRepository, IDisposable
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IBatchWriteQueue<LocalTombstone> _writeQueue;
    private readonly ILogger _logger;
    private bool _disposed;

    public LocalTombstoneRepository(
        ISqliteConnectionFactory connectionFactory,
        ILogger logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _writeQueue = new BatchWriteQueue<LocalTombstone>(
            flushHandler: InsertBatchAsync,
            logger: logger,
            name: "LocalTombstone",
            batchSize: 200,
            flushIntervalMs: 500);
    }

    /// <inheritdoc/>
    public void Insert(LocalTombstone tombstone) => _writeQueue.Enqueue(tombstone);

    /// <inheritdoc/>
    public void InsertBatch(IEnumerable<LocalTombstone> tombstones) => _writeQueue.EnqueueBatch(tombstones);

    /// <inheritdoc/>
    public async Task<LocalTombstone?> GetByIdentityKeyAsync(string identityKey, CancellationToken ct = default)
    {
        const string sql = """
            SELECT file_path, file_identity_key, operation_id, original_size,
                   original_last_write_time, deleted_at
            FROM LocalTombstone
            WHERE file_identity_key = @key
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@key", identityKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return MapFromReader(reader);
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<LocalTombstone?> GetByFilePathAsync(string filePath, CancellationToken ct = default)
    {
        const string sql = """
            SELECT file_path, file_identity_key, operation_id, original_size,
                   original_last_write_time, deleted_at
            FROM LocalTombstone
            WHERE file_path = @path
            ORDER BY deleted_at DESC
            LIMIT 1
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
    public async Task<int> DeleteByOperationIdAsync(string operationId, CancellationToken ct = default)
    {
        // 先刷新队列，确保所有墓碑已落盘
        await _writeQueue.FlushAsync(ct).ConfigureAwait(false);

        const string sql = "DELETE FROM LocalTombstone WHERE operation_id = @opId";

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@opId", operationId);

        var deletedCount = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("[墓碑] 按操作 ID 批量删除: OperationId={OpId}, 删除 {Count} 条",
            operationId, deletedCount);

        return deletedCount;
    }

    /// <inheritdoc/>
    public async Task<int> CleanupExpiredAsync(
        bool frnAvailable, int retentionDays = 30, CancellationToken ct = default)
    {
        // 先刷新队列
        await _writeQueue.FlushAsync(ct).ConfigureAwait(false);

        // FRN 不可用时强制 3 天过期（设计文档 5.1 节）
        var effectiveRetentionDays = frnAvailable ? retentionDays : 3;
        var cutoff = DateTime.UtcNow.AddDays(-effectiveRetentionDays);

        const string sql = "DELETE FROM LocalTombstone WHERE deleted_at < @cutoff";

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

        var deletedCount = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "[墓碑] 过期清理完成: FRN={FrnAvailable}, 保留天数={RetentionDays}, "
            + "截止时间={Cutoff:O}, 删除 {Count} 条",
            frnAvailable, effectiveRetentionDays, cutoff, deletedCount);

        return deletedCount;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<LocalTombstone>> GetAllAsync(CancellationToken ct = default)
    {
        // 先刷新队列，确保所有待写入的墓碑已落盘
        await _writeQueue.FlushAsync(ct).ConfigureAwait(false);

        const string sql = """
            SELECT file_path, file_identity_key, operation_id, original_size,
                   original_last_write_time, deleted_at
            FROM LocalTombstone
            ORDER BY deleted_at DESC
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        var results = new List<LocalTombstone>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapFromReader(reader));
        }

        _logger.LogInformation("[墓碑] 全量加载完成: {Count} 条", results.Count);
        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default) => _writeQueue.FlushAsync(ct);

    /// <summary>批量插入处理函数：使用 INSERT OR IGNORE 避免主键冲突报错</summary>
    private async Task InsertBatchAsync(IReadOnlyList<LocalTombstone> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        const string baseSql = """
            INSERT OR IGNORE INTO LocalTombstone
            (file_path, file_identity_key, operation_id, original_size, original_last_write_time, deleted_at)
            VALUES
            """;

        var valueClauses = new List<string>(batch.Count);
        using var cmd = connection.CreateCommand();

        for (var i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            valueClauses.Add($"(@p{i}, @ik{i}, @op{i}, @os{i}, @lt{i}, @da{i})");

            cmd.Parameters.AddWithValue($"@p{i}", entry.FilePath);
            cmd.Parameters.AddWithValue($"@ik{i}", entry.FileIdentityKey);
            cmd.Parameters.AddWithValue($"@op{i}", entry.OperationId);
            cmd.Parameters.AddWithValue($"@os{i}", entry.OriginalSize);
            cmd.Parameters.AddWithValue($"@lt{i}", entry.OriginalLastWriteTime.ToString("O"));
            cmd.Parameters.AddWithValue($"@da{i}", entry.DeletedAt.ToString("O"));
        }

        cmd.CommandText = baseSql + string.Join(", ", valueClauses);

        try
        {
            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("[墓碑] 批量插入完成: {Count} 条, 实际写入 {Affected} 条",
                batch.Count, affected);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>从 SqliteDataReader 当前行映射到 LocalTombstone 实体</summary>
    private static LocalTombstone MapFromReader(SqliteDataReader reader)
    {
        return new LocalTombstone
        {
            FilePath = reader.GetString(0),
            FileIdentityKey = reader.GetString(1),
            OperationId = reader.GetString(2),
            OriginalSize = reader.GetInt64(3),
            OriginalLastWriteTime = DateTime.Parse(reader.GetString(4)),
            DeletedAt = DateTime.Parse(reader.GetString(5)),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeQueue.Dispose();
    }
}
