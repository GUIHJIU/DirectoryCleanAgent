using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Services;

/// <summary>
/// 全局应用状态服务实现。
/// 管理 AppState 状态机，确保状态转换合法（如不允许从 Initializing 直接跳到 Cleaning）。
/// </summary>
public class AppStateService : IAppStateService
{
    private readonly ILogger<AppStateService> _logger;
    private AppState _currentState = AppState.Initializing;
    private AppMode _appMode = AppMode.Normal;

    public AppStateService(ILogger<AppStateService> logger)
    {
        _logger = logger;
    }

    public AppState CurrentState => _currentState;
    public AppMode AppMode
    {
        get => _appMode;
        set
        {
            if (_appMode != value)
            {
                _appMode = value;
                _logger.LogInformation("应用模式切换为 {Mode}", value);
                AppModeChanged?.Invoke(this, value);
            }
        }
    }
    public bool IsIndexReady { get; set; }
    public bool IsAdmin { get; set; }

    public event EventHandler<AppState>? StateChanged;
    public event EventHandler<AppMode>? AppModeChanged;

    /// <summary>
    /// 尝试转换到目标状态。
    /// 合法转换路径:
    ///   Initializing → Ready (初始化完成)
    ///   Ready → Scanning (开始扫描)
    ///   Scanning → Ready (扫描完成)
    ///   Ready → Cleaning (开始清理)
    ///   Cleaning → Ready (清理完成)
    ///   Any → Error (发生错误)
    ///   Error → Ready (错误恢复)
    /// </summary>
    public bool TransitionTo(AppState newState)
    {
        try
        {
            if (_currentState == newState)
            {
                _logger.LogDebug("状态未变更: {State}", newState);
                return true;
            }

            if (!IsValidTransition(_currentState, newState))
            {
                _logger.LogWarning("非法的状态转换: {From} → {To}", _currentState, newState);
                return false;
            }

            var oldState = _currentState;
            _currentState = newState;
            _logger.LogInformation("状态转换: {From} → {To}", oldState, newState);
            StateChanged?.Invoke(this, newState);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "状态转换异常: → {TargetState}", newState);
            return false;
        }
    }

    /// <summary>
    /// 判断状态转换是否合法。
    /// </summary>
    private static bool IsValidTransition(AppState from, AppState to)
    {
        return to switch
        {
            AppState.Ready => from is AppState.Initializing or AppState.Scanning
                               or AppState.Cleaning or AppState.Error,
            AppState.Scanning => from == AppState.Ready,
            AppState.Cleaning => from == AppState.Ready,
            AppState.Error => true, // 任何状态都可转入错误状态
            AppState.Initializing => false, // 不允许回退到初始化
            _ => false
        };
    }
}
