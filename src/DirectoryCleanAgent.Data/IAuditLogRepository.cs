using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 审计日志仓储接口。
///
/// 不可修改性约束（设计文档 4.2/8 节）：
/// 本接口仅暴露 Insert 和 Query 方法，不提供任何 Update/Delete API。
/// 应用程序数据库连接仅拥有写入和查询权限，从代码根源杜绝篡改可能。
///
/// 写操作通过批量写入队列异步执行，读操作直接查询数据库。
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>插入单条审计日志（入队，非阻塞）</summary>
    void Insert(AuditLogEntry entry);

    /// <summary>
    /// 按条件分页查询审计日志。
    /// </summary>
    /// <param name="from">起始时间（含），null 表示不限制</param>
    /// <param name="to">截止时间（含），null 表示不限制</param>
    /// <param name="userSid">按用户 SID 筛选，null 表示不筛选</param>
    /// <param name="operationType">按操作类型筛选，null 表示不筛选</param>
    /// <param name="limit">最大返回条数</param>
    /// <param name="offset">偏移量</param>
    Task<IReadOnlyList<AuditLogEntry>> QueryAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? userSid = null,
        string? operationType = null,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default);

    /// <summary>
    /// 按条件查询审计日志总数（用于分页精确计数）。
    /// 查询条件与 QueryAsync 保持一致。
    /// </summary>
    /// <param name="from">起始时间（含），null 表示不限制</param>
    /// <param name="to">截止时间（含），null 表示不限制</param>
    /// <param name="userSid">按用户 SID 筛选，null 表示不筛选</param>
    /// <param name="operationType">按操作类型筛选，null 表示不筛选</param>
    Task<int> QueryCountAsync(
        DateTime? from = null,
        DateTime? to = null,
        string? userSid = null,
        string? operationType = null,
        CancellationToken ct = default);

    /// <summary>强制刷新待写入队列</summary>
    Task FlushAsync(CancellationToken ct = default);

    // 注意：接口中不暴露任何 Update / Delete 方法。
    // 审计日志的不可修改性由接口契约在编译期强制保证。
}
