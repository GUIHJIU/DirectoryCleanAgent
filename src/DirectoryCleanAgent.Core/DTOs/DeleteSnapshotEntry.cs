using System.Text.Json.Serialization;

namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 不可变删除快照条目。
/// 在执行删除操作前，由决策引擎深拷贝所有待删文件信息生成此快照。
/// 快照一经生成便脱离外部缓存影响，后续规则变更或 AI 回调均不改变已冻结的决策。
/// 字段集与设计文档 5.1 节 DeletionRecord.decision_snapshot 结构保持一致。
/// </summary>
public record DeleteSnapshotEntry
{
    /// <summary>文件完整路径（\\?\ 格式）</summary>
    [JsonPropertyName("path")]
    public required string FilePath { get; init; }

    /// <summary>文件大小（字节）</summary>
    [JsonPropertyName("size")]
    public required long FileSize { get; init; }

    /// <summary>文件内容的 SHA-256 哈希值（十六进制小写字符串）</summary>
    [JsonPropertyName("hash")]
    public required string Sha256Hash { get; init; }

    /// <summary>冻结时的最终操作决策</summary>
    [JsonPropertyName("action")]
    public required FinalAction FinalAction { get; init; }

    /// <summary>关联的操作批次 UUID，用于回滚时精确清除墓碑</summary>
    [JsonPropertyName("op")]
    public required string OperationId { get; init; }
}
