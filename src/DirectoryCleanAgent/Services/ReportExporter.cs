using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Logging;

namespace DirectoryCleanAgent.Services;

/// <summary>
/// 报告导出器 — 将模拟分析结果导出为 HTML 或 CSV 文件。
///
/// HTML 输出：独立文件，内嵌 CSS，按语义分类分组展示，含汇总统计。
/// CSV 输出：UTF-8 BOM 编码，扁平表格，每文件一行。
///
/// 导出策略：
/// - HTML：先消费 fileStream 全量数据到内存（仅保留分组+样本，内存可控），
///   再一次性写入 HTML 文件。这样可以在写入前确定分组结构。
/// - CSV：逐条消费 fileStream，边读边写，不积累到内存。
/// </summary>
public class ReportExporter : IReportExporter
{
    private readonly ILogger<ReportExporter> _logger;

    public ReportExporter(ILogger<ReportExporter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 导出分析报告。根据 options.Format 分派到 HTML 或 CSV 生成逻辑。
    /// </summary>
    public async Task ExportAsync(
        SimulationResult summary,
        IAsyncEnumerable<SimulationFileEntry> fileStream,
        ReportExportOptions options,
        IProgress<SimulationProgress>? progress,
        CancellationToken ct)
    {
        _logger.LogMethodEntry();
        _logger.LogInformation("开始导出报告: Format={Format}, Path={Path}", options.Format, options.OutputPath);

        try
        {
            switch (options.Format)
            {
                case ReportFormat.Html:
                    await ExportHtmlAsync(summary, fileStream, options, progress, ct);
                    break;
                case ReportFormat.Csv:
                    await ExportCsvAsync(summary, fileStream, options, progress, ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options.Format), options.Format, "不支持的导出格式");
            }

            _logger.LogInformation("报告导出完成: {Path}", options.OutputPath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("报告导出被取消");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "报告导出失败");
            throw;
        }
    }

    /// <summary>
    /// 导出 HTML 报告。
    ///
    /// HTML 结构：
    ///   1. 头部：内嵌 CSS（无外部依赖）
    ///   2. 汇总区：总文件数、可释放空间、操作分布
    ///   3. 分组区：按 SemanticCategory → FinalAction 两级分组，每组一个表格
    ///   4. 尾部：生成时间戳
    /// </summary>
    private async Task ExportHtmlAsync(
        SimulationResult summary,
        IAsyncEnumerable<SimulationFileEntry> fileStream,
        ReportExportOptions options,
        IProgress<SimulationProgress>? progress,
        CancellationToken ct)
    {
        // HTML 模式：先将流中数据收集到分组结构中（内存可控——只保留每组样本文件）
        var groups = new Dictionary<string, Dictionary<FinalAction, HtmlGroupData>>();
        int totalConsumed = 0;

        await foreach (var entry in fileStream.WithCancellation(ct))
        {
            // 跳过受保护文件（除非用户明确要求包含）
            if (entry.FinalAction == FinalAction.Protected && !options.IncludeProtectedFiles)
            {
                continue;
            }

            // 按 SemanticCategory 分组
            if (!groups.TryGetValue(entry.SemanticCategory, out var actionGroups))
            {
                actionGroups = new Dictionary<FinalAction, HtmlGroupData>();
                groups[entry.SemanticCategory] = actionGroups;
            }

            // 按 FinalAction 子分组
            if (!actionGroups.TryGetValue(entry.FinalAction, out var groupData))
            {
                groupData = new HtmlGroupData(options.MaxSampleFilesPerGroup);
                actionGroups[entry.FinalAction] = groupData;
            }

            groupData.Add(entry);
            totalConsumed++;

            // 进度报告（每 500 条报告一次，导出过程可能较长）
            if (totalConsumed % 500 == 0)
            {
                progress?.Report(new SimulationProgress
                {
                    Phase = SimulationPhase.Analyzing,
                    CompletedCount = totalConsumed,
                    TotalCount = -1,
                    CurrentFilePath = entry.FilePath
                });
            }
        }

        // 写入 HTML 文件
        var dir = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var writer = new StreamWriter(options.OutputPath, false, new UTF8Encoding(true));
        var isEnglish = options.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

        await WriteHtmlHeaderAsync(writer, summary, isEnglish);
        await WriteHtmlSummaryAsync(writer, summary, isEnglish);
        await WriteHtmlGroupsAsync(writer, groups, isEnglish);
        await WriteHtmlFooterAsync(writer, isEnglish);

        progress?.Report(new SimulationProgress
        {
            Phase = SimulationPhase.Completed,
            CompletedCount = totalConsumed,
            TotalCount = totalConsumed
        });

        _logger.LogInformation("HTML 报告已写入: 消费条目={Count}, 分组数={Groups}", totalConsumed, groups.Count);
    }

