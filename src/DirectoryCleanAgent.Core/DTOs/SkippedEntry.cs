using System.Text.Json.Serialization;

namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 被跳过的文件条目 — 记录在 SHA-256 哈希阶段因异常未能进入快照的文件，
/// 供 UI 层向用户展示跳过详情（如"文件被占用"、"文件已不存在"等）。
/// </summary>
public record SkippedEntry
{
    /// <summary>文件完整路径（\\?\ 格式）</summary>
    [JsonPropertyName("path")]
    public required string FilePath { get; init; }

    /// <summary>跳过原因描述（如"文件被其他进程占用，无法访问"）</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>异常类型名称（如 UnauthorizedAccessException），用于诊断</summary>
    [JsonPropertyName("ex_type")]
    public string? ExceptionType { get; init; }
}
