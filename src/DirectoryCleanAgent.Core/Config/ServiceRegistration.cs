using Microsoft.Extensions.DependencyInjection;

namespace DirectoryCleanAgent.Core.Config;

/// <summary>
/// 配置层 DI 注册扩展方法。
///
/// 注册顺序：
/// 1. IConfigService / JsonConfigService — JSON 配置读写服务（单例）
/// 2. IRulesDirectoryWatcher / RulesDirectoryWatcher — 规则目录热加载监控器（单例）
///
/// 使用方式（在宿主 Program.cs 中）：
///   services.AddConfigLayer();
///   构建 ServiceProvider 后：
///     await sp.GetRequiredService<IConfigService>().LoadAsync();
///     await sp.GetRequiredService<IRulesDirectoryWatcher>().StartAsync();
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// 注册配置层所有服务（单例生命周期）。
    /// 包括 JSON 配置读写服务和规则目录热加载监控器。
    /// </summary>
    /// <param name="services">DI 服务集合</param>
    public static IServiceCollection AddConfigLayer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 1. IConfigService — JSON 配置读写（单例，应用生命周期内共享内存配置）
        services.AddSingleton<IConfigService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<JsonConfigService>>();
            return new JsonConfigService(logger);
        });

        // 2. IRulesDirectoryWatcher — 规则目录 FileSystemWatcher 热加载（单例）
        services.AddSingleton<IRulesDirectoryWatcher>(sp =>
        {
            var configService = sp.GetRequiredService<IConfigService>();
            var logger = sp.GetRequiredService<ILogger<RulesDirectoryWatcher>>();
            return new RulesDirectoryWatcher(configService, logger);
        });

        return services;
    }
}