    /// <summary>
    /// 导出 CSV 报告。
    ///
    /// CSV 格式：UTF-8 BOM，首行为英文字段名，每文件一行。
    /// 字段：FilePath, SizeBytes, LastWriteTime(ISO8601), Extension, SemanticCategory,
    ///        FinalAction, RuleVerdict, AiLabel, AiConfidence
    /// </summary>
    private async Task ExportCsvAsync(
        SimulationResult summary,
        IAsyncEnumerable<SimulationFileEntry> fileStream,
        ReportExportOptions options,
        IProgress<SimulationProgress>? progress,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // 使用 UTF-8 with BOM，确保 Excel/WPS 正确识别中文
        await using var writer = new StreamWriter(options.OutputPath, false, new UTF8Encoding(true));

        // 写入 CSV 列头（英文字段名，方便编程处理）
        await writer.WriteLineAsync("FilePath,SizeBytes,LastWriteTime,Extension,SemanticCategory,FinalAction,RuleVerdict,AiLabel,AiConfidence");

        int totalWritten = 0;

        // 逐条消费流，边读边写
        await foreach (var entry in fileStream.WithCancellation(ct))
        {
            // 跳过受保护文件
            if (entry.FinalAction == FinalAction.Protected && !options.IncludeProtectedFiles)
            {
                continue;
            }

            await writer.WriteLineAsync(FormatCsvRow(entry));
            totalWritten++;

            // 进度报告（每 1000 条报告一次）
            if (totalWritten % 1000 == 0)
            {
                progress?.Report(new SimulationProgress
                {
                    Phase = SimulationPhase.Analyzing,
                    CompletedCount = totalWritten,
                    TotalCount = -1,
                    CurrentFilePath = entry.FilePath
                });
            }
        }

        progress?.Report(new SimulationProgress
        {
            Phase = SimulationPhase.Completed,
            CompletedCount = totalWritten,
            TotalCount = totalWritten
        });

        _logger.LogInformation("CSV 报告已写入: 行数={Count}", totalWritten);
    }

    // ================================================================
    // HTML 写入辅助方法（逐部分写入，不构建完整字符串）
    // ================================================================

