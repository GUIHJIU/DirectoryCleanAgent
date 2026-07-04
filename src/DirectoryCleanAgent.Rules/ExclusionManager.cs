using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.PathHandling;
using DirectoryCleanAgent.Rules.Models;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Rules;

/// <summary>
/// 排除管理器实现 — 管理用户排除目录和扩展名的过滤逻辑。
///
/// 职责：
/// - 预编译用户排除目录的 Glob 模式和纯路径前缀
/// - 预编译用户排除扩展名的 HashSet（O(1) 查找）
/// - 提供 IsExcluded() 快速判断，供 B1 流式适配器在扫描阶段前置过滤
///
/// 在流式处理管道中，排除过滤发生在规则引擎裁决之前（设计文档 7.1 节流程步骤 K）。
/// 排除规则优先级高于所有规则引擎规则。
///
/// 线程安全：
/// - _cache 通过 volatile 保证多线程可见性
/// - IsExcluded() 在无锁条件下读取 _cache
/// - Reload() 通过 IConfigService.ConfigChanged 自动触发
/// </summary>
public sealed class ExclusionManager : IExclusionManager, IDisposable
{
    private readonly IConfigService _configService;
    private readonly ILogger<ExclusionManager> _logger;

    /// <summary>预编译的排除缓存，volatile 保证多线程可见性</summary>
    private volatile CompiledExclusionCache _cache = CompiledExclusionCache.Empty;

    /// <summary>是否已释放</summary>
    private bool _disposed;

    // ============================================================
    // 构造与生命周期
    // ============================================================

    public ExclusionManager(
        IConfigService configService,
        ILogger<ExclusionManager> logger)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // 初始构建排除缓存
        RebuildCache();

        // 订阅配置变更事件 — 配置变更时自动重建排除缓存
        _configService.ConfigChanged += OnConfigChanged;

        _logger.LogInformation(
            "排除管理器初始化完成：{DirCount} 个目录模式，{ExtCount} 个扩展名",
            _cache.DirPatterns.Count + _cache.PlainDirPrefixes.Count,
            _cache.Extensions.Count);
    }

    // ============================================================
    // IExclusionManager 实现
    // ============================================================

    /// <inheritdoc />
    public bool IsExcluded(string filePath)
    {
        var cache = _cache;

        // 快速路径：无排除规则时直接返回
        if (cache.PlainDirPrefixes.Count == 0 &&
            cache.DirPatterns.Count == 0 &&
            cache.Extensions.Count == 0)
            return false;

        // 目录排除检查
        if (cache.PlainDirPrefixes.Count > 0 || cache.DirPatterns.Count > 0)
        {
            return IsExcludedByDirectory(filePath, cache);
        }

        // 扩展名排除检查
        if (cache.Extensions.Count > 0)
        {
            var ext = Path.GetExtension(filePath);
            if (ext != null)
            {
                var clean = ext.TrimStart('.').ToLowerInvariant();
                if (cache.Extensions.Contains(clean))
                    return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void Reload()
    {
        RebuildCache();
    }

    /// <summary>
    /// 判断文件是否被排除（基于指定 UserConfig，供 RuleEngine 动态 config 场景使用）。
    /// </summary>
    /// <param name="filePath">文件完整路径（\\?\ 格式）</param>
    /// <param name="config">用户配置</param>
    /// <returns>true 表示被排除</returns>
    internal static bool IsExcluded(string filePath, UserConfig config)
    {
        var cache = BuildExclusionCacheFromConfig(config);

        if (cache.PlainDirPrefixes.Count == 0 &&
            cache.DirPatterns.Count == 0 &&
            cache.Extensions.Count == 0)
            return false;

        if (cache.PlainDirPrefixes.Count > 0 || cache.DirPatterns.Count > 0)
        {
            if (IsExcludedByDirectory(filePath, cache))
                return true;
        }

        if (cache.Extensions.Count > 0)
        {
            var ext = Path.GetExtension(filePath);
            if (ext != null)
            {
                var clean = ext.TrimStart('.').ToLowerInvariant();
                if (cache.Extensions.Contains(clean))
                    return true;
            }
        }

        return false;
    }

    // ============================================================
    // 内部实现
    // ============================================================

    /// <summary>重新构建排除缓存（从 IConfigService.Current 读取最新配置）</summary>
    private void RebuildCache()
    {
        var config = _configService.Current;
        _cache = BuildExclusionCacheFromConfig(config);
        _logger.LogDebug(
            "排除缓存已重建：{DirCount} 目录模式，{ExtCount} 扩展名",
            _cache.DirPatterns.Count + _cache.PlainDirPrefixes.Count,
            _cache.Extensions.Count);
    }

    /// <summary>响应配置变更事件</summary>
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        try
        {
            RebuildCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "配置变更后重建排除缓存失败，保留当前缓存");
        }
    }

    /// <summary>检查文件路径是否匹配排除目录规则</summary>
    private static bool IsExcludedByDirectory(string filePath, CompiledExclusionCache cache)
    {
        var filePathLower = filePath.ToLowerInvariant();
        var filePathNoPrefix = filePathLower.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? filePathLower[4..]
            : filePathLower;

        // 1) 纯路径前缀快速匹配
        foreach (var prefix in cache.PlainDirPrefixes)
        {
            if (filePathLower.StartsWith(prefix, StringComparison.Ordinal) ||
                filePathNoPrefix.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // 2) 预编译的 Glob 模式匹配
        foreach (var pattern in cache.DirPatterns)
        {
            if (pattern.IsMatch(filePathLower) || pattern.IsMatch(filePathNoPrefix))
            {
                return true;
            }
        }

        return false;
    }

    // ============================================================
    // 排除缓存构建
    // ============================================================

    /// <summary>
    /// 预编译的排除缓存结构。
    /// </summary>
    internal sealed class CompiledExclusionCache
    {
        /// <summary>预编译的目录 Glob 模式（含通配符）</summary>
        public IReadOnlyList<CompiledGlobPattern> DirPatterns { get; init; } = Array.Empty<CompiledGlobPattern>();

        /// <summary>纯路径前缀（无通配符），用于 StartsWith 快速匹配</summary>
        public IReadOnlyList<string> PlainDirPrefixes { get; init; } = Array.Empty<string>();

        /// <summary>排除扩展名 HashSet（小写，不含前导点），OrdinalIgnoreCase</summary>
        public HashSet<string> Extensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public static CompiledExclusionCache Empty => new();
    }

    /// <summary>
    /// 从 UserConfig 构建预编译的排除缓存。
    /// </summary>
    internal static CompiledExclusionCache BuildExclusionCacheFromConfig(UserConfig config)
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

                // 使用 Path.GetFullPath 做路径标准化（不添加 \\?\ 前缀）
                var normalized = Path.GetFullPath(dir.TrimEnd('\\'));
                var lowered = normalized.ToLowerInvariant();

                if (lowered.Contains('*') || lowered.Contains('?'))
                {
                    // 通配符模式 → 预编译为 CompiledGlobPattern
                    dirPatterns.Add(new CompiledGlobPattern(lowered));
                }
                else
                {
                    // 纯路径前缀 → 加入快速匹配列表
                    plainPrefixes.Add(lowered);
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

        return new CompiledExclusionCache
        {
            DirPatterns = dirPatterns,
            PlainDirPrefixes = plainPrefixes,
            Extensions = extensions
        };
    }

    // ============================================================
    // IDisposable
    // ============================================================

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _configService.ConfigChanged -= OnConfigChanged;
        _logger.LogDebug("ExclusionManager 已释放");
    }
}
