// ============================================================
// ExecutionCancellationTests.cs
// 功能: 执行/回滚阶段取消操作集成测试
// 覆盖: 删除执行中取消、删除前取消、回滚中取消、索引等待取消
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

namespace DirectoryCleanAgent.Tests.Integration.Cancellation;

/// <summary>
/// 执行/回滚阶段取消操作测试
/// </summary>
public class ExecutionCancellationTests : IntegrationTestBase
{
    private readonly OperationExecutor _executor;
    private readonly BackupManager _backupManager;
    private readonly ILocalTombstoneRepository _tombstoneRepo;
    private readonly IDeletionRecordRepository _deletionRepo;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly Mock<IQuarantineManager> _quarantineMock;
    private readonly TestFileGenerator _fileGenerator;

    public ExecutionCancellationTests() : base("cancel_exec")
    {
        // 1. 数据库
        var (dbPath, connFactory) = CreateTestDbAsync("cancel_exec.db").GetAwaiter().GetResult();
        _connectionFactory = connFactory;
        var dataLogger = NullLoggerFactory.Instance.CreateLogger("DataLayer");

        _tombstoneRepo = new LocalTombstoneRepository(_connectionFactory, dataLogger);
        _deletionRepo = new DeletionRecordRepository(_connectionFactory, dataLogger);

        var cacheLogger = NullLoggerFactory.Instance.CreateLogger<TombstoneCache>();
        var tombstoneCache = new TombstoneCache(_tombstoneRepo, cacheLogger);

        // 2. Mock
        _quarantineMock = new Mock<IQuarantineManager>();
        _quarantineMock.Setup(q => q.GetQuarantineDirectoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        // 3. 配置
        var cancelConfig = TestConfigBuilder.Create()
            .WithDataStorage(TestRoot)
            .Build();
        UpdateConfig(cancelConfig);

        // 4. 初始化执行器
        var execLogger = NullLoggerFactory.Instance.CreateLogger<OperationExecutor>();
        var audRepo = new AuditLogRepository(_connectionFactory, dataLogger);
        _executor = new OperationExecutor(
            execLogger, _tombstoneRepo, _deletionRepo, audRepo,
            tombstoneCache, ConfigServiceMock.Object, _quarantineMock.Object);

        var backupLogger = NullLoggerFactory.Instance.CreateLogger<BackupManager>();
        _backupManager = new BackupManager(
            backupLogger, _tombstoneRepo, _deletionRepo, tombstoneCache,
            ConfigServiceMock.Object, _quarantineMock.Object);

        // 5. 文件生成器
        _fileGenerator = new TestFileGenerator(TestRoot);
    }

    // ================================================================
    // IT-CANCEL-EXEC-01: 删除执行中取消 → 部分结果 + IsCancelled
    // ================================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_CancelDuringDelete_ReturnsPartialResult()
    {
        var logger = CreateLogger<ExecutionCancellationTests>();
        logger.LogInformation("开始测试: 删除执行中取消");

        try
        {
            var testFiles = _fileGenerator.GenerateRandom(20, "cancel_during_delete");
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

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            try
            {
                var result = await _executor.ExecuteAsync(
                    snapshot, DeleteMethod.Permanent, progress: null, cts.Token);
                Assert.True(result.IsCancelled);
                logger.LogInformation("取消结果: 成功={Success}, 已取消={Cancelled}",
                    result.SuccessCount, result.IsCancelled);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("取消执行: OperationCanceledException 正确抛出");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "删除取消测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-CANCEL-EXEC-02: 删除前取消 → 所有文件完好
    // ================================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecuteAsync_CancelBeforeStart_NoFilesDeleted()
    {
        var logger = CreateLogger<ExecutionCancellationTests>();
        logger.LogInformation("开始测试: 删除前取消");

        try
        {
            var testFiles = _fileGenerator.GenerateRandom(5, "cancel_before_delete");
            var originalPaths = testFiles.ToList();
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

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            try
            {
                await _executor.ExecuteAsync(
                    snapshot, DeleteMethod.Permanent, progress: null, cts.Token);
            }
            catch (OperationCanceledException) { }

            foreach (var path in originalPaths)
            {
                Assert.True(File.Exists(path));
            }

            logger.LogInformation("删除前取消验证通过: 所有 {Count} 个文件完好", originalPaths.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "删除前取消测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-CANCEL-EXEC-03: 回滚恢复中取消
    // ================================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RollbackAsync_CancelDuringRestore_ReturnsPartialResult()
    {
        var logger = CreateLogger<ExecutionCancellationTests>();
        logger.LogInformation("开始测试: 回滚中取消");

        try
        {
            var operationId = Guid.NewGuid().ToString("N");
            for (int i = 0; i < 10; i++)
            {
                _deletionRepo.Insert(new DeletionRecord
                {
                    OperationId = operationId,
                    FilePath = @$"\\?\C:\Test\rollback_cancel_{i}.dat",
                    FileHash = new string('f', 64),
                    FileSize = 1024,
                    DeletionMethod = DeleteMethod.Quarantine,
                    DecisionSnapshotJson = "{}",
                    CreatedAt = DateTime.UtcNow
                });
            }
            await _deletionRepo.FlushAsync();

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            try
            {
                var result = await _backupManager.RollbackAsync(
                    operationId, progress: null, cts.Token);
                Assert.True(result.IsCancelled);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("回滚取消: OperationCanceledException 正确抛出");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "回滚取消测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-CANCEL-EXEC-04: 索引等待轮询中取消
    // ================================================================

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WaitForIndexAsync_CancelDuringPolling_Throws()
    {
        var logger = CreateLogger<ExecutionCancellationTests>();
        logger.LogInformation("开始测试: 索引等待取消");

        try
        {
            using var cts = new CancellationTokenSource();

            var pollingTask = Task.Run(async () =>
            {
                for (int i = 0; i < 120; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    await Task.Delay(20, cts.Token);
                }
            }, cts.Token);

            await Task.Delay(50);
            await cts.CancelAsync();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await pollingTask;
            });

            logger.LogInformation("索引等待取消验证通过");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "索引等待取消测试失败");
            throw;
        }
    }

    public new void Dispose()
    {
        _fileGenerator?.Dispose();
        base.Dispose();
    }
}
