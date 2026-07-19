using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Everything.Installation;
using DirectoryCleanAgent.Everything.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Everything;

/// <summary>
/// Everything 模块服务注册扩展方法。
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// 向 DI 容器注册 Everything SDK 集成层的所有服务。
    ///
    /// 当前注册（A2 阶段）：
    /// - EverythingDependencyDetector（Singleton）：启动时执行 5 步检测序列。
    ///
    /// 后续 B1 阶段将补充 IFileListProvider 的注册。
    /// </summary>
    /// <param name="services">DI 服务集合</param>
    /// <returns>服务集合（链式调用）</returns>
    public static IServiceCollection AddEverythingServices(this IServiceCollection services)
    {
        // Task 1: Everything SDK 全局状态共享锁
        services.AddSingleton<EverythingSdkLock>();

        // A2: 依赖检测器（同时注册接口和具体类型）
        services.AddSingleton<EverythingDependencyDetector>();
        services.AddSingleton<IEverythingDetector>(sp => sp.GetRequiredService<EverythingDependencyDetector>());

        // B1: Everything SDK 抽象层（SDK 2.0 适配器）
        services.AddSingleton<IEverythingSdk, EverythingSdkWrapper>();

        // B1: IFileListProvider 实现 — Everything 流式适配器
        services.AddSingleton<IFileListProvider, EverythingFileListProvider>();

        // 首次运行向导：Everything 便携版一键安装
        services.AddSingleton<IEverythingInstallService, EverythingInstallService>();

        return services;
    }
}
