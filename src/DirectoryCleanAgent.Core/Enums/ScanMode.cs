namespace DirectoryCleanAgent.Core.Enums;

/// <summary>
/// 扫描模式
/// </summary>
public enum ScanMode
{
    /// <summary>智能扫描系统盘热点目录（推荐）</summary>
    SmartScanSystemDrive = 0,

    /// <summary>每次运行时询问扫描目标目录</summary>
    AskDirectoryEveryTime = 1
}
