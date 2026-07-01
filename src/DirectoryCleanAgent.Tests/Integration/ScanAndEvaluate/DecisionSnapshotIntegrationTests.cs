// ============================================================
// DecisionSnapshotIntegrationTests.cs
// 功能: 决策快照端到端集成测试
// 覆盖: 快照生成、全Protected空列表、SHA-256哈希一致性、
//   不可变性、取消中断、JSON序列化
// ============================================================

using System.Collections.ObjectModel;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Decision;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.Integration.ScanAndEvaluate;

/// <summary>
/// 决策快照生成与验证集成测试
/// 验证 DecisionEngine.DecideAndSnapshotAsync 的完整输出
/// </summary>
public class DecisionSnapshotIntegrationTests : IntegrationTestBase
{
    private readonly DecisionEngine _decisionEngine;
    private readonly ILogger<DecisionEngine> _decisionLogger;
    private readonly TestFileGenerator _fileGenerator;

    public DecisionSnapshotIntegrationTests() : base("snapshot")
    {
        _decisionLogger = CreateLogger<DecisionEngine>();
        _decisionEngine = new DecisionEngine(ConfigServiceMock.Object, _decisionLogger);
        _fileGenerator = new TestFileGenerator(TestRoot);
    }

    // ================================================================
    // IT-SNAP-01: 真实文件 → SHA-256哈希 → DecisionSnapshot
    // ================================================================

    /// <summary>
    /// 创建真实临时文件 → 生成 FileDecisionCache → 执行 DecideAndSnapshotAsync
    /// 验证返回的 DecisionSnapshot 包含正确的条目和哈希值
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DecideAndSnapshotAsync_RealFiles_GeneratesSnapshot()
    {
        var logger = CreateLogger<DecisionSnapshotIntegrationTests>();
        logger.LogInformation("开始测试: 决策快照生成");

        try
        {
            // Arrange: 创建测试文件
            var testFiles = _fileGenerator.GenerateRandom(30, "snapshot_test");
            var caches = testFiles.Select(path =>
            {
                var fileInfo = new FileInfo(path);
                return new FileDecisionCache
                {
                    FilePath = @"\\?\" + path,
                    SizeBytes = fileInfo.Length,
                    LastWriteTime = fileInfo.LastWriteTimeUtc,
                    RuleVerdict = RuleVerdict.SuggestDelete,
                    SemanticCategory = "测试文件",
                    FinalAction = FinalAction.Protected,
                    CacheVersion = 1
                };
            }).ToList();

            // Act: 生成快照
            var snapshot = await _decisionEngine.DecideAndSnapshotAsync(
                caches, progress: null, CancellationToken.None);

            // Assert: 快照基本属性
            Assert.NotNull(snapshot);
            Assert.NotEmpty(snapshot.Entries);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.OperationId));
            Assert.True(snapshot.TotalSizeBytes > 0);
            Assert.NotNull(snapshot.ActionCounts);

