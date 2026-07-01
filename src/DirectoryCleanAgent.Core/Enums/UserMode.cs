namespace DirectoryCleanAgent.Core.Enums;

/// <summary>
/// 用户使用模式
/// </summary>
public enum UserMode
{
    /// <summary>简单模式：语义标签引导，隐藏技术细节</summary>
    Simple = 0,

    /// <summary>专家模式：展示规则详情、可调参数、自定义配置</summary>
    Expert = 1
}
