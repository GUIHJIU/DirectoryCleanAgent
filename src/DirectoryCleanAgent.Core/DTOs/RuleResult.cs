namespace DirectoryCleanAgent.Core.DTOs;

/// <summary>
/// 规则引擎对单个文件的裁决结果。
/// 由 IRuleEngine.Evaluate() 产出，作为决策引擎的输入之一。
/// </summary>
public record RuleResult
{
    /// <summary>规则引擎裁决结果</summary>
    public required RuleVerdict Verdict { get; init; }

    /// <summary>
    /// 语义分类标签。
    /// 简单模式下用于 UI 展示（如"系统临时文件"、"应用缓存"）；
    /// 专家模式下作为辅助信息展示。
    /// </summary>
    public required string SemanticCategory { get; init; }

    /// <summary>匹配的规则名称（专家模式用于展示），未匹配任何规则时为 null</summary>
    public string? MatchedRuleName { get; init; }

    /// <summary>
    /// 匹配规则的优先级（1~7，对应设计文档 6.2 节优先级表）。
    /// 优先级 1（硬禁止）最高，优先级 7（默认）最低。
    /// </summary>
    public required int RulePriority { get; init; }

    /// <summary>规则的简短解释文本（如"位于系统保护目录"），用于 UI tooltip</summary>
    public string? Reason { get; init; }

    /// <summary>快速创建未命中任何规则的结果</summary>
    public static RuleResult NoMatch => new()
    {
        Verdict = RuleVerdict.Keep,
        SemanticCategory = "未分类",
        MatchedRuleName = null,
        RulePriority = 7,
        Reason = "未命中任何规则"
    };
}
