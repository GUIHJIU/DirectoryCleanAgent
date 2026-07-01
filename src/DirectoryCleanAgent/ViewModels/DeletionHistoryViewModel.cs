using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Models;
using DirectoryCleanAgent.Services;
using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.ViewModels;

/// <summary>
/// 删除历史与回滚 ViewModel。
///
/// 职责：
/// 1. 从 IDeletionRecordRepository 加载删除批次列表
/// 2. 选中批次后展示该批次的文件详情与 SHA-256 校验结果
/// 3. 管理回滚流程：确认 → 执行（带取消和进度反馈）→ 结果展示
///
/// 设计约束：
/// - 非管理员模式下回滚按钮禁用（回滚需写权限）
/// - 回滚前必须执行哈希校验预检
/// - 回滚执行期间锁定 UI（不可切换批次或重复触发）
/// </summary>
public class DeletionHistoryViewModel : ViewModelBase
{
    private readonly ILogger<DeletionHistoryViewModel> _logger;
    private readonly IDeletionRecordRepository _deletionRecordRepo;
    private readonly IBackupManager _backupManager;
    private readonly IAppStateService _appStateService;

    private CancellationTokenSource? _rollbackCts;

    // ============================================================
    // 批次列表属性
    // ============================================================

    /// <summary>删除操作批次列表（按时间降序）</summary>
    public ObservableCollection<DeletionBatchDisplayItem> Batches { get; } = new();

    private bool _isLoadingBatches;
    private bool _isBatchesEmpty;
    private string _batchesEmptyMessage = "暂无删除历史记录";

    /// <summary>是否正在加载批次列表</summary>
    public bool IsLoadingBatches
    {
        get => _isLoadingBatches;
        set => SetProperty(ref _isLoadingBatches, value);
    }

    /// <summary>批次列表是否为空</summary>
    public bool IsBatchesEmpty
    {
        get => _isBatchesEmpty;
        set => SetProperty(ref _isBatchesEmpty, value);
    }

    /// <summary>批次列表空提示文本</summary>
    public string BatchesEmptyMessage
    {
        get => _batchesEmptyMessage;
        set => SetProperty(ref _batchesEmptyMessage, value);
    }

    // ============================================================
    // 选中批次与详情属性
    // ============================================================

    private DeletionBatchDisplayItem? _selectedBatch;

    /// <summary>当前选中的删除批次</summary>
    public DeletionBatchDisplayItem? SelectedBatch
    {
        get => _selectedBatch;
        set
        {
            if (SetProperty(ref _selectedBatch, value) && value != null)
            {
                _ = LoadBatchDetailAsync(value.OperationId);
                OnPropertyChanged(nameof(HasSelectedBatch));
                OnPropertyChanged(nameof(CanRollback));
            }
        }
    }

    /// <summary>是否已选中批次</summary>
    public bool HasSelectedBatch => SelectedBatch != null;

    /// <summary>选中批次的文件列表</summary>
    public ObservableCollection<DeletionRecord> BatchFiles { get; } = new();

    /// <summary>批次文件显示数量文本</summary>
    public string BatchFileCountText =>
        BatchFiles.Count > 0 ? $"共 {BatchFiles.Count:N0} 个文件" : string.Empty;

    /// <summary>批次释放空间文本</summary>
    public string BatchTotalSizeText
    {
        get
        {
            if (BatchFiles.Count == 0) return string.Empty;
            long totalBytes = BatchFiles.Sum(f => f.FileSize);
            return FormatBytes(totalBytes);
        }
    }

    private bool _isLoadingDetail;
    private string _detailStatusText = "请从左侧列表选择一个删除批次";

    /// <summary>是否正在加载批次详情</summary>
    public bool IsLoadingDetail
    {
        get => _isLoadingDetail;
        set => SetProperty(ref _isLoadingDetail, value);
    }

    /// <summary>详情面板状态文本（加载中 / 校验结果 / 错误信息）</summary>
    public string DetailStatusText
    {
        get => _detailStatusText;
        set => SetProperty(ref _detailStatusText, value);
    }

