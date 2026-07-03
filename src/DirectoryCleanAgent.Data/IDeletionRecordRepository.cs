using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 删除记录仓储接口。
///
/// 负责 DeletionRecord 表的读写操作。
/// 写操作（Insert）通过批量写入队列异步执行。
/// 提供按操作 ID 和按时间范围查询能力，支持 UI 历史记录页面与回滚溯源。
/// </summary>
public interface IDeletionRecordRepository
{
    /// <summary>插入单条删除记录（入队，非阻塞）</summary>
    void Insert(DeletionRecord record);

    /// <summary>批量插入删除记录（入队，非阻塞）</summary>
    void InsertBatch(IEnumerable<DeletionRecord> records);

    /// <summary>按操作批次 ID 查询所有关联的删除记录</summary>
    Task<IReadOnlyList<DeletionRecord>> GetByOperationIdAsync(
        string operationId, CancellationToken ct = default);

    /// <summary>
    /// 按时间范围分页查询删除历史。
    /// </summary>
    /// <param name="from">起始时间（含），null 表示不限制</param>
    /// <param name="to">截止时间（含），null 表示不限制</param>
    /// <param name="limit">最大返回条数</param>
    /// <param name="offset">偏移量</param>
    Task<IReadOnlyList<DeletionRecord>> GetHistoryAsync(
        DateTime? from = null, DateTime? to = null,
        int limit = 100, int offset = 0,
        CancellationToken ct = default);

    /// <summary>获取所有唯一的操作批次 ID 及统计信息（用于历史列表）</summary>
    Task<IReadOnlyList<OperationBatchInfo>> GetBatchSummariesAsync(
        int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// 通过文件 SHA-256 哈希查找匹配的删除记录。
    /// 用于从隔离区文件恢复时定位原始完整路径（C7 隔离区管理界面）。
    ///
    /// 哈希完全匹配（忽略大小写，因 FileHash 统一以小写存储），
    /// 可能返回多条记录（同一文件被多次删除时）。
    /// 按 CreatedAt 降序排列，优先返回最近的删除记录。
    /// </summary>
    /// <param name="fileHash">文件内容的完整 SHA-256 哈希（64 字符十六进制）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>匹配的删除记录列表（可能为空）</returns>
    Task<IReadOnlyList<DeletionRecord>> GetByFileHashAsync(
        string fileHash, CancellationToken ct = default);

    /// <summary>强制刷新待写入队列</summary>
    Task FlushAsync(CancellationToken ct = default);
}
