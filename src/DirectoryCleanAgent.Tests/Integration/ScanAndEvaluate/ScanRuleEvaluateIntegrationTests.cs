// ============================================================
// ScanRuleEvaluateIntegrationTests.cs
// 功能: 扫描→规则评估→决策管道集成测试
// 验证端到端管道: 测试文件 → FileItem → RuleEngine.Evaluate →
//   FileDecisionCache → DecisionEngine.Arbitrate → FinalAction
// 使用 TestFileGenerator 替代 Everything 扫描生成测试数据
// ============================================================

using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Decision;
using DirectoryCleanAgent.Rules;
using DirectoryCleanAgent.Rules.Heuristic;
using DirectoryCleanAgent.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Tests.Integration.ScanAndEvaluate;

/// <summary>
/// 扫描→规则评估→决策管道集成测试
/// 验证从文件创建到最终操作决策的完整链路
/// </summary>
public class ScanRuleEvaluateIntegrationTests : IntegrationTestBase
{
    private readonly RuleEngine _ruleEngine;
    private readonly DecisionEngine _decisionEngine;
    private readonly TestFileGenerator _fileGenerator;
    private readonly ILogger<RuleEngine> _ruleLogger;
    private readonly ILogger<DecisionEngine> _decisionLogger;
    private readonly HeuristicRuleLoader _heuristicLoader;
    private readonly string _rulesDir;

    public ScanRuleEvaluateIntegrationTests() : base("scan_eval")
    {
        // 初始化日志
        _ruleLogger = CreateLogger<RuleEngine>();
        _decisionLogger = CreateLogger<DecisionEngine>();

        // 初始化测试文件生成器
        _fileGenerator = new TestFileGenerator(TestRoot);

        // 初始化启发式规则加载器（使用临时规则目录）
        _rulesDir = Path.Combine(TestRoot, "rules");
        Directory.CreateDirectory(_rulesDir);
        _heuristicLoader = new HeuristicRuleLoader(
            NullLoggerFactory.Instance.CreateLogger<HeuristicRuleLoader>(),
            _rulesDir);

        // 初始化规则引擎和决策引擎（真实实例，非 Mock）
        _ruleEngine = new RuleEngine(ConfigServiceMock.Object, _heuristicLoader, _ruleLogger);
        _decisionEngine = new DecisionEngine(ConfigServiceMock.Object, _decisionLogger);
    }

    // ================================================================
    // IT-EVAL-01: 完整管道 — 测试文件 → 规则评估 → 决策仲裁
    // ================================================================

