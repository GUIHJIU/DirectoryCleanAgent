namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 操作进度 DTO。
/// 在删除/哈希计算等长耗时操作中，通过 IProgress&lt;OperationProgress&gt; 向 UI 层报告详细进度。
/// </summary>
public readonly struct OperationProgress
{
    /// <summary>当前操作阶段</summary>
    public required OperationPhase Phase { get; init; }

    /// <summary>当前正在处理的文件路径</summary>
    public string? CurrentFilePath { get; init; }

    /// <summary>已完成的文件数量</summary>
    public int CompletedCount { get; init; }

    /// <summary>总文件数量（若未知则为 -1）</summary>
    public int TotalCount { get; init; }

    /// <summary>已完成处理的字节数</summary>
    public long ProcessedBytes { get; init; }

    /// <summary>进度百分比（0.0 ~ 100.0），总数未知时返回 -1</summary>
    public double Percentage =>
        TotalCount > 0
            ? Math.Min(100.0, (double)CompletedCount / TotalCount * 100.0)
            : -1.0;

    /// <summary>进度条是否应为不确定模式（TotalCount <= 0 时无百分比可计算）</summary>
    public bool IsIndeterminate => TotalCount <= 0;

    /// <summary>操作是否已完成</summary>
    public bool IsCompleted => TotalCount > 0 && CompletedCount >= TotalCount;

    /// <summary>创建容量预检阶段的进度</summary>
    public static OperationProgress CheckingCapacity() => new()
    {
        Phase = OperationPhase.CheckingCapacity,
        CurrentFilePath = null,
        CompletedCount = 0,
        TotalCount = -1,
        ProcessedBytes = 0
    };

    /// <summary>创建哈希计算阶段的初始进度</summary>
    public static OperationProgress Hashing(int totalCount) => new()
    {
        Phase = OperationPhase.Hashing,
        CurrentFilePath = null,
        CompletedCount = 0,
        TotalCount = totalCount,
        ProcessedBytes = 0
    };

    /// <summary>创建删除执行阶段的初始进度</summary>
    public static OperationProgress Deleting(int totalCount) => new()
    {
        Phase = OperationPhase.Deleting,
        CurrentFilePath = null,
        CompletedCount = 0,
        TotalCount = totalCount,
        ProcessedBytes = 0
    };

    public override string ToString() =>
        $"[{Phase}] {CompletedCount}/{TotalCount} ({Percentage:F1}%) — {CurrentFilePath ?? ""}";
}

/// <summary>
/// 操作执行阶段枚举
/// </summary>
public enum OperationPhase
{
    /// <summary>正在扫描/枚举文件（隔离区文件列表、目录遍历等）</summary>
    Scanning,

    /// <summary>正在检查回收站容量（删除前预检）</summary>
    CheckingCapacity,

    /// <summary>正在计算文件 SHA-256 哈希（快照生成中）</summary>
    Hashing,

    /// <summary>正在执行文件删除/移动操作</summary>
    Deleting,

    /// <summary>正在校验已删除文件（回滚准备）</summary>
    Verifying,

    /// <summary>正在执行文件恢复（回滚操作）</summary>
    Restoring,

    /// <summary>正在执行模拟运行（C5 阶段：流式分析管道，不执行实际删除）</summary>
    Simulating,

    /// <summary>操作已完成</summary>
    Completed,

    /// <summary>操作已被取消</summary>
    Cancelled
}
