using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Localization;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Data;

namespace DirectoryCleanAgent.Services;

/// <summary>
/// 模拟运行服务 — 以流式方式复用 B1→B2→B3 管道，跳过 B4 实际删除。
///
/// 核心设计原则：
/// 1. 流式处理：所有文件通过 yield return / await foreach 流转，不将所有文件加载到内存
/// 2. 两遍处理：SimulateAsync 产出聚合统计（第一遍），StreamAnalysisAsync 产出全量流（第二遍，仅导出时调用）
/// 3. 逐文件异常隔离：单个文件处理失败不影响整个模拟流程
/// 4. 进度节流：每 N 个文件报告一次进度，避免 UI 消息泵过载
/// 5. 不执行操作：跳过 SHA-256 哈希、文件删除、墓碑写入、审计日志写入
/// </summary>
public class SimulationService : ISimulationService
{
    private readonly IFileListProvider _fileListProvider;
    private readonly IRuleEngine _ruleEngine;
    private readonly IDecisionEngine _decisionEngine;
    private readonly IOperationExecutor _operationExecutor;
    private readonly IConfigService _configService;
    private readonly ILocalizationService _localization;
    private readonly IFileDecisionCacheRepository _cacheRepo;
    private readonly ILogger<SimulationService> _logger;

    // 进度报告节流：每处理此数量的文件报告一次进度
    private const int ProgressReportInterval = 100;

    // 每组保留的最大样本文件数
    private const int MaxSamplesPerGroup = 10;

