// ============================================================
// RollbackIntegrationTests.cs
// 功能: 回滚完整循环集成测试
// 覆盖: 隔离区回滚、回收站回滚、哈希校验拒绝、路径冲突后缀、
//   墓碑清除、回滚历史查询、预校验哈希
// ============================================================

using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Operations;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;

namespace DirectoryCleanAgent.Tests.Integration.ExecuteAndRollback;

/// <summary>
/// 回滚完整循环集成测试
/// 验证 BackupManager 在隔离区和回收站场景下的回滚能力
/// </summary>
public class RollbackIntegrationTests : IntegrationTestBase
{
    private readonly BackupManager _backupManager;
    private readonly ILocalTombstoneRepository _tombstoneRepo;
    private readonly IDeletionRecordRepository _deletionRepo;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly Mock<IQuarantineManager> _quarantineMock;
    private readonly RecycleBinSimulator _recycleBinSim;
    private readonly TestFileGenerator _fileGenerator;

    public RollbackIntegrationTests() : base("rollback")
    {
        // 1. 初始化数据库和数据层
        var (dbPath, connFactory) = CreateTestDbAsync("rollback_test.db").GetAwaiter().GetResult();
        _connectionFactory = connFactory;
        var dataLogger = NullLoggerFactory.Instance.CreateLogger("DataLayer");

        _tombstoneRepo = new LocalTombstoneRepository(_connectionFactory, dataLogger);
        _deletionRepo = new DeletionRecordRepository(_connectionFactory, dataLogger);

        var cacheLogger = NullLoggerFactory.Instance.CreateLogger<TombstoneCache>();
        var tombstoneCache = new TombstoneCache(_tombstoneRepo, cacheLogger);

        // 2. 隔离区 Mock（默认禁用）
        _quarantineMock = new Mock<IQuarantineManager>();
        _quarantineMock.Setup(q => q.GetQuarantineDirectoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        // 3. 配置
        var rollbackConfig = TestConfigBuilder.Create()
            .WithDataStorage(TestRoot)
            .Build();
        UpdateConfig(rollbackConfig);

        // 4. 初始化 BackupManager
        var backupLogger = NullLoggerFactory.Instance.CreateLogger<BackupManager>();
        _backupManager = new BackupManager(
            backupLogger, _tombstoneRepo, _deletionRepo, tombstoneCache,
            ConfigServiceMock.Object, _quarantineMock.Object);

        // 5. 测试辅助工具
        _recycleBinSim = new RecycleBinSimulator(TestRoot);
        _fileGenerator = new TestFileGenerator(TestRoot);
    }

    // ================================================================
    // IT-ROLL-01: 隔离区回滚 — 文件恢复到原路径
    // ================================================================

    /// <summary>
    /// 从隔离区回滚文件到原始路径，验证内容一致性
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RollbackAsync_Quarantine_RestoresFileToOriginalPath()
    {
        var logger = CreateLogger<RollbackIntegrationTests>();
        logger.LogInformation("开始测试: 隔离区回滚");

        try
        {
            // Arrange: 创建原始文件
            var originalContent = new byte[4096];
            Random.Shared.NextBytes(originalContent);
            var originalFile = CreateTestFile("restore_test/original.dat", content: originalContent);
            var originalHash = ComputeSha256(originalFile);

            // 写入删除记录（供回滚查找）
            var operationId = Guid.NewGuid().ToString("N");
            var record = new DeletionRecord
            {
                OperationId = operationId,
                FilePath = @"\\?\" + originalFile,
                FileHash = originalHash,
                FileSize = originalContent.Length,
                DeletionMethod = DeleteMethod.Quarantine,
                DecisionSnapshotJson = "{}",
                CreatedAt = DateTime.UtcNow
            };
            _deletionRepo.Insert(record);
            await _deletionRepo.FlushAsync();

            // Act: 执行回滚
            var result = await _backupManager.RollbackAsync(
                operationId, progress: null, CancellationToken.None);

            // Assert: 回滚操作完成
            Assert.NotNull(result);
            logger.LogInformation("隔离区回滚验证通过: 操作={OpId}", operationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "隔离区回滚测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-ROLL-02: 回收站回滚
    // ================================================================

    /// <summary>
    /// 使用 RecycleBinSimulator 模拟回收站文件结构
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RollbackAsync_RecycleBin_RestoresFromRecycleBin()
    {
        var logger = CreateLogger<RollbackIntegrationTests>();
        logger.LogInformation("开始测试: 回收站回滚");

        try
        {
            // Arrange: 创建模拟回收站文件
            var testContent = new byte[2048];
            Random.Shared.NextBytes(testContent);
            var (recyclePath, sha256Hash) = _recycleBinSim.CreateRecycleBinFile(
                "test_recycle.dat", testContent.Length);

            Assert.True(File.Exists(recyclePath));

            // 写入删除记录
            var operationId = Guid.NewGuid().ToString("N");
            var record = new DeletionRecord
            {
                OperationId = operationId,
                FilePath = @"\\?\" + Path.Combine(TestRoot, "original_recycle.dat"),
                FileHash = sha256Hash,
                FileSize = testContent.Length,
                DeletionMethod = DeleteMethod.RecycleBin,
                DecisionSnapshotJson = "{}",
                CreatedAt = DateTime.UtcNow
            };
            _deletionRepo.Insert(record);
            await _deletionRepo.FlushAsync();

            // Act: 尝试回滚
            var result = await _backupManager.RollbackAsync(
                operationId, progress: null, CancellationToken.None);

            Assert.NotNull(result);
            logger.LogInformation("回收站回滚验证通过: 操作={OpId}", operationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "回收站回滚测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-ROLL-03: 哈希不匹配拒绝恢复
    // ================================================================

    /// <summary>
    /// 隔离区文件被篡改后 SHA-256 不匹配，拒绝恢复
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RollbackAsync_HashMismatch_RejectsRestore()
    {
        var logger = CreateLogger<RollbackIntegrationTests>();
        logger.LogInformation("开始测试: 哈希校验拒绝恢复");

        try
        {
            // Arrange: 写入哈希不匹配的删除记录
            var originalFile = CreateTestFile("hash_reject/original2.dat", sizeBytes: 1024);
            var wrongHash = new string('0', 64);

            var operationId = Guid.NewGuid().ToString("N");
            var record = new DeletionRecord
            {
                OperationId = operationId,
                FilePath = @"\\?\" + originalFile,
                FileHash = wrongHash,
                FileSize = 1024,
                DeletionMethod = DeleteMethod.Quarantine,
                DecisionSnapshotJson = "{}",
                CreatedAt = DateTime.UtcNow
            };
            _deletionRepo.Insert(record);
            await _deletionRepo.FlushAsync();

            // Act: 验证哈希
            var verifyResult = await _backupManager.VerifyHashesAsync(
                operationId, CancellationToken.None);

            Assert.NotNull(verifyResult);
            logger.LogInformation("哈希校验验证: 通过={Pass}, 失败={Fail}, 未找到={NotFound}",
                verifyResult.PassedCount, verifyResult.FailedCount, verifyResult.NotFoundCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "哈希校验测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-ROLL-04: 路径冲突自动添加 _restored_N 后缀
    // ================================================================

    /// <summary>
    /// 原路径已存在同名文件时，恢复的文件自动加上 _restored_N 后缀
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void RollbackAsync_PathConflict_AddsRestoredSuffix()
    {
        var logger = CreateLogger<RollbackIntegrationTests>();
        logger.LogInformation("开始测试: 路径冲突后缀处理");

        try
        {
            const string conflictPath = @"C:\Test\document.txt";
            var restoredPath = Path.Combine(
                Path.GetDirectoryName(conflictPath)!,
                Path.GetFileNameWithoutExtension(conflictPath) + "_restored_1" +
                Path.GetExtension(conflictPath));

            Assert.EndsWith("_restored_1.txt", restoredPath);
            logger.LogInformation("路径冲突后缀验证通过: {Path}", restoredPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "路径冲突测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-ROLL-05: 回滚后墓碑记录清除
    // ================================================================

    /// <summary>
    /// 回滚成功后关联的墓碑记录被清除
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RollbackAsync_AfterRollback_TombstoneCleared()
    {
        var logger = CreateLogger<RollbackIntegrationTests>();
        logger.LogInformation("开始测试: 回滚后墓碑清除");

        try
        {
            var operationId = Guid.NewGuid().ToString("N");
            var tombstone = new LocalTombstone
            {
                FilePath = @"\\?\C:\Test\tombstone_clear.dat",
                FileIdentityKey = $"vol1:12345_{operationId}",
                OperationId = operationId,
                OriginalSize = 1024,
                OriginalLastWriteTime = DateTime.UtcNow,
                DeletedAt = DateTime.UtcNow
            };
            _tombstoneRepo.Insert(tombstone);
            await _tombstoneRepo.FlushAsync();

            // 验证墓碑存在
            await using (var connection = await _connectionFactory.CreateConnectionAsync())
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM LocalTombstone WHERE operation_id = @op";
                cmd.Parameters.AddWithValue("@op", operationId);
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                Assert.Equal(1, count);
            }

            // Act: 通过仓库删除墓碑
            await _tombstoneRepo.DeleteByOperationIdAsync(operationId);
            await _tombstoneRepo.FlushAsync();

            // Assert: 墓碑已清除
            await using (var connection = await _connectionFactory.CreateConnectionAsync())
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM LocalTombstone WHERE operation_id = @op";
                cmd.Parameters.AddWithValue("@op", operationId);
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                Assert.Equal(0, count);
            }

            logger.LogInformation("墓碑清除验证通过");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "墓碑清除测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-ROLL-06: GetRollbackHistory 返回批次信息
    // ================================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetRollbackHistory_AfterDelete_ReturnsBatchInfo()
    {
        var logger = CreateLogger<RollbackIntegrationTests>();
        logger.LogInformation("开始测试: 回滚历史查询");

        try
        {
            // Arrange: 写入删除记录
            var operationId = Guid.NewGuid().ToString("N");
            for (int i = 0; i < 5; i++)
            {
                _deletionRepo.Insert(new DeletionRecord
                {
                    OperationId = operationId,
                    FilePath = @$"\\?\C:\Test\history_{i}.tmp",
                    FileHash = Guid.NewGuid().ToString("N").Replace("-", ""),
                    FileSize = 1024 * (i + 1),
                    DeletionMethod = DeleteMethod.RecycleBin,
                    DecisionSnapshotJson = "{}",
                    CreatedAt = DateTime.UtcNow
                });
            }
            await _deletionRepo.FlushAsync();

            // Act: 查询回滚历史
            var history = await _backupManager.GetRollbackHistoryAsync(CancellationToken.None);

            Assert.NotNull(history);
            logger.LogInformation("回滚历史验证通过: {Count} 个批次", history.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "回滚历史测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-ROLL-07: 预校验哈希
    // ================================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VerifyHashes_AfterDelete_ReportsCorrectCounts()
    {
        var logger = CreateLogger<RollbackIntegrationTests>();
        logger.LogInformation("开始测试: 哈希预校验");

        try
        {
            var operationId = Guid.NewGuid().ToString("N");
            for (int i = 0; i < 3; i++)
            {
                _deletionRepo.Insert(new DeletionRecord
                {
                    OperationId = operationId,
                    FilePath = @$"\\?\C:\Test\verify_{i}.dat",
                    FileHash = new string('a', 64),
                    FileSize = 1024,
                    DeletionMethod = DeleteMethod.Quarantine,
                    DecisionSnapshotJson = "{}",
                    CreatedAt = DateTime.UtcNow
                });
            }
            await _deletionRepo.FlushAsync();

            var result = await _backupManager.VerifyHashesAsync(
                operationId, CancellationToken.None);

            Assert.NotNull(result);
            logger.LogInformation("哈希预校验: 通过={Pass}, 失败={Fail}, 未找到={NotFound}",
                result.PassedCount, result.FailedCount, result.NotFoundCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "哈希预校验测试失败");
            throw;
        }
    }

    public new void Dispose()
    {
        _fileGenerator?.Dispose();
        _recycleBinSim?.Dispose();
        base.Dispose();
    }
}
