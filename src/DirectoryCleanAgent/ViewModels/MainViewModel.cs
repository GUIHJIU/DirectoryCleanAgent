using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Localization;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Core.Formatting;
using DirectoryCleanAgent.Models;
using DirectoryCleanAgent.Services;
using DirectoryCleanAgent.ViewModels.Base;
using DirectoryCleanAgent.Views;

namespace DirectoryCleanAgent.ViewModels;

/// <summary>
/// 主窗口 ViewModel — 所有 UI 状态和命令的中心枢纽。
/// 管理仪表板数据、操作栏状态、进度显示、主题切换和应用全局状态。
/// </summary>
public class MainViewModel : ViewModelBase
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly IDashboardDataProvider _dataProvider;
    private readonly IAppStateService _appStateService;
    private readonly ILocalizationService _localization;
    private readonly ISimulationService _simulationService;     // C5: 模拟运行服务
    private readonly IReportExporter _reportExporter;           // C5: 报告导出器
    private readonly IConfigService _configService;             // C5: 读取扫描配置
    private readonly IQuarantineManager _quarantineManager;     // C7: 隔离区统计刷新
    private readonly IOperationExecutor _operationExecutor;     // C8: 操作执行器（B4）
    private readonly IDecisionEngine _decisionEngine;           // C8: 决策引擎（B3）
    private readonly IFileListProvider _fileListProvider;       // C8: 文件列表提供器（B1）
    private readonly IRuleEngine _ruleEngine;                   // C8: 规则引擎（B2）

    private CancellationTokenSource? _currentCts; // 当前操作的取消令牌源
    private SimulationResult? _cachedSimulationResult; // C5: 最近一次模拟运行结果，供导出使用
    private AppState _appState = AppState.Initializing;
    private bool _isAdmin;
    private bool _isIndexReady;
    private bool _isOperating;
    private string? _warningMessage;
    private bool _isDarkTheme;
    private OperationProgress _operationProgress;
    private string _operationStatusText = string.Empty;    // C8: 操作阶段中文描述

    private readonly FileListViewModel _fileListViewModel;

    public MainViewModel(
        ILogger<MainViewModel> logger,
        IDashboardDataProvider dataProvider,
        IAppStateService appStateService,
        ILocalizationService localization,
        ISimulationService simulationService,
        IReportExporter reportExporter,
        IConfigService configService,
        IQuarantineManager quarantineManager,
        IOperationExecutor operationExecutor,
        IDecisionEngine decisionEngine,
        IFileListProvider fileListProvider,
        IRuleEngine ruleEngine,
        FileListViewModel fileListViewModel)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        _appStateService = appStateService ?? throw new ArgumentNullException(nameof(appStateService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _simulationService = simulationService ?? throw new ArgumentNullException(nameof(simulationService));
        _reportExporter = reportExporter ?? throw new ArgumentNullException(nameof(reportExporter));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _quarantineManager = quarantineManager ?? throw new ArgumentNullException(nameof(quarantineManager));
        _operationExecutor = operationExecutor ?? throw new ArgumentNullException(nameof(operationExecutor));
        _decisionEngine = decisionEngine ?? throw new ArgumentNullException(nameof(decisionEngine));
        _fileListProvider = fileListProvider ?? throw new ArgumentNullException(nameof(fileListProvider));
        _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
        _fileListViewModel = fileListViewModel ?? throw new ArgumentNullException(nameof(fileListViewModel));

        // 初始化集合
        DashboardItems = new ObservableCollection<DashboardItem>();
        StatusInfo = new StatusBarInfo();

        // 初始化命令
        QuickCleanCommand = new RelayCommand(ExecuteQuickClean, () => CanQuickClean);
        SmartSelectCommand = new RelayCommand(ExecuteSmartSelect, () => CanSmartSelect);
        SimulateCommand = new RelayCommand(ExecuteSimulate, () => CanSimulate);
        ExportCommand = new RelayCommand(ExecuteExport, () => CanExport);
        RefreshCommand = new RelayCommand(ExecuteRefresh, () => CanRefresh);
        CancelCommand = new RelayCommand(ExecuteCancel, () => CanCancel);
        NavigateToSettingsCommand = new RelayCommand(ExecuteNavigateToSettings);
        NavigateToHistoryCommand = new RelayCommand(ExecuteNavigateToHistory);
        NavigateToQuarantineCommand = new RelayCommand(ExecuteNavigateToQuarantine);
        NavigateToHelpCommand = new RelayCommand(ExecuteNavigateToHelp);
        ToggleThemeCommand = new RelayCommand(ExecuteToggleTheme);
        ToggleShowAllCommand = new RelayCommand(ExecuteToggleShowAll);

        // 订阅全局状态变更
        _appStateService.StateChanged += OnAppStateChanged;
        _appStateService.AppModeChanged += OnAppModeChanged;

        _logger.LogMethodEntry("MainViewModel 构造完成");
    }

    // ============================================================
    // 属性（UI 绑定源）
    // ============================================================

    /// <summary>文件列表与分组视图 ViewModel（C2 模块）</summary>
    public FileListViewModel FileListViewModel => _fileListViewModel;

    /// <summary>仪表板 4 张分类卡片数据</summary>
    public ObservableCollection<DashboardItem> DashboardItems { get; }

    /// <summary>状态栏显示信息</summary>
    public StatusBarInfo StatusInfo { get; }

    /// <summary>全局应用状态</summary>
    public AppState AppState
    {
        get => _appState;
        set
        {
            if (SetProperty(ref _appState, value))
            {
                // 状态变更后刷新所有命令的可用性
                RefreshCommandStates();
            }
        }
    }

    /// <summary>当前是否为管理员权限（驱动按钮和横幅）</summary>
    public bool IsAdmin
    {
        get => _isAdmin;
        set
        {
            if (SetProperty(ref _isAdmin, value))
            {
                _appStateService.IsAdmin = value;
                UpdateWarningBanner();
                RefreshCommandStates();
            }
        }
    }

    /// <summary>Everything 索引是否就绪</summary>
    public bool IsIndexReady
    {
        get => _isIndexReady;
        set
        {
            if (SetProperty(ref _isIndexReady, value))
            {
                _appStateService.IsIndexReady = value;
                UpdateWarningBanner();
                RefreshCommandStates();
            }
        }
    }

    /// <summary>是否正在执行操作（显示进度条+取消按钮）</summary>
    public bool IsOperating
    {
        get => _isOperating;
        private set
        {
            if (SetProperty(ref _isOperating, value))
            {
                RefreshCommandStates();
            }
        }
    }

    /// <summary>警告横幅文本（null 时隐藏横幅）</summary>
    public string? WarningMessage
    {
        get => _warningMessage;
        set
        {
            if (SetProperty(ref _warningMessage, value))
            {
                OnPropertyChanged(nameof(ShowWarningBanner));
            }
        }
    }

    /// <summary>警告横幅是否可见</summary>
    public bool ShowWarningBanner => !string.IsNullOrEmpty(WarningMessage);

    /// <summary>是否使用深色主题</summary>
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => SetProperty(ref _isDarkTheme, value);
    }

    /// <summary>当前操作进度（绑定到 ProgressBar 和状态文本）</summary>
    public OperationProgress OperationProgress
    {
        get => _operationProgress;
        set
        {
            _operationProgress = value;
            OnPropertyChanged();
        }
    }

    /// <summary>当前操作阶段的中文描述文本（C8: 绑定到状态栏）</summary>
    public string OperationStatusText
    {
        get => _operationStatusText;
        set => SetProperty(ref _operationStatusText, value);
    }

    /// <summary>应用模式（Normal / ReadOnly）</summary>
    public AppMode AppMode => _appStateService.AppMode;

    // ============================================================
    // 命令（按钮绑定）
    // ============================================================

    public RelayCommand QuickCleanCommand { get; }
    public RelayCommand SmartSelectCommand { get; }
    public RelayCommand SimulateCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand NavigateToSettingsCommand { get; }
    public RelayCommand NavigateToHelpCommand { get; }
    public RelayCommand NavigateToHistoryCommand { get; }
    public RelayCommand NavigateToQuarantineCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand ToggleShowAllCommand { get; }

    /// <summary>一键清理按钮是否可用</summary>
    public bool CanQuickClean =>
        IsAdmin && IsIndexReady && !IsOperating && AppState == AppState.Ready;

    /// <summary>智能选择按钮是否可用</summary>
    public bool CanSmartSelect =>
        IsAdmin && IsIndexReady && !IsOperating && AppState == AppState.Ready;

    /// <summary>模拟运行按钮是否可用</summary>
    public bool CanSimulate =>
        IsIndexReady && !IsOperating && AppState == AppState.Ready;

    /// <summary>导出报告按钮是否可用</summary>
    public bool CanExport =>
        IsIndexReady && !IsOperating && AppState == AppState.Ready;

    /// <summary>刷新扫描按钮是否可用</summary>
    public bool CanRefresh =>
        !IsOperating && AppState is AppState.Ready or AppState.Error;

    /// <summary>取消按钮是否可用</summary>
    public bool CanCancel => IsOperating;

    // ============================================================
    // 初始化方法
    // ============================================================

    /// <summary>
    /// 加载仪表板数据和初始状态。
    /// 在 MainWindow.Loaded 事件中调用。
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogMethodEntry();

            // 从 AppStateService 同步管理员初始状态
            _isAdmin = _appStateService.IsAdmin;

            // 加载仪表板数据（从 SQLite 决策缓存聚合）
            var dashboardData = await _dataProvider.GetDashboardSummaryAsync();
            DashboardItems.Clear();
            foreach (var item in dashboardData)
            {
                DashboardItems.Add(item);
            }
            _logger.LogInformation("仪表板数据已加载: {Count} 张卡片", DashboardItems.Count);

            // 初始化状态栏
            StatusInfo.LastScanTime = DateTime.Now;
            StatusInfo.CumulativeBytes = 0;
            OnPropertyChanged(nameof(StatusInfo));

            // C7: 异步加载隔离区统计（不阻塞 UI 线程）
            _ = RefreshQuarantineStatsAsync();

            // 从 AppStateService 读取索引就绪状态
            IsIndexReady = true;

            // 转换到就绪状态
            _appStateService.TransitionTo(AppState.Ready);

            // C2: 异步加载文件列表数据（不阻塞 UI 线程）
            _ = Task.Run(async () =>
            {
                try
                {
                    await _fileListViewModel.LoadDataAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "文件列表初始加载失败");
                }
            });

            _logger.LogMethodExit("初始化完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MainViewModel 初始化失败");
            MessageBox.Show(
                $"初始化界面数据失败: {ex.Message}",
                "初始化错误",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ============================================================
    // 命令执行逻辑（每个方法包含 try-catch 和日志记录）
    // ============================================================

    /// <summary>
    /// 一键清理安全项：执行 B1→B2→B3→B4 管道，清理所有 FinalAction=AutoDelete 的文件。
    /// C8: 接入真实管道（替换 Mock），实时展示进度和取消能力。
    /// </summary>
    private async void ExecuteQuickClean()
    {
        _logger.LogMethodEntry("用户触发一键清理");
        try
        {
            if (!IsAdmin)
            {
                _logger.LogWarning("非管理员用户尝试执行一键清理，操作已阻止");
                return;
            }

            _currentCts = new CancellationTokenSource();
            var ct = _currentCts.Token;

            // 创建进度报告器：在 UI 线程更新进度条和状态文本
            var progress = new Progress<OperationProgress>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    OperationProgress = p;
                    StatusInfo.UpdateStatusText(p);
                    OnPropertyChanged(nameof(StatusInfo));
                });
            });

            try
            {
                AppState = AppState.Cleaning;
                IsOperating = true;

                // 阶段1: 回收站容量预检（设计文档 7.2 节：在所有删除路径之前仅调用一次）
                ((IProgress<OperationProgress>)progress).Report(OperationProgress.CheckingCapacity());
                var capacity = await _operationExecutor.QueryRecycleBinCapacityAsync(ct);

                // 阶段2: B1→B2→B3 流式管道，收集 AutoDelete 候选文件
                var candidates = await CollectCandidatesAsync(FinalAction.AutoDelete, ct);

                if (candidates.Count == 0)
                {
                    _logger.LogInformation("一键清理：未找到可自动清理的文件");
                    MessageBox.Show("未找到可自动清理的安全文件。\n\n当前没有可一键清理的项目。",
                        "一键清理", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 阶段3: 回收站容量校验（若待删文件总量超出可用空间，弹警告确认）
                var totalSize = candidates.Sum(c => c.SizeBytes);
                if (!capacity.CanAccommodate(totalSize))
                {
                    var confirmResult = MessageBox.Show(
                        $"待清理文件总大小约为 {ByteFormatter.Format(totalSize)}，" +
                        $"超出回收站可用容量 ({ByteFormatter.Format(capacity.AvailableBytes)})。\n\n" +
                        "超出部分将直接永久删除。是否继续？",
                        "回收站容量不足", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    if (confirmResult != MessageBoxResult.Yes)
                    {
                        _logger.LogInformation("用户因回收站容量不足取消了一键清理");
                        return;
                    }
                }

                // 阶段4: 生成不可变快照（含 SHA-256 哈希计算，IProgress<int> 逐文件更新进度）
                ((IProgress<OperationProgress>)progress).Report(OperationProgress.Hashing(candidates.Count));
                var hashProgress = new Progress<int>(completed =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        OperationProgress = new OperationProgress
                        {
                            Phase = OperationPhase.Hashing,
                            CompletedCount = completed,
                            TotalCount = candidates.Count,
                            ProcessedBytes = 0,
                            CurrentFilePath = null
                        };
                        StatusInfo.UpdateStatusText(OperationProgress);
                        OnPropertyChanged(nameof(StatusInfo));
                    });
                });
                var snapshot = await _decisionEngine.DecideAndSnapshotAsync(candidates, hashProgress, ct);

                // 阶段5: 执行删除操作（IOperationExecutor 内部 100ms 限流报告进度）
                ((IProgress<OperationProgress>)progress).Report(OperationProgress.Deleting(snapshot.TotalFileCount));
                var result = await _operationExecutor.ExecuteAsync(
                    snapshot.Entries, DeleteMethod.RecycleBin, progress, ct);

                // 阶段6: 更新状态栏统计信息
                StatusInfo.FreedBytes = result.FreedBytes;
                StatusInfo.CumulativeBytes += result.FreedBytes;
                StatusInfo.LastScanTime = DateTime.Now;

                // 异步刷新隔离区计数（非关键路径，失败不影响主流程）
                try
                {
                    await RefreshQuarantineStatsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "刷新隔离区统计失败（非关键错误）");
                }

                OnPropertyChanged(nameof(StatusInfo));

                _logger.LogInformation(
                    "一键清理完成: 成功={Success}, 失败={Failed}, 需人工审核={Manual}, 取消={Cancelled}, 释放={Freed}",
                    result.SuccessCount, result.FailedCount, result.ManualReviewCount,
                    result.CancelledCount, result.FreedBytes);

                // 展示操作结果
                var msg = result.IsCancelled
                    ? "操作已被取消"
                    : $"清理完成！\n\n成功: {result.SuccessCount} 项\n" +
                      $"失败: {result.FailedCount} 项\n" +
                      $"需人工审核: {result.ManualReviewCount} 项\n" +
                      $"释放空间: {StatusInfo.FreedBytesText}\n" +
                      $"耗时: {result.Elapsed:hh\\:mm\\:ss}";
                MessageBox.Show(msg, "一键清理",
                    MessageBoxButton.OK,
                    result.IsFullySuccessful ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("一键清理被用户取消");
                MessageBox.Show("操作已被取消。", "一键清理", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                IsOperating = false;
                AppState = AppState.Ready;
                StatusInfo.ClearStatusText();
                OnPropertyChanged(nameof(StatusInfo));
                _currentCts?.Dispose();
                _currentCts = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "一键清理执行失败");
            MessageBox.Show($"清理操作失败: {ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            IsOperating = false;
            AppState = AppState.Ready;
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    /// <summary>
    /// 智能选择建议项：执行 B1→B2→B3→B4 管道，清理所有 FinalAction=SuggestDelete 的文件。
    /// C8: 接入真实管道（替换 Mock），二次确认后执行。
    /// </summary>
    private async void ExecuteSmartSelect()
    {
        _logger.LogMethodEntry("用户触发智能选择");
        try
        {
            if (!IsAdmin)
            {
                _logger.LogWarning("非管理员用户尝试执行智能选择，操作已阻止");
                return;
            }

            _currentCts = new CancellationTokenSource();
            var ct = _currentCts.Token;

            var progress = new Progress<OperationProgress>(p =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    OperationProgress = p;
                    StatusInfo.UpdateStatusText(p);
                    OnPropertyChanged(nameof(StatusInfo));
                });
            });

            try
            {
                AppState = AppState.Cleaning;
                IsOperating = true;

                // 阶段1: B1→B2→B3 流式管道，收集 SuggestDelete 候选文件
                ((IProgress<OperationProgress>)progress).Report(new OperationProgress
                {
                    Phase = OperationPhase.Simulating,
                    CompletedCount = 0,
                    TotalCount = -1,
                    ProcessedBytes = 0
                });

                var candidates = await CollectCandidatesAsync(FinalAction.SuggestDelete, ct);

                if (candidates.Count == 0)
                {
                    _logger.LogInformation("智能选择：未找到建议清理的文件");
                    MessageBox.Show("未找到建议清理的文件。", "智能选择",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 阶段2: 弹出确认对话框（含真实统计数据）
                var totalSize = candidates.Sum(c => c.SizeBytes);
                var confirmResult = MessageBox.Show(
                    $"将清理所有【建议清理】分类下的文件（{candidates.Count:N0} 项，" +
                    $"约 {ByteFormatter.Format(totalSize)}）。\n\n是否继续？",
                    "智能选择确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirmResult != MessageBoxResult.Yes)
                {
                    _logger.LogInformation("用户取消了智能选择操作");
                    return;
                }

                // 阶段3: 生成不可变快照（含 SHA-256 哈希计算）
                ((IProgress<OperationProgress>)progress).Report(OperationProgress.Hashing(candidates.Count));
                var hashProgress = new Progress<int>(completed =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        OperationProgress = new OperationProgress
                        {
                            Phase = OperationPhase.Hashing,
                            CompletedCount = completed,
                            TotalCount = candidates.Count,
                            ProcessedBytes = 0,
                            CurrentFilePath = null
                        };
                        StatusInfo.UpdateStatusText(OperationProgress);
                        OnPropertyChanged(nameof(StatusInfo));
                    });
                });
                var snapshot = await _decisionEngine.DecideAndSnapshotAsync(candidates, hashProgress, ct);

                // 阶段4: 执行删除操作
                ((IProgress<OperationProgress>)progress).Report(OperationProgress.Deleting(snapshot.TotalFileCount));
                var result = await _operationExecutor.ExecuteAsync(
                    snapshot.Entries, DeleteMethod.RecycleBin, progress, ct);

                // 阶段5: 更新状态栏
                StatusInfo.FreedBytes = result.FreedBytes;
                StatusInfo.CumulativeBytes += result.FreedBytes;
                StatusInfo.LastScanTime = DateTime.Now;

                try
                {
                    await RefreshQuarantineStatsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "刷新隔离区统计失败（非关键错误）");
                }

                OnPropertyChanged(nameof(StatusInfo));

                _logger.LogInformation(
                    "智能选择完成: 成功={Success}, 失败={Failed}, 需人工审核={Manual}, 取消={Cancelled}, 释放={Freed}",
                    result.SuccessCount, result.FailedCount, result.ManualReviewCount,
                    result.CancelledCount, result.FreedBytes);

                var msg = result.IsCancelled
                    ? "操作已被取消"
                    : $"清理完成！\n\n成功: {result.SuccessCount} 项\n" +
                      $"失败: {result.FailedCount} 项\n" +
                      $"需人工审核: {result.ManualReviewCount} 项\n" +
                      $"释放空间: {StatusInfo.FreedBytesText}\n" +
                      $"耗时: {result.Elapsed:hh\\:mm\\:ss}";
                MessageBox.Show(msg, "智能选择",
                    MessageBoxButton.OK,
                    result.IsFullySuccessful ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("智能选择被用户取消");
                MessageBox.Show("操作已被取消。", "智能选择", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                IsOperating = false;
                AppState = AppState.Ready;
                StatusInfo.ClearStatusText();
                OnPropertyChanged(nameof(StatusInfo));
                _currentCts?.Dispose();
                _currentCts = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "智能选择执行失败");
            MessageBox.Show($"智能选择操作失败: {ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            IsOperating = false;
            AppState = AppState.Ready;
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    /// <summary>
    /// 模拟运行：复用 B1→B2→B3 管道，预览清理效果，不实际删除文件。
    /// C5 阶段将占位的 Mock 调用替换为 ISimulationService.SimulateAsync。
    /// </summary>
    private async void ExecuteSimulate()
    {
        _logger.LogMethodEntry("用户触发模拟运行");
        _currentCts = new CancellationTokenSource();
        var ct = _currentCts.Token;

        try
        {
            // 切换 UI 状态为模拟运行中
            AppState = AppState.Cleaning;
            IsOperating = true;

            // 构建 SimulationProgress → OperationProgress 适配器，
            // 将模拟运行的原生进度映射为 UI 进度条绑定的 OperationProgress
            var progress = new Progress<SimulationProgress>(sp =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    OperationProgress = new OperationProgress
                    {
                        Phase = sp.Phase switch
                        {
                            SimulationPhase.CheckingCapacity => OperationPhase.CheckingCapacity,
                            SimulationPhase.Enumerating => OperationPhase.Simulating,
                            SimulationPhase.Analyzing => OperationPhase.Simulating,
                            SimulationPhase.Completed => OperationPhase.Completed,
                            SimulationPhase.Cancelled => OperationPhase.Cancelled,
                            _ => OperationPhase.Simulating
                        },
                        CompletedCount = sp.CompletedCount,
                        TotalCount = sp.TotalCount,
                        ProcessedBytes = sp.ProcessedBytes,
                        CurrentFilePath = sp.CurrentFilePath
                    };
                });
            });

            // 从用户配置构建 Everything 查询参数
            var config = _configService.Current;
            var queryParams = new EverythingQueryParams
            {
                Volumes = config.IncludedVolumes.Count > 0
                    ? config.IncludedVolumes
                    : new List<string> { "C:" },
                MaxResults = config.MaxScanFiles > 0 ? config.MaxScanFiles : null
            };

            // 调用 C5 模拟运行服务：流式执行 B1→B2→B3 管道
            var result = await _simulationService.SimulateAsync(
                queryParams,
                config.AIEnabled,
                config.AITrustLevel,
                progress,
                ct);

            // 缓存结果供后续导出使用
            _cachedSimulationResult = result;

            // 更新仪表板卡片（基于模拟结果的 FinalAction 分组统计）
            UpdateDashboardFromSimulation(result);

            // 更新状态栏
            StatusInfo.FreedBytes = result.TotalFreedBytes;
            StatusInfo.LastScanTime = DateTime.Now;
            OnPropertyChanged(nameof(StatusInfo));

            _logger.LogInformation(
                "模拟运行完成: 处理={Total}, 可释放={Freed}字节, 耗时={Elapsed}",
                result.TotalProcessedCount, result.TotalFreedBytes, result.Elapsed);

            // 构建结果展示消息
            var message = new StringBuilder();
            message.AppendLine($"共分析 {result.TotalProcessedCount:N0} 个文件");
            message.AppendLine($"预估可释放空间: {ByteFormatter.Format(result.TotalFreedBytes)}");
            message.AppendLine($"耗时: {result.Elapsed:hh\\:mm\\:ss\\.fff}");
            message.AppendLine();
            message.AppendLine("操作分布:");
            message.AppendLine($"  🟢 自动删除: {result.AutoDeleteCount:N0} 项");
            message.AppendLine($"  💡 建议删除: {result.SuggestDeleteCount:N0} 项");
            message.AppendLine($"  ⚠️ 需人工确认: {result.ManualReviewCount:N0} 项");
            message.AppendLine($"  🛡️ 已保护: {result.ProtectedCount:N0} 项");

            if (result.RecycleBinCapacity is { } cap)
            {
                message.AppendLine();
                message.AppendLine($"回收站状态: {cap}");
            }

            MessageBox.Show(
                message.ToString(),
                "模拟运行结果",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("模拟运行被用户取消");
            MessageBox.Show("模拟运行已取消。", "模拟运行", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "模拟运行执行失败");
            MessageBox.Show($"模拟运行失败: {ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsOperating = false;
            AppState = AppState.Ready;
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    /// <summary>
    /// 导出报告：将最近一次模拟运行结果导出为 HTML/CSV 文件。
    /// 需要先执行模拟运行以生成分析数据。
    /// </summary>
    private async void ExecuteExport()
    {
        _logger.LogMethodEntry("用户触发导出报告");
        try
        {
            // 前置检查：必须先执行模拟运行
            if (_cachedSimulationResult == null)
            {
                _logger.LogWarning("导出报告失败：未找到模拟运行结果，请先执行模拟运行");
                MessageBox.Show(
                    "请先执行【模拟运行】以生成分析数据。",
                    "导出报告",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // 弹出保存文件对话框，让用户选择路径和格式
            var saveFileDialog = new SaveFileDialog
            {
                Title = "导出分析报告",
                Filter = "HTML 报告 (*.html)|*.html|CSV 报告 (*.csv)|*.csv",
                DefaultExt = "html",
                FileName = $"磁盘清理报告_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (saveFileDialog.ShowDialog() != true)
            {
                _logger.LogInformation("用户取消了导出报告操作");
                return;
            }

            // 根据文件扩展名确定导出格式
            var format = Path.GetExtension(saveFileDialog.FileName).ToLowerInvariant() switch
            {
                ".csv" => ReportFormat.Csv,
                _ => ReportFormat.Html
            };

            var options = new ReportExportOptions
            {
                OutputPath = saveFileDialog.FileName,
                Format = format,
                IncludeProtectedFiles = false,
                MaxSampleFilesPerGroup = 10,
                Language = _configService.Current.Language
            };

            _logger.LogInformation("开始导出报告: Format={Format}, Path={Path}", format, options.OutputPath);

            // 构建进度适配器
            var progress = new Progress<SimulationProgress>(sp =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    OperationProgress = new OperationProgress
                    {
                        Phase = sp.Phase switch
                        {
                            SimulationPhase.Analyzing => OperationPhase.Simulating,
                            SimulationPhase.Completed => OperationPhase.Completed,
                            _ => OperationPhase.Simulating
                        },
                        CompletedCount = sp.CompletedCount,
                        TotalCount = sp.TotalCount,
                        ProcessedBytes = sp.ProcessedBytes,
                        CurrentFilePath = sp.CurrentFilePath
                    };
                });
            });

            IsOperating = true;
            var ct = _currentCts?.Token ?? CancellationToken.None;

            // 重新流式遍历管道获取全量文件详情
            var fileStream = _simulationService.StreamAnalysisAsync(
                _cachedSimulationResult.QueryParams,
                _cachedSimulationResult.AiEnabled,
                _cachedSimulationResult.AiTrustLevel,
                progress,
                ct);

            // 调用导出器生成报告文件
            await _reportExporter.ExportAsync(
                _cachedSimulationResult,
                fileStream,
                options,
                progress,
                ct);

            _logger.LogInformation("报告导出成功: {Path}", options.OutputPath);

            MessageBox.Show(
                $"报告已成功导出到:\n{options.OutputPath}",
                "导出完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("报告导出被用户取消");
            MessageBox.Show("导出操作已取消。", "导出报告", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出报告执行失败");
            MessageBox.Show($"导出操作失败: {ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsOperating = false;
        }
    }

    /// <summary>
    /// 刷新扫描：重新执行 B1→B2→B3 流式管道，更新仪表板和文件列表。
    /// </summary>
    private async void ExecuteRefresh()
    {
        _logger.LogMethodEntry("用户触发刷新扫描");
        _currentCts = new CancellationTokenSource();
        var ct = _currentCts.Token;

        try
        {
            AppState = AppState.Scanning;
            IsOperating = true;

            var config = _configService.Current;
            var queryParams = new EverythingQueryParams
            {
                Volumes = config.IncludedVolumes.Count > 0
                    ? config.IncludedVolumes
                    : new List<string> { "C:" },
                MaxResults = config.MaxScanFiles > 0 ? config.MaxScanFiles : null
            };

            // 构建进度适配器
            var progress = new Progress<SimulationProgress>(sp =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    OperationProgress = new OperationProgress
                    {
                        Phase = sp.Phase switch
                        {
                            SimulationPhase.CheckingCapacity => OperationPhase.CheckingCapacity,
                            SimulationPhase.Enumerating => OperationPhase.Simulating,
                            SimulationPhase.Analyzing => OperationPhase.Simulating,
                            SimulationPhase.Completed => OperationPhase.Completed,
                            SimulationPhase.Cancelled => OperationPhase.Cancelled,
                            _ => OperationPhase.Simulating
                        },
                        CompletedCount = sp.CompletedCount,
                        TotalCount = sp.TotalCount,
                        ProcessedBytes = sp.ProcessedBytes,
                        CurrentFilePath = sp.CurrentFilePath
                    };
                });
            });

            // 执行 B1→B2→B3 管道
            var result = await _simulationService.SimulateAsync(
                queryParams,
                config.AIEnabled,
                config.AITrustLevel,
                progress,
                ct);

            _cachedSimulationResult = result;

            // 更新仪表板
            UpdateDashboardFromSimulation(result);

            // 更新状态栏
            StatusInfo.FreedBytes = result.TotalFreedBytes;
            StatusInfo.LastScanTime = DateTime.Now;
            OnPropertyChanged(nameof(StatusInfo));

            // 异步刷新文件列表
            _ = Task.Run(async () =>
            {
                try
                {
                    await _fileListViewModel.LoadDataAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "文件列表刷新失败");
                }
            });

            _logger.LogInformation("刷新扫描完成: 处理={Total}, 可释放={Freed}字节",
                result.TotalProcessedCount, result.TotalFreedBytes);

            AppState = AppState.Ready;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("刷新扫描被用户取消");
            AppState = AppState.Ready;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新扫描执行失败");
            AppState = AppState.Error;
            MessageBox.Show($"刷新扫描失败: {ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsOperating = false;
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    /// <summary>
    /// 取消当前正在执行的操作。
    /// C8: 增强状态反馈，用户点击取消后立即显示"正在取消..."提示。
    /// </summary>
    private void ExecuteCancel()
    {
        _logger.LogMethodEntry("用户触发取消操作");
        try
        {
            // 立即更新 UI 状态文本，让用户知道取消请求已收到
            OperationStatusText = "正在取消...";
            _currentCts?.Cancel();
            _logger.LogWarning("已发送取消请求");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取消操作执行失败");
        }
    }

    /// <summary>
    /// 打开设置窗口（C4 实现）。
    /// 从 DI 容器解析 SettingsWindow 并以模态方式显示。
    /// 设置窗口内部使用 UserConfig 深拷贝，仅在确认时写回。
    /// </summary>
    private void ExecuteNavigateToSettings()
    {
        _logger.LogMethodEntry("用户触发打开设置窗口");
        try
        {
            var settingsWindow = App.ServiceProvider!.GetRequiredService<SettingsWindow>();
            settingsWindow.Owner = Application.Current.MainWindow;
            settingsWindow.ShowDialog();
            _logger.LogInformation("设置窗口已关闭 (DialogResult={Result})", settingsWindow.DialogResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开设置窗口失败");
            MessageBox.Show(
                $"打开设置窗口失败:\n{ex.Message}",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 打开历史记录窗口（C6 实现）。
    /// 从 DI 容器解析 HistoryWindow 并以模态方式显示。
    /// 窗口包含审计日志和删除历史两个 Tab 页。
    /// </summary>
    private void ExecuteNavigateToHistory()
    {
        _logger.LogMethodEntry("用户触发打开历史记录窗口");
        try
        {
            var historyWindow = App.ServiceProvider!.GetRequiredService<HistoryWindow>();
            historyWindow.Owner = Application.Current.MainWindow;
            historyWindow.ShowDialog();
            _logger.LogInformation("历史记录窗口已关闭 (DialogResult={Result})", historyWindow.DialogResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开历史记录窗口失败");
            MessageBox.Show(
                $"打开历史记录窗口失败:\n{ex.Message}",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 打开隔离区管理窗口（C7 实现）。
    /// 从 DI 容器解析 QuarantineWindow 并以模态方式显示。
    /// 关闭后自动刷新状态栏的隔离区计数。
    /// </summary>
    private void ExecuteNavigateToQuarantine()
    {
        _logger.LogMethodEntry("用户点击状态栏隔离区链接");
        try
        {
            var quarantineWindow = App.ServiceProvider!.GetRequiredService<QuarantineWindow>();
            quarantineWindow.Owner = Application.Current.MainWindow;
            quarantineWindow.ShowDialog();

            // 关闭后异步刷新状态栏隔离区计数
            _ = RefreshQuarantineStatsAsync();
            _logger.LogInformation("隔离区管理窗口已关闭");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开隔离区管理窗口失败");
            MessageBox.Show(
                $"打开隔离区管理窗口失败:\n{ex.Message}",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 异步刷新状态栏隔离区计数。
    /// 从 IQuarantineManager 获取最新统计并更新 StatusInfo.QuarantineCount。
    /// 隔离区禁用或查询失败时 Count 置 0。
    /// </summary>
    private async Task RefreshQuarantineStatsAsync()
    {
        try
        {
            var stats = await _quarantineManager.GetQuarantineStatsAsync();
            StatusInfo.QuarantineCount = stats.IsDisabled ? 0 : stats.TotalFileCount;
            OnPropertyChanged(nameof(StatusInfo));
            _logger.LogDebug("状态栏隔离区计数已刷新: {Count}", StatusInfo.QuarantineCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新隔离区统计失败");
            StatusInfo.QuarantineCount = 0;
        }
    }

    /// <summary>
    /// 打开帮助窗口（C10 占位）。
    /// </summary>
    private void ExecuteNavigateToHelp()
    {
        _logger.LogMethodEntry("用户触发打开帮助");
        try
        {
            MessageBox.Show(
                "帮助文档将在 C10 阶段实现。\n\n届时提供：使用指南、常见问题、规则说明等。",
                "功能开发中",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开帮助失败");
        }
    }

    /// <summary>
    /// 切换"显示所有文件"模式（代理到 FileListViewModel）。
    /// </summary>
    private void ExecuteToggleShowAll()
    {
        _logger.LogMethodEntry("用户触发显示所有文件切换");
        try
        {
            _fileListViewModel.IsShowAllFiles = !_fileListViewModel.IsShowAllFiles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "显示所有文件切换失败");
        }
    }

    /// <summary>
    /// 切换浅色/深色主题。
    /// </summary>
    private void ExecuteToggleTheme()
    {
        _logger.LogMethodEntry("用户触发主题切换");
        try
        {
            IsDarkTheme = !IsDarkTheme;
            _logger.LogInformation("主题切换为: {Theme}", IsDarkTheme ? "深色" : "浅色");

            // 切换 ResourceDictionary
            var app = Application.Current;
            if (app != null)
            {
                var themeUri = IsDarkTheme
                    ? new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
                    : new Uri("Themes/LightTheme.xaml", UriKind.Relative);

                // 移除旧主题，添加新主题
                var oldTheme = app.Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null &&
                        (d.Source.OriginalString.Contains("LightTheme") ||
                         d.Source.OriginalString.Contains("DarkTheme")));
                if (oldTheme != null)
                {
                    app.Resources.MergedDictionaries.Remove(oldTheme);
                }

                var newTheme = new ResourceDictionary { Source = themeUri };
                app.Resources.MergedDictionaries.Add(newTheme);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "主题切换失败");
            MessageBox.Show($"主题切换失败: {ex.Message}", "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ============================================================
    // 私有辅助方法
    // ============================================================

    /// <summary>
    /// 根据模拟运行结果更新仪表板四张分类卡片。
    /// 将 SimulationResult 中的分组统计映射为 DashboardItem 列表。
    /// </summary>
    private void UpdateDashboardFromSimulation(SimulationResult result)
    {
        try
        {
            DashboardItems.Clear();

            // 按 FinalAction 汇总各分组的大小和数量
            long autoDeleteBytes = 0, suggestDeleteBytes = 0, manualReviewBytes = 0, protectedBytes = 0;
            int autoDeleteCount = 0, suggestDeleteCount = 0, manualReviewCount = 0, protectedCount = 0;

            foreach (var group in result.Groups)
            {
                switch (group.FinalAction)
                {
                    case FinalAction.AutoDelete:
                        autoDeleteBytes += group.TotalSizeBytes;
                        autoDeleteCount += group.FileCount;
                        break;
                    case FinalAction.SuggestDelete:
                        suggestDeleteBytes += group.TotalSizeBytes;
                        suggestDeleteCount += group.FileCount;
                        break;
                    case FinalAction.ManualReview:
                        manualReviewBytes += group.TotalSizeBytes;
                        manualReviewCount += group.FileCount;
                        break;
                    case FinalAction.Protected:
                        protectedBytes += group.TotalSizeBytes;
                        protectedCount += group.FileCount;
                        break;
                }
            }

            DashboardItems.Add(new DashboardItem
            {
                Category = FinalAction.AutoDelete,
                Title = "安全可自动清理",
                Icon = "🟢",
                SizeBytes = autoDeleteBytes,
                ItemCount = autoDeleteCount
            });

            DashboardItems.Add(new DashboardItem
            {
                Category = FinalAction.SuggestDelete,
                Title = "建议清理",
                Icon = "💡",
                SizeBytes = suggestDeleteBytes,
                ItemCount = suggestDeleteCount
            });

            DashboardItems.Add(new DashboardItem
            {
                Category = FinalAction.ManualReview,
                Title = "需人工确认",
                Icon = "⚠️",
                SizeBytes = manualReviewBytes,
                ItemCount = manualReviewCount
            });

            DashboardItems.Add(new DashboardItem
            {
                Category = FinalAction.Protected,
                Title = "已保护",
                Icon = "🛡️",
                SizeBytes = protectedBytes,
                ItemCount = protectedCount
            });

            _logger.LogInformation("仪表板已根据模拟结果更新");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新仪表板失败");
        }
    }

    /// <summary>
    /// 执行 B1→B2→B3 流式管道，收集匹配指定 FinalAction 的候选文件。
    /// 流式遍历 Everything 文件索引，经规则引擎评估、决策引擎仲裁后过滤目标文件。
    /// 每 100 个文件通过 Dispatcher 向 UI 线程报告一次进度。
    /// </summary>
    /// <param name="targetAction">目标 FinalAction（AutoDelete 或 SuggestDelete）</param>
    /// <param name="ct">取消令牌，遍历过程中每次迭代前检查</param>
    /// <returns>匹配的 FileDecisionCache 候选文件列表</returns>
    private async Task<List<FileDecisionCache>> CollectCandidatesAsync(
        FinalAction targetAction, CancellationToken ct)
    {
        var config = _configService.Current;
        var queryParams = new EverythingQueryParams
        {
            Volumes = config.IncludedVolumes.Count > 0
                ? config.IncludedVolumes
                : new List<string> { "C:" },
            MaxResults = config.MaxScanFiles > 0 ? config.MaxScanFiles : null
        };

        var candidates = new List<FileDecisionCache>();
        var processedCount = 0;

        await foreach (var file in _fileListProvider.EnumerateFilesAsync(queryParams, ct))
        {
            ct.ThrowIfCancellationRequested();
            processedCount++;

            // B2: 规则引擎评估（hard rules + heuristic rules）
            var ruleResult = _ruleEngine.Evaluate(file, config);

            // 跳过无规则命中的文件（默认保留，不纳入候选）
            if (ruleResult.Verdict == RuleVerdict.Keep)
                continue;

            // 构建决策缓存（临时 FinalAction 将在仲裁后被覆盖）
            var cache = new FileDecisionCache
            {
                FilePath = file.FilePath,
                SizeBytes = file.SizeBytes,
                LastWriteTime = file.LastWriteTime,
                RuleVerdict = ruleResult.Verdict,
                SemanticCategory = ruleResult.SemanticCategory,
                AiLabel = null,
                AiConfidence = null,
                AiExplanation = null,
                FinalAction = FinalAction.Protected,
                UserDecision = null,
                CacheVersion = config.RuleCacheVersion
            };

            // B3: 决策引擎仲裁（规则 + 用户策略 + AI 标签 → FinalAction）
            var arbitrated = _decisionEngine.Arbitrate(cache, config.AIEnabled, config.AITrustLevel);

            if (arbitrated.FinalAction == targetAction)
            {
                candidates.Add(arbitrated);
            }

            // 每 100 个文件向 UI 线程报告一次进度，避免高频刷新
            if (processedCount % 100 == 0)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    OperationProgress = new OperationProgress
                    {
                        Phase = OperationPhase.Simulating,
                        CompletedCount = processedCount,
                        TotalCount = -1,
                        ProcessedBytes = 0,
                        CurrentFilePath = file.FilePath
                    };
                    StatusInfo.UpdateStatusText(OperationProgress);
                    OnPropertyChanged(nameof(StatusInfo));
                });
            }
        }

        return candidates;
    }

    /// <summary>
    /// 更新警告横幅显示逻辑。
    /// 触发条件: (1) 非管理员 → 只读警告；(2) 索引未就绪 → 索引警告。
    /// </summary>
    private void UpdateWarningBanner()
    {
        if (!IsAdmin)
        {
            WarningMessage = "⚠️ 只读模式 — 请以管理员身份重新运行以启用清理功能（非管理员模式下数据分析功能仍可正常使用）";
        }
        else if (!IsIndexReady)
        {
            WarningMessage = "⏳ 索引构建中，搜索结果可能不完整，建议等待索引完成后刷新";
        }
        else
        {
            WarningMessage = null;
        }
    }

    /// <summary>
    /// 刷新所有命令的 CanExecute 状态，触发 UI 按钮启用/禁用更新。
    /// </summary>
    private void RefreshCommandStates()
    {
        OnPropertyChanged(nameof(CanQuickClean));
        OnPropertyChanged(nameof(CanSmartSelect));
        OnPropertyChanged(nameof(CanSimulate));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(AppMode));
    }

    /// <summary>
    /// 响应全局 AppState 变更。
    /// </summary>
    private void OnAppStateChanged(object? sender, AppState newState)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            AppState = newState;
            _logger.LogDebug("UI 层收到状态变更: {State}", newState);
        });
    }

    /// <summary>
    /// 响应全局 AppMode 变更（管理员/只读模式切换）。
    /// </summary>
    private void OnAppModeChanged(object? sender, AppMode newMode)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateWarningBanner();
            RefreshCommandStates();
            OnPropertyChanged(nameof(AppMode));
            _logger.LogInformation("UI 层收到模式变更: {Mode}", newMode);
        });
    }
}
