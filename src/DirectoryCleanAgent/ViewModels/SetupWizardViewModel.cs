using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Everything.Installation;
using DirectoryCleanAgent.Models;
using DirectoryCleanAgent.Services;
using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.ViewModels;

/// <summary>
/// 首次启动配置向导 ViewModel — 管理向导所有步骤的状态、流转和用户输入。
///
/// 向导流程（依据设计文档 V3.7 第 3.1 节）：
///   Step0: Everything 依赖检测 + 索引等待选项
///   Step1: 欢迎与模式选择（简单/专家）
///   Step2: 用户画像（仅专家模式显示）
///   Step3: 保护与排除配置
///   Step4: AI 配置
///
/// Step0 不可跳过（直到检测完成或用户选择跳过索引）。
/// 其他步骤均可跳过，使用默认配置。
/// 向导完成后触发 WizardCompleted 事件，由 App.xaml.cs 负责切换到主窗口。
/// </summary>
public class SetupWizardViewModel : ViewModelBase
{
    private readonly ILogger<SetupWizardViewModel> _logger;
    private readonly IConfigService _configService;
    private readonly IEverythingDetector _detector;
    private readonly IAppStateService _appStateService;
    private readonly IEverythingInstallService _installService;
    private CancellationTokenSource? _indexWaitCts;
    private CancellationTokenSource? _installCts;

    /// <summary>后台等待完成后暂存的结果，供 App 层回调 CompleteBackgroundWait 时使用</summary>
    private BackgroundWaitReadyEventArgs? _pendingBackgroundWaitResult;

    // ================================================================
    // 构造函数与依赖注入
    // ================================================================

    public SetupWizardViewModel(
        ILogger<SetupWizardViewModel> logger,
        IConfigService configService,
        IEverythingDetector detector,
        IAppStateService appStateService,
        IEverythingInstallService installService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _appStateService = appStateService ?? throw new ArgumentNullException(nameof(appStateService));
        _installService = installService ?? throw new ArgumentNullException(nameof(installService));

        Steps = new ObservableCollection<WizardStepInfo>();
        UserConfig = _configService.Current;

        // 初始化命令
        NextCommand = new RelayCommand(ExecuteNext, () => CanGoNext);
        PreviousCommand = new RelayCommand(ExecutePrevious, () => CanGoPrevious);
        SkipWizardCommand = new RelayCommand(ExecuteSkipWizard);
        SkipStepCommand = new RelayCommand(ExecuteSkipStep, () => CanSkipCurrentStep);
        StartIndexWaitCommand = new RelayCommand<int>(ExecuteStartIndexWait);
        StartBackgroundWaitCommand = new RelayCommand(ExecuteStartBackgroundWait);
        CancelIndexWaitCommand = new RelayCommand(ExecuteCancelIndexWait);
        InstallEverythingCommand = new RelayCommand(ExecuteInstallEverything,
            () => !IsInstallingEverything);
        CancelInstallCommand = new RelayCommand(ExecuteCancelInstall,
            () => IsInstallingEverything);

        // 初始化步骤（初始为简单模式步骤列表）
        BuildSteps(UserMode.Simple);

        _logger.LogInformation("SetupWizardViewModel 初始化完成");
    }

    // ================================================================
    // 公开属性（UI 绑定源）
    // ================================================================

    /// <summary>向导步骤列表（动态构建，依据用户模式）</summary>
    public ObservableCollection<WizardStepInfo> Steps { get; }

    /// <summary>当前步骤索引（可由 View 层在导航时设置）</summary>
    public int CurrentStepIndex { get; set; }

    /// <summary>当前步骤信息（派生属性）</summary>
    public WizardStepInfo CurrentStep => Steps.Count > 0 ? Steps[CurrentStepIndex] : null!;

    /// <summary>用户当前设置的配置引用</summary>
    public UserConfig UserConfig { get; }

    // ---- Step0: Everything 检测 ----

