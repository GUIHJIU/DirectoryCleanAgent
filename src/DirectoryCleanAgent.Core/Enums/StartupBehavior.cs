using System.ComponentModel;

namespace DirectoryCleanAgent.Core.Enums;

/// <summary>
/// 启动时行为偏好
/// </summary>
public enum StartupBehavior
{
    /// <summary>自动开始扫描</summary>
    [Description("自动扫描")]
    AutoScan = 0,

    /// <summary>显示首页，等待用户操作</summary>
    [Description("显示首页")]
    ShowHome = 1
}
