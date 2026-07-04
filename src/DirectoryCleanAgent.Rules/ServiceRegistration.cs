using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Rules.Heuristic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Rules;

/// <summary>
/// 规则引擎层 DI 注册扩展方法。
///
/// 注册顺序：
/// 1. HeuristicRuleLoader — 启发式规则 JSON 加载器（单例）
/// 2. IRuleEngine / RuleEngine — 规则引擎主实现（单例）
///
/// 使用方式（在宿主 Program.cs 中）：
///   services.AddRulesLayer();
///
/// 依赖条件：
///   - 必须先调用 AddConfigLayer() 注册 IConfigService
///   - 建议在 ServiceProvider 构建后调用：
///       await sp.GetRequiredService<IConfigService>().LoadAsync();
///       然后 RuleEngine 构造函数会自动编译规则列表
/// </summary>
public static class RulesServiceRegistration
{
    /// <summary>
    /// 注册规则引擎层所有服务（单例生命周期）。
    /// </summary>
    /// <param name="services">DI 服务集合</param>
    public static IServiceCollection AddRulesLayer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 1. HeuristicRuleLoader — 启发式规则加载器
        services.AddSingleton<HeuristicRuleLoader>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<HeuristicRuleLoader>>();
            var rulesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpaceClear", "rules");

            var loader = new HeuristicRuleLoader(logger, rulesDir);

            // 首次启动时引导：若规则目录为空，从内嵌资源提取默认规则
            BootstrapDefaultRules(rulesDir, logger);

            return loader;
        });

        // 2. IExclusionManager / ExclusionManager — 排除管理器（在规则引擎之前注册）
        services.AddSingleton<IExclusionManager, ExclusionManager>();

        // 3. IRuleEngine / RuleEngine — 规则引擎主实现
        services.AddSingleton<IRuleEngine>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var heuristicLoader = sp.GetRequiredService<HeuristicRuleLoader>();
            var exclusionManager = sp.GetRequiredService<IExclusionManager>();
            var logger = sp.GetRequiredService<ILogger<RuleEngine>>();
            return new RuleEngine(configService, heuristicLoader, exclusionManager, logger);
        });

        return services;
    }

    /// <summary>
    /// 首次启动引导：若规则目录中没有启发式规则 JSON 文件，
    /// 从程序集内嵌资源中提取默认规则文件。
    /// </summary>
    private static void BootstrapDefaultRules(string rulesDir, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(rulesDir))
            {
                Directory.CreateDirectory(rulesDir);
                logger.LogInformation("创建规则目录: {Path}", rulesDir);
            }

            // 检查是否已有规则文件
            var existingFiles = Directory.GetFiles(rulesDir, "heuristic_*.json", SearchOption.TopDirectoryOnly);
            if (existingFiles.Length > 0)
            {
                logger.LogDebug("规则目录中已有 {Count} 个规则文件，跳过引导", existingFiles.Length);
                return;
            }

            // 从内嵌资源中提取默认规则文件
            var assembly = typeof(RulesServiceRegistration).Assembly;
            var resourceName = "DirectoryCleanAgent.Rules.Heuristic.DefaultHeuristicRules.json";

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                logger.LogWarning("未找到内嵌默认规则资源: {Resource}", resourceName);
                return;
            }

            var destPath = Path.Combine(rulesDir, "heuristic_default.json");
            using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(fileStream);

            logger.LogInformation("已从内嵌资源提取默认规则文件到: {Path}", destPath);
        }
        catch (Exception ex)
        {
            // 引导失败不应阻止程序启动 — 规则引擎将使用空规则列表运行
            logger.LogError(ex, "引导默认规则文件失败，规则引擎将以空规则列表启动");
        }
    }
}
