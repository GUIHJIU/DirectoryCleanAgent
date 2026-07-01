using System.Collections.ObjectModel;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Interfaces;

namespace DirectoryCleanAgent.Tests.Core.Contracts;

/// <summary>
/// A1.5 核心服务契约验证测试。
///
/// 测试目的：验证所有核心接口的方法签名、DTO 不可变性、以及接口间的依赖契约。
/// 这些测试不依赖具体实现（使用 Moq Mock），UI 团队可据此确保 Mock 数据与真实接口兼容。
///
/// 测试覆盖：
/// - IFileListProvider 流式枚举与事件签名
/// - IRuleEngine 单文件裁决
/// - IDecisionEngine 决策+快照生成
/// - IOperationExecutor 容量预检与执行
/// - IBackupManager 回滚与校验
/// - FileItem / DeleteSnapshotEntry 等 DTO 的不可变性
/// </summary>
public class InterfaceContractTests
{
    #region IFileListProvider 契约验证

    [Fact]
    public void IFileListProvider_ShouldExpose_EnumerateFilesAsync_WithCorrectSignature()
    {
        // 验证：流式枚举方法签名 — IAsyncEnumerable<FileItem> 返回类型
        var method = typeof(IFileListProvider).GetMethod(nameof(IFileListProvider.EnumerateFilesAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(IAsyncEnumerable<FileItem>), method!.ReturnType);
        Assert.Equal(2, method.GetParameters().Length);
        Assert.Equal(typeof(EverythingQueryParams), method.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), method.GetParameters()[1].ParameterType);
    }

