using System.Collections.ObjectModel;
using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Core.Interfaces;

/// <summary>
/// 操作执行器接口 — 负责执行文件删除/移动操作。
///
/// 设计原则（设计文档 7.2/7.3 节）：
/// - 乐观删除：直接尝试删除，失败时降级为 manual_review，不做事前文件锁预检。
/// - 容量预检：在所有删除路径之前仅调用一次 SHQueryRecycleBin。
/// - 快照驱动：所有操作遍历不可变的 ReadOnlyCollection&lt;DeleteSnapshotEntry&gt;，不依赖外部缓存。
/// - 可取消：所有操作绑定 CancellationToken，支持随时中断。
/// - 路径标准化：所有文件操作使用 \\?\ 前缀路径。
///
/// 隔离区文件命名规范（B5/B6 共享契约）：
/// {SHA256前16位}_{原始文件名}_{删除时间戳Unix秒}.quarantine
/// 示例：A1B2C3D4E5F6A7B8_项目文档.docx_1718845200.quarantine
/// </summary>
public interface IOperationExecutor
{
    /// <summary>
    /// 回收站容量预检。
    /// 调用 SHQueryRecycleBin 获取回收站当前可用空间。
    /// 根据设计文档 7.2 节时序，此方法必须在快照生成和哈希计算之前调用（快速失败原则）。
    /// 若待删文件总大小 > 可用空间，UI 层弹出警告，用户选择缩小范围或确认永久删除。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>回收站容量信息</returns>
    Task<RecycleBinCapacity> QueryRecycleBinCapacityAsync(CancellationToken ct);

    /// <summary>
    /// 执行删除操作。
    /// 遍历不可变快照，对每个条目执行 IFileOperation 删除。
    /// 采用乐观删除策略：直接尝试删除，若返回 HRESULT 0x80070020（文件锁定），
    /// 则将该文件降级为 manual_review 并记录到 OperationResult.ManualReviewFiles。
    /// </summary>
    /// <param name="snapshot">
    /// 不可变删除快照（由 IDecisionEngine.DecideAndSnapshotAsync 产出）。
    /// 遍历此快照执行删除，不受外部缓存变更影响。
    /// </param>
    /// <param name="method">删除方式：回收站 / 隔离区 / 永久删除</param>
    /// <param name="progress">
    /// 操作进度报告，为 null 时不报告。
    /// 每个文件的删除完成时报告一次。
    /// </param>
    /// <param name="ct">取消令牌，触发后停止删除操作（已删除的文件不回滚）</param>
    /// <returns>操作结果汇总</returns>
    Task<OperationResult> ExecuteAsync(
        ReadOnlyCollection<DeleteSnapshotEntry> snapshot,
        DeleteMethod method,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}
