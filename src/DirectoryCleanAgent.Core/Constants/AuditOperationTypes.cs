namespace DirectoryCleanAgent.Core.Constants;

/// <summary>
/// 审计日志操作类型常量。
///
/// 使用 const string 而非 enum 以保持与 SQLite AuditLog 表 operation_type 字段（TEXT）
/// 的存储一致性，同时提供编译期引用点避免字符串散落各处。
///
/// 对应设计文档 4.2 节审计日志操作类型定义。
/// </summary>
public static class AuditOperationTypes
{
    /// <summary>扫描启动</summary>
    public const string ScanStarted = "SCAN_STARTED";

    /// <summary>扫描完成</summary>
    public const string ScanCompleted = "SCAN_COMPLETED";

    /// <summary>文件已删除</summary>
    public const string FileDeleted = "FILE_DELETED";

    /// <summary>配置已变更</summary>
    public const string ConfigChanged = "CONFIG_CHANGED";

    /// <summary>规则已更新</summary>
    public const string RuleUpdated = "RULE_UPDATED";

    /// <summary>回滚已执行</summary>
    public const string RollbackExecuted = "ROLLBACK_EXECUTED";

    /// <summary>隔离区已清理</summary>
    public const string QuarantineCleaned = "QUARANTINE_CLEANED";

    /// <summary>AI 分析</summary>
    public const string AiAnalysis = "AI_ANALYSIS";

    /// <summary>应用启动</summary>
    public const string AppStartup = "APP_STARTUP";

    /// <summary>应用关闭</summary>
    public const string AppShutdown = "APP_SHUTDOWN";

    /// <summary>
    /// UI 过滤下拉框使用的操作类型列表（含"(全部)"选项）。
    /// 用于 AuditLogViewModel.OperationTypes 的初始化。
    /// </summary>
    public static readonly IReadOnlyList<string> AllForFilter = new[]
    {
        "(全部)",
        ScanStarted,
        ScanCompleted,
        FileDeleted,
        ConfigChanged,
        RuleUpdated,
        RollbackExecuted,
        QuarantineCleaned,
        AiAnalysis,
        AppStartup,
        AppShutdown,
    };
}
