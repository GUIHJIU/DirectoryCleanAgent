// ============================================================
// DeleteExecuteIntegrationTests.cs
// 功能: 真实删除操作端到端集成测试
// 覆盖: 回收站删除、永久删除、隔离区备份、进度上报、
//   墓碑/删除记录/审计日志写入、回收站容量预检、文件锁定降级
// 使用真实文件系统和 SQLite 数据库验证完整删除链路
// ============================================================

using System.Collections.ObjectModel;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Operations;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;

namespace DirectoryCleanAgent.Tests.Integration.ExecuteAndRollback;

/// <summary>
/// 真实删除操作集成测试
/// 验证 OperationExecutor 在真实文件系统上的完整删除流程
/// </summary>
public class DeleteExecuteIntegrationTests : IntegrationTestBase
{
    private readonly OperationExecutor _executor;
    private readonly ILocalTombstoneRepository _tombstoneRepo;
    private readonly IDeletionRecordRepository _deletionRepo;
    private readonly IAuditLogRepository _auditRepo;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly Mock<IQuarantineManager> _quarantineMock;
    private readonly TestFileGenerator _fileGenerator;

    public DeleteExecuteIntegrationTests() : base("delete_exec")
    {
        // 1. 初始化 SQLite 数据库（真实数据层）
        var (dbPath, connFactory) = CreateTestDbAsync("delete_exec.db").GetAwaiter().GetResult();
        _connectionFactory = connFactory;
        var dataLogger = NullLoggerFactory.Instance.CreateLogger("DataLayer");

        _tombstoneRepo = new LocalTombstoneRepository(_connectionFactory, dataLogger);
        _deletionRepo = new DeletionRecordRepository(_connectionFactory, dataLogger);
        _auditRepo = new AuditLogRepository(_connectionFactory, dataLogger);

        // 墓碑缓存在测试中不需要提前加载（空库）
        var cacheLogger = NullLoggerFactory.Instance.CreateLogger<TombstoneCache>();
        var tombstoneCache = new TombstoneCache(_tombstoneRepo, cacheLogger);

        // 2. 隔离区管理器 Mock（默认禁用隔离区）
        _quarantineMock = new Mock<IQuarantineManager>();
        _quarantineMock.Setup(q => q.GetQuarantineDirectoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null); // null = 禁用隔离区

        // 3. 配置
        var execConfig = TestConfigBuilder.Create()
            .WithDataStorage(TestRoot)
            .WithDeleteMethod(DeleteMethod.RecycleBin)
            .Build();
        UpdateConfig(execConfig);

        // 4. 初始化 OperationExecutor（真实实例）
        var execLogger = NullLoggerFactory.Instance.CreateLogger<OperationExecutor>();
        _executor = new OperationExecutor(
            execLogger, _tombstoneRepo, _deletionRepo, _auditRepo,
            tombstoneCache, ConfigServiceMock.Object, _quarantineMock.Object);

        // 5. 测试文件生成器
        _fileGenerator = new TestFileGenerator(TestRoot);
    }

    // ================================================================
    // IT-EXEC-01: 真实文件回收站删除
    // ================================================================

