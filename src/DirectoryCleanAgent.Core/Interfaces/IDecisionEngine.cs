using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Core.Interfaces;

/// <summary>
/// 决策引擎接口 — 仲裁最终操作决策并生成删除快照。
///
/// 核心职责：
/// 1. 结合规则裁决（RuleVerdict）、AI 标签（ai_label + confidence）和用户策略（AI 信任度、模式）
///    按照设计文档 6.3 节的完整决策表仲裁 final_action。
/// 2. 对非 protected 的文件异步计算 SHA-256 哈希，支持进度上报和取消。
/// 3. 生成不可变的 ReadOnlyCollection&lt;DeleteSnapshotEntry&gt; 快照并封装为 DecisionSnapshot。
///
/// 时序约束（设计文档 7.2 节）：
/// 容量预检（B4.QueryRecycleBinCapacityAsync）→ 决策+哈希+快照（本方法）→ 执行删除（B4.ExecuteAsync）。
/// 容量预检必须在哈希计算之前通过，遵循快速失败原则。
/// </summary>
public interface IDecisionEngine
{
    /// <summary>
    /// 执行决策仲裁、SHA-256 哈希计算及快照生成。
    ///
    /// 处理流程：
    /// 1. 遍历候选文件列表，根据决策表为每个文件计算 final_action
    /// 2. 筛选出 final_action != Protected 的文件
    /// 3. 使用 Parallel.ForEachAsync(ProcessorCount) 控制并发，逐文件计算 SHA-256 哈希
    /// 4. 每完成一个文件报告进度
    /// 5. 全部完成后深拷贝为 ReadOnlyCollection&lt;DeleteSnapshotEntry&gt;
    /// 6. 封装为 DecisionSnapshot 返回
    /// </summary>
    /// <param name="candidates">待决策的文件缓存列表（已命中规则的文件）</param>
    /// <param name="progress">
    /// 哈希计算进度报告（0 到 candidates.Count），为 null 时不报告。
    /// 每完成一个文件的仲裁即调用一次 Report(completedCount)（含 Protected 文件立即上报和哈希计算完成后上报）。
    /// </param>
    /// <param name="ct">取消令牌，触发后停止哈希计算并抛出 OperationCanceledException</param>
    /// <returns>不可变决策快照，包含所有待操作文件的冻结信息</returns>
    Task<DecisionSnapshot> DecideAndSnapshotAsync(
        IReadOnlyList<FileDecisionCache> candidates,
        IProgress<int>? progress,
        CancellationToken ct);

    /// <summary>
    /// 对单个已缓存的文件执行决策仲裁（不计算哈希）。
    /// 用于 UI 实时展示——用户切换 AI 信任度或模式后即时刷新 final_action，
    /// 无需重新扫描和计算哈希。
    /// </summary>
    /// <param name="cache">单个文件的决策缓存条目</param>
    /// <param name="aiEnabled">是否启用 AI</param>
    /// <param name="aiTrustLevel">当前 AI 信任等级</param>
    /// <returns>更新后的 FileDecisionCache（FinalAction 已重新仲裁）</returns>
    FileDecisionCache Arbitrate(
        FileDecisionCache cache,
        bool aiEnabled,
        AITrustLevel aiTrustLevel);
}