    // ============================================================
    // 哈希校验结果
    // ============================================================

    private HashVerificationResult? _hashCheckResult;

    /// <summary>当前批次的哈希校验结果</summary>
    public HashVerificationResult? HashCheckResult
    {
        get => _hashCheckResult;
        set
        {
            if (SetProperty(ref _hashCheckResult, value))
            {
                OnPropertyChanged(nameof(HashCheckSummaryText));
                OnPropertyChanged(nameof(CanRollback));
            }
        }
    }

    /// <summary>哈希校验摘要文本</summary>
    public string HashCheckSummaryText
    {
        get
        {
            if (HashCheckResult == null) return string.Empty;
            var parts = new List<string>();
            if (HashCheckResult.PassedCount > 0) parts.Add($"✅ 可恢复: {HashCheckResult.PassedCount}");
            if (HashCheckResult.FailedCount > 0) parts.Add($"❌ 校验失败: {HashCheckResult.FailedCount}");
            if (HashCheckResult.NotFoundCount > 0) parts.Add($"⚠️ 未找到: {HashCheckResult.NotFoundCount}");
            return string.Join("  ", parts);
        }
    }

    // ============================================================
    // 回滚状态
    // ============================================================

    private bool _isRollingBack;
    private double _rollbackProgress;
    private string _rollbackProgressText = string.Empty;
    private string _rollbackFilePath = string.Empty;

    /// <summary>是否正在执行回滚</summary>
    public bool IsRollingBack
    {
        get => _isRollingBack;
        set
        {
            if (SetProperty(ref _isRollingBack, value))
            {
                OnPropertyChanged(nameof(CanRollback));
                OnPropertyChanged(nameof(CanCancelRollback));
            }
        }
    }

    /// <summary>回滚进度百分比（0~100）</summary>
    public double RollbackProgress
    {
        get => _rollbackProgress;
        set => SetProperty(ref _rollbackProgress, value);
    }

    /// <summary>回滚进度文本</summary>
    public string RollbackProgressText
    {
        get => _rollbackProgressText;
        set => SetProperty(ref _rollbackProgressText, value);
    }

    /// <summary>当前正在恢复的文件路径</summary>
    public string RollbackFilePath
    {
        get => _rollbackFilePath;
        set => SetProperty(ref _rollbackFilePath, value);
    }

    /// <summary>是否可以回滚（管理员 + 已选批次 + 可回滚 + 哈希有通过 + 未在执行）</summary>
    public bool CanRollback =>
        _appStateService.IsAdmin &&
        SelectedBatch != null &&
        SelectedBatch.IsRollbackable &&
        !IsRollingBack &&
        (HashCheckResult == null || HashCheckResult.PassedCount > 0);

    /// <summary>是否可以取消回滚</summary>
    public bool CanCancelRollback => IsRollingBack;

    // ============================================================
    // 命令
    // ============================================================

    public RelayCommand RefreshBatchesCommand { get; }
    public RelayCommand RollbackCommand { get; }
    public RelayCommand CancelRollbackCommand { get; }

    // ============================================================
    // 构造函数
    // ============================================================

    public DeletionHistoryViewModel(
        ILogger<DeletionHistoryViewModel> logger,
        IDeletionRecordRepository deletionRecordRepo,
        IBackupManager backupManager,
        IAppStateService appStateService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _deletionRecordRepo = deletionRecordRepo ?? throw new ArgumentNullException(nameof(deletionRecordRepo));
        _backupManager = backupManager ?? throw new ArgumentNullException(nameof(backupManager));
        _appStateService = appStateService ?? throw new ArgumentNullException(nameof(appStateService));

        // 初始化命令
        RefreshBatchesCommand = new RelayCommand(async () => await LoadBatchesAsync());
        RollbackCommand = new RelayCommand(async () => await ExecuteRollbackAsync(), () => CanRollback);
        CancelRollbackCommand = new RelayCommand(ExecuteCancelRollback, () => CanCancelRollback);

        // 订阅 AppMode 变更以刷新回滚按钮可用性
        _appStateService.AppModeChanged += OnAppModeChanged;

        _logger.LogMethodEntry("DeletionHistoryViewModel 构造完成");
    }

