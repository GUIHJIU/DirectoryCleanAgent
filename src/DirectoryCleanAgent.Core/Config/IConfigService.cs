namespace DirectoryCleanAgent.Core.Config;

/// <summary>
/// 配置管理服务抽象接口。
/// 负责 UserConfig 的加载、保存、变更通知以及规则版本号的原子递增。
/// </summary>
public interface IConfigService
{
    /// <summary>当前加载的用户配置（可能为默认值，直到 LoadAsync 完成）</summary>
    UserConfig Current { get; }

    /// <summary>
    /// 从默认路径异步加载配置文件。
    /// 若文件不存在，自动创建包含默认值的配置文件。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// 将当前配置异步持久化到磁盘。
    /// 使用先写临时文件、再原子替换的策略，防止写入中断导致配置损坏。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>
    /// 递增 RuleCacheVersion 并自动触发保存。
    /// 由 FileSystemWatcher（500ms 防抖后）或内置规则编辑器保存时调用。
    /// </summary>
    void IncrementRuleCacheVersion();

    /// <summary>配置变更事件（SaveAsync 或 IncrementRuleCacheVersion 后触发）</summary>
    event EventHandler? ConfigChanged;

    /// <summary>获取完整的配置文件路径</summary>
    string ConfigFilePath { get; }
}
