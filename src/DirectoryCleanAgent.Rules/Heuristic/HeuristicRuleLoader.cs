using System.IO;
using System.Text.Json;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Rules.Models;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Rules.Heuristic;

/// <summary>
/// 启发式规则加载器 — 负责从 JSON 文件加载和解析启发式规则配置。
///
/// 加载流程：
/// 1. 扫描指定目录下的所有 *.json 文件
/// 2. 逐一反序列化为 HeuristicRuleConfigFile
/// 3. 合并所有文件中的规则列表
/// 4. 过滤掉 enabled = false 的规则
/// 5. 验证规则优先级（只允许 5 或 6）
///
/// 线程安全：加载操作本身非线程安全（预期在单线程初始化或重载时调用），
/// 但加载完成后返回的规则列表为不可变集合。
/// </summary>
public sealed class HeuristicRuleLoader
{
    private readonly ILogger<HeuristicRuleLoader> _logger;
    private readonly string _rulesDirectory;

    /// <summary>规则文件名过滤器</summary>
    private const string JsonSearchPattern = "heuristic_*.json";

    /// <summary>最后一次成功加载的规则列表（作为重载失败的 fallback）</summary>
    private volatile IReadOnlyList<IRule> _lastValidRules = Array.Empty<IRule>();

    public HeuristicRuleLoader(ILogger<HeuristicRuleLoader> logger, string rulesDirectory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rulesDirectory = rulesDirectory ?? throw new ArgumentNullException(nameof(rulesDirectory));
    }

    /// <summary>
    /// 从规则目录加载所有启发式规则。
    /// 若加载失败，返回上一次成功加载的规则列表（作为安全回退）。
    /// </summary>
    /// <returns>已过滤和验证的规则列表（按优先级排序）</returns>
    public IReadOnlyList<IRule> Load()
    {
        _logger.LogMethodEntry();

        try
        {
            // 确保规则目录存在
            if (!Directory.Exists(_rulesDirectory))
            {
                _logger.LogInformation("规则目录不存在，创建: {Path}", _rulesDirectory);
                Directory.CreateDirectory(_rulesDirectory);
                return _lastValidRules;
            }

            // 扫描所有启发式规则 JSON 文件
            var jsonFiles = Directory.GetFiles(_rulesDirectory, JsonSearchPattern, SearchOption.TopDirectoryOnly);

            if (jsonFiles.Length == 0)
            {
                _logger.LogInformation("规则目录中没有找到启发式规则文件 ({Pattern})，使用上次有效规则（{Count}条）",
                    JsonSearchPattern, _lastValidRules.Count);
                return _lastValidRules;
            }

            var allRules = new List<IRule>();

            foreach (var filePath in jsonFiles)
            {
                try
                {
                    var rulesFromFile = LoadFromFile(filePath);
                    allRules.AddRange(rulesFromFile);
                    _logger.LogDebug("从 {File} 加载了 {Count} 条启发式规则",
                        Path.GetFileName(filePath), rulesFromFile.Count);
                }
                catch (JsonException ex)
                {
                    // 单个文件解析失败不影响其他文件的加载
                    _logger.LogError(ex, "解析启发式规则文件失败，跳过: {File}", filePath);
                }
            }

            // 过滤禁用的规则
            var activeRules = allRules.Where(r => r is HeuristicRuleAdapter a && a.Enabled).ToList<IRule>();

            // 验证规则优先级（启发式规则只允许 5=建议删除 或 6=建议保留）
            var invalidPriorityRules = activeRules
                .Where(r => r.Priority != 5 && r.Priority != 6)
                .ToList();
            foreach (var invalid in invalidPriorityRules)
            {
                _logger.LogWarning(
                    "启发式规则 '{RuleName}' 的优先级 {Priority} 无效（仅允许 5 或 6），已跳过该规则",
                    invalid.RuleName, invalid.Priority);
                activeRules.Remove(invalid);
            }

            // 预编译所有规则的路径 Glob 模式（一次性 RegexOptions.Compiled，避免每文件重新编译）
            CompilePathPatterns(activeRules);

            // 验证并排序（按优先级从小到大）
            var validatedRules = activeRules
                .OrderBy(r => r.Priority)
                .ToList()
                .AsReadOnly();

            _logger.LogInformation(
                "启发式规则加载完成：扫描 {FileCount} 个文件，共 {Total} 条规则，启用 {Active} 条",
                jsonFiles.Length, allRules.Count, validatedRules.Count);

            // 更新有效规则缓存
            _lastValidRules = validatedRules;
            return validatedRules;
        }
        catch (Exception ex)
        {
            // 加载失败时保留上一份有效规则，确保程序不会因错误配置而丢失全部规则
            _logger.LogError(ex, "加载启发式规则失败，保留上次有效规则（{Count}条）", _lastValidRules.Count);
            return _lastValidRules;
        }
    }

    /// <summary>
    /// 预编译所有激活规则中的路径 Glob 模式。
    /// 遍历规则列表，对每个含 PathPatterns 的规则：
    /// 1. 将每个 Glob 模式字符串编译为 CompiledGlobPattern 实例
    /// 2. 存入 RuleConditions.CompiledPathPatterns
    /// 评估阶段直接使用预编译实例，避免每文件重复编译正则。
    /// </summary>
    private static void CompilePathPatterns(List<IRule> rules)
    {
        foreach (var rule in rules)
        {
            if (rule is not HeuristicRuleAdapter adapter)
                continue;

            var conditions = adapter.Definition.Conditions;
            if (conditions.PathPatterns is not { Count: > 0 })
                continue;

            try
            {
                conditions.CompiledPathPatterns = conditions.PathPatterns
                    .Select(p => new CompiledGlobPattern(p))
                    .ToList();
            }
            catch (Exception ex)
            {
                // 单个规则的模式编译失败不应影响其他规则
                System.Diagnostics.Debug.WriteLine(
                    $"规则 '{adapter.RuleName}' 的路径模式编译失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 从单个 JSON 文件加载规则。
    /// </summary>
    private List<IRule> LoadFromFile(string filePath)
    {
        var jsonText = File.ReadAllText(filePath);

        if (string.IsNullOrWhiteSpace(jsonText))
        {
            _logger.LogWarning("规则文件为空: {File}", filePath);
            return new List<IRule>();
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip // 允许 JSON 注释
        };

        var configFile = JsonSerializer.Deserialize<HeuristicRuleConfigFile>(jsonText, options);

        if (configFile == null || configFile.Rules.Count == 0)
        {
            _logger.LogWarning("规则文件反序列化结果为空: {File}", filePath);
            return new List<IRule>();
        }

        _logger.LogDebug("规则文件版本: {Version}, 规则数: {Count}", configFile.Version, configFile.Rules.Count);

        // 将 HeuristicRuleDefinition 适配为 IRule 实例
        return configFile.Rules
            .Select(def => new HeuristicRuleAdapter(def) as IRule)
            .ToList();
    }
}
