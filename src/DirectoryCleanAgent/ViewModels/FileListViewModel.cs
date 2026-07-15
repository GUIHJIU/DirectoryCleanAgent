using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.Core.Localization;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Models;
using DirectoryCleanAgent.AI;
using DirectoryCleanAgent.AI.Models;
using DirectoryCleanAgent.Services;
using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.ViewModels;

/// <summary>
/// 文件列表与分组视图的核心 ViewModel。
///
/// 职责：
/// 1. 从 IFileDecisionCacheRepository 读取可操作文件，构建分组树
/// 2. 管理列头排序（排序下推至 Everything，语义标签列内存排序）
/// 3. "显示所有文件"切换（分组视图 → 平面列表）
/// 4. 订阅 IFileListProvider.FileChanged 事件实现增量刷新
///
/// 设计约束：
/// - 分组视图仅展示可操作文件（FinalAction != Protected）
/// - 列头排序优先委托 Everything 原生排序，语义标签列在内存中排序缓存子集
/// - "显示所有文件"模式下绕过规则引擎，直接查询 Everything 全量结果
/// </summary>
public class FileListViewModel : ViewModelBase, IDisposable
{
    // ============================================================
    // 依赖项
    // ============================================================
    private readonly ILogger<FileListViewModel> _logger;
    private readonly IFileDecisionCacheRepository _cacheRepo;
    private readonly IFileListProvider _fileProvider;
    private readonly IRuleEngine _ruleEngine;
    private readonly IDecisionEngine _decisionEngine;
    private readonly SemanticLabelLocalizer _labelLocalizer;
    private readonly IConfigService _configService;
    private readonly IAppStateService _appStateService;

    private CancellationTokenSource? _currentLoadCts;
    private readonly ConcurrentDictionary<string, FileChangeType> _pendingFileChanges = new();
    private Timer? _throttleTimer;
    private readonly IAiAnalysisCoordinator _aiCoordinator;
    private bool _disposed;

    private string _aiProgressText = string.Empty;
    private bool _isAiAnalyzing;
    private bool _isAiAnalysisCancellable;

    // ============================================================
    // 集合（绑定到 UI）
    // ============================================================

    /// <summary>分组树数据源（绑定到 TreeView.ItemsSource）</summary>
    public ObservableCollection<FileGroupNode> GroupTree { get; } = new();

    /// <summary>当前文件列表数据源（绑定到 DataGrid.ItemsSource）</summary>
    public ObservableCollection<FileListItem> CurrentFileList { get; } = new();

    // 后台数据缓存（用于快速重建分组树）
    private IReadOnlyList<FileDecisionCache> _allActionableCache = Array.Empty<FileDecisionCache>();

    // ============================================================
    // 分组状态
    // ============================================================

    private int _groupByPrimaryIndex;
    private int _groupBySecondaryIndex;

    /// <summary>一级分组依据索引：0=路径, 1=时间, 2=大小, 3=类型, 4=语义标签</summary>
    public int GroupByPrimaryIndex
    {
        get => _groupByPrimaryIndex;
        set
        {
            if (SetProperty(ref _groupByPrimaryIndex, value))
            {
                _currentLoadCts?.Cancel();
                _currentLoadCts?.Dispose();
                _currentLoadCts = new CancellationTokenSource();
                _ = RebuildGroupTreeAsync(_currentLoadCts.Token);
            }
        }
    }

    /// <summary>二级分组依据索引：0=无, 1=时间, 2=类型, 3=FinalAction, 4=语义标签</summary>
    public int GroupBySecondaryIndex
    {
        get => _groupBySecondaryIndex;
        set
        {
            if (SetProperty(ref _groupBySecondaryIndex, value))
            {
                _currentLoadCts?.Cancel();
                _currentLoadCts?.Dispose();
                _currentLoadCts = new CancellationTokenSource();
                _ = RebuildGroupTreeAsync(_currentLoadCts.Token);
            }
        }
    }

    /// <summary>分组依据模式列表（用于 ComboBox 绑定）</summary>
    public static ObservableCollection<string> GroupByModes { get; } = new()
    {
        "路径", "时间", "大小", "类型", "语义标签"
    };

    /// <summary>二级分组依据选项列表</summary>
    public static ObservableCollection<string> SubGroupModes { get; } = new()
    {
        "无", "时间", "类型", "操作建议", "语义标签"
    };

    // ============================================================
    // 显示模式
    // ============================================================

    private bool _isShowAllFiles;

    /// <summary>是否显示所有文件（true=平面列表, false=分组视图）</summary>
    public bool IsShowAllFiles
    {
        get => _isShowAllFiles;
        set
        {
            if (SetProperty(ref _isShowAllFiles, value))
            {
                OnPropertyChanged(nameof(IsGroupedView));
                OnPropertyChanged(nameof(ShowBottomHint));
                _ = HandleShowAllToggleAsync(value, CancellationToken.None);
            }
        }
    }

    /// <summary>是否为分组视图模式</summary>
    public bool IsGroupedView => !IsShowAllFiles;

    // ============================================================
    // 排序状态
    // ============================================================

    private EverythingSortType? _currentSortColumn;
    private bool _isSortDescending;

    /// <summary>当前排序列（null=默认排序）</summary>
    public EverythingSortType? CurrentSortColumn
    {
        get => _currentSortColumn;
        set => SetProperty(ref _currentSortColumn, value);
    }

    /// <summary>是否降序排列</summary>
    public bool IsSortDescending
    {
        get => _isSortDescending;
        set => SetProperty(ref _isSortDescending, value);
    }

    /// <summary>列名（SortMemberPath）到 EverythingSortType 的映射表。
    /// 语义标签列无法下推（不在 Everything 索引中），在内存中排序缓存子集。</summary>
    private static readonly Dictionary<string, EverythingSortType> ColumnSortMap = new()
    {
        ["Name"] = EverythingSortType.Name,
        ["Path"] = EverythingSortType.Path,
        ["Size"] = EverythingSortType.Size,
        ["DateModified"] = EverythingSortType.DateModified,
        ["Extension"] = EverythingSortType.Extension,
    };

    // ============================================================
    // 底部提示
    // ============================================================

    private string _bottomHintText = "当前视图仅包含可操作文件（建议清理 / 需确认 / 自动清理）。点击「显示所有文件」切换为全量浏览模式。";

    /// <summary>底部灰色提示文本</summary>
    public string BottomHintText
    {
        get => _bottomHintText;
        set => SetProperty(ref _bottomHintText, value);
    }

    /// <summary>是否显示底部提示（仅在分组视图下显示）</summary>
    public bool ShowBottomHint => IsGroupedView;

