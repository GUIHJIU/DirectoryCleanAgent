namespace DirectoryCleanAgent.Core.Progress;

/// <summary>
/// 标准化操作进度报告 DTO。
/// 用于在 UI 层（进度条、状态栏）和业务层之间传递进度信息。
/// </summary>
public readonly struct ProgressReport
{
    /// <summary>当前已完成数量</summary>
    public int CompletedCount { get; init; }

    /// <summary>总数量（若未知则为 -1）</summary>
    public int TotalCount { get; init; }

    /// <summary>进度百分比（0.0 ~ 100.0），若总数未知则返回 -1</summary>
    public double Percentage =>
        TotalCount > 0
            ? Math.Min(100.0, (double)CompletedCount / TotalCount * 100.0)
            : -1.0;

    /// <summary>当前处理中的项目名称或描述</summary>
    public string? CurrentItem { get; init; }

    /// <summary>进度消息（如"正在计算哈希..."）</summary>
    public string? Message { get; init; }

    /// <summary>是否已完成</summary>
    public bool IsCompleted => TotalCount > 0 && CompletedCount >= TotalCount;

    /// <summary>创建表示完成状态的进度报告</summary>
    public static ProgressReport Completed(int total, string? message = null) => new()
    {
        CompletedCount = total,
        TotalCount = total,
        CurrentItem = null,
        Message = message ?? "操作完成"
    };

    /// <summary>创建表示不确定进度的进度报告（无法预知总量，如流式扫描）</summary>
    public static ProgressReport Indeterminate(string? message = null) => new()
    {
        CompletedCount = 0,
        TotalCount = -1,
        CurrentItem = null,
        Message = message ?? "处理中..."
    };

    public override string ToString() =>
        TotalCount > 0
            ? $"{CompletedCount}/{TotalCount} ({Percentage:F1}%) — {Message}"
            : $"{CompletedCount} 项 — {Message}";
}
