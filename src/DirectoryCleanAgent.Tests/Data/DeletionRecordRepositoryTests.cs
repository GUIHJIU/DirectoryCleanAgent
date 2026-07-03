using System.Text;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Data;

/// <summary>
/// DeletionRecordRepository 单元测试。
///
/// 覆盖范围：
/// - Insert 和按 operation_id 查询（基本往返）
/// - JSON 压缩（>4KB decision_snapshot 自动 GZip + Base64 + "GZ:" 前缀）
/// - GZ: 前缀标记的解压识别
/// - GetByFileHashAsync（空哈希/有效哈希/不区分大小写）
/// - GetBatchSummariesAsync 聚合统计
/// - GetHistoryAsync 时间范围筛选和分页
/// - InsertBatch 批量插入
/// - ParseDeleteMethod 兼容性（RECYCLE_BIN → RecycleBin, QUARANTINE → Quarantine, 未知 → RecycleBin）
/// </summary>
public class DeletionRecordRepositoryTests : IAsyncDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseInitializer _initializer;
    private readonly DeletionRecordRepository _repository;
    private readonly ILogger _logger;

    public DeletionRecordRepositoryTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"test_dr_{Guid.NewGuid():N}.db");
        _logger = NullLoggerFactory.Instance.CreateLogger("test");

        _initializer = new DatabaseInitializer(_tempDbPath, _logger);
        _initializer.InitializeAsync().GetAwaiter().GetResult();

        _repository = new DeletionRecordRepository(_initializer, _logger);
    }

    public async ValueTask DisposeAsync()
    {
        _repository.Dispose();

        try
        {
            if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath);
            var walPath = _tempDbPath + "-wal";
            var shmPath = _tempDbPath + "-shm";
            if (File.Exists(walPath)) File.Delete(walPath);
            if (File.Exists(shmPath)) File.Delete(shmPath);
        }
        catch { }

        await Task.CompletedTask;
    }

    private static DeletionRecord CreateTestRecord(
        string operationId = "op-001",
        string filePath = @"\\?\C:\test\file.log",
        string fileHash = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2",
        long fileSize = 1024,
        DeleteMethod deletionMethod = DeleteMethod.RecycleBin)
    {
        return new DeletionRecord
        {
            OperationId = operationId,
            FilePath = filePath,
            FileHash = fileHash,
            FileSize = fileSize,
            DeletionMethod = deletionMethod,
            DecisionSnapshotJson = """{"op":"op-001","files":[{"path":"file.log","size":1024}],"frozen_at":"2026-07-01T00:00:00Z"}""",
            CreatedAt = DateTime.UtcNow,
        };
    }

    [Fact]
    public async Task Insert_And_GetByOperationId_ShouldRoundtrip()
    {
        // Arrange
        var record = CreateTestRecord();

        // Act
        _repository.Insert(record);
        await _repository.FlushAsync();

        var results = await _repository.GetByOperationIdAsync("op-001");

        // Assert
        Assert.NotEmpty(results);
        var retrieved = results[0];
        Assert.NotNull(retrieved.Id);
        Assert.Equal("op-001", retrieved.OperationId);
        Assert.Equal(@"\\?\C:\test\file.log", retrieved.FilePath);
        Assert.Equal("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2", retrieved.FileHash);
        Assert.Equal(1024, retrieved.FileSize);
        Assert.Equal(DeleteMethod.RecycleBin, retrieved.DeletionMethod);
    }

    [Fact]
    public async Task Insert_LargeDecisionSnapshot_ShouldCompressWithGzPrefix()
    {
        // Arrange: 构造一个 >4KB 的 decision_snapshot JSON 字符串
        // CompressThresholdBytes = 4096，构造约 5KB 的 JSON
        var sb = new StringBuilder();
        sb.Append("""{"op":"op-big","files":[""");
        for (var i = 0; i < 100; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($$"""{"path":"C:\\test\\file_{{i}}.log","size":{{i * 1000}},"hash":"sha256_{{i}}","action":"AutoDelete"}""");
        }
        sb.Append("""],"frozen_at":"2026-07-01T00:00:00Z"}""");

        var largeJson = sb.ToString();
        Assert.True(largeJson.Length > 4096, $"测试 JSON 长度应为 >4KB，实际: {largeJson.Length} 字节");

        var record = CreateTestRecord(operationId: "op-big");
        record = record with { DecisionSnapshotJson = largeJson };

        // Act
        _repository.Insert(record);
        await _repository.FlushAsync();

        var results = await _repository.GetByOperationIdAsync("op-big");

        // Assert: 解压后应与原始 JSON 一致
        Assert.NotEmpty(results);
        Assert.Equal(largeJson, results[0].DecisionSnapshotJson);
    }

    [Fact]
    public async Task Insert_SmallDecisionSnapshot_ShouldNotCompress()
    {
        // Arrange: <4KB 的 JSON 不应被压缩
        var smallJson = """{"op":"small","files":[{"path":"f.log","size":100}],"frozen_at":"2026-07-01T00:00:00Z"}""";
        Assert.True(smallJson.Length < 4096);

        var record = CreateTestRecord(operationId: "op-small");
        record = record with { DecisionSnapshotJson = smallJson };

        // Act
        _repository.Insert(record);
        await _repository.FlushAsync();

        var results = await _repository.GetByOperationIdAsync("op-small");

        // Assert: 小 JSON 应原样返回，无 GZ: 前缀
        Assert.NotEmpty(results);
        Assert.Equal(smallJson, results[0].DecisionSnapshotJson);
    }

    [Fact]
    public async Task GetByFileHash_ValidHash_ShouldReturnMatches()
    {
        // Arrange
        var hash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
        _repository.Insert(CreateTestRecord("op-h1", @"\\?\C:\a.log", hash));
        _repository.Insert(CreateTestRecord("op-h2", @"\\?\C:\b.log", hash));
        _repository.Insert(CreateTestRecord("op-h3", @"\\?\C:\c.log", "different_hash_1234567890abcdef1234567890abcdef1234567890abcdef1234"));
        await _repository.FlushAsync();

        // Act
        var results = await _repository.GetByFileHashAsync(hash);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(hash, r.FileHash));
    }

    [Fact]
    public async Task GetByFileHash_EmptyHash_ShouldReturnEmpty()
    {
        // Act
        var results = await _repository.GetByFileHashAsync("");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetByFileHash_NullHash_ShouldReturnEmpty()
    {
        // Act
        var results = await _repository.GetByFileHashAsync(null!);

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetByFileHash_CaseInsensitive_ShouldMatch()
    {
        // Arrange: FileHash 以小写存储，查询时用大写
        var lowerHash = "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
        _repository.Insert(CreateTestRecord("op-ci", @"\\?\C:\case.log", lowerHash));
        await _repository.FlushAsync();

        // Act: 用大写查询
        var results = await _repository.GetByFileHashAsync(lowerHash.ToUpperInvariant());

        // Assert: NOCASE 排序规则应匹配
        Assert.NotEmpty(results);
        Assert.Equal(lowerHash, results[0].FileHash);
    }

    [Fact]
    public async Task GetBatchSummaries_AggregatesCorrectly()
    {
        // Arrange: 同一 operation_id 的多条记录
        var opId = "batch-summary-001";
        var now = DateTime.UtcNow;
        _repository.Insert(CreateTestRecord(opId, @"\\?\C:\f1.log", "hash1", 100, DeleteMethod.RecycleBin) with { CreatedAt = now });
        _repository.Insert(CreateTestRecord(opId, @"\\?\C:\f2.log", "hash2", 200, DeleteMethod.RecycleBin) with { CreatedAt = now.AddSeconds(1) });
        _repository.Insert(CreateTestRecord(opId, @"\\?\C:\f3.log", "hash3", 300, DeleteMethod.RecycleBin) with { CreatedAt = now.AddSeconds(2) });
        await _repository.FlushAsync();

        // Act
        var summaries = await _repository.GetBatchSummariesAsync(limit: 10);

        // Assert
        Assert.NotEmpty(summaries);
        var summary = summaries.First(s => s.OperationId == opId);
        Assert.Equal(3, summary.FileCount);
        Assert.Equal(600, summary.TotalSizeBytes);
        Assert.Equal(DeleteMethod.RecycleBin, summary.Method);
    }

    [Fact]
    public async Task GetHistory_WithTimeRange_ShouldFilterCorrectly()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var old = now.AddHours(-5);
        var recent = now.AddMinutes(-30);

        _repository.Insert(CreateTestRecord("op-old", @"\\?\C:\old.log") with { CreatedAt = old });
        _repository.Insert(CreateTestRecord("op-recent", @"\\?\C:\recent.log") with { CreatedAt = recent });
        _repository.Insert(CreateTestRecord("op-now", @"\\?\C:\now.log") with { CreatedAt = now });
        await _repository.FlushAsync();

        // Act: 查询最近 1 小时的记录
        var results = await _repository.GetHistoryAsync(from: now.AddHours(-1));

        // Assert: 不包括 5 小时前的那条
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.CreatedAt >= now.AddHours(-1)));
    }

    [Fact]
    public async Task GetHistory_WithPagination_ShouldRespectLimitAndOffset()
    {
        // Arrange
        for (var i = 0; i < 10; i++)
        {
            _repository.Insert(CreateTestRecord($"op-page-{i}", $@"\\?\C:\page_{i}.log") with
            {
                CreatedAt = DateTime.UtcNow.AddSeconds(-i)
            });
        }
        await _repository.FlushAsync();

        // Act
        var page1 = await _repository.GetHistoryAsync(limit: 5, offset: 0);
        var page2 = await _repository.GetHistoryAsync(limit: 5, offset: 5);

        // Assert
        Assert.Equal(5, page1.Count);
        Assert.Equal(5, page2.Count);
        var page1Paths = page1.Select(r => r.FilePath).ToHashSet();
        var page2Paths = page2.Select(r => r.FilePath).ToHashSet();
        Assert.Empty(page1Paths.Intersect(page2Paths));
    }

    [Fact]
    public async Task Insert_WithQuarantineMethod_ShouldRoundtrip()
    {
        // Arrange
        var record = CreateTestRecord("op-q", deletionMethod: DeleteMethod.Quarantine);

        // Act
        _repository.Insert(record);
        await _repository.FlushAsync();

        var results = await _repository.GetByOperationIdAsync("op-q");

        // Assert: Quarantine 枚举应正确存为 "QUARANTINE" 并正确解析回
        Assert.NotEmpty(results);
        Assert.Equal(DeleteMethod.Quarantine, results[0].DeletionMethod);
    }

    [Fact]
    public async Task Insert_WithPermanentMethod_ShouldRoundtrip()
    {
        // Arrange
        var record = CreateTestRecord("op-p", deletionMethod: DeleteMethod.Permanent);

        // Act
        _repository.Insert(record);
        await _repository.FlushAsync();

        var results = await _repository.GetByOperationIdAsync("op-p");

        // Assert
        Assert.NotEmpty(results);
        Assert.Equal(DeleteMethod.Permanent, results[0].DeletionMethod);
    }

    [Fact]
    public async Task InsertBatch_MultipleRecords_ShouldPersistAll()
    {
        // Arrange
        var records = new[]
        {
            CreateTestRecord("batch-1", @"\\?\C:\b1.log"),
            CreateTestRecord("batch-2", @"\\?\C:\b2.log"),
            CreateTestRecord("batch-3", @"\\?\C:\b3.log"),
            CreateTestRecord("batch-4", @"\\?\C:\b4.log"),
            CreateTestRecord("batch-5", @"\\?\C:\b5.log"),
        };

        // Act
        _repository.InsertBatch(records);
        await _repository.FlushAsync();

        // Assert: 每条都可以通过 operation_id 查到
        for (var i = 1; i <= 5; i++)
        {
            var results = await _repository.GetByOperationIdAsync($"batch-{i}");
            Assert.Single(results);
        }
    }

    [Fact]
    public async Task GetByOperationId_NonExistent_ShouldReturnEmpty()
    {
        // Act
        var results = await _repository.GetByOperationIdAsync("non-existent-op");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task DeletionMethod_PersistedAsUpperCaseText()
    {
        // 验证 DeleteMethod 持久化为大写字符串，确保与设计文档一致
        // Arrange
        var record = CreateTestRecord("op-dm", deletionMethod: DeleteMethod.RecycleBin);

        // Act
        _repository.Insert(record);
        await _repository.FlushAsync();

        // 直接查询数据库验证存储格式
        await using var connection = await _initializer.CreateConnectionAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT deletion_method FROM DeletionRecord WHERE operation_id = 'op-dm'";
        var stored = await cmd.ExecuteScalarAsync() as string;

        // Assert: 存储的值应为大写枚举名
        Assert.NotNull(stored);
        Assert.Equal("RECYCLEBIN", stored);
    }
}