    // ============================================================
    // 加载状态
    // ============================================================

    private bool _isLoading;
    private bool _hasData;
    private bool _isEmpty;
    private string _emptyMessage = "暂无文件数据，请先执行磁盘扫描";

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool HasData
    {
        get => _hasData;
        set => SetProperty(ref _hasData, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }

    public string EmptyMessage
    {
        get => _emptyMessage;
        set => SetProperty(ref _emptyMessage, value);
    }

    // ============================================================
    // 选中状态
    // ============================================================

    private FileGroupNode? _selectedGroup;

    /// <summary>当前选中的分组节点（点击分组后过滤 DataGrid）</summary>
    public FileGroupNode? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value) && value != null)
            {
                _ = OnGroupSelectedAsync(value, CancellationToken.None);
            }
        }
    }

    // ============================================================
    // 统计摘要
    // ============================================================

    private int _totalFilesShown;
    private long _totalSizeShown;

    public int TotalFilesShown
    {
        get => _totalFilesShown;
        set
        {
            if (SetProperty(ref _totalFilesShown, value))
                OnPropertyChanged(nameof(TotalFilesSummaryText));
        }
    }

    public long TotalSizeShown
    {
        get => _totalSizeShown;
        set
        {
            if (SetProperty(ref _totalSizeShown, value))
                OnPropertyChanged(nameof(TotalSizeSummaryText));
        }
    }

    /// <summary>底部统计摘要文本（如 "显示 128 项，共 3.5 GB"）</summary>
    public string TotalSizeSummaryText => $"显示 {TotalFilesShown} 项";

    /// <summary>文件总数摘要文本</summary>
    public string TotalFilesSummaryText => $"共 {TotalFilesShown} 个文件";

    // ============================================================
    // AI 分析进度（绑定到状态栏）
    // ============================================================

    /// <summary>AI 分析进度文本（绑定到状态栏）</summary>
    public string AiProgressText
    {
        get => _aiProgressText;
        set => SetProperty(ref _aiProgressText, value);
    }

    /// <summary>是否有 AI 分析正在进行中（控制进度条和取消按钮可见性）</summary>
    public bool IsAiAnalyzing
    {
        get => _isAiAnalyzing;
        set => SetProperty(ref _isAiAnalyzing, value);
    }

    /// <summary>当前是否可取消 AI 分析</summary>
    public bool IsAiAnalysisCancellable
    {
        get => _isAiAnalysisCancellable;
        set => SetProperty(ref _isAiAnalysisCancellable, value);
    }

    /// <summary>AI 分析进度百分比（0-100），用于状态栏 ProgressBar 绑定</summary>
    public double AiProgressPercentage =>
        _aiCoordinator.CurrentProgress is { TotalCount: > 0 } p
            ? (double)p.CompletedCount / p.TotalCount * 100
            : 0;

    // ============================================================
    // 命令
    // ============================================================

    public RelayCommand<FileGroupNode> SelectGroupCommand { get; }
    public RelayCommand<string> SortByColumnCommand { get; }
    public RelayCommand RefreshListCommand { get; }
    public RelayCommand<FileListItem> ExcludeFileCommand { get; }
    public RelayCommand<FileListItem> ViewDetailCommand { get; }
    public RelayCommand<FileListItem> ToggleFileCheckCommand { get; }
    public RelayCommand<FileListItem> RequestAiAnalysisCommand { get; }
    public RelayCommand AnalyzeSelectedFilesCommand { get; }
    public RelayCommand CancelAiAnalysisCommand { get; }

    // ============================================================
    // 构造函数
    // ============================================================

    public FileListViewModel(
        ILogger<FileListViewModel> logger,
        IFileDecisionCacheRepository cacheRepo,
        IFileListProvider fileProvider,
        IRuleEngine ruleEngine,
        IDecisionEngine decisionEngine,
        SemanticLabelLocalizer labelLocalizer,
        IConfigService configService,
        IAppStateService appStateService,
        IAiAnalysisCoordinator aiCoordinator)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheRepo = cacheRepo ?? throw new ArgumentNullException(nameof(cacheRepo));
        _fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
        _ruleEngine = ruleEngine ?? throw new ArgumentNullException(nameof(ruleEngine));
        _decisionEngine = decisionEngine ?? throw new ArgumentNullException(nameof(decisionEngine));
        _labelLocalizer = labelLocalizer ?? throw new ArgumentNullException(nameof(labelLocalizer));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _appStateService = appStateService ?? throw new ArgumentNullException(nameof(appStateService));
        _aiCoordinator = aiCoordinator ?? throw new ArgumentNullException(nameof(aiCoordinator));

        // 初始化命令
        SelectGroupCommand = new RelayCommand<FileGroupNode>(ExecuteSelectGroup);
        SortByColumnCommand = new RelayCommand<string>(ExecuteSortByColumn);
        RefreshListCommand = new RelayCommand(async () => await ExecuteRefreshAsync());
        ExcludeFileCommand = new RelayCommand<FileListItem>(ExecuteExcludeFile);
        ViewDetailCommand = new RelayCommand<FileListItem>(ExecuteViewDetail);
        ToggleFileCheckCommand = new RelayCommand<FileListItem>(ExecuteToggleFileCheck);
        RequestAiAnalysisCommand = new RelayCommand<FileListItem>(ExecuteRequestAiAnalysis);
        AnalyzeSelectedFilesCommand = new RelayCommand(async () => await ExecuteAnalyzeSelectedFilesAsync());
        CancelAiAnalysisCommand = new RelayCommand(ExecuteCancelAiAnalysis);

        // 订阅 Everything 文件变更事件（增量刷新）
        _fileProvider.FileChanged += OnFileChanged;

        // 订阅全局状态变更
        _appStateService.StateChanged += OnAppStateChanged;

        // 订阅 AI 协调器进度和完成事件
        _aiCoordinator.ProgressChanged += OnAiProgressChanged;
        _aiCoordinator.AnalysisCompleted += OnAiAnalysisCompleted;

        _logger.LogMethodEntry("FileListViewModel 构造完成");
    }

    // ============================================================
    // 公共方法：数据加载
    // ============================================================

    /// <summary>
    /// 初始数据加载：从缓存读取可操作文件，构建分组树，显示默认分组下的文件列表。
    /// 在扫描完成后由 MainViewModel 调用。
    /// </summary>
    public async Task LoadDataAsync(CancellationToken ct = default)
    {
        _logger.LogMethodEntry("开始加载文件列表数据");
        IsLoading = true;

        try
        {
            // 清除 AI 分析去重状态，确保新扫描的文件可以重新分析
            _aiCoordinator.ClearFileStates();

            _currentLoadCts?.Cancel();
            _currentLoadCts?.Dispose();
            _currentLoadCts = new CancellationTokenSource();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _currentLoadCts.Token);

            // 1. 从缓存获取所有可操作文件
            var allEntries = await _cacheRepo.GetAllAsync(linkedCts.Token);
            _logger.LogInformation("从缓存加载了 {Count} 条记录", allEntries.Count);

            // 2. 过滤可操作文件（排除 Protected）
            _allActionableCache = allEntries
                .Where(e => e.FinalAction != FinalAction.Protected)
                .ToList();

            // 3. 构建分组树
            await RebuildGroupTreeAsync(linkedCts.Token);

            // 4. 更新状态
            HasData = _allActionableCache.Count > 0;
            IsEmpty = !HasData;
            if (IsEmpty)
            {
                EmptyMessage = "暂无文件数据，请先执行磁盘扫描";
            }

            _logger.LogInformation("文件列表数据加载完成: 可操作={Actionable}, 总计={Total}",
                _allActionableCache.Count, allEntries.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("文件列表加载被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载文件列表数据失败");
            await RunOnUIThreadAsync(() =>
            {
                EmptyMessage = $"加载失败: {ex.Message}";
                IsEmpty = true;
                HasData = false;
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ============================================================
    // 分组树构建
    // ============================================================

    /// <summary>
    /// 按当前分组依据重建分组树。
    /// </summary>
    public async Task RebuildGroupTreeAsync(CancellationToken ct)
    {
        if (_allActionableCache.Count == 0)
        {
            await RunOnUIThreadAsync(() =>
            {
                GroupTree.Clear();
                CurrentFileList.Clear();
            });
            return;
        }

        try
        {
            var tree = await Task.Run(() =>
                BuildGroupTree(_allActionableCache, _groupByPrimaryIndex, _groupBySecondaryIndex), ct);

            await RunOnUIThreadAsync(() =>
            {
                GroupTree.Clear();
                foreach (var node in tree)
                {
                    GroupTree.Add(node);
                }

                // 默认展开第一个节点并加载其文件列表
                if (GroupTree.Count > 0)
                {
                    GroupTree[0].IsExpanded = true;
                    _ = OnGroupSelectedAsync(GroupTree[0], CancellationToken.None);
                }
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("分组树重建被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "分组树重建失败");
        }
    }

    /// <summary>
    /// 分组树生成核心算法。
    /// 1. 过滤可操作文件（FinalAction != Protected）
    /// 2. 按一级分组依据分桶
    /// 3. 每组生成 FileGroupNode（含图标、项数、总大小）
    /// 4. 若设置二级分组，为每个一级节点生成 Children
    /// 5. 按 TotalSizeBytes 降序排列
    /// </summary>
    private List<FileGroupNode> BuildGroupTree(
        IReadOnlyList<FileDecisionCache> files,
        int primaryMode,
        int secondaryMode)
    {
        // Step 1: 过滤（已在外部完成，此处为防御性再过滤）
        var actionable = files.Where(f => f.FinalAction != FinalAction.Protected).ToList();
        if (actionable.Count == 0) return new List<FileGroupNode>();

        // Step 2: 按一级分组依据分桶
        List<FileGroupNode> primaryGroups = primaryMode switch
        {
            0 => BuildGroupByPath(actionable),
            1 => BuildGroupByTime(actionable),
            2 => BuildGroupBySize(actionable),
            3 => BuildGroupByType(actionable),
            4 => BuildGroupBySemanticLabel(actionable),
            _ => BuildGroupBySemanticLabel(actionable)
        };

        // Step 3: 应用二级分组
        if (secondaryMode > 0)
        {
            foreach (var group in primaryGroups)
            {
                group.Children = ApplySubGrouping(actionable
                    .Where(f => BelongsToGroup(f, group, primaryMode))
                    .ToList(), secondaryMode, group);
            }
        }

        // Step 4: 按总大小降序排列
        primaryGroups = primaryGroups.OrderByDescending(g => g.TotalSizeBytes).ToList();

        // Step 5: 更新统计
        TotalFilesShown = primaryGroups.Sum(g => g.ItemCount);
        TotalSizeShown = primaryGroups.Sum(g => g.TotalSizeBytes);

        return primaryGroups;
    }

    /// <summary>按路径分组 — 取顶层目录作为分组键</summary>
    private List<FileGroupNode> BuildGroupByPath(IReadOnlyList<FileDecisionCache> files)
    {
        return files
            .GroupBy(f => GetTopLevelDirectory(f.FilePath))
            .Select(g => new FileGroupNode
            {
                Label = g.Key,
                Icon = "📁",
                ItemCount = g.Count(),
                TotalSizeBytes = g.Sum(f => f.SizeBytes),
                Depth = 0,
                FileCacheKeys = g.Select(f => f.FilePath).ToList()
            })
            .ToList();
    }

    /// <summary>按时间分组 — 今天/7天/30天/90天/更早</summary>
    private List<FileGroupNode> BuildGroupByTime(IReadOnlyList<FileDecisionCache> files)
    {
        var now = DateTime.UtcNow;
        return files
            .GroupBy(f => GetTimeBucket(f.LastWriteTime, now))
            .OrderBy(g => g.Key) // 时间桶按从近到远排序
            .Select(g => new FileGroupNode
            {
                Label = g.Key,
                Icon = GetTimeBucketIcon(g.Key),
                ItemCount = g.Count(),
                TotalSizeBytes = g.Sum(f => f.SizeBytes),
                Depth = 0,
                FileCacheKeys = g.Select(f => f.FilePath).ToList()
            })
            .ToList();
    }

    /// <summary>按大小分组 — &lt;1MB / 1-10MB / 10-100MB / 100MB-1GB / &gt;1GB</summary>
    private List<FileGroupNode> BuildGroupBySize(IReadOnlyList<FileDecisionCache> files)
    {
        return files
            .GroupBy(f => GetSizeBucket(f.SizeBytes))
            .OrderBy(g =>
            {
                // 自定义排序：从小到大
                var firstSize = g.First().SizeBytes;
                return firstSize switch
                {
                    < 1_000_000 => 0,
                    < 10_000_000 => 1,
                    < 100_000_000 => 2,
                    < 1_000_000_000 => 3,
                    _ => 4
                };
            })
            .Select(g => new FileGroupNode
            {
                Label = g.Key,
                Icon = "📦",
                ItemCount = g.Count(),
                TotalSizeBytes = g.Sum(f => f.SizeBytes),
                Depth = 0,
                FileCacheKeys = g.Select(f => f.FilePath).ToList()
            })
            .ToList();
    }

    /// <summary>按类型分组 — 按扩展名聚合，常见类型给友好名称</summary>
    private List<FileGroupNode> BuildGroupByType(IReadOnlyList<FileDecisionCache> files)
    {
        return files
            .GroupBy(f => NormalizeExtension(f.FilePath))
            .Select(g => new FileGroupNode
            {
                Label = GetExtensionDisplayName(g.Key),
                Icon = GetExtensionIcon(g.Key),
                ItemCount = g.Count(),
                TotalSizeBytes = g.Sum(f => f.SizeBytes),
                Depth = 0,
                FileCacheKeys = g.Select(f => f.FilePath).ToList()
            })
            .ToList();
    }

    /// <summary>按语义标签分组 — 使用 SemanticLabelLocalizer 本地化标签名</summary>
    private List<FileGroupNode> BuildGroupBySemanticLabel(IReadOnlyList<FileDecisionCache> files)
    {
        return files
            .GroupBy(f => string.IsNullOrEmpty(f.SemanticCategory)
                ? "Uncategorized"
                : f.SemanticCategory)
            .Select(g =>
            {
                var label = _labelLocalizer.LocalizeFromString(g.Key);
                return new FileGroupNode
                {
                    Label = label,
                    Icon = GetSemanticCategoryIcon(g.Key),
                    ItemCount = g.Count(),
                    TotalSizeBytes = g.Sum(f => f.SizeBytes),
                    Depth = 0,
                    SemanticCategory = g.Key,
                    FileCacheKeys = g.Select(f => f.FilePath).ToList()
                };
            })
            .ToList();
    }

    /// <summary>应用二级分组到文件子集</summary>
    private ObservableCollection<FileGroupNode> ApplySubGrouping(
        IReadOnlyList<FileDecisionCache> files,
        int subMode,
        FileGroupNode parent)
    {
        if (files.Count == 0) return new ObservableCollection<FileGroupNode>();

        var subGroups = subMode switch
        {
            1 => BuildGroupByTime(files),         // 二级按时间
            2 => BuildGroupByType(files),         // 二级按类型
            3 => BuildGroupByFinalAction(files),  // 二级按操作建议
            4 => BuildGroupBySemanticLabel(files),// 二级按语义标签
            _ => new List<FileGroupNode>()
        };

        // 子节点深度为父节点深度+1
        foreach (var child in subGroups)
        {
            child.Depth = parent.Depth + 1;
            child.Parent = parent;
        }

        return new ObservableCollection<FileGroupNode>(
            subGroups.OrderByDescending(g => g.TotalSizeBytes));
    }

    /// <summary>按 FinalAction 分组（用于二级分组）</summary>
    private List<FileGroupNode> BuildGroupByFinalAction(IReadOnlyList<FileDecisionCache> files)
    {
        var actionLabels = new Dictionary<FinalAction, (string Label, string Icon)>
        {
            [FinalAction.AutoDelete] = ("可自动清理", "🟢"),
            [FinalAction.SuggestDelete] = ("建议清理", "💡"),
            [FinalAction.ManualReview] = ("需确认", "⚠️"),
        };

        return files
            .GroupBy(f => f.FinalAction)
            .Where(g => actionLabels.ContainsKey(g.Key))
            .Select(g => new FileGroupNode
            {
                Label = actionLabels[g.Key].Label,
                Icon = actionLabels[g.Key].Icon,
                ItemCount = g.Count(),
                TotalSizeBytes = g.Sum(f => f.SizeBytes),
                Depth = 0,
                FileCacheKeys = g.Select(f => f.FilePath).ToList()
            })
            .ToList();
    }

    // ============================================================
    // 分组辅助方法
    // ============================================================

    /// <summary>从 \\?\ 格式路径中提取顶层目录名</summary>
    private static string GetTopLevelDirectory(string filePath)
    {
        try
        {
            string cleanPath = filePath.StartsWith(@"\\?\") ? filePath[4..] : filePath;
            string? root = Path.GetPathRoot(cleanPath);
            if (root == null) return "(未知)";

            // 取根目录下一级
            string relativePath = cleanPath[root.Length..].TrimStart(Path.DirectorySeparatorChar);
            int nextSep = relativePath.IndexOf(Path.DirectorySeparatorChar);
            return nextSep > 0 ? relativePath[..nextSep] : root.TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return "(未知)";
        }
    }

    /// <summary>根据文件时间返回时间桶标签</summary>
    private static string GetTimeBucket(DateTime lastWriteTime, DateTime now)
    {
        var span = now - lastWriteTime;
        if (span.TotalDays < 1) return "今天";
        if (span.TotalDays < 7) return "最近 7 天";
        if (span.TotalDays < 30) return "最近 30 天";
        if (span.TotalDays < 90) return "30-90 天";
        return "90 天以上";
    }

    private static string GetTimeBucketIcon(string bucket) => bucket switch
    {
        "今天" => "🕐",
        "最近 7 天" => "📅",
        "最近 30 天" => "🗓️",
        "30-90 天" => "📆",
        _ => "🗄️"
    };

    /// <summary>根据文件大小返回大小桶标签</summary>
    private static string GetSizeBucket(long bytes) => bytes switch
    {
        < 1_000_000 => "< 1 MB",
        < 10_000_000 => "1-10 MB",
        < 100_000_000 => "10-100 MB",
        < 1_000_000_000 => "100 MB-1 GB",
        _ => "> 1 GB"
    };

    /// <summary>标准化扩展名（小写，含点号，无扩展名时返回"(无)"）</summary>
    private static string NormalizeExtension(string filePath)
    {
        try
        {
            string ext = Path.GetExtension(filePath);
            return string.IsNullOrEmpty(ext) ? "(无)" : ext.ToLowerInvariant();
        }
        catch
        {
            return "(无)";
        }
    }

    /// <summary>扩展名 → 友好名称映射</summary>
    private static string GetExtensionDisplayName(string ext) => ext switch
    {
        ".log" => "日志文件 (.log)",
        ".tmp" => "临时文件 (.tmp)",
        ".temp" => "临时文件 (.temp)",
        ".dmp" => "内存转储 (.dmp)",
        ".zip" => "压缩包 (.zip)",
        ".rar" => "压缩包 (.rar)",
        ".7z" => "压缩包 (.7z)",
        ".msi" => "安装包 (.msi)",
        ".exe" => "可执行文件 (.exe)",
        ".dll" => "动态链接库 (.dll)",
        ".pdb" => "调试符号 (.pdb)",
        ".cache" => "缓存文件 (.cache)",
        ".bak" => "备份文件 (.bak)",
        ".old" => "旧版本文件 (.old)",
        "(无)" => "无扩展名",
        _ => ext
    };

    private static string GetExtensionIcon(string ext) => ext switch
    {
        ".log" or ".tmp" or ".temp" or ".dmp" => "📋",
        ".zip" or ".rar" or ".7z" => "📦",
        ".msi" or ".exe" or ".dll" => "⚙️",
        ".cache" => "🧹",
        ".bak" or ".old" => "🗄️",
        "(无)" => "📄",
        _ => "📄"
    };

    /// <summary>获取语义分类对应的 Emoji 图标</summary>
    private static string GetSemanticCategoryIcon(string category)
    {
        return category switch
        {
            "SystemTempFiles" or "系统临时文件" => "🗑️",
            "AppCache" or "应用缓存" => "🧹",
            "OldDownloads" or "下载旧文件" => "📥",
            "RecycleBin" or "回收站" => "♻️",
            "LogsAndDumps" or "日志/转储" => "📋",
            "OldUserFiles" or "用户旧文件" => "📁",
            "Protected" or "保护内容" => "🛡️",
            _ => "❓"
        };
    }

    /// <summary>判断文件是否属于指定分组（用于二级分组的文件筛选）</summary>
    private static bool BelongsToGroup(FileDecisionCache file, FileGroupNode group, int primaryMode)
    {
        return primaryMode switch
        {
            0 => GetTopLevelDirectory(file.FilePath) == group.Label,
            1 => GetTimeBucket(file.LastWriteTime, DateTime.UtcNow) == group.Label,
            2 => GetSizeBucket(file.SizeBytes) == group.Label,
            3 => GetExtensionDisplayName(NormalizeExtension(file.FilePath)) == group.Label,
            4 => (string.IsNullOrEmpty(file.SemanticCategory) ? "Uncategorized" : file.SemanticCategory) == group.SemanticCategory,
            _ => false
        };
    }

    // ============================================================
    // 文件列表转换
    // ============================================================

    /// <summary>将 FileDecisionCache 列表转换为 FileListItem UI 模型列表</summary>
    private List<FileListItem> MapToFileListItems(IEnumerable<FileDecisionCache> cacheEntries)
    {
        return cacheEntries.Select(cache =>
        {
            var item = new FileListItem
            {
                FilePath = System.IO.Path.GetFileName(
                    cache.FilePath.StartsWith(@"\\?\") ? cache.FilePath[4..] : cache.FilePath),
                FullPath = cache.FilePath,
                SizeBytes = cache.SizeBytes,
                SizeText = FormatSizeBytes(cache.SizeBytes),
                LastWriteTime = cache.LastWriteTime,
                LastWriteTimeText = cache.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Extension = System.IO.Path.GetExtension(cache.FilePath)?.ToLowerInvariant(),
                SemanticCategory = _labelLocalizer.LocalizeFromString(cache.SemanticCategory),
                SemanticCategoryIcon = GetSemanticCategoryIcon(cache.SemanticCategory),
                FinalAction = cache.FinalAction,
                FinalActionText = GetFinalActionText(cache.FinalAction),
                AiLabel = cache.AiLabel,
                AiConfidence = cache.AiConfidence,
                AiExplanation = cache.AiExplanation,
                CacheKey = cache.FilePath
            };
            item.UpdateAiDisplay();
            return item;
        }).ToList();
    }

    /// <summary>FinalAction → 本地化展示文本</summary>
    private static string GetFinalActionText(FinalAction action) => action switch
    {
        FinalAction.AutoDelete => "可自动清理",
        FinalAction.SuggestDelete => "建议清理",
        FinalAction.ManualReview => "需确认",
        FinalAction.Protected => "受保护",
        _ => "未知"
    };

    // ============================================================
    // 排序下推
    // ============================================================

    /// <summary>
    /// 列头排序命令入口。
    /// 语义标签列 → 内存排序（Everything 不索引语义标签）
    /// 其他列 → 委托 Everything 原生排序
    /// </summary>
    private async void ExecuteSortByColumn(string? columnName)
    {
        if (string.IsNullOrEmpty(columnName)) return;

        _logger.LogDebug("列头排序: Column={Column}, Descending={Desc}", columnName, _isSortDescending);

        try
        {
            // 语义标签列 → 内存排序
            if (columnName == "SemanticCategory")
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var sorted = _isSortDescending
                        ? CurrentFileList.OrderByDescending(f => f.SemanticCategory).ToList()
                        : CurrentFileList.OrderBy(f => f.SemanticCategory).ToList();

                    CurrentFileList.Clear();
                    foreach (var item in sorted)
                    {
                        CurrentFileList.Add(item);
                    }
                });
                return;
            }

            // 其他列 → 尝试下推至 Everything
            if (ColumnSortMap.TryGetValue(columnName, out var sortType))
            {
                // 点击同一列时切换升降序，否则默认升序
                if (CurrentSortColumn == sortType)
                {
                    IsSortDescending = !IsSortDescending;
                }
                else
                {
                    CurrentSortColumn = sortType;
                    IsSortDescending = false;
                }

                await ApplySortAsync(sortType, IsSortDescending, CancellationToken.None);
            }
            else
            {
                _logger.LogWarning("未知排序列: {Column}", columnName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "排序操作失败: Column={Column}", columnName);
        }
    }

    /// <summary>
    /// 应用排序下推。
    /// "显示所有文件"模式 → 向 Everything 发起带排序参数的新查询
    /// 分组视图模式 → 对 FileDecisionCache 子集在内存排序（子集规模可控）
    /// </summary>
    private async Task ApplySortAsync(EverythingSortType sortType, bool descending, CancellationToken ct)
    {
        try
        {
            if (IsShowAllFiles)
            {
                // 显示所有文件模式: 向 Everything 发起排序查询
                var queryParams = new EverythingQueryParams
                {
                    SortType = sortType,
                    SortDescending = descending,
                    MaxResults = 10000 // 限制全量模式下的最大结果数
                };

                var sortedFiles = new List<FileItem>();
                await foreach (var file in _fileProvider.EnumerateFilesAsync(queryParams, ct))
                {
                    sortedFiles.Add(file);
                    if (sortedFiles.Count >= 10000) break;
                }

                // 转换为 UI 模型
                var items = sortedFiles.Select(f =>
                {
                    var item = new FileListItem
                    {
                        FilePath = System.IO.Path.GetFileName(
                            f.FilePath.StartsWith(@"\\?\") ? f.FilePath[4..] : f.FilePath),
                        FullPath = f.FilePath,
                        SizeBytes = f.SizeBytes,
                        SizeText = FormatSizeBytes(f.SizeBytes),
                        LastWriteTime = f.LastWriteTime,
                        LastWriteTimeText = f.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        Extension = f.Extension,
                        EverythingSortKey = f.EverythingSortKey,
                        CacheKey = f.FilePath
                    };
                    item.UpdateAiDisplay();
                    return item;
                }).ToList();

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    CurrentFileList.Clear();
                    foreach (var item in items)
                    {
                        CurrentFileList.Add(item);
                    }
                    TotalFilesShown = items.Count;
                });
            }
            else
            {
                // 分组视图模式: 对当前缓存子集内存排序
                var currentItems = CurrentFileList.ToList();
                var sorted = sortType switch
                {
                    EverythingSortType.Name => descending
                        ? currentItems.OrderByDescending(f => f.FilePath).ToList()
                        : currentItems.OrderBy(f => f.FilePath).ToList(),
                    EverythingSortType.Size => descending
                        ? currentItems.OrderByDescending(f => f.SizeBytes).ToList()
                        : currentItems.OrderBy(f => f.SizeBytes).ToList(),
                    EverythingSortType.DateModified => descending
                        ? currentItems.OrderByDescending(f => f.LastWriteTime).ToList()
                        : currentItems.OrderBy(f => f.LastWriteTime).ToList(),
                    EverythingSortType.Path => descending
                        ? currentItems.OrderByDescending(f => f.FullPath).ToList()
                        : currentItems.OrderBy(f => f.FullPath).ToList(),
                    EverythingSortType.Extension => descending
                        ? currentItems.OrderByDescending(f => f.Extension).ToList()
                        : currentItems.OrderBy(f => f.Extension).ToList(),
                    _ => currentItems
                };

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    CurrentFileList.Clear();
                    foreach (var item in sorted)
                    {
                        CurrentFileList.Add(item);
                    }
                });
            }

            _logger.LogDebug("排序完成: SortType={Sort}, Descending={Desc}", sortType, descending);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("排序操作被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "排序下推失败: SortType={Sort}", sortType);
        }
    }

    // ============================================================
    // 显示所有文件切换
    // ============================================================

    /// <summary>
    /// 切换"显示所有文件"开关。
    /// ON: 隐藏分组树，通过 Everything 直接查询全量文件（平面列表）
    /// OFF: 恢复分组视图，从缓存重建分组树
    /// </summary>
    private async Task HandleShowAllToggleAsync(bool showAll, CancellationToken ct)
    {
        _logger.LogInformation("显示模式切换: ShowAll={ShowAll}", showAll);

        try
        {
            if (showAll)
            {
                // 切换到全量平面列表模式
                await LoadAllFilesFromEverythingAsync(ct);
            }
            else
            {
                // 恢复到分组视图模式
                await RebuildGroupTreeAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("显示模式切换被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "显示模式切换失败: ShowAll={ShowAll}", showAll);
        }
    }

    /// <summary>
    /// 从 Everything 加载全量文件（平面列表模式）。
    /// 绕过规则引擎和决策引擎，直接获取文件元数据。
    /// </summary>
    private async Task LoadAllFilesFromEverythingAsync(CancellationToken ct)
    {
        IsLoading = true;
        try
        {
            // 构建查询参数（带当前排序设置）
            var queryParams = new EverythingQueryParams
            {
                SortType = CurrentSortColumn ?? EverythingSortType.Name,
                SortDescending = IsSortDescending,
                MaxResults = 10000 // 全量模式最大 10000 条
            };

            var files = new List<FileItem>();
            await foreach (var file in _fileProvider.EnumerateFilesAsync(queryParams, ct))
            {
                files.Add(file);
                if (files.Count >= 10000) break;
            }

            // 转换为 UI 模型（全量模式未经过规则引擎裁决，统一标记为"未评估"）
            var items = files.Select(f =>
            {
                var item = new FileListItem
                {
                    FilePath = System.IO.Path.GetFileName(
                        f.FilePath.StartsWith(@"\\?\") ? f.FilePath[4..] : f.FilePath),
                    FullPath = f.FilePath,
                    SizeBytes = f.SizeBytes,
                    SizeText = FormatSizeBytes(f.SizeBytes),
                    LastWriteTime = f.LastWriteTime,
                    LastWriteTimeText = f.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    Extension = f.Extension,
                    EverythingSortKey = f.EverythingSortKey,
                    CacheKey = f.FilePath,
                    // 全量模式下文件未经过规则引擎和决策引擎裁决，
                    // 显式标记为 ManualReview 避免误导用户（非 AutoDelete）
                    FinalAction = FinalAction.ManualReview,
                    FinalActionText = "未评估",
                    SemanticCategory = "—",
                    SemanticCategoryIcon = "📄"
                };
                item.UpdateAiDisplay();
                return item;
            }).ToList();

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentFileList.Clear();
                foreach (var item in items)
                {
                    CurrentFileList.Add(item);
                }
                TotalFilesShown = items.Count;
                HasData = items.Count > 0;
                IsEmpty = !HasData;
            });

            _logger.LogInformation("全量文件加载完成: {Count} 项", files.Count);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ============================================================
    // 分组选中
    // ============================================================

    /// <summary>
    /// 用户点击分组树节点后，过滤 DataGrid 显示该分组下的文件。
    /// 叶子节点直接使用 FileCacheKeys 过滤；父节点递归收集所有子节点的文件。
    /// </summary>
    private async Task OnGroupSelectedAsync(FileGroupNode group, CancellationToken ct)
    {
        try
        {
            var fileKeys = CollectFileKeys(group);

            if (fileKeys.Count == 0)
            {
                await RunOnUIThreadAsync(() => CurrentFileList.Clear());
                return;
            }

            // 从缓存中查找对应文件
            var matchedFiles = _allActionableCache
                .Where(f => fileKeys.Contains(f.FilePath))
                .ToList();

            var items = await Task.Run(() => MapToFileListItems(matchedFiles), ct);

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentFileList.Clear();
                foreach (var item in items)
                {
                    CurrentFileList.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "分组选中处理失败: Group={Group}", group.Label);
        }
    }

    /// <summary>递归收集节点及其子节点的所有文件键</summary>
    private static HashSet<string> CollectFileKeys(FileGroupNode node)
    {
        var keys = new HashSet<string>(node.FileCacheKeys);
        foreach (var child in node.Children)
        {
            keys.UnionWith(CollectFileKeys(child));
        }
        return keys;
    }

    // ============================================================
    // 增量刷新（Everything FileChanged 事件处理）
    // ============================================================

    /// <summary>
    /// Everything 文件变更事件处理器。
    /// 采用节流策略：500ms 内的事件累积后批量处理，避免高频 UI 刷新。
    /// </summary>
    private void OnFileChanged(object? sender, FileChangedEventArgs e)
    {
        try
        {
            // 记录待处理变更，合并策略：Deleted 优先级最高
            // （若同一文件短时间内多次变更，以 Deleted 为准）
            _pendingFileChanges.AddOrUpdate(e.FilePath, e.ChangeType,
                (_, existing) => existing == FileChangeType.Deleted
                    ? FileChangeType.Deleted
                    : e.ChangeType);

            // 节流：500ms 后批量处理（重置定时器）
            _throttleTimer?.Dispose();
            _throttleTimer = new Timer(async _ =>
            {
                await ProcessPendingChangesAsync();
            }, null, 500, Timeout.Infinite);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FileChanged 事件处理异常: Path={Path}", e.FilePath);
        }
    }

    /// <summary>
    /// 批量处理累积的文件变更。
    /// 对每个变更文件：判断是否在当前列表中 → 更新或移除对应行。
    /// </summary>
    private async Task ProcessPendingChangesAsync()
    {
        Dictionary<string, FileChangeType> pendingSnapshot;
        lock (_pendingFileChanges)
        {
            pendingSnapshot = new Dictionary<string, FileChangeType>(_pendingFileChanges);
            _pendingFileChanges.Clear();
        }

        if (pendingSnapshot.Count == 0) return;

        _logger.LogDebug("批量处理文件变更: {Count} 项", pendingSnapshot.Count);

        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var kvp in pendingSnapshot)
                {
                    var filePath = kvp.Key;
                    var changeType = kvp.Value;

                    // 处理文件删除：从当前列表中移除
                    if (changeType == FileChangeType.Deleted)
                    {
                        var toRemove = CurrentFileList
                            .Where(f => f.FullPath == filePath || f.CacheKey == filePath)
                            .ToList();
                        foreach (var item in toRemove)
                        {
                            CurrentFileList.Remove(item);
                        }
                        _logger.LogDebug("增量刷新-移除: {Path}", filePath);
                        continue;
                    }

                    // 处理文件修改/创建/重命名：在列表中查找并更新
                    var existingItem = CurrentFileList.FirstOrDefault(
                        f => f.FullPath == filePath || f.CacheKey == filePath);

                    if (existingItem != null)
                    {
                        // 文件已存在于列表 → 在缓存中查找更新
                        var cacheEntry = _allActionableCache.FirstOrDefault(
                            c => c.FilePath == filePath);
                        if (cacheEntry != null)
                        {
                            existingItem.SizeBytes = cacheEntry.SizeBytes;
                            existingItem.SizeText = FormatSizeBytes(cacheEntry.SizeBytes);
                            existingItem.LastWriteTime = cacheEntry.LastWriteTime;
                            existingItem.LastWriteTimeText =
                                cacheEntry.LastWriteTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                            existingItem.FinalAction = cacheEntry.FinalAction;
                            existingItem.FinalActionText = GetFinalActionText(cacheEntry.FinalAction);
                            existingItem.SemanticCategory =
                                _labelLocalizer.LocalizeFromString(cacheEntry.SemanticCategory);
                            existingItem.SemanticCategoryIcon =
                                GetSemanticCategoryIcon(cacheEntry.SemanticCategory);
                            existingItem.UpdateAiDisplay();
                            _logger.LogDebug("增量刷新-更新: {Path}, Type={ChangeType}", filePath, changeType);
                        }
                    }
                    // 注：Created 场景下文件不在 CurrentFileList 中，暂不自动添加。
                    // 新增文件需通过重新扫描发现（由用户手动刷新或规则热加载触发）。
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量处理文件变更失败");
        }
    }

    // ============================================================
    // 命令实现
    // ============================================================

    private void ExecuteSelectGroup(FileGroupNode? group)
    {
        if (group != null)
        {
            SelectedGroup = group;
        }
    }

    private async Task ExecuteRefreshAsync()
    {
        _logger.LogMethodEntry("手动刷新文件列表");
        await LoadDataAsync();
    }

    private void ExecuteExcludeFile(FileListItem? item)
    {
        if (item == null) return;
        _logger.LogInformation("排除文件: {Path}", item.FullPath);
        // TODO: C4 阶段实现排除管理器集成
        MessageBox.Show($"文件已排除: {item.FilePath}", "排除文件", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExecuteViewDetail(FileListItem? item)
    {
        if (item == null) return;
        _logger.LogDebug("查看详情: {Path}", item.FullPath);
        // TODO: C3 阶段实现详情面板/弹窗
        var aiInfo = item.AiExplanation != null ? $"\nAI 分析: {item.AiExplanation}" : "\nAI 分析: 尚未分析";
        MessageBox.Show(
            $"文件: {item.FilePath}\n完整路径: {item.FullPath}\n大小: {item.SizeText}\n修改日期: {item.LastWriteTimeText}\n语义标签: {item.SemanticCategory}\n操作建议: {item.FinalActionText}{aiInfo}",
            "文件详情",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ExecuteToggleFileCheck(FileListItem? item)
    {
        if (item == null) return;
        item.IsChecked = !item.IsChecked;
    }

    private async void ExecuteRequestAiAnalysis(FileListItem? item)
    {
        if (item == null) return;

        _logger.LogInformation("请求 AI 分析: {Path}", item.FullPath);

        // 设置 analyzing 状态
        item.AiLabel = "analyzing";
        item.UpdateAiDisplay();

        try
        {
            var result = await _aiCoordinator.AnalyzeSingleAsync(item.CacheKey);

            if (result?.IsSuccess == true)
            {
                item.AiLabel = result.Label;
                item.AiConfidence = result.Confidence;
                item.AiExplanation = result.Explanation;
                _logger.LogInformation("AI 单文件分析成功: {Path} → {Label} (置信度={Confidence:P})",
                    item.FullPath, result.Label, result.Confidence);
            }
            else
            {
                // 分析失败或 AI 不可用 → 重置状态
                item.AiLabel = null;
                item.AiConfidence = null;
                item.AiExplanation = null;
                _logger.LogWarning("AI 单文件分析失败: {Path}, 错误={Error}",
                    item.FullPath, result?.ErrorMessage ?? "服务不可用");
            }
        }
        catch (Exception ex)
        {
            item.AiLabel = null;
            _logger.LogError(ex, "AI 单文件分析异常: {Path}", item.FullPath);
        }
        finally
        {
            item.UpdateAiDisplay();
        }
    }

    /// <summary>
    /// 批量分析已勾选文件（工具栏按钮触发）。
    /// </summary>
    private async Task ExecuteAnalyzeSelectedFilesAsync()
    {
        // selectedItems 需要在 try 和 catch 中都可见，用于异常后重置状态
        List<FileListItem>? selectedItems = null;

        try
        {
            // 收集所有勾选的文件
            selectedItems = CurrentFileList.Where(f => f.IsChecked).ToList();
            if (selectedItems.Count == 0)
            {
                _logger.LogInformation("没有勾选的文件，跳过批量 AI 分析");
                return;
            }

            _logger.LogInformation("批量 AI 分析: 选中 {Count} 个文件", selectedItems.Count);

            // 从缓存中查找对应的 FileDecisionCache
            var cacheEntries = _allActionableCache
                .Where(c => selectedItems.Any(s => s.CacheKey == c.FilePath))
                .ToList();

            if (cacheEntries.Count == 0)
            {
                _logger.LogWarning("未在缓存中找到勾选文件的决策记录");
                return;
            }

            // 标记所有选中行为 analyzing 状态
            foreach (var item in selectedItems)
            {
                item.AiLabel = "analyzing";
                item.UpdateAiDisplay();
            }

            // 调用协调器批量分析
            var results = await _aiCoordinator.AnalyzeBatchAsync(cacheEntries, CancellationToken.None);

            // 结果回写到 UI 行
            foreach (var result in results)
            {
                var item = CurrentFileList.FirstOrDefault(f => f.CacheKey == result.FilePath);
                if (item != null)
                {
                    item.AiLabel = result.Label;
                    item.AiConfidence = result.Confidence;
                    item.AiExplanation = result.Explanation;
                    item.UpdateAiDisplay();
                }
            }

            // 重置未返回结果的文件状态（防御性：确保 stuck "analyzing" 不会残留）
            foreach (var item in selectedItems)
            {
                if (item.AiLabel == "analyzing" &&
                    results.All(r => r.FilePath != item.CacheKey))
                {
                    item.AiLabel = null;
                    item.UpdateAiDisplay();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量 AI 分析异常");
            // 异常发生后重置所有 stuck 在 "analyzing" 状态的行
            // selectedItems 可能因初始化异常为 null
            if (selectedItems is { Count: > 0 })
            {
                foreach (var item in selectedItems)
                {
                    if (item.AiLabel == "analyzing")
                    {
                        item.AiLabel = null;
                        item.UpdateAiDisplay();
                    }
                }
            }
        }
    }

    private void ExecuteCancelAiAnalysis()
    {
        _logger.LogMethodEntry("用户取消 AI 分析");
        _aiCoordinator.CancelCurrentAnalysis();
    }

    // ============================================================
    // AI 协调器事件回调
    // ============================================================

    private void OnAiProgressChanged(object? sender, AiAnalysisProgress progress)
    {
        RunOnUIThreadAsync(() =>
        {
            AiProgressText = $"AI 分析中… {progress.CompletedCount}/{progress.TotalCount}";
            IsAiAnalyzing = progress.CompletedCount < progress.TotalCount;
            IsAiAnalysisCancellable = !progress.IsCancelRequested;
            OnPropertyChanged(nameof(AiProgressPercentage));

            // 更新当前正在分析的行状态
            // 注意：当前 ProgressChanged 暂未推送 per-file 路径（CurrentFilePath 为 null），
            // 此处为将来按文件粒度标记 "analyzing" 状态的预留代码。
            // 当前 per-file 标记逻辑在 ExecuteAnalyzeSelectedFilesAsync 中统一处理。
            if (progress.CurrentFilePath != null)
            {
                var item = CurrentFileList.FirstOrDefault(f => f.CacheKey == progress.CurrentFilePath);
                if (item != null && item.AiLabel != "analyzing")
                {
                    item.AiLabel = "analyzing";
                    item.UpdateAiDisplay();
                }
            }
        });
    }

    private async void OnAiAnalysisCompleted(object? sender, AiAnalysisCompletedEventArgs e)
    {
        await RunOnUIThreadAsync(() =>
        {
            IsAiAnalyzing = false;
            AiProgressText = e.WasCancelled
                ? "AI 分析已取消"
                : $"AI 分析完成：成功 {e.SuccessCount} 个"
                  + (e.FailedCount > 0 ? $"，失败 {e.FailedCount} 个" : "");
        });

        // 刷新文件列表（AI 标签变更可能改变分组归属）
        _ = LoadDataAsync();
    }

    // ============================================================
    // 全局状态响应
    // ============================================================

    /// <summary>
    /// 响应全局 AppState 变更。
    /// 当扫描完成（Ready）时自动刷新文件列表。
    /// </summary>
    private void OnAppStateChanged(object? sender, AppState newState)
    {
        if (newState == AppState.Ready && _allActionableCache.Count == 0)
        {
            _ = LoadDataAsync();
        }
    }

    // ============================================================
    // 工具方法
    // ============================================================

    /// <summary>格式化字节数为可读大小字符串</summary>
    private static string FormatSizeBytes(long bytes)
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

    // ============================================================
    // ============================================================
    // UI 线程调度辅助（兼容单元测试场景）
    // ============================================================

    /// <summary>
    /// 在 UI 线程上执行操作。若 Application.Current 不可用（单元测试场景），
    /// 则直接在当前线程同步执行。参照 QuarantineViewModel 的 Dispatcher 安全模式。
    /// </summary>
    private async Task RunOnUIThreadAsync(Action action)
    {
        if (Application.Current?.Dispatcher != null)
            await Application.Current.Dispatcher.InvokeAsync(action);
        else
            action();
    }

    // ============================================================

    // IDisposable
    // ============================================================

    /// <summary>
    /// 释放资源：取消订阅事件，清理定时器和取消令牌。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fileProvider.FileChanged -= OnFileChanged;
        _appStateService.StateChanged -= OnAppStateChanged;
        _aiCoordinator.ProgressChanged -= OnAiProgressChanged;
        _aiCoordinator.AnalysisCompleted -= OnAiAnalysisCompleted;

        _throttleTimer?.Dispose();
        _throttleTimer = null;
        _currentLoadCts?.Cancel();
        _currentLoadCts?.Dispose();
        _currentLoadCts = null;

        _logger.LogDebug("FileListViewModel 已释放");
    }
}
