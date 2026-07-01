namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// Everything 适配器产出的文件元数据 DTO。
/// 流经规则引擎、决策引擎，路径已标准化为 \\?\ 格式。
/// 不可变记录类型，确保流式处理中数据不会被意外修改。
/// </summary>
public record FileItem
{
    /// <summary>文件完整路径，已标准化为 \\?\ 格式（由 Everything 适配器输出时保证）</summary>
    public required string FilePath { get; init; }

    /// <summary>文件大小（字节）</summary>
    public required long SizeBytes { get; init; }

    /// <summary>文件最后写入时间（UTC）</summary>
    public required DateTime LastWriteTime { get; init; }

    /// <summary>文件扩展名，小写，含点号（如 ".log"），无扩展名时为 null</summary>
    public string? Extension { get; init; }

    /// <summary>Everything 提供的卷 GUID，用于构造墓碑的物理标识键</summary>
    public string? VolumeGuid { get; init; }

    /// <summary>
    /// Everything 提供的 NTFS 文件引用号（File Reference Number）。
    /// FRN 是文件在 NTFS 卷上的物理 ID，用于墓碑精确匹配。
    /// 若启动时 FRN 能力检测失败（FRN_AVAILABLE = false），此值为 null。
    /// </summary>
    public long? FileReferenceNumber { get; init; }

    /// <summary>用于向 Everything 请求后续排序/重查的键值</summary>
    public required string EverythingSortKey { get; init; }
}
