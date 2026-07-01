using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 模拟运行结果 — 汇总按 (SemanticCategory, FinalAction) 分组后的预览数据。
///
/// 设计约束：不包含所有文件的完整列表。
/// Groups 中每个 SimulationGroup 仅保留聚合统计+每组最多 10 条样本路径。
/// 200 万文件场景下内存占用 < 50MB（分组上限 ≈ 语义分类数 × 4 种 FinalAction ≈ 80 组）。
/// 如需全量文件详情，调用 ISimulationService.StreamAnalysisAsync() 导出。
/// </summary>
public record SimulationResult
{
    /// <summary>本次模拟运行唯一标识 UUID</summary>
    public required string OperationId { get; init; }

    /// <summary>按 (语义分类, 最终操作) 分组的结果列表</summary>
    public required IReadOnlyList<SimulationGroup> Groups { get; init; }

    /// <summary>
    /// 预估可释放空间（字节）。
    /// 仅统计 FinalAction=AutoDelete 和 FinalAction=SuggestDelete 的文件大小之和。
    /// ManualReview 和 Protected 不计入此值。
    /// </summary>
    public long TotalFreedBytes { get; init; }

    /// <summary>模拟运行处理的总文件数（流经管道的全部文件）</summary>
    public int TotalProcessedCount { get; init; }

    /// <summary>模拟运行耗时</summary>
    public TimeSpan Elapsed { get; init; }

    // 以下为快捷统计属性，供 UI 直接绑定，避免每次遍历 Groups 计算

    /// <summary>AutoDelete 文件数量</summary>
    public int AutoDeleteCount { get; init; }

    /// <summary>SuggestDelete 文件数量</summary>
    public int SuggestDeleteCount { get; init; }

    /// <summary>ManualReview 文件数量</summary>
    public int ManualReviewCount { get; init; }

    /// <summary>Protected 文件数量</summary>
    public int ProtectedCount { get; init; }

    /// <summary>回收站容量信息（信息性获取，可能为 null 表示获取失败）</summary>
    public RecycleBinCapacity? RecycleBinCapacity { get; init; }

    /// <summary>
    /// 模拟时使用的查询参数快照。
    /// 导出阶段可通过此快照重新执行完全相同的查询再流式遍历管道。
    /// </summary>
    public required EverythingQueryParams QueryParams { get; init; }

    /// <summary>模拟时 AI 是否启用</summary>
    public bool AiEnabled { get; init; }

    /// <summary>模拟时 AI 信任等级</summary>
    public AITrustLevel AiTrustLevel { get; init; }
}

/// <summary>
/// (语义分类, 最终操作) 分组统计。
/// 一个 SemanticCategory 下可能有多个 FinalAction 子组（例如"系统临时文件"分类下
/// 既有 AutoDelete 也有 SuggestDelete 的文件）。
/// </summary>
public record SimulationGroup
{
    /// <summary>语义分类原始字符串（来自规则引擎），如"系统临时文件"</summary>
    public required string SemanticCategory { get; init; }

    /// <summary>最终操作决策</summary>
    public required FinalAction FinalAction { get; init; }

    /// <summary>该组文件数量</summary>
    public int FileCount { get; init; }

    /// <summary>该组文件总大小（字节）</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// 样本文件路径列表。最多保留 10 条，用于 UI 预览。
    /// 全量文件路径在导出阶段通过 StreamAnalysisAsync 获取。
    /// </summary>
    public IReadOnlyList<string> SampleFiles { get; init; } = Array.Empty<string>();

    /// <summary>分组排序键：先按 SemanticCategory 字母序，再按 FinalAction 枚举值</summary>
    public static int CompareByCategoryThenAction(SimulationGroup a, SimulationGroup b)
    {
        int catCmp = string.CompareOrdinal(a.SemanticCategory, b.SemanticCategory);
        return catCmp != 0 ? catCmp : a.FinalAction.CompareTo(b.FinalAction);
    }
}
