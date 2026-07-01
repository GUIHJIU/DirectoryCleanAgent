using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.ViewModels;

/// <summary>
/// 历史记录窗口 ViewModel — 简单的容器 VM，持有两个子 ViewModel。
/// HistoryWindow 的 TabControl 各 Tab 分别绑定到此 VM 的子属性。
/// </summary>
public class HistoryWindowViewModel : ViewModelBase
{
    private readonly ILogger<HistoryWindowViewModel> _logger;

    /// <summary>审计日志 Tab 的 ViewModel</summary>
    public AuditLogViewModel AuditLogViewModel { get; }

    /// <summary>删除历史 Tab 的 ViewModel</summary>
    public DeletionHistoryViewModel DeletionHistoryViewModel { get; }

    public HistoryWindowViewModel(
        ILogger<HistoryWindowViewModel> logger,
        AuditLogViewModel auditLogViewModel,
        DeletionHistoryViewModel deletionHistoryViewModel)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        AuditLogViewModel = auditLogViewModel ?? throw new ArgumentNullException(nameof(auditLogViewModel));
        DeletionHistoryViewModel = deletionHistoryViewModel ?? throw new ArgumentNullException(nameof(deletionHistoryViewModel));

        _logger.LogMethodEntry("HistoryWindowViewModel 构造完成");
    }
}
