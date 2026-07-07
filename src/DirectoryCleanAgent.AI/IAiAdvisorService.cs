using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.AI.Models;

namespace DirectoryCleanAgent.AI;

/// <summary>
/// AI 顾问服务接口。
///
/// AI 顾问作为可插拔异步旁路（side-car）运行，不阻塞主扫描/删除流程。
/// 核心职责：调用大模型分析文件安全性，填充 ai_label / ai_confidence / ai_explanation 字段。
///
/// 标签契约（与 B3 DecisionEngine 6.3 节决策表对齐）：
///   - "safe"    — 文件可安全删除
///   - "unknown" — 需人工确认
///   - "risky"   — 高风险，不应删除
///   任何不在此契约内的标签值将被强制校正为 "unknown"。
///
/// 速率限制（设计文档 3.5 节/10 节）：
///   - 全局 30 RPM（每分钟请求数）
///   - 最大并发 5
///   - 手动触发单次最多 200 文件
///   - 自动触发单次最多 500 文件
/// </summary>
public interface IAiAdvisorService
{
    /// <summary>
    /// AI 服务当前是否可用。
    /// 条件：AIEnabled=true + API URL 非空 + 未达日限 + 熔断器闭合。
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>今日已用 AI API 调用次数</summary>
    int DailyUsedCount { get; }

    /// <summary>今日剩余 AI API 调用次数</summary>
    int DailyRemainingCount { get; }

    // ============================================================
    // 文件分析方法
    // ============================================================

    /// <summary>
    /// 分析单个文件（用于 UI 中用户点击单个文件的"AI分析"按钮）。
    /// 自动从 IConfigService.Current 读取 AI 配置。
    /// 成功后自动更新 FileDecisionCache 并重新仲裁 FinalAction。
    /// </summary>
    /// <param name="filePath">文件完整路径（\\?\ 格式）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>分析结果；AI不可用或分析失败时返回 null</returns>
    Task<AiAnalysisResult?> AnalyzeFileAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// 批量分析文件（手动触发 — 用户选中多个文件后点击"AI分析"）。
    /// 最多处理 200 个文件（超过部分被截断并记录警告）。
    /// 已有 ai_label 的文件会被跳过。
    /// 每个文件独立处理，单个失败不影响批次其他文件。
    /// </summary>
    /// <param name="files">待分析的文件缓存列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>所有成功完成的分析结果列表</returns>
    Task<IReadOnlyList<AiAnalysisResult>> AnalyzeFilesAsync(
        IReadOnlyList<FileDecisionCache> files, CancellationToken ct = default);

    /// <summary>
    /// 自动分析未分类大文件（仅专家模式 + AIAutoAnalyze=true 时自动触发）。
    ///
    /// 筛选规则（设计文档 3.5 节）：
    ///   1. SemanticCategory 为空 或等于 "Uncategorized"
    ///   2. SizeBytes &gt; 100 MB
    ///   3. AiLabel 为空（跳过已分析文件）
    ///   4. 按 SizeBytes 降序排列
    ///   5. 取前 500 个
    /// </summary>
    /// <param name="allFiles">全量文件缓存列表</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>所有成功完成的分析结果列表</returns>
    Task<IReadOnlyList<AiAnalysisResult>> AutoAnalyzeUncategorizedAsync(
        IReadOnlyList<FileDecisionCache> allFiles, CancellationToken ct = default);

    // ============================================================
    // 连接测试
    // ============================================================

    /// <summary>
    /// 测试 AI API 连接（设置向导 Step4 使用）。
    /// 使用传入参数而非当前配置（因为配置尚未保存）。
    /// 发送简单问询，验证 API 端点和密钥的有效性。
    /// </summary>
    /// <param name="serviceType">AI 服务类型：openai / ollama / custom</param>
    /// <param name="apiUrl">API 端点 URL</param>
    /// <param name="apiKey">API 密钥（可选，Ollama 不需要）</param>
    /// <param name="model">模型名称</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>连接测试是否成功（收到有效 HTTP 200 响应）</returns>
    Task<bool> TestConnectionAsync(string serviceType, string apiUrl, string apiKey, string model,
        CancellationToken ct = default);
}
