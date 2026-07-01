using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Rules.HardRules;
using DirectoryCleanAgent.Rules.Heuristic;
using DirectoryCleanAgent.Rules.Models;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Rules;

/// <summary>
/// 规则引擎主实现 — 编排所有规则（硬规则 + 用户排除 + 启发式规则）按优先级执行。
///
/// 核心流程：
/// 1. 启动时编译规则列表（硬规则 + 用户排除规则 + 启发式规则），按优先级排序
/// 2. Evaluate() 遍历规则列表，首个匹配立即返回（短路求值）
/// 3. 订阅 IConfigService.ConfigChanged 实现热加载
/// 4. 规则变更后触发 RulesChanged 事件，通知决策引擎清空缓存并重扫
///
/// 线程安全：
///   - _rules 集合通过 lock(_rulesLock) 保护
///   - Evaluate() 在无锁条件下读取 _rules 引用（volatile 保证可见性）
///   - 单个规则异常被捕获并记录，不会中断整体评估流程
/// </summary>
public sealed class RuleEngine : IRuleEngine, IDisposable
{
    private readonly IConfigService _configService;
    private readonly HeuristicRuleLoader _heuristicLoader;
    private readonly ILogger<RuleEngine> _logger;

    /// <summary>当前编译好的规则列表（按优先级排序），volatile 保证多线程可见性</summary>
    private volatile IReadOnlyList<IRule> _rules = Array.Empty<IRule>();

    /// <summary>保护 _rules 重建操作的锁</summary>
    private readonly object _rulesLock = new();

    /// <summary>用户排除规则优先级</summary>
    private const int UserExcludePriority = 3;

    /// <summary>用户排除规则语义分类</summary>
    private const string UserExcludeCategory = "保护内容";

    /// <summary>是否已释放</summary>
    private bool _disposed;

    // ============================================================
    // 用户排除预编译缓存（V3.7 性能优化）
    // 在 CompileRules 时一次性预编译，避免每文件动态创建 HeuristicRuleDefinition + Regex
    // ============================================================

    /// <summary>预编译的用户排除目录 Glob 模式（无通配符的目录路径也存储为 CompiledGlobPattern）</summary>
    private volatile CompiledUserExclusionCache _userExclusionCache = CompiledUserExclusionCache.Empty;

    /// <summary>用户排除扩展名的 HashSet（OrdinalIgnoreCase，O(1) 查找替代 O(n) 列表遍历）</summary>
    private volatile HashSet<string> _userExcludedExtensionsSet = new(StringComparer.OrdinalIgnoreCase);

    // ============================================================
    // 构造与生命周期
    // ============================================================

    public RuleEngine(
        IConfigService configService,
        HeuristicRuleLoader heuristicLoader,
        ILogger<RuleEngine> logger)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _heuristicLoader = heuristicLoader ?? throw new ArgumentNullException(nameof(heuristicLoader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 订阅配置变更事件 — 实现热加载的核心链路：
        // FileSystemWatcher(500ms防抖) → ConfigService.IncrementRuleCacheVersion()
        // → ConfigChanged → RuleEngine.OnConfigChanged → 重建规则列表 → RulesChanged → 决策引擎清空缓存
        _configService.ConfigChanged += OnConfigChanged;

        // 初始编译规则列表
        CompileRules();
        _logger.LogInformation("规则引擎初始化完成，共 {Count} 条活动规则", _rules.Count);
    }

    // ============================================================
    // 用户排除预编译缓存（V3.7 性能优化）
    // ============================================================

    /// <summary>
    /// 预编译的用户排除缓存 — 将 UserConfig 中的排除目录和扩展名
    /// 预编译为高效查找结构，避免每文件重复解析。
    /// </summary>
    private sealed class CompiledUserExclusionCache
    {
        /// <summary>预编译的目录 Glob 模式（含无通配符的纯路径前缀）</summary>
        public IReadOnlyList<CompiledGlobPattern> DirPatterns { get; init; } = Array.Empty<CompiledGlobPattern>();

        /// <summary>纯路径前缀（无通配符），用于 StartsWith 快速匹配</summary>
        public IReadOnlyList<string> PlainDirPrefixes { get; init; } = Array.Empty<string>();

        /// <summary>排除扩展名 HashSet（小写，不含前导点）</summary>
        public HashSet<string> Extensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public static CompiledUserExclusionCache Empty => new();
    }

    /// <summary>
    /// 从 UserConfig 构建预编译的用户排除缓存。
    /// 在 CompileRules() 和 OnConfigChanged() 中调用。
    /// </summary>
    private static CompiledUserExclusionCache BuildExclusionCache(UserConfig config)
    {
        var dirPatterns = new List<CompiledGlobPattern>();
        var plainPrefixes = new List<string>();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 编译目录排除模式
        if (config.UserExcludedDirs.Count > 0)
        {
            foreach (var dir in config.UserExcludedDirs)
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                var trimmed = dir.TrimEnd('\\').ToLowerInvariant();

                if (trimmed.Contains('*') || trimmed.Contains('?'))
                {
                    // 通配符模式 → 预编译为 CompiledGlobPattern
                    dirPatterns.Add(new CompiledGlobPattern(trimmed));
                }
                else
                {
                    // 纯路径前缀 → 加入快速匹配列表
                    plainPrefixes.Add(trimmed);
                }
            }
        }

        // 编译扩展名排除
        if (config.UserExcludedExtensions.Count > 0)
        {
            foreach (var ext in config.UserExcludedExtensions)
            {
                if (string.IsNullOrWhiteSpace(ext))
                    continue;

                // 统一去除前导点，小写
                var clean = ext.TrimStart('.').ToLowerInvariant();
                if (clean.Length > 0)
                    extensions.Add(clean);
            }
        }

        return new CompiledUserExclusionCache
        {
            DirPatterns = dirPatterns,
            PlainDirPrefixes = plainPrefixes,
            Extensions = extensions
        };
    }

