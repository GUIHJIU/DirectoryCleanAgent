using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 本地墓碑仓储接口。
///
/// 负责 LocalTombstone 表的读写操作。
/// 写操作（Insert）通过批量写入队列异步执行。
/// 删除操作（按 operation_id 批量删除）直接执行，用于回滚时精确清除。
/// 定期清理过期墓碑，路径复用场景下自动移除失效记录。
/// </summary>
public interface ILocalTombstoneRepository
{
    /// <summary>插入单条墓碑记录（入队，非阻塞）</summary>
    void Insert(LocalTombstone tombstone);

    /// <summary>批量插入墓碑记录（入队，非阻塞）</summary>
    void InsertBatch(IEnumerable<LocalTombstone> tombstones);

    /// <summary>按文件物理标识键查询墓碑</summary>
    Task<LocalTombstone?> GetByIdentityKeyAsync(string identityKey, CancellationToken ct = default);

    /// <summary>按文件路径匹配墓碑（降级指纹模式使用）</summary>
    Task<LocalTombstone?> GetByFilePathAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// 按操作 ID 批量删除关联的所有墓碑记录。
    /// 用于回滚成功后精确清除，直接执行不入队。
    /// </summary>
    Task<int> DeleteByOperationIdAsync(string operationId, CancellationToken ct = default);

    /// <summary>
    /// 清理过期的墓碑记录。
    /// FRN 可用时：删除超过 retentionDays（默认 30）的墓碑。
    /// FRN 不可用时：删除超过 3 天的墓碑（降级指纹模式强制过期）。
    /// </summary>
    /// <param name="frnAvailable">FRN 能力是否可用</param>
    /// <param name="retentionDays">FRN 模式下的保留天数（默认 30）</param>
    /// <param name="ct">取消令牌</param>
    Task<int> CleanupExpiredAsync(bool frnAvailable, int retentionDays = 30, CancellationToken ct = default);

    /// <summary>
    /// 获取全部墓碑记录（用于启动时加载到内存缓存）。
    /// 仅在应用启动时调用一次，需高效支持大量记录的读取。
    /// </summary>
    Task<IReadOnlyList<LocalTombstone>> GetAllAsync(CancellationToken ct = default);

    /// <summary>强制刷新待写入队列</summary>
    Task FlushAsync(CancellationToken ct = default);
}