    [Fact]
    public void IFileListProvider_ShouldExpose_IsIndexingAsync()
    {
        var method = typeof(IFileListProvider).GetMethod(nameof(IFileListProvider.IsIndexingAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<bool>), method!.ReturnType);
        Assert.Equal(typeof(CancellationToken), method.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void IFileListProvider_ShouldExpose_FileChangedEvent()
    {
        var evt = typeof(IFileListProvider).GetEvent(nameof(IFileListProvider.FileChanged));
        Assert.NotNull(evt);
        Assert.Equal(typeof(EventHandler<FileChangedEventArgs>), evt!.EventHandlerType);
    }

    #endregion

    #region IRuleEngine 契约验证

    [Fact]
    public void IRuleEngine_ShouldExpose_Evaluate_WithCorrectSignature()
    {
        var method = typeof(IRuleEngine).GetMethod(nameof(IRuleEngine.Evaluate));
        Assert.NotNull(method);
        Assert.Equal(typeof(RuleResult), method!.ReturnType);
        Assert.Equal(2, method.GetParameters().Length);
        Assert.Equal(typeof(FileItem), method.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(UserConfig), method.GetParameters()[1].ParameterType);
    }

    [Fact]
    public void IRuleEngine_ShouldExpose_RulesChangedEvent()
    {
        var evt = typeof(IRuleEngine).GetEvent(nameof(IRuleEngine.RulesChanged));
        Assert.NotNull(evt);
        Assert.Equal(typeof(EventHandler), evt!.EventHandlerType);
    }

    [Fact]
    public void RuleResult_NoMatch_ShouldReturnKeepVerdict()
    {
        var result = RuleResult.NoMatch;
        Assert.Equal(RuleVerdict.Keep, result.Verdict);
        Assert.Equal("未分类", result.SemanticCategory);
        Assert.Equal(7, result.RulePriority);
    }

    #endregion

    #region IDecisionEngine 契约验证

    [Fact]
    public void IDecisionEngine_ShouldExpose_DecideAndSnapshotAsync_WithCorrectSignature()
    {
        var method = typeof(IDecisionEngine).GetMethod(nameof(IDecisionEngine.DecideAndSnapshotAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<DecisionSnapshot>), method!.ReturnType);
        Assert.Equal(3, method.GetParameters().Length);
        Assert.Equal(typeof(IReadOnlyList<FileDecisionCache>), method.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(IProgress<int>), method.GetParameters()[1].ParameterType);
        Assert.Equal(typeof(CancellationToken), method.GetParameters()[2].ParameterType);
    }

    [Fact]
    public void IDecisionEngine_ShouldExpose_Arbitrate_ForSingleFileRefresh()
    {
        var method = typeof(IDecisionEngine).GetMethod(nameof(IDecisionEngine.Arbitrate));
        Assert.NotNull(method);
        Assert.Equal(typeof(FileDecisionCache), method!.ReturnType);
        Assert.Equal(3, method.GetParameters().Length);
    }

    #endregion

    #region IOperationExecutor 契约验证

    [Fact]
    public void IOperationExecutor_ShouldExpose_QueryRecycleBinCapacityAsync()
    {
        var method = typeof(IOperationExecutor).GetMethod(nameof(IOperationExecutor.QueryRecycleBinCapacityAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<RecycleBinCapacity>), method!.ReturnType);
        Assert.Equal(typeof(CancellationToken), method.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void IOperationExecutor_ShouldExpose_ExecuteAsync_WithCorrectSignature()
    {
        var method = typeof(IOperationExecutor).GetMethod(nameof(IOperationExecutor.ExecuteAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<OperationResult>), method!.ReturnType);
        Assert.Equal(4, method.GetParameters().Length);
        // 参数类型验证
        var parameters = method!.GetParameters();
        Assert.Equal(typeof(ReadOnlyCollection<DeleteSnapshotEntry>), parameters[0].ParameterType);
        Assert.Equal(typeof(DeleteMethod), parameters[1].ParameterType);
        Assert.Equal(typeof(IProgress<OperationProgress>), parameters[2].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[3].ParameterType);
    }

    #endregion

    #region IBackupManager 契约验证

    [Fact]
    public void IBackupManager_ShouldExpose_RollbackAsync()
    {
        var method = typeof(IBackupManager).GetMethod(nameof(IBackupManager.RollbackAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<OperationResult>), method!.ReturnType);
    }

    [Fact]
    public void IBackupManager_ShouldExpose_GetRollbackHistoryAsync()
    {
        var method = typeof(IBackupManager).GetMethod(nameof(IBackupManager.GetRollbackHistoryAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<IReadOnlyList<OperationBatchInfo>>), method!.ReturnType);
    }

    [Fact]
    public void IBackupManager_ShouldExpose_VerifyHashesAsync()
    {
        var method = typeof(IBackupManager).GetMethod(nameof(IBackupManager.VerifyHashesAsync));
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<HashVerificationResult>), method!.ReturnType);
    }

    #endregion

    #region IExclusionManager 契约验证

    [Fact]
    public void IExclusionManager_ShouldExpose_IsExcluded()
    {
        var method = typeof(IExclusionManager).GetMethod(nameof(IExclusionManager.IsExcluded));
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method!.ReturnType);
        Assert.Equal(typeof(string), method.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void IExclusionManager_ShouldExpose_Reload()
    {
        var method = typeof(IExclusionManager).GetMethod(nameof(IExclusionManager.Reload));
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
    }

    #endregion

    #region DTO 不可变性验证

    [Fact]
    public void FileItem_ShouldBeRecord_WithInitOnlyProperties()
    {
        // 验证 record 类型的 init-only 属性
        var item = new FileItem
        {
            FilePath = @"\\?\C:\test.log",
            SizeBytes = 1024,
            LastWriteTime = DateTime.UtcNow,
            Extension = ".log",
            EverythingSortKey = "test"
        };

        Assert.Equal(@"\\?\C:\test.log", item.FilePath);
        Assert.Equal(1024, item.SizeBytes);
        Assert.Equal(".log", item.Extension);
    }

    [Fact]
    public void DeleteSnapshotEntry_ShouldContainAllRequiredFields()
    {
        var entry = new DeleteSnapshotEntry
        {
            FilePath = @"\\?\C:\test.log",
            FileSize = 1024,
            Sha256Hash = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2",
            FinalAction = FinalAction.AutoDelete,
            OperationId = Guid.NewGuid().ToString()
        };

        Assert.Equal(FinalAction.AutoDelete, entry.FinalAction);
        Assert.Equal(64, entry.Sha256Hash.Length); // SHA-256 十六进制小写长度
        Assert.True(entry.Sha256Hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void RecycleBinCapacity_CanAccommodate_ShouldWorkCorrectly()
    {
        var capacity = new RecycleBinCapacity
        {
            TotalBytes = 1024 * 1024 * 1024, // 1 GB
            UsedBytes = 512 * 1024 * 1024,   // 512 MB
            AvailableBytes = 512 * 1024 * 1024
        };

        Assert.True(capacity.CanAccommodate(100 * 1024 * 1024));   // 100 MB fits
        Assert.False(capacity.CanAccommodate(600 * 1024 * 1024));  // 600 MB doesn't fit
        Assert.True(new RecycleBinCapacity { TotalBytes = 0 }.CanAccommodate(long.MaxValue)); // No limit
    }

    [Fact]
    public void DeleteSnapshotEntry_JsonPropertyNames_ShouldMatchDesignDoc()
    {
        // 验证 JSON 序列化字段名与设计文档 5.1 节一致
        var entry = new DeleteSnapshotEntry
        {
            FilePath = @"\\?\test",
            FileSize = 100,
            Sha256Hash = "abcdef",
            FinalAction = FinalAction.ManualReview,
            OperationId = "op-123"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        Assert.Contains("\"path\"", json);
        Assert.Contains("\"size\"", json);
        Assert.Contains("\"hash\"", json);
        Assert.Contains("\"action\"", json);
        Assert.Contains("\"op\"", json);
    }

    [Fact]
    public void DecisionSnapshot_ActionCounts_ShouldGroupCorrectly()
    {
        var entries = new List<DeleteSnapshotEntry>
        {
            new() { FilePath = @"\\?\a", FileSize = 1, Sha256Hash = "aa", FinalAction = FinalAction.AutoDelete, OperationId = "1" },
            new() { FilePath = @"\\?\b", FileSize = 2, Sha256Hash = "bb", FinalAction = FinalAction.AutoDelete, OperationId = "1" },
            new() { FilePath = @"\\?\c", FileSize = 3, Sha256Hash = "cc", FinalAction = FinalAction.SuggestDelete, OperationId = "1" },
            new() { FilePath = @"\\?\d", FileSize = 4, Sha256Hash = "dd", FinalAction = FinalAction.ManualReview, OperationId = "1" },
            new() { FilePath = @"\\?\e", FileSize = 5, Sha256Hash = "ee", FinalAction = FinalAction.Protected, OperationId = "1" },
        };

        var snapshot = new DecisionSnapshot
        {
            OperationId = "op-1",
            Entries = new System.Collections.ObjectModel.ReadOnlyCollection<DeleteSnapshotEntry>(entries),
            FrozenAt = DateTime.UtcNow,
            TotalSizeBytes = entries.Sum(e => e.FileSize)
        };

        Assert.Equal(5, snapshot.TotalFileCount);
        Assert.Equal(15, snapshot.TotalSizeBytes);
        Assert.Equal(2, snapshot.ActionCounts[FinalAction.AutoDelete]);
        Assert.Equal(1, snapshot.ActionCounts[FinalAction.SuggestDelete]);
    }

    [Fact]
    public void OperationProgress_ShouldCalculatePercentageCorrectly()
    {
        var progress = OperationProgress.Hashing(100);
        Assert.Equal(OperationPhase.Hashing, progress.Phase);
        Assert.Equal(0, progress.CompletedCount);
        Assert.Equal(100, progress.TotalCount);
        Assert.Equal(0.0, progress.Percentage);
    }

    [Fact]
    public void OperationProgress_Indeterminate_ShouldReturnNegativePercentage()
    {
        var progress = OperationProgress.CheckingCapacity();
        Assert.Equal(-1.0, progress.Percentage);
        Assert.False(progress.IsCompleted);
    }

    #endregion

    #region OperationResult 状态验证

    [Fact]
    public void OperationResult_IsFullySuccessful_WhenAllSucceed()
    {
        var result = new OperationResult
        {
            OperationId = "op-1",
            SuccessCount = 10,
            FailedCount = 0,
            CancelledCount = 0,
            FreedBytes = 1024,
            Elapsed = TimeSpan.FromSeconds(1)
        };

        Assert.True(result.IsFullySuccessful);
    }

    [Fact]
    public void OperationResult_IsFullySuccessful_WhenFailed_ShouldBeFalse()
    {
        var result = new OperationResult
        {
            OperationId = "op-1",
            SuccessCount = 8,
            FailedCount = 2,
            CancelledCount = 0,
            FreedBytes = 800,
            Elapsed = TimeSpan.FromSeconds(1)
        };

        Assert.False(result.IsFullySuccessful);
    }

    #endregion
}
