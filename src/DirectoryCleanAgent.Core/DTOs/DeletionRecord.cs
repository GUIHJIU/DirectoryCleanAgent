namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 删除记录实体。
/// 记录每次文件删除操作的完整信息，包括文件哈希和冻结的决策快照 JSON，
/// 用于审计溯源和回滚时的数据校验。
/// 对应 SQLite 的 DeletionRecord 表。
/// </summary>
public record DeletionRecord
{
    /// <summary>自增主键，插入时由数据库自动分配</summary>
    public long? Id { get; init; }

    /// <summary>操作批次唯一标识 UUID</summary>
    public required string OperationId { get; init; }

    /// <summary>原始文件路径（\\?\ 格式）</summary>
    public required string FilePath { get; init; }

    /// <summary>文件内容的 SHA-256 哈希值（十六进制小写字符串）</summary>
    public required string FileHash { get; init; }

    /// <summary>文件大小（字节）</summary>
    public required long FileSize { get; init; }

    /// <summary>
    /// 删除方式：RECYCLE_BIN / QUARANTINE / PERMANENT。
    /// 使用字符串存储以兼容未来扩展（如隔离区移入等中间态）。
    /// </summary>
    public required string DeletionMethod { get; init; }

    /// <summary>
    /// 删除时冻结的决策快照 JSON（紧凑格式，无缩进）。
    /// 若 JSON 长度超过 4KB，由数据层在写入前应用 GZip 压缩 + Base64 编码。
    /// 结构定义见设计文档 5.1 节。
    /// </summary>
    public required string DecisionSnapshotJson { get; init; }

    /// <summary>记录创建时间戳（UTC）</summary>
    public required DateTime CreatedAt { get; init; }
}