    public SimulationService(
        IFileListProvider fileListProvider,
        IRuleEngine ruleEngine,
        IDecisionEngine decisionEngine,
        IOperationExecutor operationExecutor,
        IConfigService configService,
        ILocalizationService localization,
        IFileDecisionCacheRepository cacheRepo,
        ILogger<SimulationService> logger)
    {
        _fileListProvider = fileListProvider ?? throw new ArgumentNullException(nameof(fileListProvider));
        _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
        _decisionEngine = decisionEngine ?? throw new ArgumentNullException(nameof(decisionEngine));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _cacheRepo = cacheRepo ?? throw new ArgumentNullException(nameof(cacheRepo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 执行模拟运行 — 第一遍遍历管道，产出聚合统计 + 样本文件。
    /// </summary>
    public async Task<SimulationResult> SimulateAsync(
        EverythingQueryParams queryParams,
        bool aiEnabled,
        AITrustLevel aiTrustLevel,
        IProgress<SimulationProgress>? progress,
        CancellationToken ct)
    {
        var operationId = Guid.NewGuid().ToString();
        var stopwatch = Stopwatch.StartNew();

        _logger.LogMethodEntry();
        _logger.LogInformation("模拟运行开始 OperationId={OpId}, AI={AI}, Trust={Trust}",
            operationId, aiEnabled, aiTrustLevel);

        try
        {
            // ============================================================
            // 阶段1：回收站容量预检（信息性，失败不阻断）
            // ============================================================
            RecycleBinCapacity? recycleBinCapacity = null;
            progress?.Report(SimulationProgress.CheckingCapacity());

            try
            {
                recycleBinCapacity = await _operationExecutor.QueryRecycleBinCapacityAsync(ct);
                _logger.LogInformation("回收站容量获取成功: {Capacity}", recycleBinCapacity.Value);
            }
            catch (Exception ex)
            {
                // 容量检查失败不阻断模拟运行——记录日志后继续
                _logger.LogWarning(ex, "回收站容量获取失败（非致命），模拟运行继续");
            }

            // ============================================================
            // 阶段2：流式遍历 B1→B2→B3 管道，逐文件处理并聚合分组
            // ============================================================
            progress?.Report(SimulationProgress.Enumerating());

            // 清空旧缓存，准备写入新一轮扫描的决策结果
            await _cacheRepo.ClearAsync(ct);

            var groups = new Dictionary<(string Category, FinalAction Action), GroupAccumulator>();
            int totalProcessed = 0;
            long totalProcessedBytes = 0;
            int autoDeleteCount = 0;
            int suggestDeleteCount = 0;
            int manualReviewCount = 0;
            int protectedCount = 0;
            int skippedErrors = 0;

            var userConfig = _configService.Current;

            // B1: 流式枚举文件（yield return，内存中只有当前一个 FileItem）
            await foreach (var fileItem in _fileListProvider.EnumerateFilesAsync(queryParams, ct))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // B2: 规则引擎裁决 + B3: 决策引擎仲裁
                    var (entry, cache) = ProcessSingleFile(fileItem, userConfig, aiEnabled, aiTrustLevel);

                    // 跳过未命中规则的文件（KEEP 且 SemanticCategory == "未分类"）
                    // 这些文件无需清理，也不在报告中展示
                    if (entry.RuleVerdict == RuleVerdict.Keep)
                    {
                        continue;
                    }

                    // 将决策结果写入缓存，供 FileListViewModel 加载文件列表
                    _cacheRepo.Upsert(cache);

                    // 聚合到分组
                    var key = (entry.SemanticCategory, entry.FinalAction);
                    if (!groups.TryGetValue(key, out var accumulator))
                    {
                        accumulator = new GroupAccumulator(MaxSamplesPerGroup);
                        groups[key] = accumulator;
                    }

                    accumulator.Add(fileItem.FilePath, fileItem.SizeBytes);

                    // 更新汇总统计
                    totalProcessed++;
                    totalProcessedBytes += fileItem.SizeBytes;
                    switch (entry.FinalAction)
                    {
                        case FinalAction.AutoDelete: autoDeleteCount++; break;
                        case FinalAction.SuggestDelete: suggestDeleteCount++; break;
                        case FinalAction.ManualReview: manualReviewCount++; break;
                        case FinalAction.Protected: protectedCount++; break;
                    }
                }
                catch (Exception ex)
                {
                    // 逐文件异常隔离：记录日志后跳过当前文件，继续处理下一个
                    skippedErrors++;
                    _logger.LogWarning(ex, "文件处理异常（已跳过）: {FilePath}", fileItem.FilePath);
                }

                // 进度节流：每 N 个文件报告一次，避免 UI 消息泵过载
                if (totalProcessed % ProgressReportInterval == 0)
                {
                    progress?.Report(SimulationProgress.Analyzing(
                        totalProcessed, totalProcessedBytes, fileItem.FilePath));
                }
            }

            // 刷新缓存写入队列，确保 FileListViewModel 能读到完整数据
            await _cacheRepo.FlushAsync(ct);

            // ============================================================
            // 阶段3：构建 SimulationResult
            // ============================================================
            stopwatch.Stop();

            // 从聚合字典构建 SimulationGroup 列表
            var simulationGroups = groups
                .Select(kvp => new SimulationGroup
                {
                    SemanticCategory = kvp.Key.Category,
                    FinalAction = kvp.Key.Action,
                    FileCount = kvp.Value.Count,
                    TotalSizeBytes = kvp.Value.TotalSize,
                    SampleFiles = kvp.Value.Samples
                })
                .OrderBy(g => g, Comparer<SimulationGroup>.Create(SimulationGroup.CompareByCategoryThenAction))
                .ToList();

            // 计算可释放空间：仅 AutoDelete + SuggestDelete
            long totalFreed = simulationGroups
                .Where(g => g.FinalAction is FinalAction.AutoDelete or FinalAction.SuggestDelete)
                .Sum(g => g.TotalSizeBytes);

            var result = new SimulationResult
            {
                OperationId = operationId,
                Groups = simulationGroups,
                TotalFreedBytes = totalFreed,
                TotalProcessedCount = totalProcessed,
                Elapsed = stopwatch.Elapsed,
                AutoDeleteCount = autoDeleteCount,
                SuggestDeleteCount = suggestDeleteCount,
                ManualReviewCount = manualReviewCount,
                ProtectedCount = protectedCount,
                SkippedErrorCount = skippedErrors,
                RecycleBinCapacity = recycleBinCapacity,
                QueryParams = queryParams,
                AiEnabled = aiEnabled,
                AiTrustLevel = aiTrustLevel
            };

            progress?.Report(SimulationProgress.Completed(totalProcessed, totalProcessedBytes));

            _logger.LogInformation(
                "模拟运行完成: 处理={Total}, 可释放={Freed}字节, 跳过错误={Skipped}, 耗时={Elapsed}",
                totalProcessed, totalFreed, skippedErrors, stopwatch.Elapsed);

            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning("模拟运行被取消 OperationId={OpId}, 已运行={Elapsed}", operationId, stopwatch.Elapsed);

            progress?.Report(new SimulationProgress
            {
                Phase = SimulationPhase.Cancelled,
                CompletedCount = 0,
                TotalCount = -1,
                ProcessedBytes = 0
            });

            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "模拟运行异常 OperationId={OpId}", operationId);
            throw;
        }
    }

