namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 隔离区清理操作结果 DTO。
///
/// 由 IQuarantineManager.CleanupExpiredAsync 和 PurgeAllAsync 返回。
/// 遵循与 OperationResult 一致的部分成功语义：单个文件失败不终止整个批次。
/// </summary>
public readonly struct QuarantineCleanupResult
{
    /// <summary>成功清理的文件数量</summary>
    public int CleanedFileCount { get; init; }

    /// <summary>释放的磁盘空间（字节）</summary>
    public long FreedBytes { get; init; }

    /// <summary>清理失败的文件路径列表（如权限不足、文件已被外部删除等）</summary>
    public IReadOnlyList<string> FailedFiles { get; init; }

    /// <summary>操作耗时</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>清理是否完全成功（无任何失败）</summary>
    public bool IsFullySuccessful => FailedFiles.Count == 0;

    /// <summary>人类可读的结果摘要</summary>
    public override string ToString() =>
        $"清理完成: {CleanedFileCount} 个文件, "
        + $"释放 {FreedBytes / 1024.0 / 1024.0:F1} MB, "
        + $"失败 {FailedFiles.Count} 个, "
        + $"耗时 {Elapsed.TotalSeconds:F2}s";
}
