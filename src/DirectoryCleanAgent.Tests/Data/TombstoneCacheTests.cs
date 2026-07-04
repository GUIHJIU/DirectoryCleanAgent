using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;
using Microsoft.Extensions.Logging;
using Moq;

namespace DirectoryCleanAgent.Tests.Data;

/// <summary>
/// TombstoneCache 单元测试。
///
/// 覆盖范围：
/// - LoadAsync 从仓储全量加载
/// - IsTombstoned: FRN 键匹配 / 指纹键匹配 / 未命中 / 双 null
/// - Add / AddBatch 写入缓存
/// - RemoveByOperationId 精确清除
/// - RemoveWhere 条件清除
/// - Count 统计
/// - 并发访问安全性
/// </summary>
public class TombstoneCacheTests
{
    private static LocalTombstone CreateTombstone(
        string identityKey, string operationId = "op-001")
    {
        return new LocalTombstone
        {
            FilePath = $@"\\?\C:\test\{identityKey}.tmp",
            FileIdentityKey = identityKey,
            OperationId = operationId,
            OriginalSize = 1024,
            OriginalLastWriteTime = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc),
            DeletedAt = DateTime.UtcNow
        };
    }

    [Fact]
    public async Task LoadAsync_PopulatesCacheFromRepository()
    {
        // Arrange
        var tombstones = new List<LocalTombstone>
        {
            CreateTombstone("A:B", "op-001"),
            CreateTombstone("C:D", "op-002"),
        };
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tombstones.AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);

        // Act
        await cache.LoadAsync();

        // Assert
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task LoadAsync_WhenAlreadyLoaded_SkipsDuplicateLoad()
    {
        // Arrange
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocalTombstone>().AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);

        // Act
        await cache.LoadAsync();
        await cache.LoadAsync(); // 第二次调用应跳过

        // Assert: GetAllAsync 仅调用一次
        mockRepo.Verify(r => r.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsTombstoned_FrnKeyMatch_ReturnsTrue()
    {
        // Arrange
        var tombstones = new List<LocalTombstone>
        {
            CreateTombstone("A8B3-1C4D:12345", "op-001"),
        };
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tombstones.AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        await cache.LoadAsync();

        // Act & Assert: FRN 键匹配 → 命中
        Assert.True(cache.IsTombstoned("A8B3-1C4D:12345", null));
    }

    [Fact]
    public async Task IsTombstoned_FingerprintKeyMatch_ReturnsTrue()
    {
        // Arrange: 构造指纹键格式的墓碑（包含日期时间 T 分隔符）
        var fpKey = "1024:2026-06-20T12:00:00.0000000";
        var tombstones = new List<LocalTombstone>
        {
            CreateTombstone(fpKey, "op-001"),
        };
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tombstones.AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        await cache.LoadAsync();

        // Act & Assert: 指纹键匹配 → 命中
        Assert.True(cache.IsTombstoned(null, fpKey));
    }

    [Fact]
    public async Task IsTombstoned_NoMatch_ReturnsFalse()
    {
        // Arrange
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocalTombstone>().AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        await cache.LoadAsync();

        // Act & Assert: 无匹配 → 未命中
        Assert.False(cache.IsTombstoned("unknown:key", "unknown:fingerprint"));
    }

    [Fact]
    public async Task IsTombstoned_BothKeysNull_ReturnsFalse()
    {
        // Arrange
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocalTombstone>().AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        await cache.LoadAsync();

        // Act & Assert: 两个键均为 null → 无法判断，假定未删除
        Assert.False(cache.IsTombstoned(null, null));
    }

    [Fact]
    public async Task IsTombstoned_FingerprintMatch_ButPathMismatch_ReturnsFalse()
    {
        // Arrange: 指纹键匹配但文件路径不一致（指纹碰撞场景，审计偏差 #3 修复）
        var fpKey = "1024:2026-06-20T12:00:00.0000000";
        var tombstone = new LocalTombstone
        {
            FilePath = @"\\?\C:\original\file.tmp",
            FileIdentityKey = fpKey,
            OperationId = "op-001",
            OriginalSize = 1024,
            OriginalLastWriteTime = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc),
            DeletedAt = DateTime.UtcNow
        };
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { tombstone }.AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        await cache.LoadAsync();

        // Act & Assert: 指纹键命中，但路径不同 → 应返回 false（指纹碰撞）
        bool result = cache.IsTombstoned(
            null, fpKey,
            filePath: @"\\?\C:\different\file.tmp",  // ← 不同路径
            fileSize: 1024);

        Assert.False(result);
    }

    [Fact]
    public async Task IsTombstoned_FingerprintMatch_PathAndSizeMatch_ReturnsTrue()
    {
        // Arrange: 指纹键匹配 + 路径/大小一致 → 确认命中
        var fpKey = "1024:2026-06-20T12:00:00.0000000";
        var tombstone = new LocalTombstone
        {
            FilePath = @"\\?\C:\test\file.tmp",
            FileIdentityKey = fpKey,
            OperationId = "op-001",
            OriginalSize = 1024,
            OriginalLastWriteTime = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc),
            DeletedAt = DateTime.UtcNow
        };
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { tombstone }.AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        await cache.LoadAsync();

        // Act & Assert: 指纹键命中 + 路径一致 + 大小一致 → 确认命中
        bool result = cache.IsTombstoned(
            null, fpKey,
            filePath: @"\\?\C:\test\file.tmp",
            fileSize: 1024);

        Assert.True(result);
    }

    [Fact]
    public async Task IsTombstoned_FingerprintMatch_NoVerificationParams_ReturnsTrue()
    {
        // Arrange: 向后兼容 — 未提供路径和大小参数时，仅依赖键匹配
        var fpKey = "1024:2026-06-20T12:00:00.0000000";
        var tombstone = new LocalTombstone
        {
            FilePath = @"\\?\C:\test\file.tmp",
            FileIdentityKey = fpKey,
            OperationId = "op-001",
            OriginalSize = 1024,
            OriginalLastWriteTime = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc),
            DeletedAt = DateTime.UtcNow
        };
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { tombstone }.AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        await cache.LoadAsync();

        // Act & Assert: 未提供校验参数 → 信任键匹配（向后兼容）
        bool result = cache.IsTombstoned(null, fpKey); // 无 filePath/fileSize

        Assert.True(result);
    }

    [Fact]
    public void Add_IncreasesCount()
    {
        // Arrange
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);

        // Act
        cache.Add(CreateTombstone("A:B", "op-001"));

        // Assert
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void AddBatch_IncreasesCountCorrectly()
    {
        // Arrange
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        var batch = new[]
        {
            CreateTombstone("A:B", "op-001"),
            CreateTombstone("C:D", "op-001"),
            CreateTombstone("E:F", "op-002"),
        };

        // Act
        cache.AddBatch(batch);

        // Assert
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public async Task RemoveByOperationId_RemovesAllMatchingEntries()
    {
        // Arrange
        var tombstones = new List<LocalTombstone>
        {
            CreateTombstone("A:B", "op-001"),
            CreateTombstone("C:D", "op-001"),
            CreateTombstone("E:F", "op-002"),
        };
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tombstones.AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        await cache.LoadAsync();

        // Act: 移除 operation_id = "op-001" 的所有墓碑
        cache.RemoveByOperationId("op-001");

        // Assert: op-001 的两条已移除，op-002 的一条保留
        Assert.Equal(1, cache.Count);
        Assert.True(cache.IsTombstoned("E:F", null));
        Assert.False(cache.IsTombstoned("A:B", null));
    }

    [Fact]
    public async Task RemoveWhere_ReturnsCorrectCount()
    {
        // Arrange: 创建一个较新的和一个较旧的墓碑
        var oldTombstone = new LocalTombstone
        {
            FilePath = @"\\?\C:\test\old.tmp",
            FileIdentityKey = "old:key",
            OperationId = "op-001",
            OriginalSize = 100,
            OriginalLastWriteTime = DateTime.UtcNow.AddDays(-10),
            DeletedAt = DateTime.UtcNow.AddDays(-10)
        };
        var newTombstone = new LocalTombstone
        {
            FilePath = @"\\?\C:\test\new.tmp",
            FileIdentityKey = "new:key",
            OperationId = "op-002",
            OriginalSize = 200,
            OriginalLastWriteTime = DateTime.UtcNow,
            DeletedAt = DateTime.UtcNow
        };
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { oldTombstone, newTombstone }.AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        await cache.LoadAsync();

        // Act: 移除 5 天前创建的墓碑
        var cutoff = DateTime.UtcNow.AddDays(-5);
        int removed = cache.RemoveWhere(t => t.DeletedAt < cutoff);

        // Assert: 仅旧墓碑被移除
        Assert.Equal(1, removed);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task ConcurrentAccess_DoesNotThrow()
    {
        // Arrange
        var mockRepo = new Mock<ILocalTombstoneRepository>();
        mockRepo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LocalTombstone>().AsReadOnly());
        var mockLogger = new Mock<ILogger<TombstoneCache>>();
        var cache = new TombstoneCache(mockRepo.Object, mockLogger.Object);
        await cache.LoadAsync();

        // Act: 并发添加和查询，验证不抛异常
        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(() =>
            {
                cache.Add(CreateTombstone($"key:{idx}", $"op-{idx % 10}"));
                cache.IsTombstoned($"key:{idx}", null);
            }));
        }

        // Assert: 所有任务正常完成
        await Task.WhenAll(tasks);
        Assert.True(cache.Count > 0);
    }
}