    /// <summary>
    /// 以流式方式重新遍历 B1→B2→B3 管道，逐条产出 SimulationFileEntry。
    /// 用于导出阶段：消费方通过 await foreach 消费此流，逐条写入 HTML/CSV。
    /// </summary>
    public async IAsyncEnumerable<SimulationFileEntry> StreamAnalysisAsync(
        EverythingQueryParams queryParams,
        bool aiEnabled,
        AITrustLevel aiTrustLevel,
        IProgress<SimulationProgress>? progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        _logger.LogMethodEntry();
        _logger.LogInformation("流式分析开始 AI={AI}, Trust={Trust}", aiEnabled, aiTrustLevel);

        var userConfig = _configService.Current;
        int yieldedCount = 0;
        int skippedErrors = 0;

        // B1: 流式枚举文件
        await foreach (var fileItem in _fileListProvider.EnumerateFilesAsync(queryParams, ct))
        {
            ct.ThrowIfCancellationRequested();

            SimulationFileEntry? entry = null;
            try
            {
                // B2 + B3: 规则裁决 + 决策仲裁
                (entry, _) = ProcessSingleFile(fileItem, userConfig, aiEnabled, aiTrustLevel);

                // 跳过未命中规则的文件
                if (entry.RuleVerdict == RuleVerdict.Keep)
                {
                    continue;
                }
            }
            catch (Exception ex)
            {
                skippedErrors++;
                _logger.LogWarning(ex, "流式分析文件异常（已跳过）: {FilePath}", fileItem.FilePath);
                continue;
            }

            yieldedCount++;
            // 进度节流：每 100 条报告一次
            if (yieldedCount % ProgressReportInterval == 0)
            {
                progress?.Report(SimulationProgress.Analyzing(
                    yieldedCount, 0, fileItem.FilePath));
            }
            yield return entry;
        }

        _logger.LogInformation("流式分析完成: 产出={Count}, 跳过错误={Skipped}", yieldedCount, skippedErrors);
    }

    /// <summary>
    /// 对单个文件执行 B2（规则引擎裁决）+ B3（决策引擎仲裁），构建 SimulationFileEntry。
    /// 不计算 SHA-256 哈希——模拟运行和流式分析的仲裁使用单文件快速仲裁（Arbitrate），
    /// 而非批量快照生成（DecideAndSnapshotAsync）。
    /// </summary>
    private (SimulationFileEntry Entry, FileDecisionCache Cache) ProcessSingleFile(
        FileItem fileItem,
        UserConfig userConfig,
        bool aiEnabled,
        AITrustLevel aiTrustLevel)
    {
        // B2: 规则引擎裁决（逐文件调用，无 I/O、无锁）
        var ruleResult = _ruleEngine.Evaluate(fileItem, userConfig);

        // 构建 FileDecisionCache 供决策引擎仲裁使用
        var cache = new FileDecisionCache
        {
            FilePath = fileItem.FilePath,
            SizeBytes = fileItem.SizeBytes,
            LastWriteTime = fileItem.LastWriteTime,
            RuleVerdict = ruleResult.Verdict,
            SemanticCategory = ruleResult.SemanticCategory,
            FinalAction = FinalAction.Protected, // 占位值，Arbitrate 会重新赋值
            CacheVersion = userConfig.RuleCacheVersion
        };

        // B3: 决策引擎仲裁（单文件，不计算哈希）
        var arbitrated = _decisionEngine.Arbitrate(cache, aiEnabled, aiTrustLevel);

        var entry = new SimulationFileEntry
        {
            FilePath = fileItem.FilePath,
            SizeBytes = fileItem.SizeBytes,
            LastWriteTime = fileItem.LastWriteTime,
            Extension = fileItem.Extension,
            SemanticCategory = arbitrated.SemanticCategory,
            FinalAction = arbitrated.FinalAction,
            RuleVerdict = ruleResult.Verdict,
            AiLabel = arbitrated.AiLabel,
            AiConfidence = arbitrated.AiConfidence
        };

        return (entry, arbitrated);
    }

    /// <summary>
    /// 分组聚合累加器 — 维护每组的文件数量、总大小和样本路径列表。
    ///
    /// 线程安全：仅在单线程的 await foreach 循环中使用（WPF UI 线程串行化），
    /// 不需要加锁。如果未来改为 Parallel.ForEachAsync 并发处理，
    /// 需要替换为 ConcurrentDictionary + Interlocked。
    /// </summary>
    private sealed class GroupAccumulator
    {
        private readonly int _maxSamples;
        private readonly List<string> _samples;

        public int Count { get; private set; }
        public long TotalSize { get; private set; }
        public IReadOnlyList<string> Samples => _samples;

        public GroupAccumulator(int maxSamples)
        {
            _maxSamples = maxSamples;
            _samples = new List<string>(maxSamples);
        }

        /// <summary>
        /// 向该组添加一个文件。
        /// 只保留前 _maxSamples 条样本路径，超出部分仅更新计数和大小统计。
        /// 这是内存安全的关键保障：200 万文件场景下样本列表最多 10 条/组，
        /// 分组总数不超过 80 组，样本路径总内存 < 50KB。
        /// </summary>
        public void Add(string filePath, long sizeBytes)
        {
            Count++;
            TotalSize += sizeBytes;

            if (_samples.Count < _maxSamples)
            {
                _samples.Add(filePath);
            }
        }
    }
}
