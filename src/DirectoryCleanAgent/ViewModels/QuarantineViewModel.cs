using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Core.Localization;
using DirectoryCleanAgent.Core.Formatting;
using DirectoryCleanAgent.Models;
using DirectoryCleanAgent.Services;
using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.ViewModels;

/// <summary>
/// 隔离区管理界面 ViewModel — C7 阶段核心组件。
///
/// 职责：
/// - 加载并展示隔离区文件列表（通过 IQuarantineManager.ListQuarantineFilesAsync）
/// - 容量统计展示（通过 IQuarantineManager.GetQuarantineStatsAsync）
/// - 批量恢复选中文件（通过 IBackupManager.RestoreQuarantineFileAsync）
/// - 批量永久删除选中文件（通过 IQuarantineManager.DeleteQuarantineFileAsync）
/// - 操作进度报告和取消支持
///
/// 遵循 MVVM 模式，所有 UI 状态通过数据绑定驱动，
/// 所有文件操作通过 async/await 在后台执行。
/// </summary>
public class QuarantineViewModel : ViewModelBase
{
    private readonly ILogger<QuarantineViewModel> _logger;
    private readonly IQuarantineManager _quarantineManager;
    private readonly IBackupManager _backupManager;
    private readonly IConfigService _configService;
    private readonly ILocalizationService _localization;
    private readonly IAppStateService _appStateService; // C9: 管理员权限检测

    private CancellationTokenSource? _cts; // 当前操作的取消令牌源
    private bool _isLoading;
    private bool _isOperating;
    private OperationProgress _operationProgress;
    private string _statusText;
    private string? _errorMessage;
    private QuarantineStats _stats;
    private int _selectedCount; // 待确认-4: O(1) 选中计数，替代 QuarantineFiles.Any(...)

    // ============================================================
    // 构造函数
    // ============================================================

    public QuarantineViewModel(
        ILogger<QuarantineViewModel> logger,
        IQuarantineManager quarantineManager,
        IBackupManager backupManager,
        IConfigService configService,
        ILocalizationService localization,
        IAppStateService appStateService) // C9: 管理员权限检测依赖
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _quarantineManager = quarantineManager ?? throw new ArgumentNullException(nameof(quarantineManager));
        _backupManager = backupManager ?? throw new ArgumentNullException(nameof(backupManager));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _appStateService = appStateService ?? throw new ArgumentNullException(nameof(appStateService));

        // 从本地化服务初始化状态文本
        _statusText = _localization.GetString("Status.Ready");

        // 初始化集合
        QuarantineFiles = new ObservableCollection<QuarantineFileItem>();
        QuarantineFiles.CollectionChanged += OnQuarantineFilesChanged;

