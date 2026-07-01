namespace DirectoryCleanAgent.Models;

/// <summary>
/// 删除批次 UI 展示模型 — 将 OperationBatchInfo 转换为适合 ListBox 绑定的友好格式。
/// </summary>
public class DeletionBatchDisplayItem
{
    /// <summary>操作批次 UUID</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>操作执行时间（本地时区格式化字符串）</summary>
    public string ExecutedAtText { get; init; } = string.Empty;

    /// <summary>批次中的文件总数（格式化）</summary>
    public string FileCountText { get; init; } = string.Empty;

    /// <summary>释放的总空间（格式化）</summary>
    public string TotalSizeText { get; init; } = string.Empty;

    /// <summary>删除方式显示文本（回收站 / 隔离区 / 永久删除）</summary>
    public string MethodText { get; init; } = string.Empty;

    /// <summary>是否仍可回滚</summary>
    public bool IsRollbackable { get; init; }

    /// <summary>回滚状态文本（用于 UI 显示不可回滚原因）</summary>
    public string RollbackStatusText => IsRollbackable ? "可回滚" : "不可回滚";
}
