using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Core.Interfaces;

/// <summary>
/// 文件列表提供器接口 — Everything 流式适配器的核心抽象。
///
/// 所有文件枚举通过 Everything IPC 实现，使用 yield return 流式产出 FileItem，
/// 确保 200 万文件场景下内存峰值 < 200MB。
/// 排序/分组请求通过 EverythingQueryParams 下推至 Everything 原生排序，不从内存排序。
///
/// 启动时执行依赖检测序列：进程存活 → 版本检查 → IPC ping → FRN 能力探测 → 索引就绪。
/// 以上任一步骤（FRN 探测除外）失败则禁止进入主界面。
/// </summary>
public interface IFileListProvider
{
    /// <summary>
    /// Everything 索引就绪检测。
    /// 查询 Everything 的 is_indexing 状态，返回 true 表示索引构建中。
    /// 索引未就绪时 UI 提供等待/后台等待/跳过三种选项。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>true 表示 Everything 正在构建索引，false 表示索引就绪</returns>
    Task<bool> IsIndexingAsync(CancellationToken ct);

    /// <summary>
    /// 流式枚举文件列表。
    /// 向 Everything 发送查询（携带原生排序参数），通过 yield return 逐文件产出 FileItem。
    /// 产出前已完成：墓碑过滤（基于 FRN 或降级指纹）、路径标准化为 \\?\ 格式。
    /// 调用方通过 await foreach 消费流，可在任意时刻通过 CancellationToken 中断。
    /// </summary>
    /// <param name="queryParams">Everything 查询参数（搜索表达式、排序、卷过滤、路径过滤等）</param>
    /// <param name="ct">取消令牌，触发后停止流式产出</param>
    /// <returns>FileItem 异步流</returns>
    IAsyncEnumerable<FileItem> EnumerateFilesAsync(
        EverythingQueryParams queryParams,
        CancellationToken ct);

    /// <summary>
    /// 启动文件变更轮询。
    /// 在 Everything 依赖检测通过后由控制层显式调用。
    /// 在此之前不启动轮询，避免 Everything64.dll 缺失时 P/Invoke 触发 DllNotFoundException。
    /// 重复调用幂等安全。
    /// </summary>
    void StartChangePolling();

    /// <summary>
    /// 文件变更事件。
    /// 当 Everything 检测到文件系统发生新增/修改/删除/重命名时触发。
    /// UI 层订阅此事件以实现增量刷新（仅刷新受影响行，不重扫全盘）。
    /// 刷新延迟 < 1 秒（设计文档性能要求）。
    /// </summary>
    event EventHandler<FileChangedEventArgs>? FileChanged;
}
