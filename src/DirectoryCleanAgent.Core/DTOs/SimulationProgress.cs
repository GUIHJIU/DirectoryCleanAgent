namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 模拟运行进度报告 DTO。
/// 与 OperationProgress 分离的原因：模拟运行使用的是分析管道（枚举→裁决→仲裁），
/// 而非删除管道（哈希→删除→验证），两者的阶段语义完全不同。
/// UI 层通过适配器将 SimulationProgress 映射为 OperationProgress 以复用进度条绑定。
/// </summary>
public readonly struct SimulationProgress
{
    /// <summary>模拟运行当前阶段</summary>
    public SimulationPhase Phase { get; init; }

    /// <summary>已处理的文件数</summary>
    public int CompletedCount { get; init; }

    /// <summary>
    /// 总文件数。流式场景下 Everything 不提供总数，此值为 -1。
    /// UI 层应据此切换进度条为不确定模式（IsIndeterminate）。
    /// </summary>
    public int TotalCount { get; init; }

    /// <summary>已处理的字节总数（用于展示扫描吞吐）</summary>
    public long ProcessedBytes { get; init; }

    /// <summary>当前正在处理的文件路径</summary>
    public string? CurrentFilePath { get; init; }

    /// <summary>
    /// 进度百分比。TotalCount > 0 时返回 0.0~100.0，否则返回 -1 表示不确定进度。
    /// </summary>
    public double Percentage =>
        TotalCount > 0 ? Math.Min(100.0, (double)CompletedCount / TotalCount * 100.0) : -1.0;

    /// <summary>创建容量检查阶段的进度标记</summary>
    public static SimulationProgress CheckingCapacity() => new()
    {
        Phase = SimulationPhase.CheckingCapacity,
        CompletedCount = 0,
        TotalCount = -1,
        ProcessedBytes = 0
    };

    /// <summary>创建文件枚举阶段的进度标记</summary>
    public static SimulationProgress Enumerating() => new()
    {
        Phase = SimulationPhase.Enumerating,
        CompletedCount = 0,
        TotalCount = -1,
        ProcessedBytes = 0
    };

    /// <summary>创建分析阶段的进度标记</summary>
    public static SimulationProgress Analyzing(int completed, long processedBytes, string? currentFile) => new()
    {
        Phase = SimulationPhase.Analyzing,
        CompletedCount = completed,
        TotalCount = -1,
        ProcessedBytes = processedBytes,
        CurrentFilePath = currentFile
    };

    /// <summary>创建已完成阶段的进度标记</summary>
    public static SimulationProgress Completed(int totalCount, long totalBytes) => new()
    {
        Phase = SimulationPhase.Completed,
        CompletedCount = totalCount,
        TotalCount = totalCount,
        ProcessedBytes = totalBytes
    };

    public override string ToString() =>
        $"[{Phase}] {CompletedCount}/{TotalCount} ({Percentage:F1}%) — {CurrentFilePath ?? ""}";
}

/// <summary>
/// 模拟运行的执行阶段枚举
/// </summary>
public enum SimulationPhase
{
    /// <summary>正在检查回收站容量（信息性，非阻塞）</summary>
    CheckingCapacity,

    /// <summary>正在从 Everything 流式枚举文件</summary>
    Enumerating,

    /// <summary>正在逐文件执行规则裁决和决策仲裁</summary>
    Analyzing,

    /// <summary>模拟运行已完成</summary>
    Completed,

    /// <summary>模拟运行已被用户取消</summary>
    Cancelled
}