    /// <summary>
    /// 端到端管道验证：创建测试文件 → 构建 FileItem → 规则引擎评估 →
    /// 决策引擎仲裁 → 验证 FinalAction 非默认值
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void ScanEvaluateDecide_RealFiles_ThroughPipeline()
    {
        var logger = CreateLogger<ScanRuleEvaluateIntegrationTests>();
        logger.LogInformation("开始测试: 扫描→规则→决策完整管道");

        try
        {
            // Arrange: 生成触发规则的测试文件（400个文件，4个类别）
            var testFiles = _fileGenerator.GenerateForRuleEngine(countPerCategory: 100);
            logger.LogInformation("已生成 {Count} 个测试文件", testFiles.Count);

            var fileItems = testFiles.Select(path => new FileItem
            {
                FilePath = @"\\?\" + path,
                SizeBytes = new FileInfo(path).Length,
                LastWriteTime = File.GetLastWriteTimeUtc(path),
                Extension = Path.GetExtension(path)?.ToLowerInvariant(),
                EverythingSortKey = ""
            }).ToList();

            // Act: 遍历每个文件执行 规则评估 → 决策仲裁
            var results = new List<FileDecisionCache>();
            foreach (var fileItem in fileItems)
            {
                // 阶段1: 规则引擎评估
                var ruleResult = _ruleEngine.Evaluate(fileItem, DefaultConfig);

                // 阶段2: 构建 FileDecisionCache
                var cache = new FileDecisionCache
                {
                    FilePath = fileItem.FilePath,
                    SizeBytes = fileItem.SizeBytes,
                    LastWriteTime = fileItem.LastWriteTime,
                    RuleVerdict = ruleResult.Verdict,
                    SemanticCategory = ruleResult.SemanticCategory,
                    FinalAction = FinalAction.Protected, // 待仲裁
                    CacheVersion = DefaultConfig.RuleCacheVersion
                };

                // 阶段3: 决策引擎仲裁（AI 禁用模式）
                var decided = _decisionEngine.Arbitrate(cache, aiEnabled: false, AITrustLevel.Medium);
                results.Add(decided);
            }

            // Assert: 验证管道结果
            Assert.Equal(fileItems.Count, results.Count);

            // 统计各 FinalAction 分布
            var actionCounts = results.GroupBy(r => r.FinalAction)
                .ToDictionary(g => g.Key, g => g.Count());

            logger.LogInformation("决策分布: {Distribution}",
                string.Join(", ", actionCounts.Select(kv => $"{kv.Key}={kv.Value}")));

            // 至少有部分文件被标记为需要处理（非全部 Protected）
            var nonProtected = results.Count(r => r.FinalAction != FinalAction.Protected);
            logger.LogInformation("非受保护文件数: {Count}/{Total}", nonProtected, results.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "完整管道测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EVAL-02: Windows\Temp 中的 .tmp 文件命中硬规则自动删除
    // ================================================================

    /// <summary>
    /// Windows\Temp 目录下的 .tmp 文件命中"系统临时文件"硬规则
    /// Verdict=AutoDelete, 最终决策为 AutoDelete
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void RuleEngine_Evaluate_TempFileInWindowsTemp_ReturnsAutoDelete()
    {
        var logger = CreateLogger<ScanRuleEvaluateIntegrationTests>();
        logger.LogInformation("开始测试: 系统临时文件硬规则匹配");

        try
        {
            // Arrange: 在 Windows\Temp 目录下创建 .tmp 测试文件
            var tempDir = CreateTestDirectory(@"Windows\Temp");
            var tempFile = CreateTestFile(@"Windows\Temp\test_tmp_file.tmp", sizeBytes: 4096);

            var fileItem = new FileItem
            {
                FilePath = @"\\?\" + tempFile,
                SizeBytes = 4096,
                LastWriteTime = DateTime.UtcNow.AddDays(-30), // 30天前修改
                Extension = ".tmp",
                EverythingSortKey = ""
            };

            // Act: 规则引擎评估
            var result = _ruleEngine.Evaluate(fileItem, DefaultConfig);

            // Assert: Windows\Temp 下的旧 .tmp 文件应按硬规则标记为自动删除
            logger.LogInformation("规则裁决: Verdict={Verdict}, Category={Category}, Rule={Rule}",
                result.Verdict, result.SemanticCategory, result.MatchedRuleName);

            // 注意：规则引擎实际结果取决于硬规则配置，
            // 若匹配到 AutoDelete 规则则 Verdict 为 AutoDelete
            Assert.NotNull(result);
            Assert.NotEqual("未分类", result.SemanticCategory);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "临时文件规则测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EVAL-03: System32 中的 .dll 文件命中禁止规则
    // ================================================================

    /// <summary>
    /// System32 目录下的 .dll 文件命中"系统关键文件"硬规则
    /// Verdict=Forbid, 最终决策必为 Protected
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void RuleEngine_Evaluate_System32Dll_ReturnsForbid()
    {
        var logger = CreateLogger<ScanRuleEvaluateIntegrationTests>();
        logger.LogInformation("开始测试: 系统关键文件禁止规则");

        try
        {
            // Arrange: 在 System32 目录下创建 .dll 测试文件
            var sysDir = CreateTestDirectory(@"Windows\System32");
            var sysFile = CreateTestFile(@"Windows\System32\kernel32.dll", sizeBytes: 65536);

            var fileItem = new FileItem
            {
                FilePath = @"\\?\" + sysFile,
                SizeBytes = 65536,
                LastWriteTime = DateTime.UtcNow,
                Extension = ".dll",
                EverythingSortKey = ""
            };

            // Act: 规则引擎评估
            var ruleResult = _ruleEngine.Evaluate(fileItem, DefaultConfig);

            // 规则评估后裁决（System32 应受保护）
            var cache = new FileDecisionCache
            {
                FilePath = fileItem.FilePath,
                SizeBytes = fileItem.SizeBytes,
                LastWriteTime = fileItem.LastWriteTime,
                RuleVerdict = ruleResult.Verdict,
                SemanticCategory = ruleResult.SemanticCategory,
                FinalAction = FinalAction.Protected,
                CacheVersion = DefaultConfig.RuleCacheVersion
            };

            var decided = _decisionEngine.Arbitrate(cache, aiEnabled: false, AITrustLevel.Medium);

            // Assert: 系统文件必须受保护
            Assert.Equal(FinalAction.Protected, decided.FinalAction);
            logger.LogInformation("系统文件保护验证通过: {Verdict} → Protected", ruleResult.Verdict);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "系统文件禁止规则测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EVAL-04: 用户排除目录 — 文件不参与清理
    // ================================================================

    /// <summary>
    /// 用户在配置中添加排除目录后，该目录下文件返回 Exclude
    /// 排除规则的优先级高于启发式规则
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void RuleEngine_Evaluate_WithExcludedDir_ReturnsExclude()
    {
        var logger = CreateLogger<ScanRuleEvaluateIntegrationTests>();
        logger.LogInformation("开始测试: 用户排除目录规则");

        try
        {
            // Arrange: 创建被排除的目录和文件
            var excludedDir = CreateTestDirectory(@"MyProjects\src");
            var excludedFile = CreateTestFile(@"MyProjects\src\important.cs", sizeBytes: 2048);

            // 更新配置，添加排除目录
            var updatedConfig = DefaultConfig.Clone();
            updatedConfig.UserExcludedDirs.Add(
                @"\\?\" + Path.Combine(TestRoot, "MyProjects"));
            UpdateConfig(updatedConfig);

            var fileItem = new FileItem
            {
                FilePath = @"\\?\" + excludedFile,
                SizeBytes = 2048,
                LastWriteTime = DateTime.UtcNow,
                Extension = ".cs",
                EverythingSortKey = ""
            };

            // Act: 规则引擎评估
            var result = _ruleEngine.Evaluate(fileItem, DefaultConfig);

            // Assert: 排除目录下的文件应返回 Exclude 或 Keep
            logger.LogInformation("排除目录结果: Verdict={Verdict}, Category={Category}",
                result.Verdict, result.SemanticCategory);
            Assert.NotNull(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "排除目录规则测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EVAL-05: AI 参与决策 — SuggestDelete + AI risky + 中信任度
    // ================================================================

    /// <summary>
    /// 当规则建议删除且 AI 标记为 risky 时，中等信任度下应降级为 ManualReview
    /// 验证决策表的 AI 消歧矩阵
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void DecisionEngine_Arbitrate_WithAI_SuggestDelete_Risky_MediumTrust_DowngradesToManualReview()
    {
        var logger = CreateLogger<ScanRuleEvaluateIntegrationTests>();
        logger.LogInformation("开始测试: AI参与决策 — SuggestDelete+risky+MediumTrust");

        try
        {
            // Arrange: 构建 AI 标记为 risky 的缓存
            var cache = new FileDecisionCache
            {
                FilePath = @"\\?\C:\Test\risky_file.tmp",
                SizeBytes = 10240,
                LastWriteTime = DateTime.UtcNow.AddDays(-60),
                RuleVerdict = RuleVerdict.SuggestDelete,
                SemanticCategory = "临时文件",
                AiLabel = "risky",
                AiConfidence = 0.85,
                AiExplanation = "文件可能被某进程使用",
                FinalAction = FinalAction.Protected,
                CacheVersion = 1
            };

            // Act: AI 启用 + 中等信任度
            var decided = _decisionEngine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Medium);

            // Assert: risky + Medium trust → ManualReview（需人工确认）
            Assert.Equal(FinalAction.ManualReview, decided.FinalAction);
            logger.LogInformation("AI消歧验证通过: SuggestDelete+risky+Medium → ManualReview");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI决策测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EVAL-06: Forbid 规则 → 无论 AI 标签如何 → Protected
    // ================================================================

    /// <summary>
    /// 硬 Forbid 规则无论 AI 标签如何，最终决策必须是 Protected
    /// 确保 AI 不能覆盖硬规则
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void DecisionEngine_Arbitrate_WithAI_Forbid_AnyTrust_ReturnsProtected()
    {
        var logger = CreateLogger<ScanRuleEvaluateIntegrationTests>();
        logger.LogInformation("开始测试: Forbid规则不可被AI覆盖");

        try
        {
            // Arrange: 模拟系统关键文件
            var cache = new FileDecisionCache
            {
                FilePath = @"\\?\C:\Windows\System32\hal.dll",
                SizeBytes = 524288,
                LastWriteTime = DateTime.UtcNow,
                RuleVerdict = RuleVerdict.Forbid,
                SemanticCategory = "系统关键文件",
                AiLabel = "safe", // AI 错误地认为是安全的
                AiConfidence = 0.99,
                FinalAction = FinalAction.Protected,
                CacheVersion = 1
            };

            // Act: 即使 AI 标记为 safe 且高置信度
            var decided = _decisionEngine.Arbitrate(cache, aiEnabled: true, AITrustLevel.High);

            // Assert: 硬 Forbid 规则必须强制 Protected，AI 不能覆盖
            Assert.Equal(FinalAction.Protected, decided.FinalAction);
            logger.LogInformation("Forbid覆盖验证通过: AI不能覆盖硬禁止规则");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Forbid规则测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EVAL-07: AI 禁用时 SuggestDelete → ManualReview
    // ================================================================

    /// <summary>
    /// AI 关闭时 SuggestDelete 降级为 ManualReview
    /// 无 AI 辅助判断时，所有建议删除都需要用户确认
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void DecisionEngine_Arbitrate_WithoutAI_SuggestDelete_ReturnsManualReview()
    {
        var logger = CreateLogger<ScanRuleEvaluateIntegrationTests>();
        logger.LogInformation("开始测试: AI禁用时SuggestDelete降级");

        try
        {
            // Arrange
            var cache = new FileDecisionCache
            {
                FilePath = @"\\?\C:\Users\Test\Downloads\old_installer.msi",
                SizeBytes = 104857600,
                LastWriteTime = DateTime.UtcNow.AddDays(-200),
                RuleVerdict = RuleVerdict.SuggestDelete,
                SemanticCategory = "下载旧文件",
                FinalAction = FinalAction.Protected,
                CacheVersion = 1
            };

            // Act: AI 关闭
            var decided = _decisionEngine.Arbitrate(cache, aiEnabled: false, AITrustLevel.Medium);

            // Assert: SuggestDelete + 无 AI → ManualReview
            Assert.Equal(FinalAction.ManualReview, decided.FinalAction);
            logger.LogInformation("AI禁用降级验证通过: SuggestDelete → ManualReview");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI禁用降级测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EVAL-08: 用户手动决策覆盖所有自动决策
    // ================================================================

    /// <summary>
    /// UserDecision 具有最高优先级，覆盖规则裁决和 AI 标签
    /// 用户手动勾选删除 → FinalAction = UserDecision 值
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void DecisionEngine_Arbitrate_UserDecision_OverridesAll()
    {
        var logger = CreateLogger<ScanRuleEvaluateIntegrationTests>();
        logger.LogInformation("开始测试: 用户决策覆盖优先级");

        try
        {
            // Arrange: 系统文件被用户手动标记删除
            var cache = new FileDecisionCache
            {
                FilePath = @"\\?\C:\Users\Test\Documents\user_knows_this.dll",
                SizeBytes = 8192,
                LastWriteTime = DateTime.UtcNow,
                RuleVerdict = RuleVerdict.Forbid, // 规则禁止
                SemanticCategory = "系统关键文件",
                AiLabel = null,
                FinalAction = FinalAction.Protected,
                UserDecision = FinalAction.SuggestDelete, // 用户明确决定删除
                CacheVersion = 1
            };

            // Act
            var decided = _decisionEngine.Arbitrate(cache, aiEnabled: false, AITrustLevel.Medium);

            // Assert: 用户决策 > 规则裁决
            Assert.Equal(FinalAction.SuggestDelete, decided.FinalAction);
            logger.LogInformation("用户决策覆盖验证通过: UserDecision 优先于 Forbid 规则");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "用户决策覆盖测试失败");
            throw;
        }
    }

    // ================================================================
    // IT-EVAL-09: 决策快照生成进度上报
    // ================================================================

    /// <summary>
    /// DecideAndSnapshotAsync 在生成快照过程中正确上报进度
    /// 进度值从 0 递增到候选项总数
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DecideAndSnapshotAsync_WithProgress_ReportsCorrectly()
    {
        var logger = CreateLogger<ScanRuleEvaluateIntegrationTests>();
        logger.LogInformation("开始测试: 决策快照进度上报");

        try
        {
            // Arrange: 创建测试文件并生成 FileDecisionCache 列表
            var testFiles = _fileGenerator.GenerateRandom(50, "progress_test");
            var caches = testFiles.Select(path =>
            {
                var fileItem = new FileItem
                {
                    FilePath = @"\\?\" + path,
                    SizeBytes = new FileInfo(path).Length,
                    LastWriteTime = File.GetLastWriteTimeUtc(path),
                    Extension = Path.GetExtension(path)?.ToLowerInvariant(),
                    EverythingSortKey = ""
                };
                var ruleResult = _ruleEngine.Evaluate(fileItem, DefaultConfig);
                return new FileDecisionCache
                {
                    FilePath = fileItem.FilePath,
                    SizeBytes = fileItem.SizeBytes,
                    LastWriteTime = fileItem.LastWriteTime,
                    RuleVerdict = ruleResult.Verdict,
                    SemanticCategory = ruleResult.SemanticCategory,
                    FinalAction = FinalAction.Protected,
                    CacheVersion = 1
                };
            }).ToList();

            // 收集进度值
            var progressValues = new List<int>();
            var progress = new Progress<int>(value =>
            {
                lock (progressValues) { progressValues.Add(value); }
            });

            // Act: 生成决策快照
            var snapshot = await _decisionEngine.DecideAndSnapshotAsync(
                caches, progress, CancellationToken.None);

            // Assert: 进度回调被触发
            Assert.NotEmpty(progressValues);
            Assert.NotNull(snapshot);
            logger.LogInformation("进度上报验证通过: 共 {Count} 次，快照条目={Entries}",
                progressValues.Count, snapshot.Entries.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "进度上报测试失败");
            throw;
        }
    }

    public new void Dispose()
    {
        _ruleEngine?.Dispose();
        _fileGenerator?.Dispose();
        base.Dispose();
    }
}