        // 初始化命令
        RefreshCommand = new RelayCommand(async () => await LoadDataAsync(), () => !IsLoading && !IsOperating);
        RestoreSelectedCommand = new RelayCommand(async () => await BatchRestoreAsync(), () => CanRestore);
        DeleteSelectedCommand = new RelayCommand(async () => await BatchDeleteAsync(), () => CanDelete);
        CancelCommand = new RelayCommand(() => ExecuteCancel(), () => IsOperating);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));
        SelectAllCommand = new RelayCommand<bool?>(ExecuteSelectAll, _ => !IsOperating);

        // C9: 订阅 AppMode 变更以刷新操作按钮可用性
        _appStateService.AppModeChanged += OnAppModeChanged;

        _logger.LogMethodEntry("QuarantineViewModel 构造完成");
    }

    // ============================================================
    // 属性（UI 绑定源）
    // ============================================================

    /// <summary>隔离区文件列表（绑定到 DataGrid ItemsSource）</summary>
    public ObservableCollection<QuarantineFileItem> QuarantineFiles { get; }

    /// <summary>隔离区容量统计</summary>
    public QuarantineStats Stats
    {
        get => _stats;
        private set
        {
            if (SetProperty(ref _stats, value))
            {
                // 统计信息变更时刷新所有相关展示属性
                OnPropertyChanged(nameof(CapacityText));
                OnPropertyChanged(nameof(UtilizationText));
                OnPropertyChanged(nameof(ExpiredFileCount));
                OnPropertyChanged(nameof(ExpiredHintText));
                OnPropertyChanged(nameof(IsQuarantineDisabled));
            }
        }
    }

    /// <summary>容量展示文本：已用空间 / 最大空间（本地化）</summary>
    public string CapacityText => Stats.IsDisabled
        ? _localization.GetString("Quarantine.Capacity.Disabled")
        : $"{ByteFormatter.Format(Stats.TotalSizeBytes)} / {ByteFormatter.Format(Stats.QuarantineMaxSizeBytes)}";

    /// <summary>使用率展示文本</summary>
    public string UtilizationText => Stats.IsDisabled
        ? ""
        : $"{Stats.UtilizationPercentage:F1}%";

    /// <summary>过期文件数量</summary>
    public int ExpiredFileCount => Stats.ExpiredFileCount;

    /// <summary>过期提示文本（本地化，如 "3 个文件已过期"）</summary>
    public string ExpiredHintText =>
        string.Format(_localization.GetString("Quarantine.Capacity.ExpiredHint"), ExpiredFileCount);

    /// <summary>隔离区是否已禁用</summary>
    public bool IsQuarantineDisabled => Stats.IsDisabled;

    /// <summary>窗口标题（本地化）</summary>
    public string WindowTitle => _localization.GetString("Quarantine.Title");

    /// <summary>恢复选中按钮文本（本地化）</summary>
    public string RestoreSelectedText => _localization.GetString("Quarantine.RestoreSelected");

    /// <summary>永久删除按钮文本（本地化）</summary>
    public string DeleteSelectedText => _localization.GetString("Quarantine.DeleteSelected");

    /// <summary>全选文本（本地化）</summary>
    public string SelectAllText => _localization.GetString("Quarantine.SelectAll");

    /// <summary>容量标题（本地化）</summary>
    public string CapacityTitleText => _localization.GetString("Quarantine.Capacity.Title");

    /// <summary>容量使用率（用于 ProgressBar 绑定，0~100）</summary>
    public double UtilizationPercentage => Stats.UtilizationPercentage;

    /// <summary>是否正在加载数据</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
                RefreshCommandStates();
        }
    }

    /// <summary>是否正在执行批量操作</summary>
    public bool IsOperating
    {
        get => _isOperating;
        set
        {
            if (SetProperty(ref _isOperating, value))
                RefreshCommandStates();
        }
    }

    /// <summary>当前操作进度（绑定到 ProgressBar 和状态文本）</summary>
    public OperationProgress OperationProgress
    {
        get => _operationProgress;
        set => SetProperty(ref _operationProgress, value);
    }

    /// <summary>状态栏文本</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>错误信息（用于内联展示）</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    /// <summary>是否有错误信息需要展示</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>是否有文件被选中（O(1) 计数器，避免大列表 O(n) 遍历）</summary>
    public bool HasSelection => _selectedCount > 0;

    /// <summary>选中的文件列表（用于批量操作获取选中项）</summary>
    private List<QuarantineFileItem> GetSelectedFiles() =>
        QuarantineFiles.Where(f => f.IsSelected).ToList();

    /// <summary>选中文件数量文本（本地化）</summary>
    public string SelectionText
    {
        get
        {
            if (_selectedCount == 0)
                return _localization.GetString("Quarantine.Selection.None");
            var selected = GetSelectedFiles();
            var totalSize = ByteFormatter.Format(selected.Sum(f => f.FileSizeBytes));
            return string.Format(_localization.GetString("Quarantine.Selection.Count"),
                _selectedCount, totalSize);
        }
    }

    /// <summary>C9: 当前是否具有管理员权限（绑定到操作按钮的 IsEnabled）</summary>
    public bool IsAdmin => _appStateService.IsAdmin;

    /// <summary>C9: 是否应显示只读模式警告横幅</summary>
    public bool ShowReadOnlyWarning => !_appStateService.IsAdmin;

    /// <summary>C9: 只读模式警告文本</summary>
    public string ReadOnlyWarningMessage =>
        "⚠️ 只读模式 — 请以管理员身份重新运行以启用清理功能（非管理员模式下数据分析功能仍可正常使用）";

    /// <summary>C9: 是否可以恢复选中文件（管理员 + 有选中项 + 不在操作中）</summary>
    public bool CanRestore =>
        _appStateService.IsAdmin && HasSelection && !IsOperating;

    /// <summary>C9: 是否可以永久删除选中文件（管理员 + 有选中项 + 不在操作中）</summary>
    public bool CanDelete =>
        _appStateService.IsAdmin && HasSelection && !IsOperating;

    // ============================================================
    // 命令
    // ============================================================

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RestoreSelectedCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand<bool?> SelectAllCommand { get; }

    /// <summary>请求关闭窗口事件（由 QuarantineWindow 订阅）</summary>
    public event EventHandler? RequestClose;

    // ============================================================
    // 核心方法：加载数据
    // ============================================================

    /// <summary>
    /// 加载隔离区文件列表和容量统计。
    /// 窗口首次打开时自动调用，用户也可通过刷新按钮手动触发。
    /// 加载期间禁用所有操作按钮。
    /// </summary>
    public async Task LoadDataAsync()
    {
        _logger.LogMethodEntry();
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            // 并行加载统计和文件列表以提升响应速度
            var statsTask = _quarantineManager.GetQuarantineStatsAsync();
            var filesTask = _quarantineManager.ListQuarantineFilesAsync();

            await Task.WhenAll(statsTask, filesTask).ConfigureAwait(true);

            // 更新统计信息
            Stats = statsTask.Result;

            // 更新文件列表（UI 线程安全）
            QuarantineFiles.Clear();
            if (Stats.IsDisabled)
            {
                ErrorMessage = _localization.GetString("Quarantine.Error.DisabledMessage");
                StatusText = _localization.GetString("Quarantine.Status.Disabled");
            }
            else
            {
                foreach (var entry in filesTask.Result)
                {
                    QuarantineFiles.Add(new QuarantineFileItem(entry)
                    {
                        // 通过本地化服务设置过期状态显示文本
                        ExpiredStatusText = entry.IsExpired
                            ? _localization.GetString("Quarantine.Item.Expired")
                            : _localization.GetString("Quarantine.Item.Normal")
                    });
                }
                StatusText = string.Format(
                    _localization.GetString("Quarantine.Status.Loaded"), QuarantineFiles.Count);
            }

            UpdateSelectionText();

            _logger.LogInformation("隔离区数据加载完成: 文件数={FileCount}, 统计={Stats}",
                QuarantineFiles.Count, Stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载隔离区数据失败");
            ErrorMessage = string.Format(_localization.GetString("Quarantine.Error.LoadFailed"), ex.Message);
            StatusText = _localization.GetString("Quarantine.Status.LoadFailed");
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ============================================================
    // 核心方法：批量恢复
    // ============================================================

    /// <summary>
    /// 批量恢复选中的隔离区文件到原始路径。
    ///
    /// 流程：
    /// 1. 创建 CancellationTokenSource 绑定取消按钮
    /// 2. 创建 Progress 适配器将进度映射到 UI
    /// 3. 逐个调用 IBackupManager.RestoreQuarantineFileAsync
    /// 4. 单个文件失败不中断批次（部分成功语义）
    /// 5. 完成后弹窗汇总结果并刷新列表
    /// </summary>
    private async Task BatchRestoreAsync()
    {
        _logger.LogMethodEntry("用户触发批量恢复");
        var selectedFiles = GetSelectedFiles();
        if (selectedFiles.Count == 0) return;

        // C9: 非管理员运行时守卫（与 MainViewModel.ExecuteQuickClean 模式一致）
        if (!_appStateService.IsAdmin)
        {
            _logger.LogWarning("非管理员用户尝试恢复隔离区文件，操作已阻止");
            MessageBox.Show(
                "只读模式下无法恢复文件。请以管理员身份重新运行以执行此操作。",
                "权限不足",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 创建取消令牌源和进度报告器
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsOperating = true;
        ErrorMessage = null;

        var successCount = 0;
        var failCount = 0;
        var restoredBytes = 0L;
        var failedItems = new List<string>();
        var progress = new Progress<OperationProgress>(p =>
        {
            Application.Current.Dispatcher.Invoke(() => OperationProgress = p);
        });

        try
        {
            for (var i = 0; i < selectedFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = selectedFiles[i];
                StatusText = string.Format(
                    _localization.GetString("Quarantine.Progress.RestoringFile"),
                    item.OriginalFileName, i + 1, selectedFiles.Count);

                try
                {
                    var result = await _backupManager.RestoreQuarantineFileAsync(
                        item.FullPath, progress, ct).ConfigureAwait(true);

                    if (result.Success)
                    {
                        successCount++;
                        restoredBytes += result.FileSizeBytes;
                        _logger.LogInformation("[隔离区恢复] 成功: {File} → {Dest}",
                            item.OriginalFileName, result.RestoredPath);
                    }
                    else
                    {
                        failCount++;
                        failedItems.Add($"{item.OriginalFileName}: {result.ErrorMessage}");
                        _logger.LogWarning("[隔离区恢复] 失败: {File}, 原因: {Reason}",
                            item.OriginalFileName, result.ErrorMessage);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单个文件恢复异常不中断批次
                    failCount++;
                    failedItems.Add($"{item.OriginalFileName}: 异常 — {ex.Message}");
                    _logger.LogError(ex, "[隔离区恢复] 异常: {File}", item.OriginalFileName);
                }
            }

            // 显示操作结果
            var message = string.Format(
                _localization.GetString("Quarantine.Result.RestoreComplete"),
                successCount, failCount, ByteFormatter.Format(restoredBytes));
            if (failedItems.Count > 0)
            {
                message += "\n\n" + _localization.GetString("Quarantine.Result.FailureDetails")
                    + "\n" + string.Join("\n", failedItems.Take(10));
                if (failedItems.Count > 10)
                    message += "\n" + string.Format(
                        _localization.GetString("Quarantine.Result.MoreItems"), failedItems.Count - 10);
            }

            MessageBox.Show(message,
                _localization.GetString("Quarantine.BatchRestoreResult"),
                MessageBoxButton.OK,
                failCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            // 恢复完成后刷新列表（已恢复的文件会从列表中消失）
            await LoadDataAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("批量恢复被用户取消: 已完成 {SuccessCount} 项", successCount);
            StatusText = string.Format(
                _localization.GetString("Quarantine.Result.RestoreCancelled"), successCount);
            MessageBox.Show(
                string.Format(_localization.GetString("Quarantine.Result.RestoreCancelled"), successCount),
                _localization.GetString("Quarantine.BatchRestoreResult"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量恢复执行失败");
            ErrorMessage = string.Format(
                _localization.GetString("Quarantine.Error.LoadFailed"), ex.Message);
            MessageBox.Show(
                string.Format(_localization.GetString("Quarantine.Error.LoadFailed"), ex.Message),
                _localization.GetString("Error.OperationFailed"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsOperating = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ============================================================
    // 核心方法：批量删除
    // ============================================================

    /// <summary>
    /// 批量永久删除选中的隔离区文件。
    ///
    /// 与批量恢复相同的流程：
    /// 创建 CTS → 确认对话框 → 逐个删除 → 汇总结果 → 刷新列表。
    /// 删除操作不可逆（永久删除），执行前必须弹出确认对话框。
    /// </summary>
    private async Task BatchDeleteAsync()
    {
        _logger.LogMethodEntry("用户触发批量永久删除");
        var selectedFiles = GetSelectedFiles();
        if (selectedFiles.Count == 0) return;

        // C9: 非管理员运行时守卫（与 MainViewModel.ExecuteQuickClean 模式一致）
        if (!_appStateService.IsAdmin)
        {
            _logger.LogWarning("非管理员用户尝试永久删除隔离区文件，操作已阻止");
            MessageBox.Show(
                "只读模式下无法删除文件。请以管理员身份重新运行以执行此操作。",
                "权限不足",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 确认对话框：永久删除操作不可逆，必须用户明确确认
        var totalSize = selectedFiles.Sum(f => f.FileSizeBytes);
        var confirmResult = MessageBox.Show(
            string.Format(_localization.GetString("Quarantine.ConfirmDelete.Message"),
                selectedFiles.Count, ByteFormatter.Format(totalSize)),
            _localization.GetString("Quarantine.ConfirmDelete.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (confirmResult != MessageBoxResult.Yes)
        {
            _logger.LogInformation("用户取消了永久删除操作");
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsOperating = true;
        ErrorMessage = null;

        var successCount = 0;
        var failCount = 0;
        var freedBytes = 0L;
        var failedItems = new List<string>();
        var progress = new Progress<OperationProgress>(p =>
        {
            Application.Current.Dispatcher.Invoke(() => OperationProgress = p);
        });

        try
        {
            for (var i = 0; i < selectedFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = selectedFiles[i];
                StatusText = string.Format(
                    _localization.GetString("Quarantine.Progress.DeletingFile"),
                    item.OriginalFileName, i + 1, selectedFiles.Count);

                try
                {
                    var success = await _quarantineManager.DeleteQuarantineFileAsync(
                        item.FullPath, ct).ConfigureAwait(true);

                    if (success)
                    {
                        successCount++;
                        freedBytes += item.FileSizeBytes;
                    }
                    else
                    {
                        failCount++;
                        failedItems.Add($"{item.OriginalFileName}: 删除失败（权限不足或文件被锁定）");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 单个文件删除异常不中断批次
                    failCount++;
                    failedItems.Add($"{item.OriginalFileName}: 异常 — {ex.Message}");
                    _logger.LogError(ex, "[隔离区删除] 异常: {File}", item.OriginalFileName);
                }

                // 进度报告
                ((IProgress<OperationProgress>)progress).Report(new OperationProgress
                {
                    Phase = OperationPhase.Deleting,
                    CurrentFilePath = item.FullPath,
                    CompletedCount = i + 1,
                    TotalCount = selectedFiles.Count,
                    ProcessedBytes = freedBytes
                });
            }

            // 显示操作结果
            var message = string.Format(
                _localization.GetString("Quarantine.Result.DeleteComplete"),
                successCount, failCount, ByteFormatter.Format(freedBytes));
            if (failedItems.Count > 0)
            {
                message += "\n\n" + _localization.GetString("Quarantine.Result.FailureDetails")
                    + "\n" + string.Join("\n", failedItems.Take(10));
                if (failedItems.Count > 10)
                    message += "\n" + string.Format(
                        _localization.GetString("Quarantine.Result.MoreItems"), failedItems.Count - 10);
            }

            MessageBox.Show(message,
                _localization.GetString("Quarantine.BatchDeleteResult"),
                MessageBoxButton.OK,
                failCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            // 删除完成后刷新列表
            await LoadDataAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("批量删除被用户取消: 已完成 {SuccessCount} 项", successCount);
            StatusText = string.Format(
                _localization.GetString("Quarantine.Result.DeleteCancelled"), successCount);
            MessageBox.Show(
                string.Format(_localization.GetString("Quarantine.Result.DeleteCancelled"), successCount),
                _localization.GetString("Quarantine.BatchDeleteResult"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量删除执行失败");
            ErrorMessage = string.Format(
                _localization.GetString("Quarantine.Error.LoadFailed"), ex.Message);
            MessageBox.Show(
                string.Format(_localization.GetString("Quarantine.Error.LoadFailed"), ex.Message),
                _localization.GetString("Error.OperationFailed"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsOperating = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ============================================================
    // 命令执行逻辑
    // ============================================================

    /// <summary>取消当前正在执行的操作</summary>
    private void ExecuteCancel()
    {
        _logger.LogMethodEntry("用户触发取消操作");
        try
        {
            _cts?.Cancel();
            StatusText = _localization.GetString("Quarantine.Status.Cancelling");
            _logger.LogWarning("已发送取消请求到隔离区操作");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取消操作执行失败");
        }
    }

    /// <summary>全选/取消全选所有文件</summary>
    private void ExecuteSelectAll(bool? selectAll)
    {
        try
        {
            var isChecked = selectAll.GetValueOrDefault();
            foreach (var file in QuarantineFiles)
            {
                file.IsSelected = isChecked;
            }
            // 全选/取消全选时直接更新计数器（避免每个 Item.PropertyChanged 触发 N 次）
            _selectedCount = isChecked ? QuarantineFiles.Count : 0;
            UpdateSelectionText();
            _logger.LogDebug("全选切换: IsChecked={Checked}, Count={Count}", isChecked, _selectedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全选操作失败");
        }
    }

    // ============================================================
    // 私有辅助方法
    // ============================================================

    /// <summary>刷新所有命令的 CanExecute 状态</summary>
    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsAdmin));
        OnPropertyChanged(nameof(ShowReadOnlyWarning));
        OnPropertyChanged(nameof(ReadOnlyWarningMessage));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanDelete));
        UpdateSelectionText();
        // RelayCommand 通过 CommandManager.RequerySuggested 自动刷新，
        // 但手动触发确保 UI 即时响应
        RefreshCommand.RaiseCanExecuteChanged();
        RestoreSelectedCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
    }

    /// <summary>更新选中状态文本</summary>
    private void UpdateSelectionText()
    {
        OnPropertyChanged(nameof(SelectionText));
    }

    /// <summary>监听文件列表变更以更新选中计数和命令状态</summary>
    private void OnQuarantineFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 新添加的项订阅其 PropertyChanged 事件以跟踪 IsSelected 变化
        if (e.NewItems != null)
        {
            foreach (QuarantineFileItem item in e.NewItems)
            {
                item.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(QuarantineFileItem.IsSelected))
                    {
                        // 维护 O(1) 选中计数器
                        _selectedCount += item.IsSelected ? 1 : -1;
                        RefreshCommandStates();
                    }
                };
            }
        }
        // 列表被清空时重置计数器（Clear → Reset 操作）
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _selectedCount = 0;
        }
        RefreshCommandStates();
    }

    /// <summary>
    /// C9: 响应 AppMode 变更（管理员/只读模式切换）。
    /// 非管理员时操作按钮自动禁用并显示只读横幅。
    /// </summary>
    private void OnAppModeChanged(object? sender, AppMode newMode)
    {
        _logger.LogInformation("隔离区 ViewModel 收到模式变更: {Mode}", newMode);
        // Application.Current?.Dispatcher 为 null 时（单元测试环境），直接在当前线程执行
        Application.Current?.Dispatcher.Invoke(() => RefreshCommandStates());
        if (Application.Current?.Dispatcher == null)
            RefreshCommandStates();
    }

}