    /// <summary>
    /// 写入 HTML 头部（DOCTYPE + head + 内嵌 CSS）。
    /// 所有样式内嵌，无外部 CSS 文件或 CDN 依赖。
    /// </summary>
    private static async Task WriteHtmlHeaderAsync(
        StreamWriter writer, SimulationResult summary, bool isEnglish)
    {
        var title = isEnglish ? "Disk Cleanup - Analysis Report" : "智能磁盘清理 — 分析报告";

        await writer.WriteLineAsync("<!DOCTYPE html>");
        await writer.WriteLineAsync("<html lang=\"" + (isEnglish ? "en" : "zh-CN") + "\">");
        await writer.WriteLineAsync("<head>");
        await writer.WriteLineAsync("<meta charset=\"utf-8\">");
        await writer.WriteLineAsync("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        await writer.WriteLineAsync($"<title>{title}</title>");
        await writer.WriteLineAsync("<style>");
        await writer.WriteLineAsync(GetEmbeddedCss());
        await writer.WriteLineAsync("</style>");
        await writer.WriteLineAsync("</head>");
        await writer.WriteLineAsync("<body>");
        await writer.WriteLineAsync($"<h1>{title}</h1>");
    }

    /// <summary>
    /// 写入 HTML 汇总统计区域。
    /// </summary>
    private static async Task WriteHtmlSummaryAsync(
        StreamWriter writer, SimulationResult summary, bool isEnglish)
    {
        var summaryTitle = isEnglish ? "Summary" : "汇总统计";
        var totalFilesLabel = isEnglish ? "Total files analyzed" : "分析文件总数";
        var freedSpaceLabel = isEnglish ? "Estimated space that can be freed" : "预估可释放空间";
        var elapsedLabel = isEnglish ? "Time elapsed" : "耗时";
        var actionsLabel = isEnglish ? "Actions breakdown" : "操作分布";
        var genTimeLabel = isEnglish ? "Generated at" : "生成时间";
        var opIdLabel = isEnglish ? "Operation ID" : "操作批次";

        await writer.WriteLineAsync("<div class=\"summary\">");
        await writer.WriteLineAsync($"<h2>{summaryTitle}</h2>");

        // 基础统计
        await writer.WriteLineAsync("<div class=\"summary-stats\">");
        await writer.WriteLineAsync($"<div class=\"stat-item\"><span class=\"stat-label\">{totalFilesLabel}</span><span class=\"stat-value\">{summary.TotalProcessedCount:N0}</span></div>");
        await writer.WriteLineAsync($"<div class=\"stat-item\"><span class=\"stat-label\">{freedSpaceLabel}</span><span class=\"stat-value\">{FormatBytes(summary.TotalFreedBytes)}</span></div>");
        await writer.WriteLineAsync($"<div class=\"stat-item\"><span class=\"stat-label\">{elapsedLabel}</span><span class=\"stat-value\">{summary.Elapsed:hh\\:mm\\:ss\\.fff}</span></div>");
        await writer.WriteLineAsync($"<div class=\"stat-item\"><span class=\"stat-label\">{genTimeLabel}</span><span class=\"stat-value\">{DateTime.Now:yyyy-MM-dd HH:mm:ss}</span></div>");
        await writer.WriteLineAsync($"<div class=\"stat-item\"><span class=\"stat-label\">{opIdLabel}</span><span class=\"stat-value stat-opid\">{summary.OperationId}</span></div>");
        await writer.WriteLineAsync("</div>");

        // 操作分布
        var autoDeleteLabel = isEnglish ? "Auto Delete" : "自动删除";
        var suggestDeleteLabel = isEnglish ? "Suggest Delete" : "建议删除";
        var manualReviewLabel = isEnglish ? "Manual Review" : "需人工确认";
        var protectedLabel = isEnglish ? "Protected" : "已保护";

        await writer.WriteLineAsync($"<h3>{actionsLabel}</h3>");
        await writer.WriteLineAsync("<div class=\"action-breakdown\">");
        await writer.WriteLineAsync($"<div class=\"action-item action-autodelete\"><span>{autoDeleteLabel}</span><span>{summary.AutoDeleteCount:N0} files / {FormatBytes(GetActionBytes(summary, FinalAction.AutoDelete))}</span></div>");
        await writer.WriteLineAsync($"<div class=\"action-item action-suggestdelete\"><span>{suggestDeleteLabel}</span><span>{summary.SuggestDeleteCount:N0} files / {FormatBytes(GetActionBytes(summary, FinalAction.SuggestDelete))}</span></div>");
        await writer.WriteLineAsync($"<div class=\"action-item action-manualreview\"><span>{manualReviewLabel}</span><span>{summary.ManualReviewCount:N0} files / {FormatBytes(GetActionBytes(summary, FinalAction.ManualReview))}</span></div>");
        await writer.WriteLineAsync($"<div class=\"action-item action-protected\"><span>{protectedLabel}</span><span>{summary.ProtectedCount:N0} files / {FormatBytes(GetActionBytes(summary, FinalAction.Protected))}</span></div>");
        await writer.WriteLineAsync("</div>");

        // 回收站容量信息
        if (summary.RecycleBinCapacity is { } cap)
        {
            var rbLabel = isEnglish ? "Recycle Bin Status" : "回收站状态";
            await writer.WriteLineAsync($"<h3>{rbLabel}</h3>");
            await writer.WriteLineAsync($"<p class=\"recycle-bin-info\">{cap}</p>");
        }

        await writer.WriteLineAsync("</div>");
    }

    /// <summary>
    /// 写入 HTML 分组文件列表（按 SemanticCategory → FinalAction 两级分组）。
    /// </summary>
    private static async Task WriteHtmlGroupsAsync(
        StreamWriter writer,
        Dictionary<string, Dictionary<FinalAction, HtmlGroupData>> groups,
        bool isEnglish)
    {
        var categoryTitle = isEnglish ? "Analysis Details by Category" : "按分类分析详情";
        var samplesLabel = isEnglish ? "Sample files (up to" : "样本文件（每组最多";
        var pathLabel = isEnglish ? "Path" : "路径";
        var sizeLabel = isEnglish ? "Size" : "大小";
        var dateLabel = isEnglish ? "Last Modified" : "最后修改";
        var aiLabel = isEnglish ? "AI Label" : "AI 标签";

        await writer.WriteLineAsync($"<h2>{categoryTitle}</h2>");

        // 按 SemanticCategory 字母序排列
        foreach (var catKvp in groups.OrderBy(k => k.Key))
        {
            var category = catKvp.Key;
            var actionGroups = catKvp.Value;

            // 计算该分类的总文件数和总大小
            int catTotal = actionGroups.Values.Sum(g => g.Count);
            long catSize = actionGroups.Values.Sum(g => g.TotalSize);

            await writer.WriteLineAsync("<div class=\"category-section\">");
            await writer.WriteLineAsync($"<h3 class=\"category-title\">{EscapeHtml(category)} <span class=\"cat-stat\">({catTotal:N0} files, {FormatBytes(catSize)})</span></h3>");

            // 按 FinalAction 枚举值排序
            foreach (var actKvp in actionGroups.OrderBy(k => (int)k.Key))
            {
                var action = actKvp.Key;
                var data = actKvp.Value;
                var actionCssClass = GetActionCssClass(action);
                var actionLabel = isEnglish ? GetEnglishActionLabel(action) : GetChineseActionLabel(action);

                await writer.WriteLineAsync($"<div class=\"sub-group {actionCssClass}\">");
                await writer.WriteLineAsync($"<h4>{actionLabel} — {data.Count:N0} files, {FormatBytes(data.TotalSize)}</h4>");
                await writer.WriteLineAsync($"<p class=\"sample-note\">{samplesLabel} {data.Samples.Count})</p>");

                // 样本文件表格
                await writer.WriteLineAsync("<table>");
                await writer.WriteLineAsync("<thead>");
                await writer.WriteLineAsync($"<tr><th>{pathLabel}</th><th>{sizeLabel}</th><th>{dateLabel}</th><th>{aiLabel}</th></tr>");
                await writer.WriteLineAsync("</thead>");
                await writer.WriteLineAsync("<tbody>");

                foreach (var sample in data.Samples)
                {
                    var ext = Path.GetExtension(sample.FilePath) ?? "";
                    await writer.WriteLineAsync("<tr>"
                        + $"<td class=\"file-path\" title=\"{EscapeHtml(sample.FilePath)}\">{EscapeHtml(TrimPathForDisplay(sample.FilePath, 80))}</td>"
                        + $"<td class=\"file-size\">{FormatBytes(sample.SizeBytes)}</td>"
                        + $"<td class=\"file-date\">{sample.LastWriteTime:yyyy-MM-dd HH:mm}</td>"
                        + $"<td class=\"file-ai\">{EscapeHtml(sample.AiLabel ?? "-")}</td>"
                        + "</tr>");
                }

                await writer.WriteLineAsync("</tbody>");
                await writer.WriteLineAsync("</table>");
                await writer.WriteLineAsync("</div>");
            }

            await writer.WriteLineAsync("</div>");
        }
    }

    /// <summary>
    /// 写入 HTML 尾部。
    /// </summary>
    private static async Task WriteHtmlFooterAsync(StreamWriter writer, bool isEnglish)
    {
        var footer = isEnglish
            ? $"Report generated by Smart Disk Cleanup Tool at {DateTime.Now:yyyy-MM-dd HH:mm:ss UTC}"
            : $"由智能磁盘清理工具生成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

        await writer.WriteLineAsync("<footer>");
        await writer.WriteLineAsync($"<p>{EscapeHtml(footer)}</p>");
        await writer.WriteLineAsync("</footer>");
        await writer.WriteLineAsync("</body>");
        await writer.WriteLineAsync("</html>");
    }

    // ================================================================
    // CSV 辅助方法
    // ================================================================

    /// <summary>
    /// 将 SimulationFileEntry 格式化为一行 CSV 文本。
    /// 对包含逗号、引号或换行的字段进行标准 CSV 转义（RFC 4180）。
    /// </summary>
    private static string FormatCsvRow(SimulationFileEntry entry)
    {
        return string.Join(",",
            EscapeCsvField(entry.FilePath),
            entry.SizeBytes.ToString(),
            entry.LastWriteTime.ToString("O"),
            EscapeCsvField(entry.Extension ?? ""),
            EscapeCsvField(entry.SemanticCategory),
            entry.FinalAction.ToString(),
            entry.RuleVerdict.ToString(),
            EscapeCsvField(entry.AiLabel ?? ""),
            entry.AiConfidence?.ToString("F4") ?? ""
        );
    }

    /// <summary>
    /// CSV 字段转义：若包含逗号、双引号、换行符，则用双引号包裹，内部双引号加倍。
    /// </summary>
    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
            return "";

        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }

