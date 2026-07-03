using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Data;

/// <summary>
/// LocalTombstoneRepository 单元测试。
///
/// 覆盖范围：
/// - Insert 和按 identity_key 查询（基本往返）
/// - INSERT OR IGNORE 冲突策略（主键重复静默跳过）
/// - DeleteByOperationIdAsync（先 Flush 再 DELETE，确保数据落盘后删除）
/// - CleanupExpiredAsync（FRN 可用时 30 天过期 / FRN 不可用时强制 3 天过期）
/// - GetByFilePathAsync（降级路径匹配）
/// - GetAllAsync（全量加载）
/// - InsertBatch 批量插入
/// </summary>
public class LocalTombstoneRepositoryTests : IAsyncDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseInitializer _initializer;
    private readonly LocalTombstoneRepository _repository;
    private readonly ILogger _logger;

    public LocalTombstoneRepositoryTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"test_lt_{Guid.NewGuid():N}.db");
        _logger = NullLoggerFactory.Instance.CreateLogger("test");

        _initializer = new DatabaseInitializer(_tempDbPath, _logger);
        _initializer.InitializeAsync().GetAwaiter().GetResult();

        _repository = new LocalTombstoneRepository(_initializer, _logger);
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

    private static LocalTombstone CreateTombstone(
        string identityKey = "vol-001:12345",
        string operationId = "op-001",
        string filePath = @"\\?\C:\test\deleted.log",
        DateTime? deletedAt = null,
        DateTime? lastWriteTime = null)
    {
        return new LocalTombstone
        {
            FilePath = filePath,
            FileIdentityKey = identityKey,
            OperationId = operationId,
            OriginalSize = 2048,
            OriginalLastWriteTime = lastWriteTime ?? new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            DeletedAt = deletedAt ?? DateTime.UtcNow,
        };
    }

    [Fact]
    public async Task Insert_And_GetByIdentityKey_ShouldRoundtrip()
    {
        // Arrange
        var tombstone = CreateTombstone();

        // Act
        _repository.Insert(tombstone);
        await _repository.FlushAsync();

        var retrieved = await _repository.GetByIdentityKeyAsync("vol-001:12345");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("vol-001:12345", retrieved!.FileIdentityKey);
        Assert.Equal(@"\\?\C:\test\deleted.log", retrieved.FilePath);
        Assert.Equal("op-001", retrieved.OperationId);
        Assert.Equal(2048, retrieved.OriginalSize);
    }

    [Fact]
    public async Task Insert_DuplicateIdentityKey_ShouldIgnoreSilently()
    {
        // Arrange: 两次插入相同主键
        var first = CreateTombstone(identityKey: "dup-key", operationId: "op-first");
        var second = CreateTombstone(identityKey: "dup-key", operationId: "op-second");

        // Act
        _repository.Insert(first);
        _repository.Insert(second);
        await _repository.FlushAsync();

        var retrieved = await _repository.GetByIdentityKeyAsync("dup-key");

        // Assert: INSERT OR IGNORE 策略 → 第一条保留，第二条被静默跳过
        Assert.NotNull(retrieved);
        Assert.Equal("op-first", retrieved!.OperationId);
    }

    [Fact]
    public async Task DeleteByOperationId_ShouldFlushThenDelete()
    {
        // Arrange: 同一批次插入多条墓碑
        _repository.Insert(CreateTombstone("key-a", "op-del"));
        _repository.Insert(CreateTombstone("key-b", "op-del"));
        _repository.Insert(CreateTombstone("key-c", "op-keep"));
        await _repository.FlushAsync();

        // Act: 按 operation_id 删除
        var deletedCount = await _repository.DeleteByOperationIdAsync("op-del");

        // Assert
        Assert.Equal(2, deletedCount);

        // 已删除的应查不到
        Assert.Null(await _repository.GetByIdentityKeyAsync("key-a"));
        Assert.Null(await _repository.GetByIdentityKeyAsync("key-b"));

        // 其他批次的应保留
        Assert.NotNull(await _repository.GetByIdentityKeyAsync("key-c"));
    }

    [Fact]
    public async Task DeleteByOperationId_FlushesBeforeDelete()
    {
        // Arrange: 插入但未 Flush → DeleteByOperationId 应先 Flush 再删除
        _repository.Insert(CreateTombstone("key-flush-del", "op-flush"));
        // 不显式调用 FlushAsync

        // Act: DeleteByOperationId 内部应自动 Flush
        var deletedCount = await _repository.DeleteByOperationIdAsync("op-flush");

        // Assert: 即使未显式 Flush，也应正确删除
        Assert.Equal(1, deletedCount);
        Assert.Null(await _repository.GetByIdentityKeyAsync("key-flush-del"));
    }

    [Fact]
    public async Task CleanupExpired_FrnAvailable_ShouldUseRetentionDays()
    {
        // Arrange: 创建 35 天前的墓碑（超过默认 30 天保留期）
        var old = CreateTombstone(
            identityKey: "old-key",
            deletedAt: DateTime.UtcNow.AddDays(-35));
        var recent = CreateTombstone(
            identityKey: "recent-key",
            deletedAt: DateTime.UtcNow.AddDays(-5));

        _repository.Insert(old);
        _repository.Insert(recent);
        await _repository.FlushAsync();

        // Act: FRN 可用 → 保留 30 天
        var deletedCount = await _repository.CleanupExpiredAsync(frnAvailable: true, retentionDays: 30);

        // Assert
        Assert.Equal(1, deletedCount);
        Assert.Null(await _repository.GetByIdentityKeyAsync("old-key"));
        Assert.NotNull(await _repository.GetByIdentityKeyAsync("recent-key"));
    }

    [Fact]
    public async Task CleanupExpired_FrnUnavailable_ShouldForceThreeDayExpiry()
    {
        // Arrange: FRN 不可用时强制 3 天过期
        var old = CreateTombstone(
            identityKey: "old-fp-key",
            deletedAt: DateTime.UtcNow.AddDays(-5));
        var recent = CreateTombstone(
            identityKey: "recent-fp-key",
            deletedAt: DateTime.UtcNow.AddDays(-1));

        _repository.Insert(old);
        _repository.Insert(recent);
        await _repository.FlushAsync();

        // Act: FRN 不可用 → 忽略 retentionDays 参数，强制 3 天
        var deletedCount = await _repository.CleanupExpiredAsync(
            frnAvailable: false, retentionDays: 999); // 传 999 天，但应被忽略

        // Assert: 5 天前的应被清理，1 天前的保留
        Assert.Equal(1, deletedCount);
        Assert.Null(await _repository.GetByIdentityKeyAsync("old-fp-key"));
        Assert.NotNull(await _repository.GetByIdentityKeyAsync("recent-fp-key"));
    }

    [Fact]
    public async Task GetByFilePath_ShouldReturnLatestTombstone()
    {
        // Arrange: 同一路径被多次删除
        var older = CreateTombstone(
            identityKey: "key-older",
            filePath: @"\\?\C:\test\repeated.log",
            deletedAt: DateTime.UtcNow.AddHours(-2));
        var newer = CreateTombstone(
            identityKey: "key-newer",
            filePath: @"\\?\C:\test\repeated.log",
            deletedAt: DateTime.UtcNow);

        _repository.Insert(older);
        _repository.Insert(newer);
        await _repository.FlushAsync();

        // Act
        var retrieved = await _repository.GetByFilePathAsync(@"\\?\C:\test\repeated.log");

        // Assert: ORDER BY deleted_at DESC LIMIT 1 → 返回最新的
        Assert.NotNull(retrieved);
        Assert.Equal("key-newer", retrieved!.FileIdentityKey);
    }

    [Fact]
    public async Task GetByFilePath_NonExistentPath_ShouldReturnNull()
    {
        // Act
        var result = await _repository.GetByFilePathAsync(@"\\?\C:\nonexistent\file.log");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAll_ShouldReturnAllTombstones()
    {
        // Arrange
        _repository.Insert(CreateTombstone("all-a"));
        _repository.Insert(CreateTombstone("all-b"));
        _repository.Insert(CreateTombstone("all-c"));
        await _repository.FlushAsync();

        // Act
        var all = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(3, all.Count);
        Assert.Contains(all, t => t.FileIdentityKey == "all-a");
        Assert.Contains(all, t => t.FileIdentityKey == "all-b");
        Assert.Contains(all, t => t.FileIdentityKey == "all-c");
    }

    [Fact]
    public async Task GetAll_FlushesPendingWritesFirst()
    {
        // Arrange: 插入但未显式 Flush
        _repository.Insert(CreateTombstone("all-pending"));
        // 不调用 FlushAsync

        // Act: GetAllAsync 内部应先 Flush
        var all = await _repository.GetAllAsync();

        // Assert
        Assert.NotEmpty(all);
        Assert.Contains(all, t => t.FileIdentityKey == "all-pending");
    }

    [Fact]
    public async Task InsertBatch_MultipleTombstones_ShouldPersistAll()
    {
        // Arrange
        var batch = new[]
        {
            CreateTombstone("batch-a"),
            CreateTombstone("batch-b"),
            CreateTombstone("batch-c"),
            CreateTombstone("batch-d"),
            CreateTombstone("batch-e"),
        };

        // Act
        _repository.InsertBatch(batch);
        await _repository.FlushAsync();

        var all = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(5, all.Count);
    }

    [Fact]
    public async Task GetByIdentityKey_NonExistent_ShouldReturnNull()
    {
        // Act
        var result = await _repository.GetByIdentityKeyAsync("no-such-key");

        // Assert
        Assert.Null(result);
    }
}
