using DirectoryCleanAgent.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DirectoryCleanAgent.Operations;

/// <summary>
/// Operations 模块服务注册扩展方法。
///
/// B4 阶段注册：
/// - IOperationExecutor（Singleton）：操作执行器，负责文件删除/移动/隔离区备份。
///
/// 后续 B5 阶段将补充 IBackupManager 的注册。
/// </summary>
public static class OperationsServiceRegistration
{
    /// <summary>
    /// 向 DI 容器注册 Operations 层的所有服务。
    /// </summary>
    /// <param name="services">DI 服务集合</param>
    /// <returns>服务集合（链式调用）</returns>
    public static IServiceCollection AddOperationsLayer(this IServiceCollection services)
    {
        // B4: IOperationExecutor 实现 — 乐观删除 + 回收站 + 跨卷隔离区
        services.AddSingleton<IOperationExecutor, OperationExecutor>();

        // B5: IBackupManager 实现 — 回滚管理器（隔离区/回收站恢复 + 哈希校验 + 墓碑清除）
        services.AddSingleton<IBackupManager, BackupManager>();

        // B6: IQuarantineManager 实现 — 隔离区管理（空间监控、容量强制、过期清理、批量操作、命名规范）
        services.AddSingleton<IQuarantineManager, QuarantineManager>();

        return services;
    }
}
