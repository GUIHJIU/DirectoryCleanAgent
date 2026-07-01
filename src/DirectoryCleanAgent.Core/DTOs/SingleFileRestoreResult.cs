namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 单文件恢复操作结果 — 用于 C7 隔离区管理界面恢复操作反馈。
///
/// 每个隔离区文件的恢复操作独立报告结果，
/// 单个文件失败不中断批量恢复（部分成功语义）。
/// </summary>
public record SingleFileRestoreResult
{
    /// <summary>恢复是否成功</summary>
    public required bool Success { get; init; }

    /// <summary>恢复后的文件路径（成功时返回，失败时为 null）</summary>
    public string? RestoredPath { get; init; }

    /// <summary>恢复的文件大小（字节）</summary>
    public required long FileSizeBytes { get; init; }

    /// <summary>隔离区中的源文件路径</summary>
    public required string QuarantineFilePath { get; init; }

    /// <summary>原始文件名</summary>
    public required string OriginalFileName { get; init; }

    /// <summary>原始文件路径未找到（DeletionRecord 中无匹配哈希）</summary>
    public bool NotFound { get; init; }

    /// <summary>错误描述（失败时返回）</summary>
    public string? ErrorMessage { get; init; }
}