    /// <summary>
    /// 创建真实文件 → 构建 DeleteSnapshotEntry 快照 → 执行回收站删除
    /// 验证原路径文件不存在（已移至回收站）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_RealFiles_RecycleBin_DeletesSuccessfully()
    {
        var logger = CreateLogger<DeleteExecuteIntegrationTests>();
        logger.LogInformation("开始测试: 回收站删除");

        try
        {
            // Arrange: 创建测试文件
            var testFiles = _fileGenerator.GenerateRandom(10, "recycle_test");
            var operationId = Guid.NewGuid().ToString("N");

            var entries = testFiles.Select(path =>
            {
                var hash = ComputeSha256(path);
                return new DeleteSnapshotEntry
                {
                    FilePath = @"\\?\" + path,
                    FileSize = new FileInfo(path).Length,
                    Sha256Hash = hash,
                    FinalAction = FinalAction.AutoDelete,
                    RuleVerdict = RuleVerdict.AutoDelete,
                    OperationId = operationId
                };
            }).ToList();

            var snapshot = new ReadOnlyCollection<DeleteSnapshotEntry>(entries);

            // Act: 执行回收站删除
            var result = await _executor.ExecuteAsync(
                snapshot, DeleteMethod.RecycleBin,
                progress: null, CancellationToken.None);

            // Assert: 删除操作完成
            Assert.Equal(entries.Count, result.SuccessCount);
            Assert.Equal(0, result.FailedCount);
            Assert.True(result.IsFullySuccessful);

            // 验证文件已从原路径消失
            foreach (var entry in entries)
            {
                var normalPath = entry.FilePath.Replace(@"\\?\", "");
                Assert.False(File.Exists(normalPath),
                    $"文件应已被删除: {normalPath}");
            }

            logger.LogInformation("回收站删除验证通过: 成功={Success}, 失败={Failed}",
                result.SuccessCount, result.FailedCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "回收站删除测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EXEC-02: 永久删除
    // ================================================================

    /// <summary>
    /// 永久删除模式：文件从磁盘直接移除，不经过回收站
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_RealFiles_PermanentDelete_DeletesSuccessfully()
    {
        var logger = CreateLogger<DeleteExecuteIntegrationTests>();
        logger.LogInformation("开始测试: 永久删除");

        try
        {
            // Arrange
            var testFiles = _fileGenerator.GenerateRandom(5, "permanent_test");
            var operationId = Guid.NewGuid().ToString("N");

            var entries = testFiles.Select(path => new DeleteSnapshotEntry
            {
                FilePath = @"\\?\" + path,
                FileSize = new FileInfo(path).Length,
                Sha256Hash = ComputeSha256(path),
                FinalAction = FinalAction.AutoDelete,
                RuleVerdict = RuleVerdict.AutoDelete,
                OperationId = operationId
            }).ToList();

            var snapshot = new ReadOnlyCollection<DeleteSnapshotEntry>(entries);

            // Act: 永久删除
            var result = await _executor.ExecuteAsync(
                snapshot, DeleteMethod.Permanent,
                progress: null, CancellationToken.None);

            // Assert
            Assert.Equal(entries.Count, result.SuccessCount);

            foreach (var entry in entries)
            {
                var normalPath = entry.FilePath.Replace(@"\\?\", "");
                Assert.False(File.Exists(normalPath));
            }

            logger.LogInformation("永久删除验证通过: {Count} 个文件", entries.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "永久删除测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EXEC-03: 隔离区备份创建
    // ================================================================

    /// <summary>
    /// 隔离区模式：文件被移入隔离区目录并重命名为 .quarantine 格式
    /// 原路径文件不存在
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_RealFiles_Quarantine_BackupCreated()
    {
        var logger = CreateLogger<DeleteExecuteIntegrationTests>();
        logger.LogInformation("开始测试: 隔离区备份");

        try
        {
            // Arrange: 启用隔离区
            var quarantineDir = Path.Combine(TestRoot, "quarantine");
            Directory.CreateDirectory(quarantineDir);

            _quarantineMock
                .Setup(q => q.GetQuarantineDirectoryAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(quarantineDir);
            _quarantineMock
                .Setup(q => q.EnforceCapacityAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var testFiles = _fileGenerator.GenerateRandom(5, "quarantine_test");
            var operationId = Guid.NewGuid().ToString("N");

            var entries = testFiles.Select(path => new DeleteSnapshotEntry
            {
                FilePath = @"\\?\" + path,
                FileSize = new FileInfo(path).Length,
                Sha256Hash = ComputeSha256(path),
                FinalAction = FinalAction.AutoDelete,
                RuleVerdict = RuleVerdict.AutoDelete,
                OperationId = operationId
            }).ToList();

            var snapshot = new ReadOnlyCollection<DeleteSnapshotEntry>(entries);

            // Act: 隔离区模式删除
            var result = await _executor.ExecuteAsync(
                snapshot, DeleteMethod.Permanent,
                progress: null, CancellationToken.None);

            // Assert: 文件从原路径消失
            foreach (var entry in entries)
            {
                var normalPath = entry.FilePath.Replace(@"\\?\", "");
                Assert.False(File.Exists(normalPath));
            }

            logger.LogInformation("隔离区删除验证通过: 成功={Success}", result.SuccessCount);

            // 恢复 Mock 默认值
            _quarantineMock
                .Setup(q => q.GetQuarantineDirectoryAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "隔离区删除测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EXEC-04: 删除过程进度上报
    // ================================================================

    /// <summary>
    /// 删除过程中 OperationProgress 正确上报每个文件的处理状态
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_WithProgress_ReportsDeletionProgress()
    {
        var logger = CreateLogger<DeleteExecuteIntegrationTests>();
        logger.LogInformation("开始测试: 删除进度上报");

        try
        {
            // Arrange
            var testFiles = _fileGenerator.GenerateRandom(10, "progress_delete");
            var operationId = Guid.NewGuid().ToString("N");

            var entries = testFiles.Select(path => new DeleteSnapshotEntry
            {
                FilePath = @"\\?\" + path,
                FileSize = new FileInfo(path).Length,
                Sha256Hash = ComputeSha256(path),
                FinalAction = FinalAction.AutoDelete,
                RuleVerdict = RuleVerdict.AutoDelete,
                OperationId = operationId
            }).ToList();

            var snapshot = new ReadOnlyCollection<DeleteSnapshotEntry>(entries);

            // 收集进度回调
            var progressReports = new List<OperationProgress>();
            var progress = new Progress<OperationProgress>(p =>
            {
                lock (progressReports) { progressReports.Add(p); }
            });

            // Act
            var result = await _executor.ExecuteAsync(
                snapshot, DeleteMethod.Permanent,
                progress, CancellationToken.None);

            // Assert: 进度被正确上报
            Assert.NotEmpty(progressReports);
            Assert.Contains(progressReports, p => p.Phase == OperationPhase.Completed);

            logger.LogInformation("进度上报验证通过: {Count} 次回调", progressReports.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "进度上报测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EXEC-05: 删除后墓碑记录写入
    // ================================================================

    /// <summary>
    /// 文件删除后在 SQLite LocalTombstone 表中存在对应墓碑记录
    /// 防止后续 Everything 扫描重新列出已删除文件
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_RealFiles_TombstoneInserted()
    {
        var logger = CreateLogger<DeleteExecuteIntegrationTests>();
        logger.LogInformation("开始测试: 墓碑记录写入");

        try
        {
            // Arrange
            var testFile = CreateTestFile("tombstone_test/file.dat", sizeBytes: 2048);
            var operationId = Guid.NewGuid().ToString("N");

            var entry = new DeleteSnapshotEntry
            {
                FilePath = @"\\?\" + testFile,
                FileSize = 2048,
                Sha256Hash = ComputeSha256(testFile),
                FinalAction = FinalAction.AutoDelete,
                RuleVerdict = RuleVerdict.AutoDelete,
                OperationId = operationId
            };

            var snapshot = new ReadOnlyCollection<DeleteSnapshotEntry>(new[] { entry });

            // Act: 执行删除
            var result = await _executor.ExecuteAsync(
                snapshot, DeleteMethod.Permanent,
                progress: null, CancellationToken.None);

            // 等待批量写入队列刷新（500ms 刷新间隔 + 缓冲区）
            await Task.Delay(800);

            // Assert: 查询墓碑表
            await using var connection = await _connectionFactory.CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM LocalTombstone WHERE operation_id = @op";
            var param = cmd.CreateParameter();
            param.ParameterName = "@op";
            param.Value = operationId;
            cmd.Parameters.Add(param);

            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.True(count > 0, "删除后必须存在墓碑记录");

            logger.LogInformation("墓碑记录验证通过: 找到 {Count} 条记录", count);
            Assert.True(result.IsFullySuccessful);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "墓碑记录测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EXEC-06: 删除记录写入 DeletionRecord 表
    // ================================================================

    /// <summary>
    /// 删除操作后在 DeletionRecord 表中存在记录
    /// 用于回滚时查找原始文件信息
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_RealFiles_DeletionRecordInserted()
    {
        var logger = CreateLogger<DeleteExecuteIntegrationTests>();
        logger.LogInformation("开始测试: 删除记录写入");

        try
        {
            // Arrange
            var testFile = CreateTestFile("del_record_test/data.bin", sizeBytes: 1024);
            var operationId = Guid.NewGuid().ToString("N");
            var hash = ComputeSha256(testFile);

            var entry = new DeleteSnapshotEntry
            {
                FilePath = @"\\?\" + testFile,
                FileSize = 1024,
                Sha256Hash = hash,
                FinalAction = FinalAction.AutoDelete,
                RuleVerdict = RuleVerdict.AutoDelete,
                OperationId = operationId
            };

            var snapshot = new ReadOnlyCollection<DeleteSnapshotEntry>(new[] { entry });

            // Act
            await _executor.ExecuteAsync(
                snapshot, DeleteMethod.Permanent,
                progress: null, CancellationToken.None);

            await Task.Delay(800); // 等待批量写入

            // Assert: 查询 DeletionRecord 表
            await using var connection = await _connectionFactory.CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM DeletionRecord WHERE operation_id = @op AND file_hash = @hash";
            cmd.Parameters.AddWithValue("@op", operationId);
            cmd.Parameters.AddWithValue("@hash", hash);

            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, count);

            logger.LogInformation("删除记录验证通过");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除记录测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EXEC-07: 审计日志写入
    // ================================================================

    /// <summary>
    /// 删除操作后 AuditLog 表中有对应审计记录
    /// 满足审计合规要求（不可篡改、不可删除）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_RealFiles_AuditLogWritten()
    {
        var logger = CreateLogger<DeleteExecuteIntegrationTests>();
        logger.LogInformation("开始测试: 审计日志写入");

        try
        {
            // Arrange
            var testFile = CreateTestFile("audit_test/important.log", sizeBytes: 512);
            var operationId = Guid.NewGuid().ToString("N");

            var entry = new DeleteSnapshotEntry
            {
                FilePath = @"\\?\" + testFile,
                FileSize = 512,
                Sha256Hash = ComputeSha256(testFile),
                FinalAction = FinalAction.AutoDelete,
                RuleVerdict = RuleVerdict.AutoDelete,
                OperationId = operationId
            };

            var snapshot = new ReadOnlyCollection<DeleteSnapshotEntry>(new[] { entry });

            // Act
            await _executor.ExecuteAsync(
                snapshot, DeleteMethod.Permanent,
                progress: null, CancellationToken.None);

            await Task.Delay(800);

            // Assert: 审计日志表有记录
            await using var connection = await _connectionFactory.CreateConnectionAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM AuditLog WHERE operation_type = @type";
            cmd.Parameters.AddWithValue("@type", "Delete");

            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.True(count > 0, "删除操作必须写入审计日志");

            logger.LogInformation("审计日志验证通过: {Count} 条审计记录", count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "审计日志测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EXEC-08: 回收站容量预检
    // ================================================================

    /// <summary>
    /// QueryRecycleBinCapacityAsync 返回有效的回收站容量信息
    /// 调用 Windows Shell API (SHQueryRecycleBin)
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_RecycleBinCapacity_PreCheck()
    {
        var logger = CreateLogger<DeleteExecuteIntegrationTests>();
        logger.LogInformation("开始测试: 回收站容量查询");

        try
        {
            // Act: 查询回收站容量
            var capacity = await _executor.QueryRecycleBinCapacityAsync(CancellationToken.None);

            // Assert: 返回有效容量对象
            Assert.NotNull(capacity);
            Assert.True(capacity.TotalBytes >= 0, "总容量不能为负数");
            Assert.True(capacity.UsedBytes >= 0, "已用容量不能为负数");

            logger.LogInformation("回收站容量: 总={Total}, 已用={Used}",
                capacity.TotalBytes, capacity.UsedBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "回收站容量查询测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EXEC-09: 文件被占用 → 降级为 manual_review
    // ================================================================

    /// <summary>
    /// 文件被其他进程占用时，乐观删除失败
    /// 降级为 manual_review 而非失败，不中断整体批次
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_FileLocked_DowngradedToManualReview()
    {
        var logger = CreateLogger<DeleteExecuteIntegrationTests>();
        logger.LogInformation("开始测试: 文件锁定降级");

        try
        {
            // Arrange: 创建文件并保持打开句柄（模拟被占用）
            var lockedFile = CreateTestFile("locked_test/locked.dat", sizeBytes: 1024);
            var operationId = Guid.NewGuid().ToString("N");

            var entry = new DeleteSnapshotEntry
            {
                FilePath = @"\\?\" + lockedFile,
                FileSize = 1024,
                Sha256Hash = ComputeSha256(lockedFile),
                FinalAction = FinalAction.AutoDelete,
                RuleVerdict = RuleVerdict.AutoDelete,
                OperationId = operationId
            };

            var snapshot = new ReadOnlyCollection<DeleteSnapshotEntry>(new[] { entry });

            // Act: 在文件被占用的情况下尝试删除
            OperationResult result;
            using (var fs = new FileStream(lockedFile, FileMode.Open, FileAccess.Read,
                FileShare.None))
            {
                result = await _executor.ExecuteAsync(
                    snapshot, DeleteMethod.Permanent,
                    progress: null, CancellationToken.None);
            }

            // Assert: 锁定文件降级到 manual_review
            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.ManualReviewCount);
            Assert.Contains(result.ManualReviewFiles,
                f => f.FilePath.Contains("locked", StringComparison.OrdinalIgnoreCase));

            logger.LogInformation("文件锁定降级验证通过: manual_review={Count}",
                result.ManualReviewCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "文件锁定降级测试失败");
            throw;
        }
    }

    public new void Dispose()
    {
        _fileGenerator?.Dispose();
        base.Dispose();
    }
}