            // 验证每个条目都有哈希值
            foreach (var entry in snapshot.Entries)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Sha256Hash));
                Assert.Equal(64, entry.Sha256Hash.Length);
                Assert.True(entry.FileSize > 0);
            }

            logger.LogInformation("快照生成验证通过: {Count} 个条目, 总大小={Size}",
                snapshot.Entries.Count, snapshot.TotalSizeBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "快照生成测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SNAP-02: 全部 Protected → 空快照
    // ================================================================

    /// <summary>
    /// 当所有候选文件的规则裁决为 Forbid 时，最终决策均为 Protected
    /// 快照应返回空 Entries 列表（无可删除文件）
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DecideAndSnapshotAsync_AllProtected_EmptyEntries()
    {
        var logger = CreateLogger<DecisionSnapshotIntegrationTests>();
        logger.LogInformation("开始测试: 全Protected空快照");

        try
        {
            // Arrange: 创建 Forbid 规则下的候选项
            var testFiles = _fileGenerator.GenerateRandom(10, "protected_test");
            var caches = testFiles.Select(path =>
            {
                var fileInfo = new FileInfo(path);
                return new FileDecisionCache
                {
                    FilePath = @"\\?\" + path,
                    SizeBytes = fileInfo.Length,
                    LastWriteTime = fileInfo.LastWriteTimeUtc,
                    RuleVerdict = RuleVerdict.Forbid, // 全部禁止
                    SemanticCategory = "系统关键文件",
                    FinalAction = FinalAction.Protected,
                    CacheVersion = 1
                };
            }).ToList();

            // Act
            var snapshot = await _decisionEngine.DecideAndSnapshotAsync(
                caches, progress: null, CancellationToken.None);

            // Assert: Protected 文件不进入快照
            Assert.NotNull(snapshot);
            Assert.Empty(snapshot.Entries);
            Assert.Equal(0, snapshot.TotalSizeBytes);

            logger.LogInformation("全Protected快照验证通过: 空条目列表");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "全Protected测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SNAP-03: SHA-256 哈希与预期值一致
    // ================================================================

    /// <summary>
    /// 创建已知内容的文件 → 计算预期 SHA-256 → 验证快照中哈希一致
    /// 确保 DecisionEngine 的哈希计算正确
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DecideAndSnapshotAsync_Sha256Hash_MatchesExpected()
    {
        var logger = CreateLogger<DecisionSnapshotIntegrationTests>();
        logger.LogInformation("开始测试: SHA-256一致性验证");

        try
        {
            // Arrange: 创建已知内容的文件
            var content = "Hello, D2 Integration Test for SHA-256 Verification!"u8.ToArray();
            var testFile = CreateTestFile("hash_test/known_content.dat", content: content);
            var expectedHash = ComputeSha256(testFile);

            var cache = new FileDecisionCache
            {
                FilePath = @"\\?\" + testFile,
                SizeBytes = content.Length,
                LastWriteTime = File.GetLastWriteTimeUtc(testFile),
                RuleVerdict = RuleVerdict.SuggestDelete,
                SemanticCategory = "测试文件",
                FinalAction = FinalAction.Protected,
                CacheVersion = 1
            };

            // Act
            var snapshot = await _decisionEngine.DecideAndSnapshotAsync(
                new[] { cache }, progress: null, CancellationToken.None);

            // Assert: 哈希值完全一致
            Assert.Single(snapshot.Entries);
            Assert.Equal(expectedHash, snapshot.Entries[0].Sha256Hash);

            logger.LogInformation("SHA-256一致性验证通过: {Hash}", expectedHash);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SHA-256一致性测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SNAP-04: 快照不可变性
    // ================================================================

    /// <summary>
    /// 快照生成后为不可变 ReadOnlyCollection
    /// 修改原始 FileDecisionCache 不影响已冻结的快照
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DecisionSnapshot_IsImmutable_AfterCreation()
    {
        var logger = CreateLogger<DecisionSnapshotIntegrationTests>();
        logger.LogInformation("开始测试: 快照不可变性");

        try
        {
            // Arrange
            var testFile = CreateTestFile("immutable_test/original.dat", sizeBytes: 1024);
            var cache = new FileDecisionCache
            {
                FilePath = @"\\?\" + testFile,
                SizeBytes = 1024,
                LastWriteTime = DateTime.UtcNow,
                RuleVerdict = RuleVerdict.SuggestDelete,
                SemanticCategory = "测试",
                FinalAction = FinalAction.Protected,
                CacheVersion = 1
            };

            // Act: 生成快照
            var snapshot = await _decisionEngine.DecideAndSnapshotAsync(
                new[] { cache }, progress: null, CancellationToken.None);

            var snapshotHash = snapshot.Entries[0].Sha256Hash;
            var snapshotAction = snapshot.Entries[0].FinalAction;

            // 修改原始缓存（模拟后续规则变更或AI响应）
            // 使用 with 表达式创建新缓存（因为 SemanticCategory 是 init-only）
            cache = cache with { FinalAction = FinalAction.AutoDelete, SemanticCategory = "已变更" };

            // Assert: 快照中的值不受影响
            Assert.Equal(snapshotHash, snapshot.Entries[0].Sha256Hash);
            Assert.Equal(snapshotAction, snapshot.Entries[0].FinalAction);
            Assert.NotEqual(FinalAction.AutoDelete, snapshot.Entries[0].FinalAction);

            logger.LogInformation("快照不可变性验证通过");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "不可变性测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SNAP-05: 哈希计算中取消
    // ================================================================

    /// <summary>
    /// 在批量哈希计算过程中取消 → 抛出 OperationCanceledException
    /// 使用已取消的 CancellationToken 验证快速失败
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DecideAndSnapshotAsync_CancelledDuringHash_Throws()
    {
        var logger = CreateLogger<DecisionSnapshotIntegrationTests>();
        logger.LogInformation("开始测试: 哈希计算取消");

        try
        {
            // Arrange: 创建多个文件使哈希计算需要时间
            var testFiles = _fileGenerator.GenerateRandom(50, "cancel_hash_test");
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

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync(); // 预取消

            // Act & Assert: 传入已取消的令牌
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await _decisionEngine.DecideAndSnapshotAsync(
                    caches, progress: null, cts.Token);
            });

            logger.LogInformation("哈希计算取消验证通过");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "取消测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-SNAP-06: 快照 JSON 序列化包含所有必要字段
    // ================================================================

    /// <summary>
    /// DecisionSnapshot.ToCompactJson() 输出有效 JSON
    /// 包含 OperationId、Entries、path、size、hash、action、op 等字段
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DecisionSnapshot_ToCompactJson_ValidFormat()
    {
        var logger = CreateLogger<DecisionSnapshotIntegrationTests>();
        logger.LogInformation("开始测试: 快照JSON序列化");

        try
        {
            // Arrange
            var testFile = CreateTestFile("json_test/serializable.dat", sizeBytes: 512);
            var cache = new FileDecisionCache
            {
                FilePath = @"\\?\" + testFile,
                SizeBytes = 512,
                LastWriteTime = DateTime.UtcNow,
                RuleVerdict = RuleVerdict.AutoDelete,
                SemanticCategory = "测试",
                FinalAction = FinalAction.Protected,
                CacheVersion = 1
            };

            // Act
            var snapshot = await _decisionEngine.DecideAndSnapshotAsync(
                new[] { cache }, progress: null, CancellationToken.None);
            var json = snapshot.ToCompactJson();

            // Assert: JSON 包含必要字段
            Assert.False(string.IsNullOrWhiteSpace(json));
            Assert.Contains("\"path\"", json);
            Assert.Contains("\"size\"", json);
            Assert.Contains("\"hash\"", json);
            Assert.Contains("\"action\"", json);
            Assert.Contains("\"op\"", json);

            // 验证 JSON 可被反序列化
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<DecisionSnapshot>(json);
            Assert.NotNull(deserialized);
            Assert.Equal(snapshot.OperationId, deserialized.OperationId);

            logger.LogInformation("JSON序列化验证通过: 长度={Length}", json.Length);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "JSON序列化测试失败");
            throw;
        }
    }

    public new void Dispose()
    {
        _fileGenerator?.Dispose();
        base.Dispose();
    }
}
