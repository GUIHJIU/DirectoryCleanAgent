using DirectoryCleanAgent.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DirectoryCleanAgent.Data;

/// <summary>
/// 数据层 DI 注册扩展方法。
///
/// 注册顺序：
/// 1. ISqliteConnectionFactory → DatabaseInitializer（单例，应用生命周期内共享）
/// 2. 数据库初始化（建表/WAL/索引，幂等执行）
/// 3. 四个仓储接口绑定（单例，内部管理批量写入队列生命周期）
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// 注册数据层所有服务。
    /// </summary>
    /// <param name="services">DI 服务集合</param>
    /// <param name="databasePath">SQLite 数据库文件完整路径</param>
    public static IServiceCollection AddDataLayer(
        this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        // 1. 注册连接工厂（DatabaseInitializer 单例）
        services.AddSingleton<ISqliteConnectionFactory>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DatabaseInitializer>>();
            return new DatabaseInitializer(databasePath, logger);
        });

        // 2. 注册仓储（单例，各自管理内部批量写入队列）
        services.AddSingleton<IFileDecisionCacheRepository>(sp =>
        {
            var factory = sp.GetRequiredService<ISqliteConnectionFactory>();
            var logger = sp.GetRequiredService<ILogger<FileDecisionCacheRepository>>();
            return new FileDecisionCacheRepository(factory, logger);
        });

        services.AddSingleton<ILocalTombstoneRepository>(sp =>
        {
            var factory = sp.GetRequiredService<ISqliteConnectionFactory>();
            var logger = sp.GetRequiredService<ILogger<LocalTombstoneRepository>>();
            return new LocalTombstoneRepository(factory, logger);
        });

        services.AddSingleton<IDeletionRecordRepository>(sp =>
        {
            var factory = sp.GetRequiredService<ISqliteConnectionFactory>();
            var logger = sp.GetRequiredService<ILogger<DeletionRecordRepository>>();
            return new DeletionRecordRepository(factory, logger);
        });

        services.AddSingleton<IAuditLogRepository>(sp =>
        {
            var factory = sp.GetRequiredService<ISqliteConnectionFactory>();
            var logger = sp.GetRequiredService<ILogger<AuditLogRepository>>();
            return new AuditLogRepository(factory, logger);
        });

        // B1: 墓碑内存缓存（启动时全量加载，流式枚举中查询）
        services.AddSingleton<ITombstoneCache>(sp =>
        {
            var repository = sp.GetRequiredService<ILocalTombstoneRepository>();
            var logger = sp.GetRequiredService<ILogger<TombstoneCache>>();
            return new TombstoneCache(repository, logger);
        });

        return services;
    }

    /// <summary>
    /// 初始化数据库（启动时调用一次）。
    /// 应在宿主构建完 ServiceProvider 后立即执行。
    /// 此方法幂等，可安全重复调用。
    /// </summary>
    /// <param name="services">IServiceProvider 实例</param>
    /// <param name="ct">取消令牌</param>
    public static async Task InitializeDatabaseAsync(
        this IServiceProvider services, CancellationToken ct = default)
    {
        var factory = services.GetRequiredService<ISqliteConnectionFactory>();
        if (factory is DatabaseInitializer initializer)
        {
            await initializer.InitializeAsync(ct).ConfigureAwait(false);
        }
    }
}