    // ============================================================
    // IRuleEngine 实现
    // ============================================================

    /// <inheritdoc />
    public event EventHandler? RulesChanged;

    /// <inheritdoc />
    public RuleResult Evaluate(FileItem file, UserConfig config)
    {
        // 参数校验
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(config);

        var rules = _rules; // 捕获 volatile 引用，确保遍历期间规则列表一致
        bool userExclusionsChecked = false;

        // 按优先级从高到低遍历（规则列表已排序），首个匹配立即返回（短路求值）
        // 用户排除规则（P3）在编译规则中不存在，而是在 P1/P2 检查后、P4 之前动态插入
        foreach (var rule in rules)
        {
            // 在 P4 级别规则之前插入 P3（用户排除）检查
            if (!userExclusionsChecked && rule.Priority >= UserExcludePriority)
            {
                userExclusionsChecked = true;
                var excludeResult = EvaluateUserExclusions(file, config);
                if (excludeResult != null)
                    return excludeResult;
            }

            try
            {
                var verdict = rule.Evaluate(file, config);
                if (verdict.HasValue)
                {
                    _logger.LogDebug(
                        "文件 {Path} 命中规则: {RuleName} (P{Priority}) → {Verdict}",
                        file.FilePath, rule.RuleName, rule.Priority, verdict.Value);

                    return new RuleResult
                    {
                        Verdict = verdict.Value,
                        SemanticCategory = rule.SemanticCategory,
                        MatchedRuleName = rule.RuleName,
                        RulePriority = rule.Priority,
                        Reason = rule.Reason
                    };
                }
            }
            catch (Exception ex)
            {
                // 单个规则异常不中断整体评估 — 记录警告后跳过该规则继续
                _logger.LogWarning(ex,
                    "规则 {RuleName} (P{Priority}) 评估文件 {Path} 时异常，跳过该规则",
                    rule.RuleName, rule.Priority, file.FilePath);
            }
        }

        // 如果编译规则全部遍历完但用户排除尚未检查（规则列表可能为空或不含P4+）
        if (!userExclusionsChecked)
        {
            var excludeResult = EvaluateUserExclusions(file, config);
            if (excludeResult != null)
                return excludeResult;
        }

        // 所有规则均未命中 → 返回默认保留
        return RuleResult.NoMatch;
    }

    /// <summary>
    /// 评估用户排除规则（优先级3）。
    /// 使用预编译缓存（_userExclusionCache + _userExcludedExtensionsSet），
    /// 避免每文件动态创建 HeuristicRuleDefinition 和编译正则。
    /// </summary>
    private RuleResult? EvaluateUserExclusions(FileItem file, UserConfig config)
    {
        var cache = _userExclusionCache;

        // 检查目录排除
        if (cache.PlainDirPrefixes.Count > 0 || cache.DirPatterns.Count > 0)
        {
            // FileItem.FilePath 使用 \\?\ 前缀，去除前缀后用于匹配
            var filePathLower = file.FilePath.ToLowerInvariant();
            var filePathNoPrefix = filePathLower.StartsWith(@"\\?\")
                ? filePathLower[4..]
                : filePathLower;

            // 1) 纯路径前缀快速匹配（无通配符，仅 StartsWith）
            foreach (var prefix in cache.PlainDirPrefixes)
            {
                if (filePathLower.StartsWith(prefix, StringComparison.Ordinal) ||
                    filePathNoPrefix.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return new RuleResult
                    {
                        Verdict = RuleVerdict.Exclude,
                        SemanticCategory = UserExcludeCategory,
                        MatchedRuleName = "user_exclude_dir",
                        RulePriority = UserExcludePriority,
                        Reason = $"用户排除目录（前缀匹配）"
                    };
                }
            }

            // 2) 预编译的 Glob 模式匹配
            foreach (var pattern in cache.DirPatterns)
            {
                // 同时检查 \\?\ 前缀版和去除前缀版
                if (pattern.IsMatch(filePathLower) || pattern.IsMatch(filePathNoPrefix))
                {
                    return new RuleResult
                    {
                        Verdict = RuleVerdict.Exclude,
                        SemanticCategory = UserExcludeCategory,
                        MatchedRuleName = "user_exclude_dir",
                        RulePriority = UserExcludePriority,
                        Reason = $"用户排除目录（Glob 匹配）"
                    };
                }
            }
        }

        // 检查扩展名排除（O(1) HashSet 查找替代 O(n) 列表遍历）
        if (cache.Extensions.Count > 0 && file.Extension != null)
        {
            var fileExt = file.Extension.TrimStart('.'); // 去除前导点
            if (cache.Extensions.Contains(fileExt))
            {
                return new RuleResult
                {
                    Verdict = RuleVerdict.Exclude,
                    SemanticCategory = UserExcludeCategory,
                    MatchedRuleName = "user_exclude_ext",
                    RulePriority = UserExcludePriority,
                    Reason = $"用户排除扩展名"
                };
            }
        }

        return null;
    }

    // ============================================================
    // 热加载：配置变更 → 重建规则列表
    // ============================================================

    /// <summary>
    /// 响应 IConfigService.ConfigChanged 事件 — 重新加载启发式规则并重建规则列表。
    ///
    /// 执行顺序：
    /// 1. 重新从磁盘加载启发式规则 JSON 文件
    /// 2. 重建完整规则列表（硬规则 + 用户排除 + 新启发式规则）
    /// 3. 触发 RulesChanged 事件（异步，不阻塞事件源线程）
    ///
    /// 此方法在 IConfigService 的事件触发线程上执行（可能是 ThreadPool 线程），
    /// 因此内部逻辑必须是线程安全的。
    /// </summary>
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        try
        {
            _logger.LogDebug("收到配置变更通知，开始重建规则列表");

            // 重建规则列表（内部有锁保护）
            CompileRules();

            _logger.LogInformation("规则列表已重建，当前共 {Count} 条活动规则", _rules.Count);

            // 触发 RulesChanged 事件 — 通知决策引擎清空缓存并异步重扫
            OnRulesChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置变更后重建规则列表失败");
        }
    }

