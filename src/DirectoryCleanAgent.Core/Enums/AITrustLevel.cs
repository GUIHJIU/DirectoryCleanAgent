namespace DirectoryCleanAgent.Core.Enums;

/// <summary>
/// AI 顾问信任等级
/// </summary>
public enum AITrustLevel
{
    /// <summary>低信任：AI 建议仅作参考，以规则为准</summary>
    Low = 0,

    /// <summary>中信任：AI 建议与规则并重</summary>
    Medium = 1,

    /// <summary>高信任：AI 建议权重高于规则</summary>
    High = 2
}
