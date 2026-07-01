using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 文件决策缓存仓储接口。
///
/// 负责 FileDecisionCache 表的读写操作。
/// 写操作（Upsert/Clear）通过批量写入队列异步执行，不阻塞调用方。
/// 读操作直接查询数据库（WAL 模式支持并发读）。
///
/// 缓存版本控制：启动时，决策引擎对比 cache_version 与 UserConfig.RuleCacheVersion，
/// 若不一致则调用 ClearAsync 清空缓存并重新裁决（设计文档 5.2/6.5 节）。
/// </summary>
public interface IFileDecisionCacheRepository
{
    /// <summary>Upsert 单条缓存记录（入队，非阻塞）</summary>
    void Upsert(FileDecisionCache entry);

    /// <summary>批量 Upsert 缓存记录（入队，非阻塞）</summary>
    void UpsertBatch(IEnumerable<FileDecisionCache> entries);

    /// <summary>按文件路径查询缓存记录</summary>
    Task<FileDecisionCache?> GetByFilePathAsync(string filePath, CancellationToken ct = default);

    /// <summary>查询指定 final_action 的所有缓存记录</summary>
    Task<IReadOnlyList<FileDecisionCache>> GetByActionAsync(
        FinalAction action, CancellationToken ct = default);

    /// <summary>查询所有缓存记录（用于仪表板统计）</summary>
    Task<IReadOnlyList<FileDecisionCache>> GetAllAsync(CancellationToken ct = default);

    /// <summary>按语义分类标签查询缓存记录</summary>
    Task<IReadOnlyList<FileDecisionCache>> GetBySemanticCategoryAsync(
        string category, CancellationToken ct = default);

    /// <summary>按规则版本号查询缓存记录</summary>
    Task<IReadOnlyList<FileDecisionCache>> GetByVersionAsync(
        int cacheVersion, CancellationToken ct = default);

    /// <summary>清空整张缓存表（规则版本变更时调用），立即执行不入队</summary>
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>强制刷新待写入队列</summary>
    Task FlushAsync(CancellationToken ct = default);
}