    // ============================================================
    // 数据加载
    // ============================================================

    /// <summary>
    /// 初始加载：加载删除批次列表。
    /// 在 HistoryWindow 打开后由 HistoryWindow.Loaded 事件调用。
    /// </summary>
    public async Task LoadInitialDataAsync()
    {
        _logger.LogMethodEntry("开始加载删除历史批次列表");
        await LoadBatchesAsync();
    }

    /// <summary>
    /// 从数据库加载删除操作批次列表。
    /// 若数据库连接失败或表为空，UI 展示相应提示。
    /// </summary>
    private async Task LoadBatchesAsync()
    {
        _logger.LogMethodEntry("加载删除批次列表");
        IsLoadingBatches = true;

        try
        {
            // 从数据层获取批次摘要（按时间降序，最多 100 条）
            var batches = await _deletionRecordRepo.GetBatchSummariesAsync(limit: 100);

            // 映射为 UI 展示模型
            var displayItems = batches.Select(b => new DeletionBatchDisplayItem
            {
                OperationId = b.OperationId,
                ExecutedAtText = b.ExecutedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                FileCountText = $"{b.FileCount:N0} 个文件",
                TotalSizeText = FormatBytes(b.TotalSizeBytes),
                MethodText = b.Method switch
                {
                    DeleteMethod.RecycleBin => "回收站",
                    DeleteMethod.Permanent => "永久删除",
                    _ => b.Method.ToString()
                },
                IsRollbackable = b.IsRollbackable
            }).ToList();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Batches.Clear();
                foreach (var item in displayItems)
                {
                    Batches.Add(item);
                }
                IsBatchesEmpty = Batches.Count == 0;
                if (IsBatchesEmpty)
                {
                    BatchesEmptyMessage = "暂无删除历史记录。执行清理操作后，记录将在此显示。";
                }
            });

