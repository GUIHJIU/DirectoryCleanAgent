using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;

namespace DirectoryCleanAgent.Core.Interfaces;

/// <summary>
/// 规则引擎接口 — 负责对单个文件进行规则匹配与裁决。
///
/// 规则按优先级分为 7 级（设计文档 6.2 节）：
/// 1. 硬禁止规则（FORBID）    — 系统关键目录，绝对不可触碰
/// 2. 硬自动删除规则（AUTO_DELETE） — 确认安全的系统临时文件
/// 3. 用户排除规则（EXCLUDE）  — 用户白名单，视为禁止
/// 4. 保护规则（PROTECT）     — 自动检测的开发环境（Docker/WSL/虚拟机）
/// 5. 启发式建议删除（SUGGEST_DELETE） — 匹配删除模式
/// 6. 启发式建议保留（SUGGEST_KEEP）  — 匹配保留模式
/// 7. 默认保留（KEEP）        — 未命中任何规则
///
/// 支持热加载：FileSystemWatcher 监控规则目录 → 500ms 防抖 → 递增 RuleCacheVersion → 触发重扫。
/// </summary>
public interface IRuleEngine
{
    /// <summary>
    /// 对单个文件执行规则匹配与裁决。
    /// 按优先级从高到低遍历所有规则，返回第一个匹配的规则结果。
    /// 此方法在流式处理的每条文件上调用，必须高效（无 I/O、无内存分配、无锁）。
    /// </summary>
    /// <param name="file">待评估的文件元数据</param>
    /// <param name="config">当前用户配置（含排除列表、AI 设置等）</param>
    /// <returns>规则裁决结果，包含 verdict、semantic_category、matched_rule_name 等</returns>
    RuleResult Evaluate(FileItem file, UserConfig config);

    /// <summary>
    /// 规则变更事件。
    /// 由 FileSystemWatcher（500ms 防抖后）或内置编辑器保存操作触发。
    /// 决策引擎订阅此事件以清空缓存并异步触发全量重扫。
    /// </summary>
    event EventHandler? RulesChanged;
}
