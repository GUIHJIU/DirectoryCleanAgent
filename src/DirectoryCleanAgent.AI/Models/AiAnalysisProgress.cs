namespace DirectoryCleanAgent.AI.Models;

/// <summary>
/// AI 分析进度快照，通过 IAiAnalysisCoordinator.ProgressChanged 事件广播。
/// 按 200ms 最小间隔节流发布。
/// </summary>
public sealed class AiAnalysisProgress
{
    /// <summary>总任务数</summary>
    public int TotalCount { get; init; }

    /// <summary>已完成任务数（含成功和失败）。SucceededCount = CompletedCount - FailedCount</summary>
    public int CompletedCount { get; init; }

    /// <summary>失败任务数（是 CompletedCount 的子集）</summary>
    public int FailedCount { get; init; }

    /// <summary>正在处理的文件完整路径，无时为 null</summary>
    public string? CurrentFilePath { get; init; }

    /// <summary>是否已请求取消</summary>
    public bool IsCancelRequested { get; init; }

    /// <summary>便捷属性：成功完成数 = CompletedCount - FailedCount</summary>
    public int SucceededCount => CompletedCount - FailedCount;
}

/// <summary>
/// 批量/自动 AI 分析完成事件参数。
/// </summary>
public sealed class AiAnalysisCompletedEventArgs : EventArgs
{
    /// <summary>成功完成数</summary>
    public int SuccessCount { get; init; }

    /// <summary>失败数</summary>
    public int FailedCount { get; init; }

    /// <summary>是否因用户取消而中止</summary>
    public bool WasCancelled { get; init; }
}
