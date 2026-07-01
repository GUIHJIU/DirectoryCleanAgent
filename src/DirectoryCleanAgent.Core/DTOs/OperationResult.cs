namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 操作执行结果 DTO。
/// 由 IOperationExecutor.ExecuteAsync() 产出，汇总删除操作的最终统计信息。
/// </summary>
public record OperationResult
{
    /// <summary>本次操作唯一标识 UUID</summary>
    public required string OperationId { get; init; }

    /// <summary>成功删除/移动的文件数量</summary>
    public int SuccessCount { get; init; }

    /// <summary>操作失败的文件数量（含文件锁定、权限不足等）</summary>
    public int FailedCount { get; init; }

    /// <summary>因文件锁定等原因降级为 manual_review 的文件数量</summary>
    public int ManualReviewCount { get; init; }

    /// <summary>操作中被用户取消的文件数量</summary>
    public int CancelledCount { get; init; }

    /// <summary>失败文件的详细信息列表</summary>
    public IReadOnlyList<FileOperationError> FailedFiles { get; init; } = Array.Empty<FileOperationError>();

    /// <summary>被降级为人工审核的文件列表</summary>
    public IReadOnlyList<FileOperationError> ManualReviewFiles { get; init; } = Array.Empty<FileOperationError>();

    /// <summary>操作是否完全成功（无任何失败或取消）</summary>
    public bool IsFullySuccessful => FailedCount == 0 && CancelledCount == 0;

    /// <summary>是否被用户中途取消</summary>
    public bool IsCancelled { get; init; }

    /// <summary>成功释放的磁盘空间（字节）</summary>
    public long FreedBytes { get; init; }

    /// <summary>操作耗时</summary>
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// 单个文件操作错误的详细信息
/// </summary>
public record FileOperationError
{
    /// <summary>文件路径（\\?\ 格式）</summary>
    public required string FilePath { get; init; }

    /// <summary>错误码（HRESULT 或异常类型名）</summary>
    public required string ErrorCode { get; init; }

    /// <summary>错误描述信息</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>原始异常（序列化时忽略）</summary>
    public Exception? Exception { get; init; }
}