    /// <summary>Everything 依赖检测结果（Step0 完成后填充）</summary>
    public EverythingDetectionResult? DetectionResult { get; private set; }

    /// <summary>Everything 检测是否正在执行</summary>
    public bool IsDetecting { get; private set; }

    /// <summary>Everything 检测是否已完成</summary>
    public bool IsDetectionComplete { get; set; }

    /// <summary>检测失败时的错误信息</summary>
    public string? DetectionError { get; private set; }

    // ---- 安装 Everything ----

    /// <summary>是否未安装 Everything（展示安装面板）</summary>
    public bool IsEverythingNotInstalled { get; private set; }

    /// <summary>是否正在安装 Everything</summary>
    public bool IsInstallingEverything { get; private set; }

    /// <summary>安装进度百分比（0-100）</summary>
    public int InstallProgressPercentage { get; private set; }

    /// <summary>安装状态描述文本</summary>
    public string InstallStatusText { get; private set; } = string.Empty;

    /// <summary>当前安装阶段文本</summary>
    public string InstallStageText { get; private set; } = string.Empty;

    /// <summary>是否版本过低需要升级</summary>
    public bool IsVersionTooLow { get; private set; }

    /// <summary>当前版本字符串（版本过低时展示）</summary>
    public string CurrentVersionText { get; private set; } = string.Empty;

    // ---- 索引等待 ----

    /// <summary>是否正在等待索引就绪</summary>
    public bool IsWaitingForIndex { get; private set; }

    /// <summary>索引等待已用时间文本（如"已等待 2 分 30 秒"）</summary>
    public string WaitElapsedText { get; private set; } = string.Empty;

    /// <summary>是否进入后台等待模式（最小化托盘）</summary>
    public bool IsBackgroundWaiting { get; private set; }

    /// <summary>索引等待超时时是否自动进入警告模式</summary>
    public bool IsIndexTimeout { get; private set; }

    // ---- Step1: 模式选择 ----

