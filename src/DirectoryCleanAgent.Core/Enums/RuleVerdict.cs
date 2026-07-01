namespace DirectoryCleanAgent.Core.Enums;

/// <summary>
/// 规则引擎裁决结果（按优先级从高到低排列）
/// </summary>
public enum RuleVerdict
{
    /// <summary>硬禁止删除：系统关键目录/文件，绝对不可触碰</summary>
    Forbid = 0,

    /// <summary>硬自动删除：确认安全的系统临时文件，可直接清理</summary>
    AutoDelete = 1,

    /// <summary>用户排除：用户主动加入白名单，视为禁止删除</summary>
    Exclude = 2,

    /// <summary>保护规则：自动检测的开发环境（Docker/WSL/虚拟机），禁止删除</summary>
    Protect = 3,

    /// <summary>启发式建议删除：匹配启发式规则，推荐清理</summary>
    SuggestDelete = 4,

    /// <summary>启发式建议保留：匹配保留规则，不建议删除</summary>
    SuggestKeep = 5,

    /// <summary>默认保留：未命中任何规则，不做处理</summary>
    Keep = 6
}
