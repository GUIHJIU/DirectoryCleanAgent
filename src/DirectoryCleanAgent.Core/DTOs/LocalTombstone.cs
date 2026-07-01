namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 本地墓碑实体。
/// 记录已删除文件的物理标识，用于防止已删除文件在后续扫描中被重复列出。
///
/// 匹配策略（两级）：
/// 1. FRN 可用时：以 file_identity_key（VolumeGuid + FileReferenceNumber）精确匹配。
/// 2. FRN 不可用时：降级为 Size + LastWriteTime 指纹匹配，并强制 3 天过期，
///    额外校验 file_path 和 original_size 一致性。
///
/// 回滚时通过 operation_id 批量清除关联墓碑记录。
/// 对应 SQLite 的 LocalTombstone 表。
/// </summary>
public record LocalTombstone
{
    /// <summary>文件原始路径（\\?\ 格式）</summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// 文件物理标识键（主键）。
    /// FRN 可用时格式为 "{VolumeGuid}:{FileReferenceNumber}"；
    /// FRN 不可用时格式为 "{Size}:{LastWriteTime:O}"（降级指纹模式，3 天强制过期）。
    /// </summary>
    public required string FileIdentityKey { get; init; }

    /// <summary>关联的删除操作 UUID，用于回滚时精确批量清除</summary>
    public required string OperationId { get; init; }

    /// <summary>删除时的文件大小（字节），用于降级指纹模式的额外校验</summary>
    public required long OriginalSize { get; init; }

    /// <summary>删除时的文件最后写入时间，用于降级指纹匹配</summary>
    public required DateTime OriginalLastWriteTime { get; init; }

    /// <summary>文件被删除的时间戳（UTC）</summary>
    public required DateTime DeletedAt { get; init; }
}
