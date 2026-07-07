using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Core.Interfaces;

/// <summary>
/// 模拟运行服务接口 — 以流式方式复用 B1→B2→B3 管道，跳过 B4 实际删除。
///
/// 核心设计：
/// - SimulateAsync：第一遍遍历，产出聚合统计 + 每组最多 N 条样本路径（内存安全）。
/// - StreamAnalysisAsync：第二遍遍历（仅在导出时调用），逐条 yield return 给导出器，
///   避免在内存中积累全量文件详情。
///
/// 两遍处理带来额外遍历开销，但换取了内存安全。导出是低频操作，这个权衡可以接受。
/// </summary>
public interface ISimulationService
{
    /// <summary>
    /// 执行模拟运行（流式处理管道）。
    ///
    /// 处理流程：
    /// 1. 容量预检：调用 B4.QueryRecycleBinCapacityAsync（信息性，失败不阻断）
    /// 2. B1 流式枚举：await foreach IFileListProvider.EnumerateFilesAsync
    /// 3. B2 逐文件规则评估：IRuleEngine.Evaluate(file, config)
    /// 4. B3 逐文件决策仲裁：IDecisionEngine.Arbitrate(cache, aiEnabled, aiTrustLevel)
    /// 5. 按 (SemanticCategory, FinalAction) 聚合分组，每组保留 ≤N 条样本路径
    /// 6. 返回 SimulationResult
    ///
    /// 不执行的操作：SHA-256 哈希、文件删除、墓碑写入、审计日志写入。
    /// </summary>
    /// <param name="queryParams">Everything 查询参数（卷过滤、路径过滤、排序等）</param>
    /// <param name="aiEnabled">是否启用 AI 顾问</param>
    /// <param name="aiTrustLevel">AI 信任等级（影响决策仲裁逻辑）</param>
    /// <param name="progress">进度报告（SimulationProgress 类型），为 null 时不报告</param>
    /// <param name="ct">取消令牌，触发后停止流式处理并抛出 OperationCanceledException</param>
    /// <returns>模拟运行结果（分组统计 + 样本文件 + 汇总数据）</returns>
    Task<SimulationResult> SimulateAsync(
        EverythingQueryParams queryParams,
        bool aiEnabled,
        AITrustLevel aiTrustLevel,
        IProgress<SimulationProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 以流式方式重新遍历 B1→B2→B3 管道，逐条产出 SimulationFileEntry。
    ///
    /// 用于导出阶段：消费方通过 await foreach 消费此流，
    /// 将每条 SimulationFileEntry 逐条写入 HTML/CSV，不积累在内存中。
    ///
    /// 此方法与 SimulateAsync 共享同一个管道逻辑，但不做聚合——直接 yield return。
    /// </summary>
    /// <param name="queryParams">Everything 查询参数（应与 SimulateAsync 使用相同的参数以保持一致性）</param>
    /// <param name="aiEnabled">AI 启用状态</param>
    /// <param name="aiTrustLevel">AI 信任等级</param>
    /// <param name="progress">进度报告（SimulationProgress 类型），为 null 时不报告</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>SimulationFileEntry 异步流，逐条产出每个文件的模拟分析结果</returns>
    IAsyncEnumerable<SimulationFileEntry> StreamAnalysisAsync(
        EverythingQueryParams queryParams,
        bool aiEnabled,
        AITrustLevel aiTrustLevel,
        IProgress<SimulationProgress>? progress,
        CancellationToken ct);
}

/// <summary>
/// 模拟分析流中的单条文件记录。
/// 由 StreamAnalysisAsync 逐条产出，供 ReportExporter 消费写入导出文件。
/// </summary>
public record SimulationFileEntry
{
    /// <summary>文件完整路径（\\?\ 格式）</summary>
    public required string FilePath { get; init; }

    /// <summary>文件大小（字节）</summary>
    public required long SizeBytes { get; init; }

    /// <summary>文件最后写入时间（UTC，来自 Everything 索引）</summary>
    public required DateTime LastWriteTime { get; init; }

    /// <summary>文件扩展名（小写，含点号），如 ".log"</summary>
    public string? Extension { get; init; }

    /// <summary>语义分类字符串（来自规则引擎），如"系统临时文件"</summary>
    public required string SemanticCategory { get; init; }

    /// <summary>最终操作决策（来自决策引擎仲裁）</summary>
    public required FinalAction FinalAction { get; init; }

    /// <summary>规则裁决结果（用于报告详情）</summary>
    public RuleVerdict RuleVerdict { get; init; }

    /// <summary>AI 顾问标签（safe/unknown/risky），未调用 AI 时为 null</summary>
    public string? AiLabel { get; init; }

    /// <summary>AI 置信度（0.0~1.0），未调用 AI 时为 null</summary>
    public double? AiConfidence { get; init; }
}
