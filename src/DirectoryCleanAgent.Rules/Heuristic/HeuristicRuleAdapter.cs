using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Rules.Models;

namespace DirectoryCleanAgent.Rules.Heuristic;

/// <summary>
/// 启发式规则适配器 — 将可配置的 HeuristicRuleDefinition 适配为 IRule 接口。
///
/// 职责：桥接 JSON 配置中的规则定义与规则引擎的统一 IRule 接口，
/// 委托 HeuristicRuleEvaluator 执行条件匹配。
/// </summary>
internal sealed class HeuristicRuleAdapter : IRule
{
    private readonly HeuristicRuleDefinition _definition;

    /// <summary>获取底层规则定义（供 HeuristicRuleLoader 预编译路径模式使用）</summary>
    internal HeuristicRuleDefinition Definition => _definition;

    public int Priority => _definition.Priority;
    public string RuleName => _definition.Name;
    public string SemanticCategory => _definition.SemanticCategory;
    public string Reason => _definition.Reason;
    public bool Enabled => _definition.Enabled;

    public HeuristicRuleAdapter(HeuristicRuleDefinition definition)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public RuleVerdict? Evaluate(FileItem file, UserConfig config)
    {
        return HeuristicRuleEvaluator.Evaluate(_definition, file);
    }
}
