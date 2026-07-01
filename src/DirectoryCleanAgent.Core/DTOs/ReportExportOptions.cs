namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 报告导出选项 DTO。
/// 由 UI 层（MainViewModel）根据用户选择构建，传递给 IReportExporter.ExportAsync()。
/// </summary>
public record ReportExportOptions
{
    /// <summary>输出文件全路径（含扩展名）</summary>
    public required string OutputPath { get; init; }

    /// <summary>导出格式</summary>
    public ReportFormat Format { get; init; } = ReportFormat.Html;

    /// <summary>是否在报告中包含受保护文件（Protected），默认不包含</summary>
    public bool IncludeProtectedFiles { get; init; } = false;

    /// <summary>HTML 报告中每组最大样本文件数。CSV 导出忽略此字段（全量输出）</summary>
    public int MaxSampleFilesPerGroup { get; init; } = 10;

    /// <summary>
    /// 报告语言。默认 zh-CN，影响 HTML 列标题和标签文本。
    /// </summary>
    public string Language { get; init; } = "zh-CN";
}

/// <summary>
/// 报告导出格式枚举
/// </summary>
public enum ReportFormat
{
    /// <summary>独立 HTML 文件 — 内嵌 CSS，无外部依赖，可直接用浏览器打开</summary>
    Html = 0,

    /// <summary>CSV 文件 — UTF-8 BOM 编码，Excel/WPS 可直接打开，中文不乱码</summary>
    Csv = 1
}
