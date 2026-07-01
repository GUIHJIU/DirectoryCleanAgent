using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Models;

/// <summary>
/// 操作栏各按钮/控件的状态模型。
/// 由 MainViewModel 根据 AppState、IsAdmin、IsIndexReady 等计算后更新。
/// </summary>
public class ActionBarState
{
    /// <summary>当前是否为管理员权限</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Everything 索引是否就绪</summary>
    public bool IsIndexReady { get; set; }

    /// <summary>是否正在执行操作（扫描/清理中）</summary>
    public bool IsOperating { get; set; }

    /// <summary>全局应用状态</summary>
    public AppState AppState { get; set; } = AppState.Initializing;

    /// <summary>一键清理按钮是否可用</summary>
    public bool CanQuickClean => IsAdmin && IsIndexReady && !IsOperating && AppState == AppState.Ready;

    /// <summary>智能选择按钮是否可用</summary>
    public bool CanSmartSelect => IsAdmin && IsIndexReady && !IsOperating && AppState == AppState.Ready;

    /// <summary>模拟运行按钮是否可用</summary>
    public bool CanSimulate => IsIndexReady && !IsOperating && AppState == AppState.Ready;

    /// <summary>导出报告按钮是否可用</summary>
    public bool CanExport => IsIndexReady && !IsOperating && AppState == AppState.Ready;

    /// <summary>刷新扫描按钮是否可用</summary>
    public bool CanRefresh => !IsOperating && AppState != AppState.Initializing;

    /// <summary>取消按钮是否可见</summary>
    public bool CanCancel => IsOperating;
}
