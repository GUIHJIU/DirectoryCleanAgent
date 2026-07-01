namespace DirectoryCleanAgent.Core.Enums;

/// <summary>
/// 应用程序全局状态
/// </summary>
public enum AppState
{
    /// <summary>正在初始化（检测 Everything 依赖、加载配置、预热缓存）</summary>
    Initializing = 0,

    /// <summary>就绪：等待用户操作</summary>
    Ready = 1,

    /// <summary>正在扫描文件系统</summary>
    Scanning = 2,

    /// <summary>正在执行清理操作</summary>
    Cleaning = 3,

    /// <summary>发生错误，部分功能不可用</summary>
    Error = 4
}
