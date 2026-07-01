using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Rules.Models;

namespace DirectoryCleanAgent.Rules.Heuristic;

/// <summary>
/// 启发式规则评估器 — 对单个 FileItem 评估一条启发式规则的所有条件。
///
/// 所有非 null 的条件必须同时满足才算命中（AND 逻辑）。
/// 条件包括：扩展名、路径 Glob 模式、文件大小范围、文件年龄范围。
///
/// 性能设计（V3.7 优化）：
/// - 路径 Glob 模式在规则加载时预编译为 CompiledGlobPattern，缓存在 RuleConditions.CompiledPathPatterns 中
/// - 评估阶段直接使用预编译实例的 .IsMatch()，无每文件 Regex 编译开销
/// - 快速路径：先检查扩展名（O(n) 字符串比较），再检查路径（已编译正则），最后检查大小/年龄
/// - DateTime.UtcNow 仅获取一次，复用于 MinAgeDays 和 MaxAgeDays 检查
/// </summary>
internal static class HeuristicRuleEvaluator
{
    /// <summary>
    /// 评估单个启发式规则是否匹配指定文件。
    /// </summary>
    /// <param name="definition">规则定义（含预编译的 CompiledPathPatterns）</param>
    /// <param name="file">待评估的文件</param>
    /// <returns>匹配成功返回对应的 RuleVerdict，不匹配返回 null</returns>
    public static RuleVerdict? Evaluate(HeuristicRuleDefinition definition, FileItem file)
    {
        var conditions = definition.Conditions;

        // 快速路径1：扩展名过滤（所有指定扩展名中匹配任一即可）
        if (conditions.Extensions is { Count: > 0 })
        {
            if (!MatchesExtension(file.Extension, conditions.Extensions))
                return null;
        }

        // 快速路径2：路径 Glob 模式匹配（使用预编译的 CompiledGlobPattern）
        if (conditions.CompiledPathPatterns is { Count: > 0 })
        {
            if (!MatchesAnyPathPattern(file.FilePath, conditions.CompiledPathPatterns))
                return null;
        }

        // 快速路径3：文件大小下限检查
        if (conditions.MinSizeBytes.HasValue)
        {
            if (file.SizeBytes < conditions.MinSizeBytes.Value)
                return null;
        }

        // 快速路径4：文件大小上限检查
        if (conditions.MaxSizeBytes.HasValue)
        {
            if (file.SizeBytes > conditions.MaxSizeBytes.Value)
                return null;
        }

        // 快速路径5：文件年龄检查（仅获取一次 DateTime.UtcNow）
        if (conditions.MinAgeDays.HasValue || conditions.MaxAgeDays.HasValue)
        {
            var now = DateTime.UtcNow;
            var age = now - file.LastWriteTime;

            if (conditions.MinAgeDays.HasValue && age.TotalDays < conditions.MinAgeDays.Value)
                return null;

            if (conditions.MaxAgeDays.HasValue && age.TotalDays > conditions.MaxAgeDays.Value)
                return null;
        }

        // 所有条件通过 → 根据优先级返回对应的 RuleVerdict
        return definition.Priority switch
        {
            5 => RuleVerdict.SuggestDelete,
            6 => RuleVerdict.SuggestKeep,
            _ => null
        };
    }

    // ============================================================
    // 私有辅助方法
    // ============================================================

    /// <summary>
    /// 检查文件扩展名是否匹配列表中任一扩展名（大小写不敏感）。
    /// 使用 OrdinalIgnoreCase 比较，避免 ToLowerInvariant 分配。
    /// </summary>
    private static bool MatchesExtension(string? fileExtension, List<string> allowedExtensions)
    {
        if (fileExtension == null)
            return false;

        foreach (var allowed in allowedExtensions)
        {
            if (fileExtension.Equals(allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 检查文件路径是否匹配任一预编译的 Glob 模式。
    /// </summary>
    private static bool MatchesAnyPathPattern(string filePath, List<CompiledGlobPattern> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.IsMatch(filePath))
                return true;
        }
        return false;
    }
}