    /// <summary>
    /// 触发 RulesChanged 事件。事件处理器中的异常不会中断其他订阅者。
    /// </summary>
    private void OnRulesChanged()
    {
        try
        {
            RulesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RulesChanged 事件处理器抛出异常");
        }
    }

    // ============================================================
    // 规则列表编译
    // ============================================================

    /// <summary>
    /// 编译完整规则列表：硬规则 + 用户排除规则 + 启发式规则，按优先级排序。
    ///
    /// 规则来源（对应设计文档 6.2 节优先级表）：
    /// P1 — 内置硬禁止规则（BuiltInForbidRules）
    /// P2 — 内置硬自动删除规则（BuiltInAutoDeleteRules）
    /// P3 — 用户排除规则（从 UserConfig 动态生成）
    /// P4 — 内置保护规则（BuiltInProtectRules）
    /// P5 — 启发式建议删除规则（从 JSON 加载）
    /// P6 — 启发式建议保留规则（从 JSON 加载）
    /// P7 — 默认保留（由 Evaluate 的 NoMatch 返回，无需显式规则）
    /// </summary>
    private void CompileRules()
    {
        lock (_rulesLock)
        {
            try
            {
                var allRules = new List<IRule>();

                // 优先级1：内置硬禁止规则（只读，编译在代码中）
                allRules.AddRange(BuiltInForbidRules.All);
                _logger.LogDebug("加载硬禁止规则: {Count} 条", BuiltInForbidRules.All.Count);

                // 优先级2：内置硬自动删除规则（只读，编译在代码中）
                allRules.AddRange(BuiltInAutoDeleteRules.All);
                _logger.LogDebug("加载硬自动删除规则: {Count} 条", BuiltInAutoDeleteRules.All.Count);

                // 优先级3：用户排除规则 — 由 Evaluate() 方法动态评估
                // （不在此处编译，因为用户排除依赖于每次 Evaluate 传入的 UserConfig 参数）

                // 优先级4：内置保护规则（Docker/WSL/虚拟机）
                allRules.AddRange(BuiltInProtectRules.All);
                _logger.LogDebug("加载保护规则: {Count} 条", BuiltInProtectRules.All.Count);

                // 优先级5/6：启发式规则（从 JSON 配置文件加载，支持用户自定义）
                var heuristicRules = _heuristicLoader.Load();
                allRules.AddRange(heuristicRules);
                _logger.LogDebug("加载启发式规则: {Count} 条", heuristicRules.Count);

                // 按优先级排序（值越小优先级越高）
                allRules.Sort((a, b) => a.Priority.CompareTo(b.Priority));

                _rules = allRules.AsReadOnly();

                // 重建用户排除预编译缓存
                var config = _configService.Current;
                _userExclusionCache = BuildExclusionCache(config);
                _userExcludedExtensionsSet = _userExclusionCache.Extensions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "编译规则列表失败，保留当前规则集（{Count}条）", _rules.Count);
                // 保留 _rules 的现有值不变，确保不会因编译失败导致规则列表为空
            }
        }
    }

    // ============================================================
    // IDisposable
    // ============================================================

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // 取消订阅配置变更事件，防止内存泄漏
        _configService.ConfigChanged -= OnConfigChanged;

        _logger.LogDebug("RuleEngine 已释放");
    }

}
