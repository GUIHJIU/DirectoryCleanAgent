namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// Everything 文件变更事件参数。
/// 当 Everything 检测到文件系统发生新增、修改或删除时，
/// 由 IFileListProvider.FileChanged 事件携带此参数通知 UI 层增量刷新。
/// </summary>
public class FileChangedEventArgs : EventArgs
{
    /// <summary>发生变更的文件路径（\\?\ 格式）</summary>
    public required string FilePath { get; init; }

    /// <summary>变更类型</summary>
    public required FileChangeType ChangeType { get; init; }

    /// <summary>事件发生时间戳（UTC）</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 文件变更类型枚举
/// </summary>
public enum FileChangeType
{
    /// <summary>新文件创建</summary>
    Created,

    /// <summary>文件内容或属性被修改</summary>
    Modified,

    /// <summary>文件被删除</summary>
    Deleted,

    /// <summary>文件被重命名（FilePath 为新路径）</summary>
    Renamed
}
