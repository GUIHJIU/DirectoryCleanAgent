using System.Reflection;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryCleanAgent.Tests.Data;

/// <summary>
/// AuditLogRepository 单元测试。
///
/// 核心测试点：
/// 1. 审计日志仅能通过 Insert 写入，无 Update/Delete 公开方法
/// 2. Query 支持多维条件筛选
/// 3. 批量写入队列正确集成
/// </summary>
public class AuditLogRepositoryTests : IAsyncDisposable
{
    private readonly string _tempDbPath;
    private readonly DatabaseInitializer _initializer;
    private readonly AuditLogRepository _repository;
    private readonly ILogger _logger;

    public AuditLogRepositoryTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"test_audit_{Guid.NewGuid():N}.db");
        _logger = NullLoggerFactory.Instance.CreateLogger("test");

        _initializer = new DatabaseInitializer(_tempDbPath, _logger);
        _initializer.InitializeAsync().GetAwaiter().GetResult();

        _repository = new AuditLogRepository(_initializer, _logger);
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

    /// <summary>
    /// 验证 IAuditLogRepository 接口不暴露任何 Update/Delete 方法（设计文档 4.2/8 节）。
    /// 这是编译期强制保证的不可修改性契约测试。
    /// </summary>
    [Fact]
    public void IAuditLogRepository_ShouldNotExposeUpdateOrDelete()
    {
        // Arrange
        var interfaceType = typeof(IAuditLogRepository);

        // Act
        var methods = interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        // Assert — 不应包含任何 Update 或 Delete 相关方法
        var updateMethods = methods.Where(m =>
            m.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase));

        Assert.Empty(updateMethods);

        // 验证 Insert 和 Query 方法存在
        Assert.Contains(methods, m => m.Name == "Insert");
        Assert.Contains(methods, m => m.Name == "QueryAsync");
        Assert.Contains(methods, m => m.Name == "FlushAsync");
    }

    [Fact]
    public async Task Insert_And_Query_ShouldPersistEntry()
    {
        // Arrange
        var entry = new AuditLogEntry
        {
            Timestamp = DateTime.UtcNow,
            UserSid = "S-1-5-21-TEST",
            OperationType = "SCAN_STARTED",
            Target = "C:",
            Result = "SUCCESS",
            Details = """{"fileCount":1500}"""
        };

        // Act
        _repository.Insert(entry);
        await _repository.FlushAsync();

        var results = await _repository.QueryAsync();

        // Assert
        Assert.NotEmpty(results);
        var persisted = results[0];
        Assert.NotNull(persisted.Id);
        Assert.Equal("S-1-5-21-TEST", persisted.UserSid);
        Assert.Equal("SCAN_STARTED", persisted.OperationType);
        Assert.Equal("C:", persisted.Target);
        Assert.Equal("SUCCESS", persisted.Result);
        Assert.Equal("""{"fileCount":1500}""", persisted.Details);
    }

    [Fact]
    public async Task Query_WithOperationTypeFilter_ShouldReturnMatchingEntries()
    {
        // Arrange
        var entries = new[]
        {
            new AuditLogEntry { Timestamp = DateTime.UtcNow, UserSid = "S-1", OperationType = "SCAN_STARTED", Target = "C:", Result = "SUCCESS" },
            new AuditLogEntry { Timestamp = DateTime.UtcNow, UserSid = "S-1", OperationType = "FILE_DELETED", Target = "test.log", Result = "SUCCESS" },
            new AuditLogEntry { Timestamp = DateTime.UtcNow, UserSid = "S-1", OperationType = "FILE_DELETED", Target = "test2.log", Result = "FAILED" },
        };

        foreach (var e in entries) _repository.Insert(e);
        await _repository.FlushAsync();

        // Act
        var results = await _repository.QueryAsync(operationType: "FILE_DELETED");

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("FILE_DELETED", r.OperationType));
    }

    [Fact]
    public async Task Query_WithUserSidFilter_ShouldReturnMatchingEntries()
    {
        // Arrange
        _repository.Insert(new AuditLogEntry { Timestamp = DateTime.UtcNow, UserSid = "USER-A", OperationType = "LOGIN", Target = "-", Result = "SUCCESS" });
        _repository.Insert(new AuditLogEntry { Timestamp = DateTime.UtcNow, UserSid = "USER-B", OperationType = "LOGIN", Target = "-", Result = "SUCCESS" });
        _repository.Insert(new AuditLogEntry { Timestamp = DateTime.UtcNow, UserSid = "USER-A", OperationType = "LOGOUT", Target = "-", Result = "SUCCESS" });
        await _repository.FlushAsync();

        // Act
        var results = await _repository.QueryAsync(userSid: "USER-A");

        // Assert
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("USER-A", r.UserSid));
    }

    [Fact]
    public async Task Query_WithTimeRangeFilter_ShouldReturnMatchingEntries()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var old = now.AddHours(-2);
        var recent = now.AddMinutes(-30);

        _repository.Insert(new AuditLogEntry { Timestamp = old, UserSid = "S-1", OperationType = "SYSTEM_START", Target = "-", Result = "SUCCESS" });
        _repository.Insert(new AuditLogEntry { Timestamp = recent, UserSid = "S-1", OperationType = "SCAN_STARTED", Target = "C:", Result = "SUCCESS" });
        _repository.Insert(new AuditLogEntry { Timestamp = now, UserSid = "S-1", OperationType = "SCAN_COMPLETED", Target = "C:", Result = "SUCCESS" });
        await _repository.FlushAsync();

        // Act — 查询最近 1 小时的记录
        var results = await _repository.QueryAsync(from: now.AddHours(-1));

        // Assert
        Assert.Equal(2, results.Count); // 不包含 2 小时前的那条
    }

    [Fact]
    public async Task Query_WithLimitAndOffset_ShouldPaginateCorrectly()
    {
        // Arrange
        for (var i = 0; i < 10; i++)
        {
            _repository.Insert(new AuditLogEntry
            {
                Timestamp = DateTime.UtcNow.AddSeconds(-i),
                UserSid = "S-1",
                OperationType = "TEST",
                Target = $"item-{i}",
                Result = "SUCCESS"
            });
        }
        await _repository.FlushAsync();

        // Act — 取前 5 条
        var page1 = await _repository.QueryAsync(limit: 5, offset: 0);
        // Act — 取后 5 条
        var page2 = await _repository.QueryAsync(limit: 5, offset: 5);

        // Assert
        Assert.Equal(5, page1.Count);
        Assert.Equal(5, page2.Count);

        // 验证两页不重叠
        var page1Targets = page1.Select(e => e.Target).ToHashSet();
        var page2Targets = page2.Select(e => e.Target).ToHashSet();
        Assert.Empty(page1Targets.Intersect(page2Targets));
    }
}
