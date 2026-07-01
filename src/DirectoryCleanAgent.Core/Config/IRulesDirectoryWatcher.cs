namespace DirectoryCleanAgent.Core.Config;

/// <summary>
/// 规则目录文件监控器抽象接口。
///
/// 职责：监控 %LocalAppData%\SpaceClear\rules\ 目录下的 *.json 启发式规则文件变更，
/// 应用防抖后调用 <see cref="IConfigService.IncrementRuleCacheVersion"/> 触发缓存失效。
///
/// 线程安全：StartAsync / StopAsync 应顺序调用；
/// 底层 FileSystemWatcher 事件在 ThreadPool 线程上触发。
/// </summary>
public interface IRulesDirectoryWatcher : IDisposable
{
    /// <summary>
    /// 启动对规则目录的监控。
    /// 若 <see cref="RulesDirectoryPath"/> 不存在则自动创建。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 停止监控。与 <see cref="IDisposable.Dispose"/> 不同，
    /// StopAsync 后仍可再次 StartAsync 重启监控。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// 被监控的规则目录完整路径。
    /// 默认：%LocalAppData%\SpaceClear\rules\
    /// 必须在 <see cref="StartAsync"/> 之前设置才生效。
    /// </summary>
    string RulesDirectoryPath { get; set; }

    /// <summary>
    /// 防抖间隔（毫秒），默认 500ms。
    /// 必须在 <see cref="StartAsync"/> 之前设置才生效。
    /// </summary>
    int DebounceIntervalMs { get; set; }

    /// <summary>监控是否正在运行</summary>
    bool IsRunning { get; }
}
