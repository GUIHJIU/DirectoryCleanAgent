using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.AI;

/// <summary>
/// AI 分析协调器 — ViewModels 与 IAiAdvisorService 之间的中间层。
///
/// 职责：
///   - 请求去重（同一文件不重复分析）
///   - 进度聚合与事件广播（ProgressChanged / AnalysisCompleted）
///   - 取消管理（CancelCurrentAnalysis）
///   - 通知策略（前台状态栏 vs 后台 Toast）
///
/// 生命周期：单例，注册在 AiServiceRegistration.AddAiLayer()。
/// </summary>
public interface IAiAnalysisCoordinator
{
    // ============================================================
    // 操作入口
    // ============================================================

    /// <summary>
    /// 分析单个文件（UI 单文件"AI分析"按钮）。
    /// 不触发全局进度事件，不纳入去重集合。
    /// </summary>
    /// <returns>分析结果；AI 不可用时返回 null</returns>
    Task<AiAnalysisResult?> AnalyzeSingleAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// 分析批量文件（工具栏按钮 / 右键菜单触发）。
    /// 内部自动去重 + 跳过已有 AiLabel 的文件。
    /// 通过 ProgressChanged 事件广播进度。
    /// </summary>
    /// <param name="files">待分析的文件缓存列表</param>
    Task<IReadOnlyList<AiAnalysisResult>> AnalyzeBatchAsync(
        IReadOnlyList<FileDecisionCache> files, CancellationToken ct = default);

    /// <summary>
    /// 触发自动分析（扫描完成后 MainViewModel 调用）。
    ///
    /// 前置条件（任一不满足直接返回空列表，不抛异常）：
    ///   1. config.AIEnabled == true
    ///   2. config.AIAutoAnalyze == true
    ///   3. config.UserMode == Expert
    ///   4. _aiAdvisorService.IsAvailable == true
    ///
    /// 筛选规则（委托给 IAiAdvisorService.AutoAnalyzeUncategorizedAsync）：
    ///   未分类 + >100MB + 无 AiLabel，最多 500 个。
    /// </summary>
    Task<IReadOnlyList<AiAnalysisResult>> TriggerAutoAnalyzeAsync(
        IReadOnlyList<FileDecisionCache> allFiles, CancellationToken ct = default);

    /// <summary>
    /// 取消当前正在进行的批量/自动分析。
    /// 已完成文件的结果保留，未开始的文件被跳过。
    /// </summary>
    void CancelCurrentAnalysis();

    // ============================================================
    // 状态查询
    // ============================================================

    /// <summary>是否有批量/自动分析正在进行中</summary>
    bool IsAnalyzing { get; }

    /// <summary>当前分析进度快照，无分析时为 null</summary>
    AiAnalysisProgress? CurrentProgress { get; }

    /// <summary>
    /// 进度变更事件（200ms 节流）。
    /// 批量分析和自动分析期间按文件粒度触发。
    /// </summary>
    event EventHandler<AiAnalysisProgress>? ProgressChanged;

    /// <summary>批量/自动分析完成事件</summary>
    event EventHandler<AiAnalysisCompletedEventArgs>? AnalysisCompleted;
}