    /// <summary>用户选择的使用模式（简单/专家）</summary>
    public UserMode SelectedUserMode
    {
        get => UserConfig.UserMode;
        set
        {
            if (UserConfig.UserMode != value)
            {
                UserConfig.UserMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsExpertMode));
                OnPropertyChanged(nameof(IsSimpleMode));
                // 模式切换后重新构建步骤列表
                BuildSteps(value);
                _logger.LogInformation("用户模式切换为 {Mode}", value);
            }
        }
    }

    /// <summary>是否为专家模式（用于 UI 条件显示）</summary>
    public bool IsExpertMode => UserConfig.UserMode == UserMode.Expert;

    /// <summary>是否为简单模式</summary>
    public bool IsSimpleMode => UserConfig.UserMode == UserMode.Simple;

    // ---- Step2: 用户画像 ----

    /// <summary>用户职业（专家模式收集）</summary>
    public string UserProfession
    {
        get => UserConfig.UserProfession;
        set
        {
            if (UserConfig.UserProfession != value)
            {
                UserConfig.UserProfession = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>设备主要用途（专家模式收集）</summary>
    public string DeviceUsage
    {
        get => UserConfig.DeviceUsage;
        set
        {
            if (UserConfig.DeviceUsage != value)
            {
                UserConfig.DeviceUsage = value;
                OnPropertyChanged();
            }
        }
    }

    // ---- Step4: AI 配置 ----

    /// <summary>AI 是否启用</summary>
    public bool AIEnabled
    {
        get => UserConfig.AIEnabled;
        set
        {
            if (UserConfig.AIEnabled != value)
            {
                UserConfig.AIEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>AI API 地址</summary>
    public string AIApiUrl
    {
        get => UserConfig.AIApiUrl;
        set
        {
            if (UserConfig.AIApiUrl != value)
            {
                UserConfig.AIApiUrl = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>AI API Key（内存中明文，持久化时加密）</summary>
    public string AIApiKey
    {
        get => UserConfig.AIApiKey;
        set
        {
            if (UserConfig.AIApiKey != value)
            {
                UserConfig.AIApiKey = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>AI 模型名称</summary>
    public string AIModel
    {
        get => UserConfig.AIModel;
        set
        {
            if (UserConfig.AIModel != value)
            {
                UserConfig.AIModel = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>AI 每日调用限额</summary>
    public int AIDailyLimit
    {
        get => UserConfig.AIDailyLimit;
        set
        {
            if (UserConfig.AIDailyLimit != value)
            {
                UserConfig.AIDailyLimit = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>AI 服务类型</summary>
    public string AIServiceType
    {
        get => UserConfig.AIServiceType;
        set
        {
            if (UserConfig.AIServiceType != value)
            {
                UserConfig.AIServiceType = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>AI 连接测试是否通过</summary>
    public bool IsAIConnectionOk { get; set; }

    /// <summary>是否正在测试 AI 连接</summary>
    public bool IsTestingAIConnection { get; private set; }

    // ---- 步骤导航状态 ----

    /// <summary>是否可以前进到下一步（Step0 需要检测完成后才能前进）</summary>
    public bool CanGoNext => !IsDetecting && !IsWaitingForIndex && !IsInstallingEverything
        && (CurrentStepIndex > 0 || IsDetectionComplete)
        && CurrentStepIndex < Steps.Count - 1;

    /// <summary>是否可以回退到上一步</summary>
    public bool CanGoPrevious => CurrentStepIndex > 0 && !IsDetecting && !IsWaitingForIndex && !IsInstallingEverything;

    /// <summary>当前步骤是否可以跳过</summary>
    public bool CanSkipCurrentStep =>
        Steps.Count > 0 && CurrentStep.IsSkippable && !IsDetecting && !IsWaitingForIndex && !IsInstallingEverything;

    /// <summary>是否在最后一步</summary>
    public bool IsLastStep => Steps.Count > 0 && CurrentStepIndex == Steps.Count - 1;

    /// <summary>下一步按钮显示文本（最后一步显示"完成"）</summary>
    public string NextButtonText => IsLastStep ? "完成" : "下一步 →";

    /// <summary>用户是否选择了跳过整个向导（由 App 层监听）</summary>
    public bool IsWizardSkipped { get; private set; }

    // ================================================================
    // 命令
    // ================================================================

    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand SkipWizardCommand { get; }
    public RelayCommand SkipStepCommand { get; }
    public RelayCommand<int> StartIndexWaitCommand { get; }
    public RelayCommand StartBackgroundWaitCommand { get; }
    public RelayCommand CancelIndexWaitCommand { get; }
    public RelayCommand InstallEverythingCommand { get; }
    public RelayCommand CancelInstallCommand { get; }

    /// <summary>向导完成事件（App 层监听此事件以切换窗口）</summary>
    public event EventHandler? WizardCompleted;

    /// <summary>向导被跳过事件</summary>
    public event EventHandler? WizardSkipped;

    /// <summary>请求关闭向导窗口事件（后台等待时 App 层处理）</summary>
    public event EventHandler? RequestMinimizeToTray;

    /// <summary>后台等待完成事件（App 层据此显示通知气泡）</summary>
    public event EventHandler<BackgroundWaitReadyEventArgs>? BackgroundWaitReady;

    /// <summary>后台等待被取消事件（App 层据此清理托盘并恢复窗口）</summary>
    public event EventHandler? BackgroundWaitCanceled;

    // ================================================================
    // 步骤管理
    // ================================================================

    /// <summary>
    /// 根据用户选择的模式构建向导步骤列表。
    /// 简单模式：Step0 → Step1 → Step3 → Step4（跳过 Step2 用户画像）
    /// 专家模式：Step0 → Step1 → Step2 → Step3 → Step4（完整流程）
    /// </summary>
    /// <param name="mode">用户模式</param>
    private void BuildSteps(UserMode mode)
    {
        Steps.Clear();

        // Step 0: Everything 依赖检测（始终存在）
        Steps.Add(new WizardStepInfo
        {
            Index = 0,
            Title = "环境检测",
            Description = "正在检测 Everything 搜索引擎依赖状态…",
            IsSkippable = false
        });

        // Step 1: 欢迎与模式选择（始终存在，但可跳过整个向导）
        Steps.Add(new WizardStepInfo
        {
            Index = 1,
            Title = "欢迎",
            Description = "欢迎使用智能磁盘清理工具！选择适合您的使用模式。",
            IsSkippable = true
        });

        // Step 2: 用户画像（仅专家模式）
        if (mode == UserMode.Expert)
        {
            Steps.Add(new WizardStepInfo
            {
                Index = 2,
                Title = "用户画像",
                Description = "帮助我们了解您的使用场景，优化清理策略。",
                IsSkippable = true
            });
        }

        // Step 3: 保护与排除
        Steps.Add(new WizardStepInfo
        {
            Index = Steps.Count,
            Title = "保护与排除",
            Description = "配置需要保护的目录和文件类型，防止误删。",
            IsSkippable = true
        });

        // Step 4: AI 配置
        Steps.Add(new WizardStepInfo
        {
            Index = Steps.Count,
            Title = "AI 配置",
            Description = "配置 AI 顾问，为您的清理决策提供智能建议（可选）。",
            IsSkippable = true
        });

        // 更新所有步骤的激活状态
        UpdateStepActiveState();
        _logger.LogDebug("步骤列表已构建，模式={Mode}，步骤数={Count}", mode, Steps.Count);
    }

    /// <summary>更新每个步骤的 IsActive 状态</summary>
    private void UpdateStepActiveState()
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsActive = (i == CurrentStepIndex);
        }
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
    }

    // ================================================================
    // Step0: Everything 依赖检测
    // ================================================================

    /// <summary>
    /// 执行 Everything 依赖检测（5 步检测序列）。
    /// 在向导窗口加载后自动调用。
    /// 前 3 步（进程/版本/IPC）任一失败则阻断并显示错误；
    /// FRN 不可用仅降级标记；
    /// 索引未就绪则展示等待选项面板。
    /// </summary>
    public async Task RunEverythingDetectionAsync()
    {
        if (IsDetectionComplete)
        {
            _logger.LogDebug("Everything 检测已完成，跳过重复执行");
            return;
        }

        _logger.LogMethodEntry("开始执行 Everything 依赖检测");

        // 重置安装相关状态
        IsEverythingNotInstalled = false;
        IsVersionTooLow = false;
        IsDetecting = true;
        DetectionError = null;
        OnPropertyChanged(nameof(IsDetecting));
        OnPropertyChanged(nameof(IsEverythingNotInstalled));
        OnPropertyChanged(nameof(IsVersionTooLow));
        RefreshNavigationState();

        try
        {
            DetectionResult = await _detector.DetectAsync(_configService);

            if (!DetectionResult.CanProceedToMainUI)
            {
                // 前 3 步检测失败 — 根据 InstallSuggestion 决定展示安装/升级/错误面板
                if (DetectionResult.InstallSuggestion == "install")
                {
                    IsEverythingNotInstalled = true;
                    OnPropertyChanged(nameof(IsEverythingNotInstalled));
                }
                else if (DetectionResult.InstallSuggestion == "upgrade")
                {
                    IsVersionTooLow = true;
                    CurrentVersionText = DetectionResult.VersionString;
                    OnPropertyChanged(nameof(IsVersionTooLow));
                    OnPropertyChanged(nameof(CurrentVersionText));
                }
                else
                {
                    DetectionError = DetectionResult.FailureReason ?? "Everything 依赖检测失败，未知错误。";
                    OnPropertyChanged(nameof(DetectionError));
                }
                _logger.LogWarning("Everything 检测阻断: {Reason}, Suggestion={Suggestion}",
                    DetectionResult.FailureReason, DetectionResult.InstallSuggestion);
            }
            else if (DetectionResult.IsIndexing)
            {
                // 索引未就绪 — 展示等待选项面板
                _logger.LogWarning("Everything 索引正在构建中，等待用户选择处理方式");
                OnPropertyChanged(nameof(DetectionResult));
                // 停留在 Step0，等待用户选择（等待/后台/跳过）
            }
            else
            {
                // 一切就绪，标记 Step0 完成
                MarkStep0Complete();
            }
        }
        catch (Exception ex)
        {
            DetectionError = $"Everything 依赖检测异常: {ex.Message}";
            _logger.LogError(ex, "Everything 依赖检测异常");
            OnPropertyChanged(nameof(DetectionError));
        }
        finally
        {
            IsDetecting = false;
            OnPropertyChanged(nameof(IsDetecting));
            OnPropertyChanged(nameof(DetectionResult));
            RefreshNavigationState();
        }
    }

    /// <summary>标记 Step0 完成并允许前进到 Step1</summary>
    private void MarkStep0Complete()
    {
        IsDetectionComplete = true;
        if (Steps.Count > 0)
        {
            Steps[0].IsCompleted = true;
        }
        _appStateService.IsIndexReady = !DetectionResult!.IsIndexing;
        _logger.LogInformation("Everything 依赖检测完成，索引就绪={Ready}", _appStateService.IsIndexReady);
    }

    // ================================================================
    // 索引等待（WaitForIndex / Background / Skip）
    // ================================================================

    /// <summary>
    /// 开始等待索引就绪（前台轮询模式）。
    /// 每 2 秒轮询索引状态，通过 IProgress 更新 UI。
    /// 120 秒超时后自动降级为警告模式。
    /// </summary>
    /// <param name="waitMode">1=等待完成, 2=后台等待（通过 StartBackgroundWaitCommand 直接调用）</param>
    private async void ExecuteStartIndexWait(int waitMode)
    {
        _logger.LogInformation("用户选择索引等待模式: {Mode}", waitMode);
        IsWaitingForIndex = true;
        IsIndexTimeout = false;
        OnPropertyChanged(nameof(IsWaitingForIndex));
        RefreshNavigationState();

        _indexWaitCts = new CancellationTokenSource();
        var progress = new Progress<int>(elapsedSeconds =>
        {
            var minutes = elapsedSeconds / 60;
            var seconds = elapsedSeconds % 60;
            WaitElapsedText = $"已等待 {minutes} 分 {seconds} 秒";
            OnPropertyChanged(nameof(WaitElapsedText));
        });

        try
        {
            bool indexReady = await _detector.WaitForIndexAsync(_indexWaitCts.Token, progress);

            if (indexReady)
            {
                // 索引在超时前就绪
                _logger.LogInformation("索引等待完成 — 索引已就绪");
                _appStateService.IsIndexReady = true;
            }
            else
            {
                // 120 秒超时 — 进入警告模式（仍可进入主界面）
                _logger.LogWarning("索引等待超时 (120s)，进入警告模式");
                IsIndexTimeout = true;
                _appStateService.IsIndexReady = false;
            }

            MarkStep0Complete();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("用户取消了索引等待");
            // 取消等待 → 同"跳过"逻辑
            HandleSkipIndex();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "索引等待期间异常");
            DetectionError = $"索引等待异常: {ex.Message}";
            OnPropertyChanged(nameof(DetectionError));
        }
        finally
        {
            IsWaitingForIndex = false;
            OnPropertyChanged(nameof(IsWaitingForIndex));
            RefreshNavigationState();
            _indexWaitCts?.Dispose();
            _indexWaitCts = null;
        }
    }

    /// <summary>
    /// 进入后台等待模式：最小化到系统托盘，索引完成后通知。
    /// 触发 RequestMinimizeToTray 事件，由 App 层处理托盘逻辑。
    /// 索引完成后先触发 BackgroundWaitReady（App 层显示通知），
    /// App 层在用户点击通知/双击托盘/超时后回调 CompleteBackgroundWait()。
    /// </summary>
    private async void ExecuteStartBackgroundWait()
    {
        _logger.LogInformation("用户选择后台等待模式 — 最小化到系统托盘");
        IsBackgroundWaiting = true;
        OnPropertyChanged(nameof(IsBackgroundWaiting));

        // 通知 App 层创建托盘图标并隐藏窗口
        RequestMinimizeToTray?.Invoke(this, EventArgs.Empty);

        _indexWaitCts = new CancellationTokenSource();

        try
        {
            // 后台轮询（使用同一个 WaitForIndexAsync 方法，不传 progress 以节省资源）
            bool indexReady = await _detector.WaitForIndexAsync(_indexWaitCts.Token, progress: null);

            // 暂存结果，等待 App 层通过 CompleteBackgroundWait() 确认通知已显示
            _pendingBackgroundWaitResult = indexReady
                ? BackgroundWaitReadyEventArgs.Success()
                : BackgroundWaitReadyEventArgs.Timeout();

            _logger.LogInformation(
                "后台索引等待结束: IndexReady={Ready}, Timeout={Timeout}",
                _pendingBackgroundWaitResult.IsIndexReady,
                _pendingBackgroundWaitResult.IsTimeout);

            // 先通知 App 层显示通知气泡（不直接触发 WizardCompleted）
            // App 层会在通知显示后回调 CompleteBackgroundWait() 完成最终转换
            BackgroundWaitReady?.Invoke(this, _pendingBackgroundWaitResult);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("后台索引等待被取消");
            IsBackgroundWaiting = false;
            OnPropertyChanged(nameof(IsBackgroundWaiting));
            BackgroundWaitCanceled?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "后台索引等待异常");
            HandleSkipIndex();    // 标记 Step0 完成，允许用户继续向导（警告模式）
            IsBackgroundWaiting = false;
            OnPropertyChanged(nameof(IsBackgroundWaiting));
            // 异常时也通知 App 层清理托盘状态
            BackgroundWaitCanceled?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _indexWaitCts?.Dispose();
            _indexWaitCts = null;
        }
    }

    /// <summary>取消当前索引等待操作</summary>
    private void ExecuteCancelIndexWait()
    {
        _logger.LogInformation("用户取消索引等待");
        _indexWaitCts?.Cancel();
    }

    /// <summary>
    /// 完成后台等待转换（由 App 层在通知显示后回调）。
    /// 设置 IsIndexReady/IsIndexTimeout 状态，标记 Step0 完成，
    /// 最后触发 WizardCompleted 事件，App 层据此关闭向导进入主界面。
    ///
    /// 此方法设计为幂等：重复调用会被 _pendingBackgroundWaitResult 的 null 检查拦截。
    /// </summary>
    public void CompleteBackgroundWait()
    {
        var result = _pendingBackgroundWaitResult;
        _pendingBackgroundWaitResult = null;

        // 幂等保护：防止用户同时点击气泡+双击托盘导致重复转换
        if (result == null)
            return;

        IsBackgroundWaiting = false;
        OnPropertyChanged(nameof(IsBackgroundWaiting));

        // 标记 Step0 完成（允许后续步骤导航）
        IsDetectionComplete = true;
        if (Steps.Count > 0)
        {
            Steps[0].IsCompleted = true;
        }

        // 根据实际的等待结果更新应用状态
        _appStateService.IsIndexReady = result.IsIndexReady;
        if (result.IsTimeout)
        {
            IsIndexTimeout = true;
            OnPropertyChanged(nameof(IsIndexTimeout));
        }

        _logger.LogInformation(
            "后台等待转换完成: IsIndexReady={Ready}, IsTimeout={Timeout}",
            result.IsIndexReady, result.IsTimeout);

        // 触发向导完成 → App 层关闭向导、清理托盘、显示主窗口
        WizardCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>跳过索引等待 — 标记为索引超时并允许前进</summary>
    private void HandleSkipIndex()
    {
        IsIndexTimeout = true;
        _appStateService.IsIndexReady = false;
        MarkStep0Complete();
        _logger.LogWarning("用户跳过索引等待 — 进入警告模式");
    }

    /// <summary>
    /// 用户在 Step0 索引等待面板点击"跳过"按钮。
    /// 直接进入主界面并显示黄色横幅，禁用一键清理。
    /// </summary>
    public void ExecuteSkipIndexWait()
    {
        HandleSkipIndex();
        // 跳过索引后，用户可以继续向导或直接进入主界面
        // 此处允许用户继续配置向导
    }

    // ================================================================
    // Everything 一键安装
    // ================================================================

    /// <summary>
    /// 执行 Everything 便携版的一键安装（下载 → 解压 → 注册 → 启动 → 重新检测）。
    /// </summary>
    private async void ExecuteInstallEverything()
    {
        _logger.LogInformation("用户选择一键安装 Everything 便携版");
        IsInstallingEverything = true;
        InstallProgressPercentage = 0;
        InstallStatusText = "准备安装…";
        IsEverythingNotInstalled = false;
        DetectionError = null;
        OnPropertyChanged(nameof(IsInstallingEverything));
        OnPropertyChanged(nameof(InstallProgressPercentage));
        OnPropertyChanged(nameof(InstallStatusText));
        OnPropertyChanged(nameof(IsEverythingNotInstalled));
        RefreshNavigationState();

        _installCts = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p =>
        {
            InstallProgressPercentage = p.Percentage;
            InstallStageText = p.Stage switch
            {
                InstallStage.Downloading => "下载中",
                InstallStage.Extracting => "解压中",
                InstallStage.Installing => "注册服务中",
                InstallStage.Verifying => "验证中",
                InstallStage.Complete => "已完成",
                _ => ""
            };
            InstallStatusText = p.Message;
            OnPropertyChanged(nameof(InstallProgressPercentage));
            OnPropertyChanged(nameof(InstallStageText));
            OnPropertyChanged(nameof(InstallStatusText));
        });

        try
        {
            var appDir = AppContext.BaseDirectory;
            await _installService.InstallPortableAsync(appDir, progress, _installCts.Token);

            InstallProgressPercentage = 100;
            InstallStatusText = "安装完成，正在重新检测…";
            OnPropertyChanged(nameof(InstallProgressPercentage));
            OnPropertyChanged(nameof(InstallStatusText));

            await Task.Delay(1500);
            await RunEverythingDetectionAsync();
        }
        catch (EverythingInstallException ex)
        {
            _logger.LogError(ex, "Everything 安装失败");
            DetectionError = ex.Message;
            OnPropertyChanged(nameof(DetectionError));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("用户取消了 Everything 安装");
            IsEverythingNotInstalled = true;
            OnPropertyChanged(nameof(IsEverythingNotInstalled));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Everything 安装意外失败");
            DetectionError = $"安装失败: {ex.Message}";
            OnPropertyChanged(nameof(DetectionError));
        }
        finally
        {
            IsInstallingEverything = false;
            OnPropertyChanged(nameof(IsInstallingEverything));
            RefreshNavigationState();
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    private void ExecuteCancelInstall()
    {
        _logger.LogInformation("用户取消 Everything 安装");
        _installCts?.Cancel();
    }

    // ================================================================
    // 步骤导航命令
    // ================================================================

    /// <summary>
    /// 前进到下一步。若在最后一步，保存配置并触发 WizardCompleted 事件。
    /// </summary>
    private async void ExecuteNext()
    {
        _logger.LogMethodEntry($"当前步骤 {CurrentStepIndex}/{Steps.Count - 1}");

        try
        {
            if (IsLastStep)
            {
                // 向导最后一步 → 保存配置并完成
                await FinishWizard();
            }
            else
            {
                // 标记当前步骤完成并前进
                if (CurrentStep != null)
                {
                    CurrentStep.IsCompleted = true;
                }

                CurrentStepIndex++;
                UpdateStepActiveState();
                RefreshNavigationState();

                _logger.LogInformation("向导前进到步骤 {Index}: {Title}",
                    CurrentStepIndex, CurrentStep.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "步骤前进失败: {Step}", CurrentStepIndex);
            MessageBox.Show(
                $"操作失败: {ex.Message}",
                "向导错误",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 回退到上一步。保留已填写的用户数据。
    /// </summary>
    private void ExecutePrevious()
    {
        _logger.LogMethodEntry($"当前步骤 {CurrentStepIndex}");

        try
        {
            if (CurrentStepIndex > 0)
            {
                CurrentStepIndex--;
                UpdateStepActiveState();
                RefreshNavigationState();
                _logger.LogInformation("向导回退到步骤 {Index}: {Title}",
                    CurrentStepIndex, CurrentStep.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "步骤回退失败: {Step}", CurrentStepIndex);
        }
    }

    /// <summary>
    /// 跳过整个配置向导，使用默认配置并直接进入主界面。
    /// </summary>
    private async void ExecuteSkipWizard()
    {
        _logger.LogInformation("用户选择跳过整个配置向导");

        try
        {
            IsWizardSkipped = true;
            UserConfig.IsFirstRun = false;
            await _configService.SaveAsync();
            _logger.LogInformation("已使用默认配置保存，IsFirstRun=false");
            WizardSkipped?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "跳过向导时保存默认配置失败");
            // 即使保存失败也允许进入主界面（使用内存中的默认配置）
            WizardSkipped?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 跳过当前步骤（仅限标记为 IsSkippable 的步骤）。
    /// 使用默认配置值继续，不修改用户配置中对应字段。
    /// </summary>
    private void ExecuteSkipStep()
    {
        _logger.LogInformation("用户跳过当前步骤 {Index}: {Title}",
            CurrentStepIndex, CurrentStep?.Title);

        try
        {
            CurrentStep.IsCompleted = true;

            if (IsLastStep)
            {
                // 最后一步也跳过了，直接完成向导
                _ = FinishWizard();
            }
            else
            {
                CurrentStepIndex++;
                UpdateStepActiveState();
                RefreshNavigationState();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "跳过步骤失败: {Step}", CurrentStepIndex);
        }
    }

    // ================================================================
    // 向导完成
    // ================================================================

    /// <summary>
    /// 完成向导：保存配置、标记首次运行完成。
    /// 若保存失败仍允许进入主界面（使用内存中的配置）。
    /// </summary>
    private async Task FinishWizard()
    {
        _logger.LogInformation("用户完成配置向导，正在保存配置…");

        try
        {
            UserConfig.IsFirstRun = false;

            // 若无索引等待过，但检测结果中索引仍在构建 — 标记 IsIndexReady=false
            if (DetectionResult?.IsIndexing == true && !IsIndexTimeout)
            {
                _appStateService.IsIndexReady = false;
            }

            await _configService.SaveAsync();
            _logger.LogInformation(
                "配置已保存: 模式={Mode}, 语言={Lang}, AI={AI}",
                UserConfig.UserMode, UserConfig.Language, UserConfig.AIEnabled);

            // 触发完成事件，App 层关闭向导并打开主窗口
            WizardCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存配置失败，但允许进入主界面（使用内存配置）");
            // 容错：即使持久化失败也不阻塞用户进入主界面
            WizardCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 公开的配置保存方法，供窗口关闭等场景使用。
    /// 仅保存配置，不触发 WizardCompleted 事件。
    /// </summary>
    public async Task SaveConfigAsync()
    {
        try
        {
            await _configService.SaveAsync();
            _logger.LogInformation("配置已持久化");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "公开 SaveConfigAsync 调用失败");
            throw;
        }
    }

    // ================================================================
    // 导航状态管理
    // ================================================================

    /// <summary>
    /// 刷新所有导航相关按钮的 CanExecute 状态。
    /// 在步骤切换、检测状态变更后调用。
    /// </summary>
    public void RefreshNavigationState()
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanSkipCurrentStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
    }
}
