using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Services;

/// <summary>
/// 全局应用状态服务接口。
/// 管理 AppState 状态机转换，并通过事件通知订阅者。
/// </summary>
public interface IAppStateService
{
    /// <summary>当前应用全局状态</summary>
    AppState CurrentState { get; }

    /// <summary>当前应用模式（Normal / ReadOnly）</summary>
    AppMode AppMode { get; set; }

    /// <summary>Everything 索引是否就绪</summary>
    bool IsIndexReady { get; set; }

    /// <summary>当前是否具有管理员权限</summary>
    bool IsAdmin { get; set; }

    /// <summary>
    /// 尝试转换到目标状态。不符合状态机规则的转换将被拒绝并记录警告。
    /// 状态机规则: Initializing → Ready → Scanning/Cleaning/Error → Ready
    /// </summary>
    /// <param name="newState">目标状态</param>
    /// <returns>是否转换成功</returns>
    bool TransitionTo(AppState newState);

    /// <summary>状态变更事件</summary>
    event EventHandler<AppState>? StateChanged;

    /// <summary>应用模式变更事件</summary>
    event EventHandler<AppMode>? AppModeChanged;
}
