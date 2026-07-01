using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Core.Interfaces;

/// <summary>
/// 回滚管理器接口 — 负责从回收站或隔离区恢复已删除文件。
///
/// 回滚流程（设计文档 7.6 节）：
/// 1. 用户从历史记录中选择删除批次（operation_id）。
/// 2. 根据 DeletionRecord 中的原路径和 SHA-256 哈希查找并恢复文件。
/// 3. 恢复后进行 SHA-256 校验，哈希不匹配则拒绝恢复并警告用户。
/// 4. 恢复成功后，按 operation_id 批量删除 LocalTombstone 中所有关联记录。
///
/// 隔离区恢复依赖 B5/B6 共享的隔离区文件命名规范：
/// {SHA256前16位}_{原始文件名}_{删除时间戳Unix秒}.quarantine
/// </summary>
public interface IBackupManager
{
    /// <summary>
    /// 根据操作 ID 回滚指定批次的删除操作。
    /// 从回收站或隔离区中查找对应文件，校验哈希后恢复至原路径。
    /// 恢复成功后自动清除关联的墓碑记录。
    /// </summary>
    /// <param name="operationId">要回滚的删除操作 UUID</param>
    /// <param name="progress">
    /// 回滚进度报告（每恢复一个文件报告一次），为 null 时不报告。
    /// </param>
    /// <param name="ct">取消令牌，触发后停止回滚（已恢复的文件保留）</param>
    /// <returns>
    /// 回滚结果汇总：成功恢复数、失败数（含哈希不匹配）、跳过的文件路径列表
    /// </returns>
    Task<OperationResult> RollbackAsync(
        string operationId,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);

    /// <summary>
    /// 获取所有可回滚的删除操作批次列表。
    /// 用于 UI 历史记录页面，展示每次删除操作的时间、文件数、总大小。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>操作批次信息列表（按时间降序排列）</returns>
    Task<IReadOnlyList<OperationBatchInfo>> GetRollbackHistoryAsync(CancellationToken ct);

    /// <summary>
    /// 验证指定操作批次中所有文件的 SHA-256 哈希是否仍然有效。
    /// 在 UI 展示回滚详情时调用，提前告知用户哪些文件可能已损坏。
    /// </summary>
    /// <param name="operationId">操作批次 UUID</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>哈希校验结果：通过的文件数、失败的文件路径及其原因</returns>
    Task<HashVerificationResult> VerifyHashesAsync(string operationId, CancellationToken ct);

    /// <summary>
    /// 从隔离区恢复单个文件（C7 隔离区管理界面专用）。
    ///
    /// 流程：
    /// 1. 计算隔离区文件的完整 SHA-256 哈希
    /// 2. 在 DeletionRecord 中按哈希查找原始完整路径
    /// 3. 验证哈希一致性（防篡改校验）
    /// 4. 将文件移动回原始位置，路径冲突时自动添加 _restored_N 后缀
    ///
    /// 未找到匹配的 DeletionRecord 时返回 NotFound=true，
    /// 由 UI 层提示用户手动指定恢复路径。
    /// </summary>
    /// <param name="quarantineFilePath">隔离区中的文件路径（\\?\ 格式）</param>
    /// <param name="progress">进度报告，为 null 时不报告</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>单文件恢复结果</returns>
    Task<SingleFileRestoreResult> RestoreQuarantineFileAsync(
        string quarantineFilePath,
        IProgress<OperationProgress>? progress,
        CancellationToken ct);
}

/// <summary>
/// 可回滚操作批次摘要信息（用于历史记录列表展示）
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

/// <summary>
/// SHA-256 哈希校验结果
/// </summary>
public record HashVerificationResult
{
    /// <summary>校验通过的文件数量</summary>
    public required int PassedCount { get; init; }

    /// <summary>校验失败的文件数量（哈希不匹配，文件可能损坏或被篡改）</summary>
    public required int FailedCount { get; init; }

    /// <summary>找不到的文件数量（已被永久删除或从回收站清空）</summary>
    public required int NotFoundCount { get; init; }

    /// <summary>校验失败的文件详情列表</summary>
    public IReadOnlyList<HashMismatchEntry> Failures { get; init; } = Array.Empty<HashMismatchEntry>();
}

/// <summary>
/// 哈希不匹配文件条目
/// </summary>
public record HashMismatchEntry
{
    /// <summary>文件原路径</summary>
    public required string FilePath { get; init; }

    /// <summary>原始 SHA-256 哈希</summary>
    public required string ExpectedHash { get; init; }

    /// <summary>当前实际 SHA-256 哈希</summary>
    public required string ActualHash { get; init; }

    /// <summary>失败原因描述</summary>
    public required string Reason { get; init; }
}
