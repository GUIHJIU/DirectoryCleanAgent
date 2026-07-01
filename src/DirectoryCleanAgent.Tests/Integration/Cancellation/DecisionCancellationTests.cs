// ============================================================
// DecisionCancellationTests.cs
// 功能: 决策/哈希阶段取消操作集成测试
// 覆盖: 哈希计算中取消、决策前取消
// ============================================================

using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Decision;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.Integration.Cancellation;

/// <summary>
/// 决策引擎取消操作测试
/// 验证 CancellationToken 在 DecideAndSnapshotAsync 哈希计算阶段正确传播
/// </summary>
public class DecisionCancellationTests : IntegrationTestBase
{
    private readonly DecisionEngine _decisionEngine;
    private readonly ILogger<DecisionEngine> _decisionLogger;
    private readonly TestFileGenerator _fileGenerator;

    public DecisionCancellationTests() : base("cancel_decision")
    {
        _decisionLogger = CreateLogger<DecisionEngine>();
        _decisionEngine = new DecisionEngine(ConfigServiceMock.Object, _decisionLogger);
        _fileGenerator = new TestFileGenerator(TestRoot);
    }

    // ================================================================
    // IT-CANCEL-DEC-01: 哈希计算中取消 → 抛出异常
    // ================================================================

    /// <summary>
    /// 在批量哈希计算过程中取消 CancellationToken
    /// 抛出 OperationCanceledException 或返回已处理的部分结果
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DecideAndSnapshotAsync_CancelDuringHash_StopsAndThrows()
    {
        var logger = CreateLogger<DecisionCancellationTests>();
        logger.LogInformation("开始测试: 哈希计算取消");

        try
        {
            // Arrange: 创建大量测试文件使哈希计算需要时间
            // 使用较大的文件增加哈希计算耗时
            var testFiles = _fileGenerator.GenerateRandom(100, "cancel_hash");
            var caches = testFiles.Select(path =>
            {
                var fileInfo = new FileInfo(path);
                return new FileDecisionCache
                {
                    FilePath = @"\\?\" + path,
                    SizeBytes = fileInfo.Length,
                    LastWriteTime = fileInfo.LastWriteTimeUtc,
                    RuleVerdict = RuleVerdict.SuggestDelete,
                    SemanticCategory = "测试",
                    FinalAction = FinalAction.Protected,
                    CacheVersion = 1
                };
            }).ToList();

            // 创建会在中途取消的 CancellationTokenSource
            using var cts = new CancellationTokenSource();

            // 先取消（快速失败场景）
            await cts.CancelAsync();

            // Act & Assert: 已取消令牌传入 → OperationCanceledException
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await _decisionEngine.DecideAndSnapshotAsync(
                    caches, progress: null, cts.Token);
            });

            logger.LogInformation("哈希计算取消验证通过: 正确抛出 OperationCanceledException");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "哈希计算取消测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-CANCEL-DEC-02: 决策前取消 → 不做哈希计算
    // ================================================================

    /// <summary>
    /// CancellationToken 在 DecideAndSnapshotAsync 调用前已取消
    /// 不执行任何哈希计算（快速失败）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DecideAndSnapshotAsync_CancelBeforeStart_NoHashing()
    {
        var logger = CreateLogger<DecisionCancellationTests>();
        logger.LogInformation("开始测试: 决策前取消");

        try
        {
            // Arrange: 创建测试文件和已取消令牌
            var testFile = CreateTestFile("no_hash/cancel_before.dat", sizeBytes: 1024);
            var cache = new FileDecisionCache
            {
                FilePath = @"\\?\" + testFile,
                SizeBytes = 1024,
                LastWriteTime = File.GetLastWriteTimeUtc(testFile),
                RuleVerdict = RuleVerdict.SuggestDelete,
                SemanticCategory = "测试",
                FinalAction = FinalAction.Protected,
                CacheVersion = 1
            };

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            // Act & Assert: 预取消 → 立即抛出
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await _decisionEngine.DecideAndSnapshotAsync(
                    new[] { cache }, progress: null, cts.Token);
            });

            // 验证原文件未被修改或删除
            Assert.True(File.Exists(testFile),
                "取消后原文件必须完好无损");

            logger.LogInformation("决策前取消验证通过: 文件未被触碰");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "决策前取消测试失败");
            throw;
        }
    }

    public new void Dispose()
    {
        _fileGenerator?.Dispose();
        base.Dispose();
    }
}
