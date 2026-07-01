using System.Collections.ObjectModel;
using System.Security.Principal;
using System.Windows;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Models;
using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.ViewModels;

/// <summary>
/// 审计日志列表 ViewModel。
///
/// 职责：
/// 1. 从 IAuditLogRepository 分页查询审计日志
/// 2. 管理过滤条件（时间范围、操作类型、用户 SID）
/// 3. 将 AuditLogEntry 映射为 AuditLogDisplayItem 供 DataGrid 绑定
///
/// 约束：审计日志为只追加不可变记录，本 ViewModel 不提供任何编辑/删除操作。
/// </summary>
public class AuditLogViewModel : ViewModelBase
{
    private readonly ILogger<AuditLogViewModel> _logger;
    private readonly IAuditLogRepository _auditLogRepo;

    private const int PageSize = 50;

    // ============================================================
    // 绑定属性
    // ============================================================

    /// <summary>当前页的审计日志条目集合</summary>
    public ObservableCollection<AuditLogDisplayItem> Entries { get; } = new();

    /// <summary>过滤条件（双向绑定到过滤栏控件）</summary>
    public AuditLogFilter Filter { get; } = new();

    /// <summary>操作类型下拉列表选项</summary>
    public static ObservableCollection<string> OperationTypes { get; } = new()
    {
        "(全部)",
        "SCAN_STARTED",
        "SCAN_COMPLETED",
        "FILE_DELETED",
        "CONFIG_CHANGED",
        "RULE_UPDATED",
        "ROLLBACK_EXECUTED",
        "QUARANTINE_CLEANED",
        "AI_ANALYSIS",
        "APP_STARTUP",
        "APP_SHUTDOWN"
    };

    private int _currentPage = 1;
    private int _totalPages = 1;
    private int _totalCount;
    private bool _isLoading;
    private bool _isEmpty;
    private string _emptyMessage = "暂无审计日志记录";

