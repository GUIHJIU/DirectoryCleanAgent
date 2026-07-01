namespace DirectoryCleanAgent.Models;

/// <summary>
/// 审计日志 UI 展示模型 — 将 AuditLogEntry 的内部字段转换为适合 DataGrid 绑定的友好格式。
/// </summary>
public class AuditLogDisplayItem
{
    /// <summary>记录 ID（自增主键）</summary>
    public long Id { get; init; }

    /// <summary>操作发生时间（本地时区格式化字符串）</summary>
    public string TimestampText { get; init; } = string.Empty;

    /// <summary>操作用户 SID 的简短显示</summary>
    public string UserDisplay { get; init; } = string.Empty;

    /// <summary>操作类型（如 SCAN_STARTED, FILE_DELETED）</summary>
    public string OperationType { get; init; } = string.Empty;

    /// <summary>操作目标（文件路径 / 配置键 / 规则名称）</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>操作结果（SUCCESS / FAILED / WARNING）</summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>详情 JSON 摘要（截断显示，ToolTip 展示完整内容）</summary>
    public string DetailsSummary { get; init; } = string.Empty;

    /// <summary>完整详情 JSON（用于 ToolTip）</summary>
    public string DetailsFull { get; init; } = string.Empty;

    /// <summary>是否有详情内容</summary>
    public bool HasDetails => !string.IsNullOrEmpty(DetailsFull);
}
