namespace DirectoryCleanAgent.Core.Enums;

/// <summary>
/// 应用程序运行模式
/// </summary>
public enum AppMode
{
    /// <summary>正常模式：所有功能可用</summary>
    Normal = 0,

    /// <summary>只读模式：非管理员启动时自动降级，仅分析功能可用，删除/清理按钮禁用</summary>
    ReadOnly = 1
}
