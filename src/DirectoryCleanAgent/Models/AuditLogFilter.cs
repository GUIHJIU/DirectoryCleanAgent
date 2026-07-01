using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DirectoryCleanAgent.Models;

/// <summary>
/// 审计日志过滤参数模型 — 绑定到 AuditLogView 过滤栏控件。
/// 属性变更时自动通知 SearchCommand 刷新列表。
/// </summary>
public class AuditLogFilter : INotifyPropertyChanged
{
    private DateTime? _from;
    private DateTime? _to;
    private string? _operationType;
    private string? _userSid;

    /// <summary>起始时间（含），null 表示不限制</summary>
    public DateTime? From
    {
        get => _from;
        set { if (_from != value) { _from = value; OnPropertyChanged(); } }
    }

    /// <summary>截止时间（含），null 表示不限制</summary>
    public DateTime? To
    {
        get => _to;
        set { if (_to != value) { _to = value; OnPropertyChanged(); } }
    }

    /// <summary>操作类型筛选，null 表示全部</summary>
    public string? OperationType
    {
        get => _operationType;
        set { if (_operationType != value) { _operationType = value; OnPropertyChanged(); } }
    }

    /// <summary>用户 SID 筛选，null 表示全部</summary>
    public string? UserSid
    {
        get => _userSid;
        set { if (_userSid != value) { _userSid = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
