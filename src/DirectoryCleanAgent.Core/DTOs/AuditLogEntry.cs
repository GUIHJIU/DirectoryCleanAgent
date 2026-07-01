namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 审计日志实体。
/// 记录所有配置变更、扫描、删除等关键操作，满足审计追踪需求。
///
/// 不可修改性约束（设计文档 4.2/5.1 节）：
/// 数据访问层仅暴露 Insert 方法，不提供任何 Update/Delete API。
/// 应用程序数据库连接仅拥有写入和查询权限，从根源杜绝篡改。
///
/// 对应 SQLite 的 AuditLog 表。
/// </summary>
public record AuditLogEntry
{
    /// <summary>自增主键，插入时由数据库自动分配</summary>
    public long? Id { get; init; }

    /// <summary>操作发生时间戳（UTC ISO8601）</summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>执行操作的用户 SID（如 "S-1-5-21-..."）</summary>
    public required string UserSid { get; init; }

    /// <summary>
    /// 操作类型。
    /// 如：SCAN_STARTED / SCAN_COMPLETED / FILE_DELETED / CONFIG_CHANGED /
    /// ROLLBACK_EXECUTED / QUARANTINE_CLEANED 等。
    /// </summary>
    public required string OperationType { get; init; }

    /// <summary>操作目标对象描述（如文件路径、配置项名称等）</summary>
    public required string Target { get; init; }

    /// <summary>
    /// 操作结果：SUCCESS / FAILED / CANCELLED / PARTIAL。
    /// 由调用方根据操作实际结果设置。
    /// </summary>
    public required string Result { get; init; }

    /// <summary>
    /// 操作详情 JSON。
    /// 格式：{ "message": "...", "errorCode": "...", "fileCount": N, ... }
    /// 为 null 时表示无额外详情。
    /// </summary>
    public string? Details { get; init; }
}
