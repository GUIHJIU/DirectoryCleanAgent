using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Rules;

/// <summary>
/// 规则统一接口 — 所有规则（硬规则 + 启发式规则）必须实现此接口。
///
/// 规则按优先级从高到低排序执行，首个匹配的规则立即返回结果（短路求值）。
/// Evaluate 返回 null 表示规则不匹配当前文件，非 null 表示命中。
/// </summary>
public interface IRule
{
    /// <summary>规则优先级（1~7），值越小优先级越高</summary>
    int Priority { get; }

    /// <summary>规则唯一标识名称（如 "forbid_system32"）</summary>
    string RuleName { get; }

    /// <summary>语义分类标签（如"系统临时文件"、"应用缓存"），用于 UI 展示</summary>
    string SemanticCategory { get; }

    /// <summary>规则的简短解释文本，用于 UI tooltip</summary>
    string Reason { get; }

    /// <summary>
    /// 对单个文件执行规则匹配。
    /// 返回 null 表示不匹配（未命中），返回 RuleVerdict 枚举值表示命中。
    /// 此方法在流式处理的每条文件上调用，必须高效（无 I/O、无锁）。
    /// </summary>
    /// <param name="file">待评估的文件元数据</param>
    /// <param name="config">当前用户配置（含排除列表等）</param>
    /// <returns>命中时返回对应的 RuleVerdict 值，未命中返回 null</returns>
    RuleVerdict? Evaluate(FileItem file, UserConfig config);
}
