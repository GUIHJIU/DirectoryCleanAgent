using Microsoft.Extensions.DependencyInjection;

namespace DirectoryCleanAgent.Core.Localization;

/// <summary>
/// 本地化层 DI 注册扩展方法。
///
/// 注册顺序：
/// 1. ILocalizationService / LocalizationService — 本地化服务（单例，含语言切换）
/// 2. SemanticLabelLocalizer — 语义标签本地化器（单例，依赖 ILocalizationService）
///
/// 使用方式（在宿主 Program.cs 中）：
///   services.AddLocalizationLayer();
///   构建 ServiceProvider 后即可通过 ILocalizationService 获取本地化字符串。
///   注意：IConfigService 必须在 AddLocalizationLayer 之前注册。
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// 注册本地化层所有服务（单例生命周期）。
    /// 包含通用本地化服务和语义标签专用本地化器。
    /// </summary>
    /// <param name="services">DI 服务集合</param>
    /// <returns>服务集合（链式调用）</returns>
    /// <exception cref="ArgumentNullException">services 为 null</exception>
    public static IServiceCollection AddLocalizationLayer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 1. ILocalizationService — 通用本地化服务（单例）
        services.AddSingleton<ILocalizationService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<LocalizationService>>();
            var config = sp.GetRequiredService<Config.IConfigService>();
            return new LocalizationService(logger, config);
        });

        // 2. SemanticLabelLocalizer — 语义标签专用本地化器（单例）
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SemanticLabelLocalizer>>();
            var locService = sp.GetRequiredService<ILocalizationService>();
            return new SemanticLabelLocalizer(logger, locService);
        });

        return services;
    }
}
