namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 隔离区单个文件条目 DTO — C7 隔离区管理界面核心数据模型。
///
/// 通过 IQuarantineManager.ListQuarantineFilesAsync 获取，
/// 用于隔离区管理界面的文件列表展示。
/// 每个条目包含从文件名解析的元数据（原始文件名、SHA256 前缀、隔离时间戳）
/// 以及文件系统元数据（文件大小、是否过期）。
///
/// 使用 readonly struct 保持与 QuarantineStats、RecycleBinCapacity 一致的值语义。
/// </summary>
public readonly struct QuarantineFileEntry
{
    /// <summary>隔离区中的完整文件路径（\\?\ 格式）</summary>
    public required string FullPath { get; init; }

    /// <summary>原始文件名（含扩展名，从隔离区文件名解析）</summary>
    public required string OriginalFileName { get; init; }

    /// <summary>SHA-256 哈希前缀（16 位十六进制，从隔离区文件名解析）</summary>
    public required string Sha256Prefix { get; init; }

    /// <summary>文件移入隔离区的 UTC 时间戳（从文件名解析的 Unix 秒转换）</summary>
    public required DateTime QuarantineTimestamp { get; init; }

    /// <summary>文件大小（字节）</summary>
    public required long FileSizeBytes { get; init; }

    /// <summary>文件是否超过配置的保留天数</summary>
    public required bool IsExpired { get; init; }

    /// <summary>人类可读格式的隔离时间</summary>
    public string QuarantineTimestampText =>
        QuarantineTimestamp.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>人类可读格式的文件大小</summary>
    public string FileSizeText { get; init; }

    /// <summary>摘要信息</summary>
    public override string ToString() =>
        $"{OriginalFileName} ({FileSizeText}) — {QuarantineTimestampText}";
}
