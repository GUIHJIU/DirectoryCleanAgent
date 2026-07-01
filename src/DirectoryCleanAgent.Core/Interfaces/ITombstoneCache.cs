using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Core.Interfaces;

/// <summary>
/// 墓碑内存缓存抽象接口。
///
/// 在 EverythingFileListProvider 流式枚举的 yield return 循环中，
/// 每个 FileItem 产出前需查询墓碑缓存以判断文件是否已被删除。
/// 为避免每次查询都访问 SQLite，启动时将全部墓碑加载到此内存缓存。
///
/// 设计要点：
/// - 定义在 Core 层，使 Everything 项目（读取方）和 Operations 项目（写入方）
///   无需依赖 Data 项目即可依赖此接口。
/// - 内部使用 ConcurrentDictionary 保证线程安全。
/// - 支持两级匹配策略：FRN 精确匹配 和 降级指纹匹配。
/// </summary>
public interface ITombstoneCache
{
    /// <summary>
    /// 启动时从持久化存储全量加载墓碑到内存缓存。
    /// 仅在应用启动后调用一次。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// 检查指定文件是否已被墓碑标记（已删除）。
    ///
    /// 两级匹配策略：
    /// 1. 若 frnKey 非空 → 查询 FRN 缓存（VolumeGuid:FRN 精确匹配）
    /// 2. 若 frnKey 为空且 fingerprintKey 非空 → 查询指纹缓存（Size:LastWriteTime 降级匹配）
    /// 3. 两者均为空 → 返回 false（无法判断，假定未删除）
    /// </summary>
    /// <param name="frnKey">FRN 精确匹配键，格式 "{VolumeGuid}:{FRN}"，FRN 不可用时为 null</param>
    /// <param name="fingerprintKey">指纹降级键，格式 "{Size}:{LastWriteTime:O}"，FRN 可用时为 null</param>
    /// <returns>true 表示文件已被删除（命中墓碑），应在枚举中跳过</returns>
    bool IsTombstoned(string? frnKey, string? fingerprintKey);

    /// <summary>
    /// 向内存缓存添加单条墓碑（不负责持久化）。
    /// 调用方需同时通过 ILocalTombstoneRepository 写入数据库。
    /// </summary>
    void Add(LocalTombstone tombstone);

    /// <summary>
    /// 批量添加墓碑到内存缓存（不负责持久化）。
    /// 调用方需同时通过 ILocalTombstoneRepository 批量写入数据库。
    /// </summary>
    void AddBatch(IEnumerable<LocalTombstone> tombstones);

    /// <summary>
    /// 按操作 ID 从内存缓存中移除关联的所有墓碑记录。
    /// 用于回滚成功后精确清除。
    /// </summary>
    /// <param name="operationId">删除操作 UUID</param>
    void RemoveByOperationId(string operationId);

    /// <summary>
    /// 按条件移除墓碑记录。
    /// 用于过期清理（如 FRN 不可用时强制 3 天过期）。
    /// </summary>
    /// <param name="predicate">移除条件</param>
    /// <returns>移除的记录数</returns>
    int RemoveWhere(Func<LocalTombstone, bool> predicate);

    /// <summary>当前内存缓存中的墓碑总数</summary>
    int Count { get; }
}