        return field;
    }

    // ================================================================
    // HTML/CSS 工具方法
    // ================================================================

    /// <summary>
    /// 返回完整的嵌入式 CSS 样式表。
    /// 使用基础 CSS 2.1 属性，兼容所有主流浏览器（包括 Edge/Chrome/Firefox）。
    /// </summary>
    private static string GetEmbeddedCss()
    {
        return @"
* { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: 'Segoe UI', 'Microsoft YaHei', 'PingFang SC', sans-serif; max-width: 1200px; margin: 0 auto; padding: 20px; color: #333; background: #fafafa; }
h1 { color: #1a73e8; border-bottom: 2px solid #1a73e8; padding-bottom: 10px; margin-bottom: 20px; }
h2 { color: #444; margin: 25px 0 15px 0; }
h3 { margin: 15px 0 10px 0; }
.summary { background: #fff; border-radius: 8px; padding: 20px; box-shadow: 0 1px 3px rgba(0,0,0,0.1); margin-bottom: 20px; }
.summary-stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 12px; margin: 15px 0; }
.stat-item { display: flex; flex-direction: column; }
.stat-label { font-size: 0.85em; color: #666; }
.stat-value { font-size: 1.2em; font-weight: 600; color: #222; }
.stat-opid { font-size: 0.8em; font-family: 'Consolas', 'Courier New', monospace; word-break: break-all; }
.action-breakdown { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 10px; margin: 10px 0; }
.action-item { display: flex; justify-content: space-between; padding: 10px 14px; border-radius: 6px; font-size: 0.95em; }
.action-autodelete { background: #e6f4ea; color: #1a7d36; }
.action-suggestdelete { background: #e8f0fe; color: #1967d2; }
.action-manualreview { background: #fef7e0; color: #e37400; }
.action-protected { background: #f1f3f4; color: #5f6368; }
.category-section { background: #fff; border-radius: 8px; padding: 15px 20px; margin-bottom: 15px; box-shadow: 0 1px 3px rgba(0,0,0,0.08); }
.category-title { color: #333; border-bottom: 1px solid #e0e0e0; padding-bottom: 8px; }
.cat-stat { font-size: 0.85em; color: #888; font-weight: normal; }
.sub-group { margin: 10px 0; padding: 10px; border-left: 4px solid transparent; }
.sub-group.action-autodelete { border-left-color: #1a7d36; }
.sub-group.action-suggestdelete { border-left-color: #1967d2; }
.sub-group.action-manualreview { border-left-color: #e37400; }
.sub-group.action-protected { border-left-color: #5f6368; }
.sample-note { font-size: 0.85em; color: #888; margin-bottom: 8px; }
table { width: 100%; border-collapse: collapse; margin: 8px 0; font-size: 0.9em; }
th { background: #f5f5f5; text-align: left; padding: 8px 10px; border-bottom: 2px solid #ddd; font-weight: 600; }
td { padding: 6px 10px; border-bottom: 1px solid #eee; }
.file-path { font-family: 'Consolas', 'Courier New', monospace; font-size: 0.85em; max-width: 400px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.file-size { text-align: right; white-space: nowrap; }
.file-date { white-space: nowrap; }
.file-ai { font-size: 0.85em; }
.recycle-bin-info { font-size: 0.9em; color: #666; padding: 8px; background: #f9f9f9; border-radius: 4px; }
footer { margin-top: 30px; padding: 15px 0; border-top: 1px solid #e0e0e0; text-align: center; font-size: 0.85em; color: #999; }
@media print { body { background: #fff; } .summary, .category-section { box-shadow: none; border: 1px solid #e0e0e0; } }
";
    }

    /// <summary>
    /// HTML 实体转义，防止 XSS 和格式破坏。
    /// </summary>
    private static string EscapeHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    /// <summary>
    /// 格式化字节数为可读字符串（自动选择 GB/MB/KB）。
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F2} MB",
            >= 1_024 => $"{bytes / 1_024.0:F2} KB",
            _ => $"{bytes} B"
        };
    }

    /// <summary>
    /// 截断路径用于 HTML 展示：保留前 N 个字符，超出部分替换为 "…"。
    /// </summary>
    private static string TrimPathForDisplay(string path, int maxLength)
    {
        if (path.Length <= maxLength) return path;
        return path[..(maxLength - 1)] + "\u2026";
    }

    private static string GetActionCssClass(FinalAction action) => action switch
    {
        FinalAction.AutoDelete => "action-autodelete",
        FinalAction.SuggestDelete => "action-suggestdelete",
        FinalAction.ManualReview => "action-manualreview",
        FinalAction.Protected => "action-protected",
        _ => ""
    };

    private static string GetChineseActionLabel(FinalAction action) => action switch
    {
        FinalAction.AutoDelete => "自动删除",
        FinalAction.SuggestDelete => "建议删除",
        FinalAction.ManualReview => "需人工确认",
        FinalAction.Protected => "已保护",
        _ => action.ToString()
    };

    private static string GetEnglishActionLabel(FinalAction action) => action switch
    {
        FinalAction.AutoDelete => "Auto Delete",
        FinalAction.SuggestDelete => "Suggest Delete",
        FinalAction.ManualReview => "Manual Review",
        FinalAction.Protected => "Protected",
        _ => action.ToString()
    };

    /// <summary>
    /// 从 SimulationResult.Groups 中查找指定 FinalAction 的总字节数。
    /// </summary>
    private static long GetActionBytes(SimulationResult summary, FinalAction action)
    {
        return summary.Groups
            .Where(g => g.FinalAction == action)
            .Sum(g => g.TotalSizeBytes);
    }

    /// <summary>
    /// HTML 分组中每个 FinalAction 子组的累加数据。
    /// 与 SimulationService.GroupAccumulator 类似，但存储的是 SimulationFileEntry 样本。
    /// </summary>
    private sealed class HtmlGroupData
    {
        private readonly int _maxSamples;
        private readonly List<SimulationFileEntry> _samples;

        public int Count { get; private set; }
        public long TotalSize { get; private set; }
        public IReadOnlyList<SimulationFileEntry> Samples => _samples;

        public HtmlGroupData(int maxSamples)
        {
            _maxSamples = maxSamples;
            _samples = new List<SimulationFileEntry>(maxSamples);
        }

        public void Add(SimulationFileEntry entry)
        {
            Count++;
            TotalSize += entry.SizeBytes;

            if (_samples.Count < _maxSamples)
            {
                _samples.Add(entry);
            }
        }
    }
}