            _logger.LogInformation("删除批次列表加载完成: {Count} 个批次", displayItems.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载删除批次列表失败");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                BatchesEmptyMessage = $"加载失败: {ex.Message}";
                IsBatchesEmpty = true;
                Batches.Clear();
            });
        }
        finally
        {
            IsLoadingBatches = false;
        }
    }

    /// <summary>
    /// 加载选中批次的文件详情，并执行 SHA-256 哈希校验预检。
    /// </summary>
    private async Task LoadBatchDetailAsync(string operationId)
    {
        _logger.LogMethodEntry("加载批次详情: OperationId={OpId}", operationId);
        IsLoadingDetail = true;
        DetailStatusText = "正在加载文件列表和校验哈希...";
        HashCheckResult = null;

        try
        {
            // 并行加载文件列表和哈希校验结果
            var filesTask = _deletionRecordRepo.GetByOperationIdAsync(operationId);
            var hashTask = _backupManager.VerifyHashesAsync(operationId, CancellationToken.None);

            await Task.WhenAll(filesTask, hashTask);

            var files = filesTask.Result;
            var hashResult = hashTask.Result;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // 更新文件列表
                BatchFiles.Clear();
                foreach (var file in files)
                {
                    BatchFiles.Add(file);
                }
                OnPropertyChanged(nameof(BatchFileCountText));
                OnPropertyChanged(nameof(BatchTotalSizeText));

                // 更新哈希校验结果显示
                HashCheckResult = hashResult;
                DetailStatusText = hashResult.FailedCount == 0 && hashResult.NotFoundCount == 0
                    ? $"✅ 全部 {hashResult.PassedCount} 个文件校验通过，可以安全回滚"
                    : $"校验完成: 通过 {hashResult.PassedCount}, 失败 {hashResult.FailedCount}, 未找到 {hashResult.NotFoundCount}";
            });

            _logger.LogInformation("批次详情加载完成: 文件={FileCount}, 通过={Passed}, 失败={Failed}, 未找到={NotFound}",
                files.Count, hashResult.PassedCount, hashResult.FailedCount, hashResult.NotFoundCount);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("批次详情加载被取消: OperationId={OpId}", operationId);
            DetailStatusText = "加载已取消";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批次详情加载失败: OperationId={OpId}", operationId);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                DetailStatusText = $"加载失败: {ex.Message}";
            });
        }
        finally
        {
            IsLoadingDetail = false;
        }
    }

    // ============================================================
    // 回滚流程
    // ============================================================

    /// <summary>
    /// 回滚执行主流程。
    /// 1. 弹出确认对话框（文件数、大小、校验状态）
    /// 2. 创建取消令牌源，锁定 UI
    /// 3. 通过 IBackupManager.RollbackAsync 执行恢复
    /// 4. 展示结果并刷新批次列表
    /// </summary>
    private async Task ExecuteRollbackAsync()
    {
        if (SelectedBatch == null) return;

        var operationId = SelectedBatch.OperationId;
        _logger.LogMethodEntry("用户触发回滚操作: OperationId={OpId}", operationId);

        try
        {
            // Step 1: 确认对话框（显示待恢复文件数、大小、校验失败数）
            var hashWarn = HashCheckResult is { FailedCount: > 0 }
                ? $"\n\n⚠️ 警告：{HashCheckResult.FailedCount} 个文件哈希校验失败，将被跳过。"
                : string.Empty;

            if (HashCheckResult is { NotFoundCount: > 0 })
            {
                hashWarn += $"\n⚠️ {HashCheckResult.NotFoundCount} 个文件未找到（可能已被永久删除），将被跳过。";
            }

            if (HashCheckResult is { PassedCount: 0 })
            {
                MessageBox.Show(
                    "当前批次没有可恢复的文件。\n\n所有文件要么哈希校验失败，要么原始文件已不存在。",
                    "无法回滚",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var confirmResult = MessageBox.Show(
                $"即将回滚以下删除批次:\n\n" +
                $"操作时间: {SelectedBatch.ExecutedAtText}\n" +
                $"文件数量: {SelectedBatch.FileCountText}\n" +
                $"释放空间: {SelectedBatch.TotalSizeText}\n" +
                $"删除方式: {SelectedBatch.MethodText}" +
                hashWarn +
                $"\n\n是否确认执行回滚？",
                "回滚确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmResult != MessageBoxResult.Yes)
            {
                _logger.LogInformation("用户取消了回滚操作: OperationId={OpId}", operationId);
                return;
            }

            // Step 2: 创建取消令牌并锁定 UI
            _rollbackCts = new CancellationTokenSource();
            var ct = _rollbackCts.Token;
            IsRollingBack = true;
            RollbackProgress = 0;
            RollbackProgressText = "正在准备回滚...";
            RollbackFilePath = string.Empty;

            // Step 3: 构建进度报告器（在 UI 线程更新属性）
            var progress = new Progress<OperationProgress>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    RollbackProgress = p.Percentage > 0 ? p.Percentage : 0;
                    RollbackProgressText = $"{p.CompletedCount} / {p.TotalCount}";
                    if (p.CurrentFilePath != null)
                    {
                        RollbackFilePath = p.CurrentFilePath;
                    }

                    // 根据阶段更新文本
                    if (p.Phase == OperationPhase.Restoring && p.CurrentFilePath != null)
                    {
                        RollbackProgressText = $"正在恢复: {p.CompletedCount}/{p.TotalCount}";
                    }
                    else if (p.Phase == OperationPhase.Verifying)
                    {
                        RollbackProgressText = $"正在校验: {p.CompletedCount}/{p.TotalCount}";
                    }
                    else if (p.Phase == OperationPhase.Completed)
                    {
                        RollbackProgressText = "回滚完成";
                    }
                });
            });

            // Step 4: 执行回滚（异步，可取消）
            _logger.LogInformation("开始执行回滚: OperationId={OpId}", operationId);
            var result = await _backupManager.RollbackAsync(operationId, progress, ct);

            // Step 5: 展示结果
            if (result.IsCancelled)
            {
                _logger.LogWarning("回滚被取消: OperationId={OpId}, 已恢复={Count}", operationId, result.SuccessCount);
                RollbackProgressText = $"回滚已取消（已恢复 {result.SuccessCount} 个文件）";
                MessageBox.Show(
                    $"回滚已取消。\n已恢复: {result.SuccessCount} 个文件\n失败: {result.FailedCount}\n跳过: {result.ManualReviewCount}",
                    "回滚取消",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                _logger.LogInformation("回滚完成: OperationId={OpId}, 成功={Success}, 失败={Fail}, 跳过={Skip}",
                    operationId, result.SuccessCount, result.FailedCount, result.ManualReviewCount);

                RollbackProgress = 100;
                RollbackProgressText = "回滚完成";
                RollbackFilePath = string.Empty;

                var icon = result.IsFullySuccessful ? MessageBoxImage.Information : MessageBoxImage.Warning;
                var title = result.IsFullySuccessful ? "回滚完成" : "回滚完成（部分失败）";

                MessageBox.Show(
                    $"回滚操作完成:\n\n" +
                    $"✅ 成功恢复: {result.SuccessCount} 个文件\n" +
                    $"❌ 失败: {result.FailedCount} 个文件\n" +
                    $"⚠️ 需人工处理: {result.ManualReviewCount} 个文件\n" +
                    $"释放空间（回退）: {FormatBytes(result.FreedBytes)}\n" +
                    $"耗时: {result.Elapsed:hh\\:mm\\:ss}",
                    title,
                    MessageBoxButton.OK,
                    icon);
            }

            // Step 6: 刷新批次列表和详情
            SelectedBatch = null;
            BatchFiles.Clear();
            HashCheckResult = null;
            DetailStatusText = "请从左侧列表选择一个删除批次";
            await LoadBatchesAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("回滚操作被用户取消: OperationId={OpId}", operationId);
            RollbackProgressText = "回滚已取消";
            MessageBox.Show("回滚操作已取消。已恢复的文件将保留。", "回滚取消", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "回滚操作执行失败: OperationId={OpId}", operationId);
            RollbackProgressText = "回滚失败";
            MessageBox.Show(
                $"回滚操作失败:\n\n{ex.Message}\n\n请检查日志获取详细错误信息。",
                "回滚失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsRollingBack = false;
            _rollbackCts?.Dispose();
            _rollbackCts = null;
            OnPropertyChanged(nameof(CanRollback));
            OnPropertyChanged(nameof(CanCancelRollback));
        }
    }

    /// <summary>取消当前正在执行的回滚操作</summary>
    private void ExecuteCancelRollback()
    {
        _logger.LogMethodEntry("用户触发取消回滚");
        try
        {
            _rollbackCts?.Cancel();
            RollbackProgressText = "正在取消...";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取消回滚操作失败");
        }
    }

    // ============================================================
    // 事件响应
    // ============================================================

    /// <summary>
    /// 响应 AppMode 变更（管理员/只读模式切换）。
    /// 非管理员时回滚按钮自动禁用。
    /// </summary>
    private void OnAppModeChanged(object? sender, AppMode newMode)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(CanRollback));
        });
    }

    // ============================================================
    // 工具方法
    // ============================================================

    /// <summary>格式化字节数为可读字符串</summary>
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unitIndex = 0;
        double size = bytes;

        while (size >= 1024.0 && unitIndex < units.Length - 1)
        {
            size /= 1024.0;
            unitIndex++;
        }

        return size < 10
            ? $"{size:F1} {units[unitIndex]}"
            : $"{size:F0} {units[unitIndex]}";
    }
}
