using System.IO.Compression;
using System.Text;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 删除记录仓储实现。
///
/// 负责 DeletionRecord 表的读写操作。
/// decision_snapshot 字段在 JSON 长度超过 4KB 时自动应用 GZip 压缩 + Base64 编码存储。
/// 压缩后的字符串以 "GZ:" 前缀标记，读取时自动解压缩还原。
/// </summary>
public sealed class DeletionRecordRepository : IDeletionRecordRepository, IDisposable
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IBatchWriteQueue<DeletionRecord> _writeQueue;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>JSON 压缩阈值（字节），超过此值自动 GZip + Base64 编码</summary>
    private const int CompressThresholdBytes = 4096;

    /// <summary>压缩标记前缀，存储在 decision_snapshot 字段开头用于识别</summary>
    private const string CompressionPrefix = "GZ:";

    public DeletionRecordRepository(
        ISqliteConnectionFactory connectionFactory,
        ILogger logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _writeQueue = new BatchWriteQueue<DeletionRecord>(
            flushHandler: InsertBatchAsync,
            logger: logger,
            name: "DeletionRecord",
            batchSize: 200,
            flushIntervalMs: 500);
    }

    /// <inheritdoc/>
    public void Insert(DeletionRecord record)
    {
        // 在入队前检查并压缩 decision_snapshot
        if (record.DecisionSnapshotJson.Length > CompressThresholdBytes)
        {
            record = record with { DecisionSnapshotJson = CompressJson(record.DecisionSnapshotJson) };
        }

        _writeQueue.Enqueue(record);
    }

    /// <inheritdoc/>
    public void InsertBatch(IEnumerable<DeletionRecord> records)
    {
        // 对每条记录检查并压缩
        var processed = records.Select(r =>
            r.DecisionSnapshotJson.Length > CompressThresholdBytes
                ? r with { DecisionSnapshotJson = CompressJson(r.DecisionSnapshotJson) }
                : r);

        _writeQueue.EnqueueBatch(processed);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeletionRecord>> GetByOperationIdAsync(
        string operationId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, operation_id, file_path, file_hash, file_size,
                   deletion_method, decision_snapshot, created_at
            FROM DeletionRecord
            WHERE operation_id = @opId
            ORDER BY id
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@opId", operationId);

        var results = new List<DeletionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapFromReader(reader));
        }

        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeletionRecord>> GetHistoryAsync(
        DateTime? from = null, DateTime? to = null,
        int limit = 100, int offset = 0,
        CancellationToken ct = default)
    {
        // 动态构建查询条件
        var conditions = new List<string>();
        using var cmd = new SqliteCommand();

        if (from.HasValue)
        {
            conditions.Add("created_at >= @from");
            cmd.Parameters.AddWithValue("@from", from.Value.ToString("O"));
        }
        if (to.HasValue)
        {
            conditions.Add("created_at <= @to");
            cmd.Parameters.AddWithValue("@to", to.Value.ToString("O"));
        }

        var whereClause = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        var sql = $"""
            SELECT id, operation_id, file_path, file_hash, file_size,
                   deletion_method, decision_snapshot, created_at
            FROM DeletionRecord
            {whereClause}
            ORDER BY created_at DESC
            LIMIT @limit OFFSET @offset
            """;

        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        cmd.Connection = connection;

        // 将参数合并到连接的命令中
        cmd.CommandText = sql;

        var results = new List<DeletionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(MapFromReader(reader));
        }

        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeletionRecord>> GetByFileHashAsync(
        string fileHash, CancellationToken ct = default)
    {
        // 参数校验：哈希为空快速返回空列表
        if (string.IsNullOrEmpty(fileHash))
        {
            _logger.LogWarning("[删除记录] GetByFileHashAsync 收到空哈希");
            return Array.Empty<DeletionRecord>();
        }

        const string sql = """
            SELECT id, operation_id, file_path, file_hash, file_size,
                   deletion_method, decision_snapshot, created_at
            FROM DeletionRecord
            WHERE file_hash = @FileHash COLLATE NOCASE
            ORDER BY created_at DESC
            """;

        try
        {
            await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@FileHash", fileHash);

            var results = new List<DeletionRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                results.Add(MapFromReader(reader));
            }

            _logger.LogDebug("[删除记录] 按哈希查询返回 {Count} 条记录 (Hash={Hash})",
                results.Count, fileHash[..Math.Min(16, fileHash.Length)]);
            return results.AsReadOnly();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[删除记录] 按哈希查询异常 (Hash={Hash})", fileHash);
            return Array.Empty<DeletionRecord>();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OperationBatchInfo>> GetBatchSummariesAsync(
        int limit = 50, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                operation_id,
                MIN(created_at) AS first_created,
                COUNT(*) AS file_count,
                SUM(file_size) AS total_size,
                MIN(deletion_method) AS method
            FROM DeletionRecord
            GROUP BY operation_id
            ORDER BY first_created DESC
            LIMIT @limit
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<OperationBatchInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new OperationBatchInfo
            {
                OperationId = reader.GetString(0),
                ExecutedAt = DateTime.Parse(reader.GetString(1)),
                FileCount = reader.GetInt32(2),
                TotalSizeBytes = reader.GetInt64(3),
                Method = ParseDeleteMethod(reader.GetString(4)),
                IsRollbackable = true, // 由调用方根据实际情况更新
            });
        }

        return results.AsReadOnly();
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default) => _writeQueue.FlushAsync(ct);

    /// <summary>批量插入处理函数</summary>
    private async Task InsertBatchAsync(IReadOnlyList<DeletionRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        const string baseSql = """
            INSERT INTO DeletionRecord
            (operation_id, file_path, file_hash, file_size, deletion_method, decision_snapshot, created_at)
            VALUES
            """;

        var valueClauses = new List<string>(batch.Count);
        using var cmd = connection.CreateCommand();

        for (var i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            valueClauses.Add($"(@op{i}, @p{i}, @h{i}, @s{i}, @dm{i}, @ds{i}, @ca{i})");

            cmd.Parameters.AddWithValue($"@op{i}", entry.OperationId);
            cmd.Parameters.AddWithValue($"@p{i}", entry.FilePath);
            cmd.Parameters.AddWithValue($"@h{i}", entry.FileHash);
            cmd.Parameters.AddWithValue($"@s{i}", entry.FileSize);
            cmd.Parameters.AddWithValue($"@dm{i}", entry.DeletionMethod);
            cmd.Parameters.AddWithValue($"@ds{i}", entry.DecisionSnapshotJson);
            cmd.Parameters.AddWithValue($"@ca{i}", entry.CreatedAt.ToString("O"));
        }

        cmd.CommandText = baseSql + string.Join(", ", valueClauses);

        try
        {
            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("[删除记录] 批量插入完成: {Count} 条", batch.Count);
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>GZip 压缩 JSON 字符串并以 Base64 + 前缀编码</summary>
    private static string CompressJson(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        return CompressionPrefix + Convert.ToBase64String(output.ToArray());
    }

    /// <summary>解压缩：若字符串以 GZ: 开头则 Base64 解码 + GZip 解压，否则原样返回</summary>
    private static string DecompressJson(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(CompressionPrefix))
            return stored ?? "";

        try
        {
            var compressedBytes = Convert.FromBase64String(stored[CompressionPrefix.Length..]);

            using var input = new MemoryStream(compressedBytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);

            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception)
        {
            // 解压缩失败时返回原始存储值（可能是未压缩的 JSON）
            return stored;
        }
    }

    /// <summary>从 SqliteDataReader 当前行映射到 DeletionRecord 实体</summary>
    private static DeletionRecord MapFromReader(SqliteDataReader reader)
    {
        return new DeletionRecord
        {
            Id = reader.GetInt64(0),
            OperationId = reader.GetString(1),
            FilePath = reader.GetString(2),
            FileHash = reader.GetString(3),
            FileSize = reader.GetInt64(4),
            DeletionMethod = reader.GetString(5),
            DecisionSnapshotJson = DecompressJson(reader.GetString(6)),
            CreatedAt = DateTime.Parse(reader.GetString(7)),
        };
    }

    /// <summary>将数据库中的 deletion_method 字符串转为 DeleteMethod 枚举</summary>
    private static DeleteMethod ParseDeleteMethod(string method)
    {
        return method switch
        {
            "RECYCLE_BIN" => DeleteMethod.RecycleBin,
            "PERMANENT" => DeleteMethod.Permanent,
            "QUARANTINE" => DeleteMethod.Permanent, // 隔离区最终仍为永久删除
            _ => DeleteMethod.RecycleBin,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeQueue.Dispose();
    }
}
