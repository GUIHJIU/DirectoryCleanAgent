using System.Collections.ObjectModel;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Decision;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace DirectoryCleanAgent.Tests.Decision;

/// <summary>
/// 决策引擎完整测试套件 — 覆盖：
/// - 所有 4 种决策表情况的仲裁逻辑
/// - 用户手动决策覆盖（UserDecision）
/// - AI 数据缺失时的回退行为
/// - 快照生成（空输入 / 全 Protected / 混合 / 进度 / 取消 / 文件不存在）
/// - SHA-256 哈希正确性
/// - 不可变性与线程安全性
/// </summary>
public class DecisionEngineTests : IDisposable
{
    private readonly Mock<IConfigService> _configServiceMock;
    private readonly DecisionEngine _engine;
    private readonly ILogger<DecisionEngine> _engineLogger;
    private readonly string _tempTestDir;
    private bool _disposed;

    public DecisionEngineTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), $"test_decision_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempTestDir);

        // Mock IConfigService — 默认 AI 启用、中等信任度
        _configServiceMock = new Mock<IConfigService>();
        _configServiceMock.Setup(c => c.Current).Returns(new UserConfig
        {
            AIEnabled = true,
            AITrustLevel = AITrustLevel.Medium
        });

        _engineLogger = NullLoggerFactory.Instance.CreateLogger<DecisionEngine>();
        _engine = new DecisionEngine(_configServiceMock.Object, _engineLogger);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (Directory.Exists(_tempTestDir))
                Directory.Delete(_tempTestDir, recursive: true);
        }
        catch { }
    }

    // ================================================================
    // 辅助工厂方法
    // ================================================================

    /// <summary>创建测试用 FileDecisionCache（无 AI 数据、无用户决策）</summary>
    private static FileDecisionCache CreateCache(
        string path, RuleVerdict verdict,
        string? aiLabel = null, double? aiConfidence = null,
        FinalAction? userDecision = null)
    {
        return new FileDecisionCache
        {
            FilePath = path,
            SizeBytes = 1024,
            LastWriteTime = DateTime.UtcNow,
            RuleVerdict = verdict,
            SemanticCategory = "测试分类",
            AiLabel = aiLabel,
            AiConfidence = aiConfidence,
            FinalAction = FinalAction.Protected, // 初始默认值，由 Arbitrate 覆盖
            UserDecision = userDecision,
            CacheVersion = 1
        };
    }

    /// <summary>创建已知内容的临时文件，返回完整路径</summary>
    private string CreateTempFile(string content)
    {
        var path = Path.Combine(_tempTestDir, $"test_{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>计算已知字符串的期望 SHA-256 哈希</summary>
    private static string ExpectedSha256(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ================================================================
    // 情况 1 测试：决定性规则（不依赖 AI）
    // ================================================================

    [Fact]
    public void Arbitrate_Forbid_ReturnsProtected()
    {
        var cache = CreateCache(@"\\?\C:\Windows\System32\test.dll", RuleVerdict.Forbid);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Medium);
        Assert.Equal(FinalAction.Protected, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_AutoDelete_ReturnsAutoDelete()
    {
        var cache = CreateCache(@"\\?\C:\Windows\Temp\test.tmp", RuleVerdict.AutoDelete);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Medium);
        Assert.Equal(FinalAction.AutoDelete, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_SuggestKeep_ReturnsProtected()
    {
        var cache = CreateCache(@"\\?\C:\Users\test\doc.docx", RuleVerdict.SuggestKeep);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Medium);
        Assert.Equal(FinalAction.Protected, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_Exclude_ReturnsProtected()
    {
        var cache = CreateCache(@"\\?\C:\Projects\excluded\file.cs", RuleVerdict.Exclude);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Medium);
        Assert.Equal(FinalAction.Protected, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_Protect_ReturnsProtected()
    {
        var cache = CreateCache(@"\\?\C:\docker\data\file.bin", RuleVerdict.Protect);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Medium);
        Assert.Equal(FinalAction.Protected, result.FinalAction);
    }

    // ================================================================
    // 情况 2 测试：SUGGEST_DELETE + AI 启用
    // ================================================================

    [Fact]
    public void Arbitrate_SuggestDelete_SafeLabel_AnyConfidence_AnyTrust_ReturnsSuggestDelete()
    {
        // safe 标签：无条件建议删除
        var cache = CreateCache(@"\\?\C:\Logs\app.log", RuleVerdict.SuggestDelete,
            aiLabel: "safe", aiConfidence: 0.3);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Low);
        Assert.Equal(FinalAction.SuggestDelete, result.FinalAction);
    }

    [Theory]
    [InlineData(AITrustLevel.Low, 0.75)]
    [InlineData(AITrustLevel.Medium, 0.75)]
    [InlineData(AITrustLevel.High, 0.75)]
    public void Arbitrate_SuggestDelete_Unknown_ConfidenceBelow08_ReturnsSuggestDelete(
        AITrustLevel trust, double confidence)
    {
        var cache = CreateCache(@"\\?\C:\Logs\unknown.log", RuleVerdict.SuggestDelete,
            aiLabel: "unknown", aiConfidence: confidence);
        var result = _engine.Arbitrate(cache, aiEnabled: true, trust);
        Assert.Equal(FinalAction.SuggestDelete, result.FinalAction);
    }

    [Theory]
    [InlineData(AITrustLevel.Low)]
    [InlineData(AITrustLevel.Medium)]
    public void Arbitrate_SuggestDelete_Unknown_HighConf_LowMedTrust_ReturnsSuggestDelete(
        AITrustLevel trust)
    {
        var cache = CreateCache(@"\\?\C:\Logs\unknown.log", RuleVerdict.SuggestDelete,
            aiLabel: "unknown", aiConfidence: 0.85);
        var result = _engine.Arbitrate(cache, aiEnabled: true, trust);
        Assert.Equal(FinalAction.SuggestDelete, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_SuggestDelete_Unknown_HighConf_HighTrust_ReturnsManualReview()
    {
        var cache = CreateCache(@"\\?\C:\Logs\unknown.log", RuleVerdict.SuggestDelete,
            aiLabel: "unknown", aiConfidence: 0.9);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.High);
        Assert.Equal(FinalAction.ManualReview, result.FinalAction);
    }

    [Theory]
    [InlineData(AITrustLevel.Low, 0.55)]
    [InlineData(AITrustLevel.Medium, 0.55)]
    [InlineData(AITrustLevel.High, 0.55)]
    public void Arbitrate_SuggestDelete_Risky_ConfidenceBelow06_ReturnsSuggestDelete(
        AITrustLevel trust, double confidence)
    {
        var cache = CreateCache(@"\\?\C:\Downloads\risky.exe", RuleVerdict.SuggestDelete,
            aiLabel: "risky", aiConfidence: confidence);
        var result = _engine.Arbitrate(cache, aiEnabled: true, trust);
        Assert.Equal(FinalAction.SuggestDelete, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_SuggestDelete_Risky_HighConf_LowTrust_ReturnsSuggestDelete()
    {
        var cache = CreateCache(@"\\?\C:\Downloads\risky.exe", RuleVerdict.SuggestDelete,
            aiLabel: "risky", aiConfidence: 0.85);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Low);
        Assert.Equal(FinalAction.SuggestDelete, result.FinalAction);
    }

    [Theory]
    [InlineData(AITrustLevel.Medium)]
    [InlineData(AITrustLevel.High)]
    public void Arbitrate_SuggestDelete_Risky_HighConf_MedHighTrust_ReturnsManualReview(
        AITrustLevel trust)
    {
        var cache = CreateCache(@"\\?\C:\Downloads\risky.exe", RuleVerdict.SuggestDelete,
            aiLabel: "risky", aiConfidence: 0.85);
        var result = _engine.Arbitrate(cache, aiEnabled: true, trust);
        Assert.Equal(FinalAction.ManualReview, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_SuggestDelete_NullAiLabel_AiEnabled_ReturnsManualReview()
    {
        var cache = CreateCache(@"\\?\C:\Logs\nodata.log", RuleVerdict.SuggestDelete,
            aiLabel: null, aiConfidence: null);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.High);
        Assert.Equal(FinalAction.ManualReview, result.FinalAction);
    }

    // ================================================================
    // 情况 3 测试：KEEP + AI 启用
    // ================================================================

    [Fact]
    public void Arbitrate_Keep_Safe_HighTrust_HighConfidence_ReturnsSuggestDelete()
    {
        var cache = CreateCache(@"\\?\C:\Data\unknown.dat", RuleVerdict.Keep,
            aiLabel: "safe", aiConfidence: 0.85);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.High);
        Assert.Equal(FinalAction.SuggestDelete, result.FinalAction);
    }

    [Theory]
    [InlineData(AITrustLevel.Low)]
    [InlineData(AITrustLevel.Medium)]
    public void Arbitrate_Keep_Safe_LowMedTrust_HighConf_ReturnsManualReview(AITrustLevel trust)
    {
        var cache = CreateCache(@"\\?\C:\Data\unknown.dat", RuleVerdict.Keep,
            aiLabel: "safe", aiConfidence: 0.85);
        var result = _engine.Arbitrate(cache, aiEnabled: true, trust);
        Assert.Equal(FinalAction.ManualReview, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_Keep_Safe_LowConfidence_ReturnsManualReview()
    {
        var cache = CreateCache(@"\\?\C:\Data\unknown.dat", RuleVerdict.Keep,
            aiLabel: "safe", aiConfidence: 0.65);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.High);
        Assert.Equal(FinalAction.ManualReview, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_Keep_Unknown_ReturnsManualReview()
    {
        var cache = CreateCache(@"\\?\C:\Data\unknown.dat", RuleVerdict.Keep,
            aiLabel: "unknown", aiConfidence: 0.95);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.High);
        Assert.Equal(FinalAction.ManualReview, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_Keep_Risky_ReturnsManualReview()
    {
        var cache = CreateCache(@"\\?\C:\Data\risky.dat", RuleVerdict.Keep,
            aiLabel: "risky", aiConfidence: 0.95);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.High);
        Assert.Equal(FinalAction.ManualReview, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_Keep_NullAiLabel_AiEnabled_ReturnsManualReview()
    {
        var cache = CreateCache(@"\\?\C:\Data\nodata.dat", RuleVerdict.Keep,
            aiLabel: null, aiConfidence: null);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.High);
        Assert.Equal(FinalAction.ManualReview, result.FinalAction);
    }

    // ================================================================
    // 情况 4 测试：AI 未启用
    // ================================================================

    [Fact]
    public void Arbitrate_SuggestDelete_AiDisabled_ReturnsSuggestDelete()
    {
        var cache = CreateCache(@"\\?\C:\Logs\app.log", RuleVerdict.SuggestDelete,
            aiLabel: "risky", aiConfidence: 0.95);
        var result = _engine.Arbitrate(cache, aiEnabled: false, AITrustLevel.High);
        Assert.Equal(FinalAction.SuggestDelete, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_Keep_AiDisabled_ReturnsProtected()
    {
        var cache = CreateCache(@"\\?\C:\Data\file.dat", RuleVerdict.Keep,
            aiLabel: "safe", aiConfidence: 0.95);
        var result = _engine.Arbitrate(cache, aiEnabled: false, AITrustLevel.High);
        Assert.Equal(FinalAction.Protected, result.FinalAction);
    }

    // ================================================================
    // 用户决策覆盖测试（UserDecision 优先级最高）
    // ================================================================

    [Fact]
    public void Arbitrate_UserDecisionProtected_OverridesForbid()
    {
        var cache = CreateCache(@"\\?\C:\System32\file.dll", RuleVerdict.Forbid,
            userDecision: FinalAction.Protected);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Medium);
        Assert.Equal(FinalAction.Protected, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_UserDecisionAutoDelete_OverridesKeep()
    {
        var cache = CreateCache(@"\\?\C:\Data\file.dat", RuleVerdict.Keep,
            userDecision: FinalAction.AutoDelete);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Medium);
        Assert.Equal(FinalAction.AutoDelete, result.FinalAction);
    }

    [Fact]
    public void Arbitrate_UserDecisionManualReview_OverridesAutoDelete()
    {
        var cache = CreateCache(@"\\?\C:\Temp\file.tmp", RuleVerdict.AutoDelete,
            userDecision: FinalAction.ManualReview);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Medium);
        Assert.Equal(FinalAction.ManualReview, result.FinalAction);
    }

    // ================================================================
    // Arbitrate 不可变性测试
    // ================================================================

    [Fact]
    public void Arbitrate_ReturnsNewInstance_DoesNotMutateInput()
    {
        var cache = CreateCache(@"\\?\C:\Test\file.log", RuleVerdict.Forbid);
        var originalAction = cache.FinalAction;

        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Medium);

        // 原始输入不变
        Assert.Equal(originalAction, cache.FinalAction);
        // 返回值是更新后的实例
        Assert.Equal(FinalAction.Protected, result.FinalAction);
        // 不同引用
        Assert.NotSame(cache, result);
    }

    [Fact]
    public void Arbitrate_NullCache_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _engine.Arbitrate(null!, aiEnabled: true, AITrustLevel.Medium));
    }

    // ================================================================
    // AI 标签大小写不敏感测试
    // ================================================================

    [Theory]
    [InlineData("SAFE")]
    [InlineData("Safe")]
    [InlineData("safe")]
    public void Arbitrate_AiLabel_CaseInsensitive(string label)
    {
        var cache = CreateCache(@"\\?\C:\Logs\app.log", RuleVerdict.SuggestDelete,
            aiLabel: label, aiConfidence: 0.5);
        var result = _engine.Arbitrate(cache, aiEnabled: true, AITrustLevel.Low);
        Assert.Equal(FinalAction.SuggestDelete, result.FinalAction);
    }

    // ================================================================
    // DecideAndSnapshotAsync 测试
    // ================================================================

    [Fact]
    public async Task DecideAndSnapshot_EmptyInput_ReturnsEmptySnapshot()
    {
        var snapshot = await _engine.DecideAndSnapshotAsync(
            Array.Empty<FileDecisionCache>(), progress: null, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.OperationId);
        Assert.Equal(0, snapshot.TotalFileCount);
        Assert.Equal(0, snapshot.TotalSizeBytes);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public async Task DecideAndSnapshot_AllProtected_ReturnsEmptyEntries()
    {
        var candidates = new List<FileDecisionCache>
        {
            CreateCache(@"\\?\C:\Windows\System32\a.dll", RuleVerdict.Forbid),
            CreateCache(@"\\?\C:\Windows\System32\b.dll", RuleVerdict.Protect),
            CreateCache(@"\\?\C:\Users\docs\c.docx", RuleVerdict.SuggestKeep),
        };

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress: null, CancellationToken.None);

        Assert.Equal(0, snapshot.TotalFileCount);
        Assert.Equal(0, snapshot.TotalSizeBytes);
    }

    [Fact]
    public async Task DecideAndSnapshot_Mixed_ReturnsOnlyNonProtected()
    {
        var tempPath = CreateTempFile("hello world");
        var candidates = new List<FileDecisionCache>
        {
            CreateCache(@"\\?\C:\Windows\System32\a.dll", RuleVerdict.Forbid),          // Protected
            CreateCache(tempPath, RuleVerdict.AutoDelete),                               // AutoDelete → 非Protected
            CreateCache(@"\\?\C:\protected\docker\b.bin", RuleVerdict.Protect),          // Protected
            CreateCache(@"\\?\C:\nonexistent\file.log", RuleVerdict.SuggestDelete,
                aiLabel: "safe", aiConfidence: 0.9),                                     // SuggestDelete
        };

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress: null, CancellationToken.None);

        // 仅 2 个非 Protected 文件（不含不存在的文件）
        Assert.True(snapshot.TotalFileCount <= 2);
        Assert.All(snapshot.Entries, e => Assert.NotEqual(FinalAction.Protected, e.FinalAction));
    }

    [Fact]
    public async Task DecideAndSnapshot_HashComputed_CorrectSha256()
    {
        var content = "Hello SHA-256 Test Content";
        var tempPath = CreateTempFile(content);
        var expectedHash = ExpectedSha256(content);

        var candidates = new List<FileDecisionCache>
        {
            CreateCache(tempPath, RuleVerdict.AutoDelete),
        };

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress: null, CancellationToken.None);

        Assert.Single(snapshot.Entries);
        Assert.Equal(expectedHash, snapshot.Entries[0].Sha256Hash);
        Assert.Equal(64, snapshot.Entries[0].Sha256Hash.Length); // SHA-256 十六进制 = 64 字符
        // 验证为小写
        Assert.Equal(snapshot.Entries[0].Sha256Hash, snapshot.Entries[0].Sha256Hash.ToLowerInvariant());
    }

    [Fact]
    public async Task DecideAndSnapshot_Progress_ReportsCorrectCount()
    {
        var tempPath = CreateTempFile("progress test");
        var candidates = new List<FileDecisionCache>
        {
            CreateCache(@"\\?\C:\Windows\System32\protected.dll", RuleVerdict.Forbid),  // 计入进度
            CreateCache(tempPath, RuleVerdict.AutoDelete),                               // 计入进度
            CreateCache(@"\\?\C:\Windows\System32\protected2.dll", RuleVerdict.Protect), // 计入进度
        };

        var reportedValues = new List<int>();
        var progress = new Progress<int>(val => reportedValues.Add(val));

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress, CancellationToken.None);

        // 上报次数 == 候选文件总数（3 个）
        Assert.Equal(3, reportedValues.Count);
        // 最终值 == 3
        Assert.Equal(3, reportedValues[^1]);
    }

    [Fact]
    public async Task DecideAndSnapshot_Cancellation_ThrowsOperationCanceledException()
    {
        // 创建一个较大的临时文件，确保哈希有足够时间被取消
        var tempPath = CreateTempFile(new string('A', 10_000_000)); // 10MB
        var candidates = new List<FileDecisionCache>
        {
            CreateCache(tempPath, RuleVerdict.AutoDelete),
        };

        using var cts = new CancellationTokenSource();
        // 立即取消 — Parallel.ForEachAsync 应在开始哈希后尽快抛出
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _engine.DecideAndSnapshotAsync(candidates, progress: null, cts.Token));
    }

    [Fact]
    public async Task DecideAndSnapshot_FileNotFound_SkipsFile()
    {
        var tempPath = CreateTempFile("will be deleted soon");
        var candidates = new List<FileDecisionCache>
        {
            CreateCache(Path.Combine(_tempTestDir, "nonexistent_file.tmp"), RuleVerdict.AutoDelete),
            CreateCache(tempPath, RuleVerdict.AutoDelete),  // 这个文件存在
        };

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress: null, CancellationToken.None);

        // 仅存在的文件进入快照
        Assert.Single(snapshot.Entries);
        Assert.Equal(tempPath, snapshot.Entries[0].FilePath);
    }

    [Fact]
    public async Task DecideAndSnapshot_OperationId_UniquePerCall()
    {
        var tempPath = CreateTempFile("unique test");

        var snapshot1 = await _engine.DecideAndSnapshotAsync(
            new[] { CreateCache(tempPath, RuleVerdict.AutoDelete) },
            progress: null, CancellationToken.None);

        var snapshot2 = await _engine.DecideAndSnapshotAsync(
            new[] { CreateCache(tempPath, RuleVerdict.AutoDelete) },
            progress: null, CancellationToken.None);

        Assert.NotEqual(snapshot1.OperationId, snapshot2.OperationId);
    }

    [Fact]
    public async Task DecideAndSnapshot_Snapshot_EntriesAreReadOnly()
    {
        var tempPath = CreateTempFile("readonly test");
        var candidates = new List<FileDecisionCache>
        {
            CreateCache(tempPath, RuleVerdict.AutoDelete),
        };

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress: null, CancellationToken.None);

        Assert.IsType<ReadOnlyCollection<DeleteSnapshotEntry>>(snapshot.Entries);
    }

    [Fact]
    public async Task DecideAndSnapshot_AllEntries_HaveCorrectOperationId()
    {
        var tempPath = CreateTempFile("operation id test");
        var candidates = new List<FileDecisionCache>
        {
            CreateCache(tempPath, RuleVerdict.AutoDelete),
        };

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress: null, CancellationToken.None);

        Assert.All(snapshot.Entries, e => Assert.Equal(snapshot.OperationId, e.OperationId));
    }

    [Fact]
    public async Task DecideAndSnapshot_TotalSizeBytes_MatchesEntries()
    {
        var tempPath1 = CreateTempFile("file one");
        var tempPath2 = CreateTempFile("file two content");

        var candidates = new List<FileDecisionCache>
        {
            CreateCache(tempPath1, RuleVerdict.AutoDelete),
            CreateCache(tempPath2, RuleVerdict.AutoDelete),
        };

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress: null, CancellationToken.None);

        Assert.Equal(2, snapshot.TotalFileCount);
        Assert.True(snapshot.TotalSizeBytes > 0);
    }

    [Fact]
    public async Task DecideAndSnapshot_ActionCounts_CorrectlyGrouped()
    {
        var tempPath1 = CreateTempFile("content a");
        var tempPath2 = CreateTempFile("content b");
        var candidates = new List<FileDecisionCache>
        {
            CreateCache(tempPath1, RuleVerdict.AutoDelete),
            CreateCache(tempPath2, RuleVerdict.SuggestDelete,
                aiLabel: "safe", aiConfidence: 0.9),
        };

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress: null, CancellationToken.None);

        Assert.True(snapshot.ActionCounts.ContainsKey(FinalAction.AutoDelete));
        Assert.True(snapshot.ActionCounts.ContainsKey(FinalAction.SuggestDelete));
    }

    [Fact]
    public async Task DecideAndSnapshot_NullCandidates_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _engine.DecideAndSnapshotAsync(null!, progress: null, CancellationToken.None));
    }

    [Fact]
    public async Task DecideAndSnapshot_UserDecisionInSnapshot()
    {
        var tempPath = CreateTempFile("user decision test");
        var candidates = new List<FileDecisionCache>
        {
            CreateCache(tempPath, RuleVerdict.AutoDelete,
                userDecision: FinalAction.ManualReview),
        };

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress: null, CancellationToken.None);

        Assert.Single(snapshot.Entries);
        Assert.Equal(FinalAction.ManualReview, snapshot.Entries[0].FinalAction);
    }

    [Fact]
    public async Task DecideAndSnapshot_FrozenAt_IsUtc()
    {
        var tempPath = CreateTempFile("frozen test");
        var before = DateTime.UtcNow;

        var snapshot = await _engine.DecideAndSnapshotAsync(
            new[] { CreateCache(tempPath, RuleVerdict.AutoDelete) },
            progress: null, CancellationToken.None);

        var after = DateTime.UtcNow;

        Assert.InRange(snapshot.FrozenAt, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public async Task DecideAndSnapshot_ToCompactJson_ValidJson()
    {
        var tempPath = CreateTempFile("json test content");
        var candidates = new List<FileDecisionCache>
        {
            CreateCache(tempPath, RuleVerdict.AutoDelete),
        };

        var snapshot = await _engine.DecideAndSnapshotAsync(
            candidates, progress: null, CancellationToken.None);

        var json = snapshot.ToCompactJson();

        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.StartsWith("[", json); // JSON 数组格式
        Assert.Contains(tempPath.Replace("\\", "\\\\"), json);
    }

    [Fact]
    public async Task DecideAndSnapshot_ConcurrentExecution_Safe()
    {
        var tempPath1 = CreateTempFile("concurrent 11111");
        var tempPath2 = CreateTempFile("concurrent 22222");

        var task1 = _engine.DecideAndSnapshotAsync(
            new[] { CreateCache(tempPath1, RuleVerdict.AutoDelete) },
            progress: null, CancellationToken.None);

        var task2 = _engine.DecideAndSnapshotAsync(
            new[] { CreateCache(tempPath2, RuleVerdict.AutoDelete) },
            progress: null, CancellationToken.None);

        await Task.WhenAll(task1, task2);

        Assert.NotEqual(task1.Result.OperationId, task2.Result.OperationId);
        Assert.Single(task1.Result.Entries);
        Assert.Single(task2.Result.Entries);
    }
}
