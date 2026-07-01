using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Data;

namespace DirectoryCleanAgent.AI;

/// <summary>
/// AI 顾问层 DI 注册扩展方法。
///
/// 注册顺序：
///   1. AiRateLimiter          — 全局令牌桶速率限制器（单例）
///   2. AiCircuitBreaker       — 熔断器状态机（单例）
///   3. AiDailyUsageTracker    — 每日调用次数追踪器（单例，构造函数注入 dataPath + dailyLimit）
///   4. AiPromptBuilder        — 提示词构建器（单例，无状态）
///   5. HttpClient             — HTTP 客户端（单例，30 秒超时）
///   6. IAiAdvisorService      — AI 顾问主服务（单例，聚合上述组件）
///
/// 使用方式（在宿主 App.xaml.cs ConfigureServices 中）：
///   services.AddAiLayer();
///
/// 依赖条件（必须先注册）：
///   - AddConfigLayer()         → IConfigService
///   - AddDataLayer()            → IFileDecisionCacheRepository
///   - AddDecisionLayer()        → IDecisionEngine
/// </summary>
public static class AiServiceRegistration
{
    /// <summary>
    /// 注册 AI 顾问层所有服务（全部为单例生命周期）。
    /// AI 组件均为无外部资源持有的计算或状态管理服务，单例天然线程安全。
    /// </summary>
    /// <param name="services">DI 服务集合</param>
    /// <returns>服务集合（链式调用）</returns>
    public static IServiceCollection AddAiLayer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 1. AiRateLimiter — 全局速率限制器（令牌桶 + 并发控制）
        services.AddSingleton<AiRateLimiter>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AiRateLimiter>>();
            return new AiRateLimiter(logger);
        });

        // 2. AiCircuitBreaker — 熔断器（状态机，连续失败保护）
        services.AddSingleton<AiCircuitBreaker>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AiCircuitBreaker>>();
            return new AiCircuitBreaker(logger);
        });

        // 3. AiDailyUsageTracker — 每日调用次数持久化追踪
        services.AddSingleton<AiDailyUsageTracker>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = sp.GetRequiredService<ILogger<AiDailyUsageTracker>>();

            // 数据存储路径：优先使用配置路径，否则使用默认 %LocalAppData%\SpaceClear
            var dataPath = string.IsNullOrEmpty(configService.Current.DataStoragePath)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SpaceClear")
                : configService.Current.DataStoragePath;

            var dailyLimit = configService.Current.AIDailyLimit;
            return new AiDailyUsageTracker(dataPath, dailyLimit, logger);
        });

        // 4. AiPromptBuilder — 提示词构建器（无状态，线程安全）
        services.AddSingleton<AiPromptBuilder>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AiPromptBuilder>>();
            return new AiPromptBuilder(logger);
        });

        // 5. HttpClient — 单例（.NET 最佳实践：HttpClient 不应频繁创建/销毁）
        services.AddSingleton<HttpClient>(_ =>
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            // 设置 User-Agent
            client.DefaultRequestHeaders.Add("User-Agent", "DirectoryCleanAgent/1.0");
            return client;
        });

        // 6. IAiAdvisorService / AiAdvisorService — 主实现（聚合上述所有组件）
        services.AddSingleton<IAiAdvisorService>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var cacheRepo = sp.GetRequiredService<IFileDecisionCacheRepository>();
            var decisionEngine = sp.GetRequiredService<IDecisionEngine>();
            var logger = sp.GetRequiredService<ILogger<AiAdvisorService>>();
            var rateLimiter = sp.GetRequiredService<AiRateLimiter>();
            var circuitBreaker = sp.GetRequiredService<AiCircuitBreaker>();
            var usageTracker = sp.GetRequiredService<AiDailyUsageTracker>();
            var promptBuilder = sp.GetRequiredService<AiPromptBuilder>();
            var httpClient = sp.GetRequiredService<HttpClient>();

            return new AiAdvisorService(
                configService, cacheRepo, decisionEngine, logger,
                rateLimiter, circuitBreaker, usageTracker, promptBuilder,
                httpClient);
        });

        return services;
    }
}
