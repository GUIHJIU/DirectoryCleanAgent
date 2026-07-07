using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Localization;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Models;
using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.ViewModels;

/// <summary>
/// 设置窗口 ViewModel。
/// 核心设计：操作 UserConfig 的深拷贝副本（_editConfig），仅在用户确认（OK/Apply）时同步回 IConfigService 并持久化。
/// 取消时直接丢弃副本，确保不会产生部分修改。
/// 语言和主题修改实时生效（无需点确认即可预览效果），其余设置需确认后生效。
/// </summary>
public class SettingsViewModel : ViewModelBase, IDisposable
{
    // ============================================================
    // 依赖注入
    // ============================================================

    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IConfigService _configService;
    private readonly ILocalizationService _localization;

    /// <summary>当前编辑的配置副本（深拷贝），所有 UI 绑定均指向此副本的属性</summary>
    private readonly UserConfig _editConfig;

    /// <summary>防止重复保存的标志</summary>
    private bool _isSaving;

    // ============================================================
    // 构造函数
    // ============================================================

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        IConfigService configService,
        ILocalizationService localization)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));

        // 深拷贝当前配置，所有编辑操作在此副本上进行
        _editConfig = _configService.Current.Clone();

        // 初始化集合
        IncludedVolumes = new ObservableCollection<VolumeItem>();
        UserExcludedDirs = new ObservableCollection<string>();
        ProtectedDirs = new ObservableCollection<string>();

        // 初始化 ComboBox 选项源（本地化文本）
        RebuildComboOptions();

        // 初始化命令
        OKCommand = new RelayCommand(ExecuteOK);
        CancelCommand = new RelayCommand(ExecuteCancel);
        ApplyCommand = new RelayCommand(ExecuteApply, () => !_isSaving);
        BrowseDataPathCommand = new RelayCommand(ExecuteBrowseDataPath);
        AddExcludedDirCommand = new RelayCommand(ExecuteAddExcludedDir, CanExecuteAddExcludedDir);
        RemoveExcludedDirCommand = new RelayCommand<string>(ExecuteRemoveExcludedDir);
        ResetToDefaultsCommand = new RelayCommand(ExecuteResetToDefaults);

        // 从 _editConfig 加载数据到 ViewModel 属性
        LoadFromEditConfig();

        // 加载 NTFS 固定卷列表
        LoadVolumes();

        // 订阅语言变更事件，以便语言切换时刷新所有本地化标签
        _localization.LanguageChanged += OnLanguageChanged;

        _logger.LogMethodEntry("SettingsViewModel 构造完成");
    }

    // ============================================================
    // 事件（供 SettingsWindow 代码后台订阅）
    // ============================================================

    /// <summary>用户点击确定：窗口应设置 DialogResult=true 并关闭</summary>
    public event EventHandler? RequestCloseWithOK;

    /// <summary>用户点击取消：窗口应设置 DialogResult=false 并关闭</summary>
    public event EventHandler? RequestCloseWithCancel;

    /// <summary>用户点击应用：配置已保存，窗口不关闭（可用于状态栏提示）</summary>
    public event EventHandler? SettingsApplied;

    // ============================================================
    // === Tab 1: 通用设置 ===
    // ============================================================

    /// <summary>启动行为 ComboBox 选项列表</summary>
    public List<ComboItem> StartupBehaviorOptions { get; private set; } = new();

    private StartupBehavior _editStartupBehavior;
    /// <summary>编辑中的启动行为</summary>
    public StartupBehavior EditStartupBehavior
    {
        get => _editStartupBehavior;
        set => SetProperty(ref _editStartupBehavior, value);
    }

    /// <summary>主题 ComboBox 选项列表</summary>
    public List<ComboItem> ThemeOptions { get; private set; } = new();

    private ThemeType _editTheme;
    /// <summary>编辑中的主题（修改时实时应用，不等待确认）</summary>
    public ThemeType EditTheme
    {
        get => _editTheme;
        set
        {
            if (SetProperty(ref _editTheme, value))
            {
                // 实时应用主题，用户可立即预览效果
                ApplyThemeImmediately(value);
            }
        }
    }

    /// <summary>语言 ComboBox 选项列表</summary>
    public List<ComboItem> LanguageOptions { get; private set; } = new();

    private string _editLanguage = "zh-CN";
    /// <summary>编辑中的语言代码（修改时实时切换，不等待确认）</summary>
    public string EditLanguage
    {
        get => _editLanguage;
        set
        {
            if (SetProperty(ref _editLanguage, value) && !string.IsNullOrEmpty(value))
            {
                // 实时切换语言，UI 文本立即刷新
                _localization.SetLanguage(value);
                RebuildComboOptions();
                RefreshAllLocalizedLabels();
            }
        }
    }

    private string _editDataStoragePath = "";
    /// <summary>编辑中的数据存储路径</summary>
    public string EditDataStoragePath
    {
        get => _editDataStoragePath;
        set => SetProperty(ref _editDataStoragePath, value);
    }

    // ============================================================
    // === Tab 2: 扫描与清理 ===
    // ============================================================

    /// <summary>扫描模式 ComboBox 选项列表</summary>
    public List<ComboItem> ScanModeOptions { get; private set; } = new();

    private ScanMode _editScanMode;
    /// <summary>编辑中的扫描模式</summary>
    public ScanMode EditScanMode
    {
        get => _editScanMode;
        set => SetProperty(ref _editScanMode, value);
    }

    /// <summary>删除方式 ComboBox 选项列表</summary>
    public List<ComboItem> DeleteMethodOptions { get; private set; } = new();

    private DeleteMethod _editDeleteMethod;
    /// <summary>编辑中的删除方式</summary>
    public DeleteMethod EditDeleteMethod
    {
        get => _editDeleteMethod;
        set => SetProperty(ref _editDeleteMethod, value);
    }

    private bool _editConfirmBeforeClean;
    /// <summary>一键清理前是否弹出二次确认</summary>
    public bool EditConfirmBeforeClean
    {
        get => _editConfirmBeforeClean;
        set => SetProperty(ref _editConfirmBeforeClean, value);
    }

    private bool _editShowManualReview;
    /// <summary>是否显示"需人工确认"类别的文件</summary>
    public bool EditShowManualReview
    {
        get => _editShowManualReview;
        set => SetProperty(ref _editShowManualReview, value);
    }

    private bool _editAutoRefresh;
    /// <summary>是否自动刷新扫描结果</summary>
    public bool EditAutoRefresh
    {
        get => _editAutoRefresh;
        set => SetProperty(ref _editAutoRefresh, value);
    }

    private int _editMaxScanFiles = 100_000;
    /// <summary>单次扫描最大文件数，0=不限制</summary>
    public int EditMaxScanFiles
    {
        get => _editMaxScanFiles;
        set => SetProperty(ref _editMaxScanFiles, Math.Max(0, value));
    }

    private int _editHashConcurrency;
    /// <summary>SHA-256 哈希并发度，0=自动（使用 CPU 核心数）</summary>
    public int EditHashConcurrency
    {
        get => _editHashConcurrency;
        set => SetProperty(ref _editHashConcurrency, Math.Clamp(value, 0, 32));
    }

    /// <summary>磁盘卷复选框集合（仅 NTFS 固定卷）</summary>
    public ObservableCollection<VolumeItem> IncludedVolumes { get; }

    // ============================================================
    // === Tab 3: AI 顾问 ===
    // ============================================================

    private bool _editAIEnabled;
    /// <summary>是否启用 AI 顾问</summary>
    public bool EditAIEnabled
    {
        get => _editAIEnabled;
        set => SetProperty(ref _editAIEnabled, value);
    }

    /// <summary>AI 服务类型 ComboBox 选项列表</summary>
    public List<ComboItem> AIServiceTypeOptions { get; private set; } = new();

    private string _editAIServiceType = "custom";
    /// <summary>编辑中的 AI 服务类型</summary>
    public string EditAIServiceType
    {
        get => _editAIServiceType;
        set => SetProperty(ref _editAIServiceType, value);
    }

    private string _editAIApiUrl = "";
    /// <summary>编辑中的 AI API 地址</summary>
    public string EditAIApiUrl
    {
        get => _editAIApiUrl;
        set => SetProperty(ref _editAIApiUrl, value);
    }

    private string _editAIApiKey = "";
    /// <summary>编辑中的 AI API Key（内存中明文，持久化后存储在配置文件中）</summary>
    public string EditAIApiKey
    {
        get => _editAIApiKey;
        set => SetProperty(ref _editAIApiKey, value);
    }

    private string _editAIModel = "gpt-3.5-turbo";
    /// <summary>编辑中的 AI 模型名称</summary>
    public string EditAIModel
    {
        get => _editAIModel;
        set => SetProperty(ref _editAIModel, value);
    }

    /// <summary>AI 信任等级 ComboBox 选项列表</summary>
    public List<ComboItem> AITrustLevelOptions { get; private set; } = new();

    private AITrustLevel _editAITrustLevel;
    /// <summary>编辑中的 AI 信任等级</summary>
    public AITrustLevel EditAITrustLevel
    {
        get => _editAITrustLevel;
        set => SetProperty(ref _editAITrustLevel, value);
    }

    private int _editAIDailyLimit = 50;
    /// <summary>编辑中的 AI 每日调用限额</summary>
    public int EditAIDailyLimit
    {
        get => _editAIDailyLimit;
        set => SetProperty(ref _editAIDailyLimit, value);
    }

    private bool _editAIAutoAnalyze;
    /// <summary>是否自动为未分类文件请求 AI 分析（仅专家模式）</summary>
    public bool EditAIAutoAnalyze
    {
        get => _editAIAutoAnalyze;
        set => SetProperty(ref _editAIAutoAnalyze, value);
    }

    // ============================================================
    // === Tab 5: 排除与保护 ===
    // ============================================================

    /// <summary>自动保护目录列表（只读，系统/硬规则保护目录）</summary>
    public ObservableCollection<string> ProtectedDirs { get; }

    /// <summary>用户手动添加的排除目录列表（可增删）</summary>
    public ObservableCollection<string> UserExcludedDirs { get; }

    private string _newExcludedDir = "";
    /// <summary>新增排除目录输入框文本</summary>
    public string NewExcludedDir
    {
        get => _newExcludedDir;
        set
        {
            if (SetProperty(ref _newExcludedDir, value))
                AddExcludedDirCommand.RaiseCanExecuteChanged();
        }
    }

    private string _excludedExtensionsText = "";
    /// <summary>排除文件扩展名文本框（逗号分隔，如 ".log, .tmp"）</summary>
    public string ExcludedExtensionsText
    {
        get => _excludedExtensionsText;
        set => SetProperty(ref _excludedExtensionsText, value);
    }

    /// <summary>当前是否为专家模式（用于控制专家选项可见性）</summary>
    public bool IsExpertMode => _editConfig.UserMode == UserMode.Expert;

    /// <summary>隔离区大小单位 ComboBox 选项列表（MB / GB）</summary>
    public List<ComboItem> QuarantineSizeUnitOptions { get; private set; } = new();

    private double _quarantineSizeValue = 10;
    /// <summary>隔离区大小数值</summary>
    public double QuarantineSizeValue
    {
        get => _quarantineSizeValue;
        set => SetProperty(ref _quarantineSizeValue, value);
    }

    private int _quarantineSizeUnitIndex; // 0=MB, 1=GB
    /// <summary>隔离区大小单位索引（0=MB, 1=GB）</summary>
    public int QuarantineSizeUnitIndex
    {
        get => _quarantineSizeUnitIndex;
        set => SetProperty(ref _quarantineSizeUnitIndex, value);
    }

    private int _editQuarantineRetentionDays = 30;
    /// <summary>编辑中的隔离区保留天数</summary>
    public int EditQuarantineRetentionDays
    {
        get => _editQuarantineRetentionDays;
        set => SetProperty(ref _editQuarantineRetentionDays, value);
    }

    // ============================================================
    // === 本地化显示字符串（通过 ILocalizationService 获取） ===
    // ============================================================

    public string LblTabGeneral => GetLoc("Settings.Tab.General");
    public string LblTabScanClean => GetLoc("Settings.Tab.ScanAndClean");
    public string LblTabAI => GetLoc("Settings.Tab.AIAdvisor");
    public string LblTabAutomation => GetLoc("Settings.Tab.Automation");
    public string LblTabExclusion => GetLoc("Settings.Tab.Exclusions");
    public string LblTabVSS => GetLoc("Settings.Tab.VSS");
    public string LblBtnOK => GetLoc("Common.OK");
    public string LblBtnCancel => GetLoc("Common.Cancel");
    public string LblBtnApply => GetLoc("Common.Apply");
    public string LblAutomationComingSoon => GetLoc("App.ComingSoon");
    public string LblStartupBehavior => GetLoc("Settings.General.StartupBehavior");
    public string LblTheme => GetLoc("Settings.General.Theme");
    public string LblLanguage => GetLoc("Settings.General.Language");
    public string LblDataPath => GetLoc("Settings.General.DataStorage");
    public string LblScanMode => GetLoc("Settings.Scan.ScanMode");
    public string LblDeleteMethod => GetLoc("Settings.Scan.DeleteMethod");
    public string LblConfirmBeforeClean => GetLoc("Settings.Scan.ConfirmBeforeClean");
    public string LblShowManualReview => GetLoc("Settings.Scan.ShowManualReview");
    public string LblAutoRefresh => GetLoc("Settings.Scan.AutoRefresh");
    public string LblIncludedVolumes => GetLoc("Settings.Scan.IncludedVolumes");
    public string LblAIEnable => GetLoc("Settings.AI.Enable");
    public string LblAIServiceType => GetLoc("Settings.AI.ServiceType");
    public string LblAIApiUrl => GetLoc("Settings.AI.ApiUrl");
    public string LblAIApiKey => GetLoc("Settings.AI.ApiKey");
    public string LblAIModel => GetLoc("Settings.AI.Model");
    public string LblAITrustLevel => GetLoc("Settings.AI.TrustLevel");
    public string LblAIDailyLimit => GetLoc("Settings.AI.DailyLimit");
    public string LblProtectedDirs => GetLoc("Settings.Exclusions.ProtectedDirs");
    public string LblUserExcludedDirs => GetLoc("Settings.Exclusions.UserExcludedDirs");
    public string LblExcludedExtensions => GetLoc("Settings.Exclusions.ExcludedExtensions");
    public string LblQuarantineSize => GetLoc("Settings.Exclusions.QuarantineMaxSize");
    public string LblQuarantineRetention => GetLoc("Settings.Exclusions.QuarantineRetention");
    public string LblMaxScanFiles => GetLoc("Settings.Scan.MaxScanFiles");
    public string LblHashConcurrency => GetLoc("Settings.Scan.HashConcurrency");
    public string LblAdvancedOptions => GetLoc("Settings.Scan.AdvancedOptions");
    public string LblNtfsOnlyHint => GetLoc("Settings.Scan.NtfsOnlyHint");
    public string LblNoNtfsWarning => GetLoc("Settings.Scan.NoNtfsWarning");
    public string LblAddButton => GetLoc("Settings.Exclusions.AddButton");
    public string LblWildcardHint => GetLoc("Settings.Exclusions.WildcardHint");
    public string LblExtensionHint => GetLoc("Settings.Exclusions.ExtensionHint");
    public string LblQuarantineSizeDefault => GetLoc("Settings.Exclusions.QuarantineSizeDefault");
    public string LblRetentionHint => GetLoc("Settings.Exclusions.RetentionHint");
    public string LblAutomationDescription => GetLoc("Settings.Automation.Description");
    public string LblVSSDescription => GetLoc("Settings.VSS.Description");
    public string LblButtonBrowse => GetLoc("Settings.Button.Browse");
    public string LblButtonReset => GetLoc("Settings.Button.Reset");
    public string LblButtonResetTooltip => GetLoc("Settings.Button.ResetTooltip");
    public string LblWindowTitle => GetLoc("Settings.Title");

    /// <summary>带回退的本地化字符串获取</summary>
    private string GetLoc(string key) => _localization.GetString(key);

    /// <summary>刷新所有 Lbl* 属性的绑定</summary>
    private void RefreshAllLocalizedLabels()
    {
        foreach (var prop in typeof(SettingsViewModel).GetProperties()
            .Where(p => p.Name.StartsWith("Lbl")))
        {
            OnPropertyChanged(prop.Name);
        }
    }

    // ============================================================
    // 命令属性
    // ============================================================

    public RelayCommand OKCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand BrowseDataPathCommand { get; }
    public RelayCommand AddExcludedDirCommand { get; }
    public RelayCommand<string> RemoveExcludedDirCommand { get; }
    public RelayCommand ResetToDefaultsCommand { get; }

    // ============================================================
    // 命令执行方法
    // ============================================================

    /// <summary>
    /// 确定按钮：保存配置到 IConfigService + 持久化，关闭窗口。
    /// 若保存失败则弹出错误提示，窗口不关闭。
    /// </summary>
    private async void ExecuteOK()
    {
        _logger.LogMethodEntry("用户点击【确定】");
        try
        {
            await SaveChangesAsync();
            RequestCloseWithOK?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("设置已保存，窗口关闭");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设置失败");
            MessageBox.Show(
                $"保存设置失败:\n{ex.Message}",
                "保存错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 取消按钮：放弃所有修改，不保存，关闭窗口。
    /// </summary>
    private void ExecuteCancel()
    {
        _logger.LogMethodEntry("用户点击【取消】，放弃修改");
        try
        {
            // 语言可能在编辑期间已被实时切换，这里不做回退（语言是实时生效的）
            RequestCloseWithCancel?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取消操作异常");
        }
    }

    /// <summary>
    /// 应用按钮：保存配置但不关闭窗口。
    /// 可多次点击以增量保存。
    /// </summary>
    private async void ExecuteApply()
    {
        _logger.LogMethodEntry("用户点击【应用】");
        try
        {
            await SaveChangesAsync();
            SettingsApplied?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation("设置已应用（窗口保持打开）");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用设置失败");
            MessageBox.Show(
                $"应用设置失败:\n{ex.Message}",
                "应用错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 浏览数据存储目录：当前版本提供一个提示说明，用户可直接在文本框中输入或粘贴路径。
    /// 未来可用 Windows Shell COM IFileOpenDialog 实现原生文件夹选择对话框。
    /// </summary>
    private void ExecuteBrowseDataPath()
    {
        _logger.LogMethodEntry("用户点击浏览数据路径");
        try
        {
            MessageBox.Show(
                "请直接在文本框中输入或粘贴数据存储路径。\n\n" +
                $"当前路径: {EditDataStoragePath}\n\n" +
                "推荐使用默认路径（%LocalAppData%\\SpaceClear）。",
                "数据存储路径",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "浏览数据路径失败");
        }
    }

    /// <summary>
    /// 添加排除目录：将 NewExcludedDir 输入框内容添加到列表（去重）。
    /// </summary>
    private void ExecuteAddExcludedDir()
    {
        _logger.LogMethodEntry("用户添加排除目录");
        try
        {
            var dir = NewExcludedDir?.Trim();
            if (string.IsNullOrEmpty(dir))
                return;

            if (!UserExcludedDirs.Contains(dir, StringComparer.OrdinalIgnoreCase))
            {
                UserExcludedDirs.Add(dir);
                NewExcludedDir = string.Empty;
                _logger.LogInformation("已添加排除目录: {Dir}", dir);
            }
            else
            {
                _logger.LogWarning("排除目录已存在，跳过重复添加: {Dir}", dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加排除目录失败");
        }
    }

    private bool CanExecuteAddExcludedDir() => !string.IsNullOrWhiteSpace(NewExcludedDir);

    /// <summary>
    /// 删除排除目录：从列表中移除指定目录。
    /// </summary>
    /// <param name="dir">要移除的目录路径</param>
    private void ExecuteRemoveExcludedDir(string? dir)
    {
        try
        {
            if (dir != null && UserExcludedDirs.Contains(dir))
            {
                UserExcludedDirs.Remove(dir);
                _logger.LogInformation("已移除排除目录: {Dir}", dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移除排除目录失败: {Dir}", dir);
        }
    }

    /// <summary>
    /// 重置为默认值：弹出确认对话框，确认后新建一个默认 UserConfig 副本替换当前编辑副本。
    /// </summary>
    private void ExecuteResetToDefaults()
    {
        _logger.LogMethodEntry("用户点击重置为默认值");
        try
        {
            var result = MessageBox.Show(
                "确定要重置所有设置为默认值吗？\n\n此操作不会立即保存，您可以点击取消放弃重置。",
                "确认重置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                _logger.LogInformation("用户取消了重置操作");
                return;
            }

            // 创建全新的默认配置副本（保留系统运行时字段）
            var frnAvailable = _editConfig.FRN_AVAILABLE;
            var ruleCacheVersion = _editConfig.RuleCacheVersion;
            var isFirstRun = _editConfig.IsFirstRun;
            var freshConfig = new UserConfig
            {
                FRN_AVAILABLE = frnAvailable,
                RuleCacheVersion = ruleCacheVersion,
                IsFirstRun = isFirstRun
            };

            // 将 _editConfig 替换为默认副本
            CopyConfigFields(freshConfig, _editConfig);

            // 重新加载 ViewModel 属性
            LoadFromEditConfig();
            LoadVolumes();
            RebuildComboOptions();
            RefreshAllLocalizedLabels();

            _logger.LogInformation("设置已重置为默认值（未持久化）");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重置为默认值失败");
            MessageBox.Show(
                $"重置失败: {ex.Message}",
                "重置错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ============================================================
    // 核心保存逻辑
    // ============================================================

    /// <summary>
    /// 将编辑副本的修改同步到 IConfigService.Current 并调用 SaveAsync 持久化。
    /// 设置 _isSaving 标志防止并发保存，完成后恢复。
    /// </summary>
    private async Task SaveChangesAsync()
    {
        if (_isSaving)
        {
            _logger.LogWarning("保存操作已在进行中，跳过重复保存");
            return;
        }

        _isSaving = true;
        ApplyCommand.RaiseCanExecuteChanged();

        try
        {
            // 将编辑副本的值同步到正式配置实例
            var current = _configService.Current;

            // --- 通用 ---
            current.StartupBehavior = _editStartupBehavior;
            current.Theme = _editTheme;
            current.Language = _editLanguage;
            current.DataStoragePath = string.IsNullOrWhiteSpace(_editDataStoragePath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceClear")
                : _editDataStoragePath;

            // --- 扫描与清理 ---
            current.ScanMode = _editScanMode;
            current.DeleteMethod = _editDeleteMethod;
            current.ConfirmBeforeOneClickClean = _editConfirmBeforeClean;
            current.ShowManualReviewFiles = _editShowManualReview;
            current.AutoRefreshScanResults = _editAutoRefresh;
            current.MaxScanFiles = Math.Max(0, _editMaxScanFiles);
            current.HashConcurrency = Math.Clamp(_editHashConcurrency, 0, 32);
            // 从 UI 卷列表中收集勾选状态
            current.IncludedVolumes = IncludedVolumes
                .Where(v => v.IsIncluded)
                .Select(v => v.DriveLetter)
                .ToList();

            // --- AI 顾问 ---
            current.AIEnabled = _editAIEnabled;
            current.AIServiceType = _editAIServiceType;
            current.AIApiUrl = _editAIApiUrl;
            current.AIApiKey = _editAIApiKey;
            current.AIModel = _editAIModel;
            current.AITrustLevel = _editAITrustLevel;
            current.AIDailyLimit = Math.Max(0, _editAIDailyLimit);
            current.AIAutoAnalyze = _editAIAutoAnalyze;

            // --- 排除与保护 ---
            current.UserExcludedDirs = UserExcludedDirs.ToList();
            current.UserExcludedExtensions = ParseExtensionsText(_excludedExtensionsText);
            current.QuarantineMaxSizeBytes = CalculateQuarantineBytes(_quarantineSizeValue, _quarantineSizeUnitIndex);
            current.QuarantineRetentionDays = Math.Max(1, _editQuarantineRetentionDays);

            // 持久化到磁盘（JsonConfigService 内部使用原子写入）
            await _configService.SaveAsync();

            _logger.LogInformation("设置已成功保存到 {Path}", _configService.ConfigFilePath);
        }
        finally
        {
            _isSaving = false;
            ApplyCommand.RaiseCanExecuteChanged();
        }
    }

    // ============================================================
    // 私有辅助方法
    // ============================================================

    /// <summary>
    /// 从编辑副本 _editConfig 加载数据到 ViewModel 各属性。
    /// </summary>
    private void LoadFromEditConfig()
    {
        // 通用
        _editStartupBehavior = _editConfig.StartupBehavior;
        _editTheme = _editConfig.Theme;
        _editLanguage = _editConfig.Language;
        _editDataStoragePath = _editConfig.DataStoragePath;
        if (string.IsNullOrEmpty(_editDataStoragePath))
        {
            _editDataStoragePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceClear");
        }

        // 扫描与清理
        _editScanMode = _editConfig.ScanMode;
        _editDeleteMethod = _editConfig.DeleteMethod;
        _editConfirmBeforeClean = _editConfig.ConfirmBeforeOneClickClean;
        _editShowManualReview = _editConfig.ShowManualReviewFiles;
        _editAutoRefresh = _editConfig.AutoRefreshScanResults;
        _editMaxScanFiles = _editConfig.MaxScanFiles;
        _editHashConcurrency = _editConfig.HashConcurrency;

        // AI 顾问
        _editAIEnabled = _editConfig.AIEnabled;
        _editAIServiceType = _editConfig.AIServiceType;
        _editAIApiUrl = _editConfig.AIApiUrl;
        _editAIApiKey = _editConfig.AIApiKey;
        _editAIModel = _editConfig.AIModel;
        _editAITrustLevel = _editConfig.AITrustLevel;
        _editAIDailyLimit = _editConfig.AIDailyLimit;
        _editAIAutoAnalyze = _editConfig.AIAutoAnalyze;

        // 排除与保护
        UserExcludedDirs.Clear();
        foreach (var dir in _editConfig.UserExcludedDirs)
        {
            UserExcludedDirs.Add(dir);
        }
        _excludedExtensionsText = string.Join(", ", _editConfig.UserExcludedExtensions);
        ParseQuarantineSizeForDisplay(_editConfig.QuarantineMaxSizeBytes, out double val, out int unitIdx);
        _quarantineSizeValue = val;
        _quarantineSizeUnitIndex = unitIdx;
        _editQuarantineRetentionDays = _editConfig.QuarantineRetentionDays;

        OnPropertyChanged(nameof(IsExpertMode));
        _logger.LogDebug("从编辑副本加载初始数据完成");
    }

    /// <summary>
    /// 加载本机 NTFS 固定卷列表，并恢复用户勾选状态。
    /// </summary>
    private void LoadVolumes()
    {
        IncludedVolumes.Clear();
        var savedVolumes = _editConfig.IncludedVolumes;

        try
        {
            foreach (var drive in DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                var driveLetter = drive.Name.TrimEnd('\\'); // "C:"
                var isNtfs = string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);

                // 格式化显示名称
                var label = string.IsNullOrEmpty(drive.VolumeLabel) ? "本地磁盘" : drive.VolumeLabel;
                var sizeText = FormatBytesHuman(drive.TotalSize);
                var formatText = isNtfs ? drive.DriveFormat : $"{drive.DriveFormat} (不支持)";
                var displayName = $"{driveLetter} ({label}) - {formatText} - {sizeText}";

                // 判断用户是否勾选过此卷
                var isIncluded = savedVolumes.Contains(driveLetter, StringComparer.OrdinalIgnoreCase);

                IncludedVolumes.Add(new VolumeItem
                {
                    DriveLetter = driveLetter,
                    DisplayName = displayName,
                    Format = drive.DriveFormat,
                    IsIncluded = isIncluded && isNtfs // 非 NTFS 卷不可选
                });
            }

            _logger.LogInformation("已加载 {Count} 个磁盘卷", IncludedVolumes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载磁盘卷列表失败");
        }

        // 加载自动保护目录
        LoadProtectedDirs();
    }

    /// <summary>
    /// 加载自动保护目录列表（硬规则保护的系统目录）。
    /// 这些目录由规则引擎硬编码保护，在设置中只读展示。
    /// </summary>
    private void LoadProtectedDirs()
    {
        ProtectedDirs.Clear();
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        ProtectedDirs.Add($@"{systemRoot} — 系统目录（硬保护）");
        ProtectedDirs.Add($@"{programFiles} — 应用程序目录（硬保护）");
        if (!string.IsNullOrEmpty(programFilesX86) && programFilesX86 != programFiles)
        {
            ProtectedDirs.Add($@"{programFilesX86} — 应用程序目录（硬保护）");
        }
        ProtectedDirs.Add(@"Docker Desktop / WSL 数据目录（自动检测）");
    }

    /// <summary>
    /// 构建所有 ComboBox 选项列表（本地化文本）。
    /// 语言切换时需重新调用以刷新 Display 文本。
    /// </summary>
    private void RebuildComboOptions()
    {
        StartupBehaviorOptions = new List<ComboItem>
        {
            new() { Display = GetLoc("Settings.Startup.AutoScan"), Value = StartupBehavior.AutoScan },
            new() { Display = GetLoc("Settings.Startup.ShowHome"), Value = StartupBehavior.ShowHome }
        };
        OnPropertyChanged(nameof(StartupBehaviorOptions));

        ThemeOptions = new List<ComboItem>
        {
            new() { Display = GetLoc("Settings.Theme.Light"), Value = ThemeType.Light },
            new() { Display = GetLoc("Settings.Theme.Dark"), Value = ThemeType.Dark },
            new() { Display = GetLoc("Settings.Theme.FollowSystem"), Value = ThemeType.FollowSystem }
        };
        OnPropertyChanged(nameof(ThemeOptions));

        LanguageOptions = new List<ComboItem>
        {
            new() { Display = "简体中文", Value = "zh-CN" },
            new() { Display = "English", Value = "en-US" }
        };
        OnPropertyChanged(nameof(LanguageOptions));

        ScanModeOptions = new List<ComboItem>
        {
            new() { Display = GetLoc("Settings.ScanMode.Smart"), Value = ScanMode.SmartScanSystemDrive },
            new() { Display = GetLoc("Settings.ScanMode.AskDirectory"), Value = ScanMode.AskDirectoryEveryTime }
        };
        OnPropertyChanged(nameof(ScanModeOptions));

        DeleteMethodOptions = new List<ComboItem>
        {
            new() { Display = GetLoc("Settings.DeleteMethod.RecycleBin"), Value = DeleteMethod.RecycleBin },
            new() { Display = GetLoc("Settings.DeleteMethod.Permanent"), Value = DeleteMethod.Permanent }
        };
        OnPropertyChanged(nameof(DeleteMethodOptions));

        AIServiceTypeOptions = new List<ComboItem>
        {
            new() { Display = GetLoc("Settings.AIService.OpenAI"), Value = "openai" },
            new() { Display = GetLoc("Settings.AIService.Ollama"), Value = "ollama" },
            new() { Display = GetLoc("Settings.AIService.Custom"), Value = "custom" }
        };
        OnPropertyChanged(nameof(AIServiceTypeOptions));

        AITrustLevelOptions = new List<ComboItem>
        {
            new() { Display = GetLoc("Settings.AITrust.Low"), Value = AITrustLevel.Low },
            new() { Display = GetLoc("Settings.AITrust.Medium"), Value = AITrustLevel.Medium },
            new() { Display = GetLoc("Settings.AITrust.High"), Value = AITrustLevel.High }
        };
        OnPropertyChanged(nameof(AITrustLevelOptions));

        QuarantineSizeUnitOptions = new List<ComboItem>
        {
            new() { Display = "MB", Value = 0 },
            new() { Display = "GB", Value = 1 }
        };
        OnPropertyChanged(nameof(QuarantineSizeUnitOptions));
    }

    /// <summary>
    /// 实时应用主题（不等待确认）。
    /// 委托给 App.ApplyTheme 静态方法，避免代码重复。
    /// 若 Application.Current 为 null（单元测试场景），安全跳过。
    /// </summary>
    private static void ApplyThemeImmediately(ThemeType theme)
    {
        try
        {
            if (Application.Current != null)
            {
                App.ApplyTheme(theme);
            }
        }
        catch (Exception ex)
        {
            // 主题切换失败不应阻塞用户操作（非关键路径）
            System.Diagnostics.Debug.WriteLine($"主题切换失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 语言变更回调：在 UI 线程刷新所有本地化标签和 ComboBox 选项文本。
    /// </summary>
    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        try
        {
            // 确保在 UI 线程执行
            if (Application.Current != null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    RebuildComboOptions();
                    RefreshAllLocalizedLabels();
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "语言切换回调异常");
        }
    }

    /// <summary>
    /// 解析逗号分隔的扩展名字符串为 List。
    /// 自动去除空白字符并确保每个扩展名以 "." 开头。
    /// </summary>
    private static List<string> ParseExtensionsText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        return text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.StartsWith('.') ? s : "." + s)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 根据数值和单位计算隔离区最大容量（字节）。
    /// </summary>
    private static long CalculateQuarantineBytes(double value, int unitIndex)
    {
        return unitIndex switch
        {
            0 => (long)(value * 1024 * 1024),           // MB
            1 => (long)(value * 1024 * 1024 * 1024),   // GB
            _ => (long)(value * 1024 * 1024 * 1024)    // 默认 GB
        };
    }

    /// <summary>
    /// 将隔离区字节数解析为适合 UI 显示的数值+单位。
    /// 优先使用 GB 显示（若值为整数 GB 则用 GB），否则用 MB。
    /// </summary>
    private static void ParseQuarantineSizeForDisplay(long bytes, out double value, out int unitIndex)
    {
        const long gb = 1024L * 1024 * 1024;
        const long mb = 1024L * 1024;

        if (bytes >= gb && bytes % gb == 0)
        {
            value = bytes / (double)gb;
            unitIndex = 1; // GB
        }
        else
        {
            value = bytes / (double)mb;
            unitIndex = 0; // MB
        }
    }

    /// <summary>
    /// 将字节数格式化为人类可读的大小字符串（如 "238 GB"）。
    /// </summary>
    private static string FormatBytesHuman(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// 将源 UserConfig 的所有用户可配置字段值复制到目标实例（用于重置场景）。
    /// 注意：运行时标记字段（RuleCacheVersion / FRN_AVAILABLE / IsFirstRun）不在此复制，
    /// 由调用方 ExecuteResetToDefaults 显式保留原值。
    /// </summary>
    private static void CopyConfigFields(UserConfig source, UserConfig target)
    {
        // --- 通用设置 ---
        target.StartupBehavior = source.StartupBehavior;
        target.Theme = source.Theme;
        target.Language = source.Language;
        target.DataStoragePath = source.DataStoragePath;

        // --- 扫描与清理 ---
        target.ScanMode = source.ScanMode;
        target.DeleteMethod = source.DeleteMethod;
        target.ConfirmBeforeOneClickClean = source.ConfirmBeforeOneClickClean;
        target.ShowManualReviewFiles = source.ShowManualReviewFiles;
        target.AutoRefreshScanResults = source.AutoRefreshScanResults;
        target.MaxScanFiles = source.MaxScanFiles;
        target.HashConcurrency = source.HashConcurrency;
        target.IncludedVolumes = new List<string>(source.IncludedVolumes);

        // --- AI 顾问 ---
        target.AIEnabled = source.AIEnabled;
        target.AIServiceType = source.AIServiceType;
        target.AIApiUrl = source.AIApiUrl;
        target.AIApiKey = source.AIApiKey;
        target.AIModel = source.AIModel;
        target.AITrustLevel = source.AITrustLevel;
        target.AIDailyLimit = source.AIDailyLimit;
        target.AIAutoAnalyze = source.AIAutoAnalyze;

        // --- 排除与保护 ---
        target.UserExcludedDirs = new List<string>(source.UserExcludedDirs);
        target.UserExcludedExtensions = new List<string>(source.UserExcludedExtensions);
        target.QuarantineMaxSizeBytes = source.QuarantineMaxSizeBytes;
        target.QuarantineRetentionDays = source.QuarantineRetentionDays;

        // --- 用户画像 ---
        target.UserMode = source.UserMode;
        target.UserProfession = source.UserProfession;
        target.DeviceUsage = source.DeviceUsage;

        // 运行时标记字段（RuleCacheVersion / FRN_AVAILABLE / IsFirstRun）不在此复制
    }

    /// <summary>
    /// 释放资源：取消订阅语言变更事件，防止 Singleton ILocalizationService 持有本实例引用导致泄漏。
    /// 幂等安全，重复调用无副作用。
    /// </summary>
    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _logger.LogDebug("SettingsViewModel 已释放");
        GC.SuppressFinalize(this);
    }
}
