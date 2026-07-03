using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 可回滚操作批次摘要信息（用于历史记录列表展示）。
///
/// 由 DeletionRecordRepository.GetBatchSummariesAsync 从 DeletionRecord 表聚合生成，
/// 供 IBackupManager.GetRollbackHistoryAsync 和 UI 历史记录页面使用。
///
/// 原本定义在 IBackupManager.cs 中（与回滚管理器接口耦合），
/// 已按 A3 代码审计建议移至独立 DTO 文件以解除数据层与接口层的隐式耦合。
/// </summary>
public record OperationBatchInfo
{
    /// <summary>操作批次 UUID</summary>
    public required string OperationId { get; init; }

    /// <summary>操作执行时间戳（UTC）</summary>
    public required DateTime ExecutedAt { get; init; }

    /// <summary>批次中的文件总数</summary>
    public required int FileCount { get; init; }

    /// <summary>批次释放的总空间（字节）</summary>
    public required long TotalSizeBytes { get; init; }

    /// <summary>删除方式</summary>
    public required DeleteMethod Method { get; init; }

    /// <summary>是否仍可回滚（部分文件可能已被永久删除或过期）</summary>
    public required bool IsRollbackable { get; init; }
}
