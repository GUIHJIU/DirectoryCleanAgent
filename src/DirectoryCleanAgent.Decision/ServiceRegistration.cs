using Microsoft.Extensions.DependencyInjection;

namespace DirectoryCleanAgent.Decision;

/// <summary>
/// 决策引擎层 DI 注册扩展方法。
///
/// 注册顺序：
/// 1. IDecisionEngine / DecisionEngine — 决策引擎主实现（单例）
///
/// 使用方式（在宿主 Program.cs 中）：
///   services.AddDecisionLayer();
///
/// 依赖条件：
///   - 必须先调用 AddConfigLayer() 注册 IConfigService
///   - DecisionEngine 构造函数注入 IConfigService 和 ILogger&lt;DecisionEngine&gt;
/// </summary>
public static class DecisionServiceRegistration
{
    /// <summary>
    /// 注册决策引擎层所有服务（单例生命周期）。
    /// 决策引擎为纯计算服务，无外部资源持有，单例即可满足并发安全要求。
    /// </summary>
    /// <param name="services">DI 服务集合</param>
    public static IServiceCollection AddDecisionLayer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 1. IDecisionEngine / DecisionEngine — 决策引擎主实现
        services.AddSingleton<IDecisionEngine>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = sp.GetRequiredService<ILogger<DecisionEngine>>();
            return new DecisionEngine(configService, logger);
        });

        return services;
    }
}
