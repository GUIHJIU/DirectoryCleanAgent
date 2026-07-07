using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Core.Interfaces;

/// <summary>
/// 报告导出器接口 — 将模拟分析结果导出为 HTML 或 CSV 文件。
///
/// HTML 输出：独立文件，内嵌 CSS，按语义分类分组展示，含汇总统计。
/// CSV 输出：UTF-8 BOM 编码，扁平表格，每文件一行。
///
/// 导出器通过 IAsyncEnumerable&lt;SimulationFileEntry&gt; 流式消费文件数据，
/// 不将所有条目加载到内存中。
/// </summary>
public interface IReportExporter
{
    /// <summary>
    /// 将模拟分析结果导出为报告文件。
    ///
    /// 内部根据 options.Format 分派到 HTML 或 CSV 生成逻辑。
    /// HTML 仅输出分组汇总 + 每组样本文件（受 MaxSampleFilesPerGroup 控制）。
    /// CSV 逐条输出 fileStream 中的所有条目。
    /// </summary>
    /// <param name="summary">模拟运行摘要（用于 HTML 顶部的统计汇总区域）</param>
    /// <param name="fileStream">
    /// 文件分析结果异步流。导出器逐条消费此流：
    /// - HTML 模式下消费全部条目到分组字典中，每组仅保留 MaxSampleFilesPerGroup 条样本
    ///   （2M 文件 × ~80 组 × 10 样本 ≈ 内存 &lt; 1MB，远低于 200MB 硬约束）
    /// - CSV 模式下逐条写入，不累积任何条目
    /// </param>
    /// <param name="options">导出选项（格式、路径、语言、是否包含受保护文件等）</param>
    /// <param name="progress">进度报告，为 null 时不报告</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>导出任务</returns>
    Task ExportAsync(
        SimulationResult summary,
        IAsyncEnumerable<SimulationFileEntry> fileStream,
        ReportExportOptions options,
        IProgress<SimulationProgress>? progress,
        CancellationToken ct);
}