    /// <summary>当前页码（1-based）</summary>
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(CanGoPrevPage));
                OnPropertyChanged(nameof(CanGoNextPage));
                OnPropertyChanged(nameof(PageInfoText));
            }
        }
    }

    /// <summary>总页数</summary>
    public int TotalPages
    {
        get => _totalPages;
        set
        {
            if (SetProperty(ref _totalPages, value))
            {
                OnPropertyChanged(nameof(PageInfoText));
            }
        }
    }

    /// <summary>符合条件的总记录数</summary>
    public int TotalCount
    {
        get => _totalCount;
        set => SetProperty(ref _totalCount, value);
    }

    /// <summary>是否正在加载数据</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>是否无数据</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>空数据提示文本</summary>
    public string EmptyMessage
    {
        get => _emptyMessage;
        set => SetProperty(ref _emptyMessage, value);
    }

    /// <summary>可分页导航文本</summary>
    public string PageInfoText =>
        TotalPages > 0
            ? $"第 {CurrentPage}/{TotalPages} 页，共 {TotalCount} 条"
            : "暂无数据";

    /// <summary>是否有上一页</summary>
    public bool CanGoPrevPage => CurrentPage > 1;

    /// <summary>是否有下一页</summary>
    public bool CanGoNextPage => CurrentPage < TotalPages;

    // ============================================================
    // 命令
    // ============================================================

    public RelayCommand SearchCommand { get; }
    public RelayCommand NextPageCommand { get; }
    public RelayCommand PrevPageCommand { get; }
    public RelayCommand ResetFilterCommand { get; }

    // ============================================================
    // 构造函数
    // ============================================================

    public AuditLogViewModel(
        ILogger<AuditLogViewModel> logger,
        IAuditLogRepository auditLogRepo)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditLogRepo = auditLogRepo ?? throw new ArgumentNullException(nameof(auditLogRepo));

        // 初始化命令
        SearchCommand = new RelayCommand(async () => await ExecuteSearchAsync());
        NextPageCommand = new RelayCommand(async () => await ExecuteNextPageAsync(), () => CanGoNextPage);
        PrevPageCommand = new RelayCommand(async () => await ExecutePrevPageAsync(), () => CanGoPrevPage);
        ResetFilterCommand = new RelayCommand(ExecuteResetFilter);

        _logger.LogMethodEntry("AuditLogViewModel 构造完成");
    }

    // ============================================================
    // 数据加载
    // ============================================================

    /// <summary>
    /// 初始加载：从数据库加载第 1 页审计日志（最新 50 条）。
    /// 在 HistoryWindow 打开后由 HistoryWindow.Loaded 事件调用。
    /// </summary>
    public async Task LoadInitialDataAsync()
    {
        _logger.LogMethodEntry("开始加载审计日志初始数据");
        await ExecuteSearchAsync(resetPage: true);
    }

    /// <summary>
    /// 执行搜索：根据当前过滤条件查询审计日志。
    /// </summary>
    /// <param name="resetPage">是否重置到第 1 页（过滤条件变更时为 true）</param>
    private async Task ExecuteSearchAsync(bool resetPage = true)
    {
        _logger.LogMethodEntry("执行审计日志搜索");

        IsLoading = true;
        try
        {
            // 过滤条件变更时重置到第 1 页
            if (resetPage)
            {
                CurrentPage = 1;
            }

            int offset = (CurrentPage - 1) * PageSize;

            // 处理操作类型筛选：(全部) 视为 null
            string? operationType = Filter.OperationType == "(全部)" ? null : Filter.OperationType;

            // 调用数据层查询（实时从 SQLite 读取，无缓存）
            var entries = await _auditLogRepo.QueryAsync(
                from: Filter.From,
                to: Filter.To,
                userSid: Filter.UserSid,
                operationType: operationType,
                limit: PageSize,
                offset: offset);

            // 将 AuditLogEntry 映射为 UI 展示模型
            var displayItems = entries.Select(MapToDisplayItem).ToList();

            // 更新 UI 集合
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Entries.Clear();
                foreach (var item in displayItems)
                {
                    Entries.Add(item);
                }
                IsEmpty = Entries.Count == 0;
                if (IsEmpty)
                {
                    EmptyMessage = "未找到匹配的审计日志记录";
                }
            });

            // 估算总页数：若当前页满则可能有更多
            if (entries.Count == PageSize)
            {
                TotalPages = CurrentPage + 1; // 乐观估算，下一页加载时会修正
            }
            else
            {
                TotalPages = CurrentPage;
            }
            TotalCount = (CurrentPage - 1) * PageSize + entries.Count;

            _logger.LogInformation("审计日志搜索完成: 返回 {Count} 条, Page={Page}", entries.Count, CurrentPage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "审计日志查询失败");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                EmptyMessage = $"查询失败: {ex.Message}";
                IsEmpty = true;
                Entries.Clear();
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>加载下一页</summary>
    private async Task ExecuteNextPageAsync()
    {
        _logger.LogDebug("审计日志翻到下一页: {Page}", CurrentPage + 1);
        CurrentPage++;
        await ExecuteSearchAsync(resetPage: false);
    }

    /// <summary>加载上一页</summary>
    private async Task ExecutePrevPageAsync()
    {
        _logger.LogDebug("审计日志翻到上一页: {Page}", CurrentPage - 1);
        if (CurrentPage > 1)
        {
            CurrentPage--;
            await ExecuteSearchAsync(resetPage: false);
        }
    }

    /// <summary>重置过滤条件并重新搜索</summary>
    private void ExecuteResetFilter()
    {
        _logger.LogMethodEntry("重置审计日志过滤条件");
        try
        {
            Filter.From = null;
            Filter.To = null;
            Filter.OperationType = "(全部)";
            Filter.UserSid = null;
            _ = ExecuteSearchAsync(resetPage: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重置过滤条件失败");
        }
    }

    // ============================================================
    // 映射方法
    // ============================================================

    /// <summary>
    /// 将 AuditLogEntry 数据实体映射为 UI 展示模型。
    /// 时间戳从 UTC 转为本地时区，用户 SID 做友好化处理。
    /// </summary>
    private static AuditLogDisplayItem MapToDisplayItem(AuditLogEntry entry)
    {
        // 获取用户友好名称（尝试从 SID 翻译为账户名）
        string userDisplay = entry.UserSid;
        try
        {
            var account = new SecurityIdentifier(entry.UserSid)
                .Translate(typeof(NTAccount))
                .ToString();
            userDisplay = account;
        }
        catch
        {
            // SID 无法翻译时直接使用原始 SID
        }

        // 详情 JSON 超过 100 字符时截断，完整内容放入 ToolTip
        string detailsSummary = string.Empty;
        string detailsFull = string.Empty;
        if (!string.IsNullOrEmpty(entry.Details))
        {
            detailsFull = entry.Details;
            detailsSummary = entry.Details.Length > 100
                ? entry.Details[..100] + "..."
                : entry.Details;
        }

        return new AuditLogDisplayItem
        {
            Id = entry.Id ?? 0,
            TimestampText = entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            UserDisplay = userDisplay,
            OperationType = entry.OperationType,
            Target = entry.Target,
            Result = entry.Result,
            DetailsSummary = detailsSummary,
            DetailsFull = detailsFull
        };
    }
}
