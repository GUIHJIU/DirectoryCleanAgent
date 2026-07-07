using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using DirectoryCleanAgent.Core.Config;
using DirectoryCleanAgent.Core.Enums;
using DirectoryCleanAgent.Core.Interfaces;
using DirectoryCleanAgent.AI;
using DirectoryCleanAgent.Core.Localization;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.Data;
using DirectoryCleanAgent.Decision;
using DirectoryCleanAgent.Everything;
using DirectoryCleanAgent.Operations;
using DirectoryCleanAgent.Rules;
using DirectoryCleanAgent.Services;
using DirectoryCleanAgent.ViewModels;
using DirectoryCleanAgent.Views;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using MessageBox = System.Windows.MessageBox;

namespace DirectoryCleanAgent;

/// <summary>
/// WPF 应用程序入口。
/// 职责：
/// 1. 配置 Serilog 结构化日志
/// 2. 构建 DI 容器并注册所有服务
/// 3. 检测管理员权限，设置 AppMode
/// 4. 加载用户配置和主题
/// 5. 首次运行时显示配置向导，否则直接启动主窗口
/// </summary>
public partial class App : System.Windows.Application
{
    private IServiceProvider? _serviceProvider;
    private ILogger<App>? _logger;

    /// <summary>全局 DI 容器访问点，供 View 层按需解析服务（如 MainViewModel 打开设置窗口）</summary>
    public static IServiceProvider? ServiceProvider { get; private set; }

    // 托盘图标 — 后台等待模式时使用
    private TrayIconService? _trayIconService;
    private SetupWizardWindow? _wizardWindow;
    private MainWindow? _mainWindow;

    /// <summary>后台等待完成后自动进入主界面的延迟计时器（5秒）</summary>
    private System.Windows.Threading.DispatcherTimer? _backgroundWaitCompletionTimer;
    private const int BackgroundWaitAutoProceedSeconds = 5;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // ============================================================
            // 第1步: 配置 Serilog 结构化日志
            // ============================================================
            ConfigureSerilog();

            // 创建临时日志记录器用于启动阶段
            var tempLoggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSerilog();
            });
            _logger = tempLoggerFactory.CreateLogger<App>();
            _logger.LogInformation("========== 应用程序启动 ==========");
            _logger.LogInformation("OS: {OS}, .NET: {Runtime}", Environment.OSVersion, Environment.Version);

            // ============================================================
            // 第2步: 构建 DI 容器
            // ============================================================
            _serviceProvider = ConfigureServices();

            // 替换为 DI 容器中的正式 Logger
            _logger = _serviceProvider.GetRequiredService<ILogger<App>>();

            // ---- 数据库初始化（幂等建表，WAL 模式配置） ----
            await _serviceProvider.InitializeDatabaseAsync();

            // ============================================================
            // 第3步: 检测管理员权限，设置 AppMode
            // ============================================================
            var appStateService = _serviceProvider.GetRequiredService<IAppStateService>();
            DetectAdminRights(appStateService);

            // ============================================================
            // 第4步: 加载用户配置（主题、语言等）
            // ============================================================
            await LoadConfigurationAsync(appStateService);

            // ============================================================
            // 第5步: 判断是否首次运行，决定启动流程
            // ============================================================
            var configService = _serviceProvider.GetRequiredService<IConfigService>();
            if (configService.Current.IsFirstRun)
            {
                _logger.LogInformation("首次运行 — 启动配置向导");
                await ShowSetupWizardAsync();
            }
            else
            {
                _logger.LogInformation("非首次运行 — 直接启动主界面");
                ShowMainWindow();
            }
        }
        catch (Exception ex)
        {
            // 启动失败：写日志 + 弹错误对话框
            try
            {
                _logger?.LogError(ex, "应用程序启动失败");
            }
            catch
            {
                // 日志可能尚未初始化，写 Debug 输出
                System.Diagnostics.Debug.WriteLine($"启动失败: {ex}");
            }

            MessageBox.Show(
                $"应用程序启动失败:\n\n{ex.Message}\n\n" +
                "请检查以下事项:\n" +
                "1. .NET 8 运行时是否已安装\n" +
                "2. 日志目录是否有写入权限\n" +
                "3. 配置文件是否损坏\n\n" +
                $"详细错误: {ex}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(-1);
        }
    }

    // ================================================================
    // 配置向导启动流程
    // ================================================================

    /// <summary>
    /// 显示首次启动配置向导。
    /// 向导完成后自动关闭并启动主窗口。
    /// </summary>
    private async Task ShowSetupWizardAsync()
    {
        try
        {
            _wizardWindow = _serviceProvider!.GetRequiredService<SetupWizardWindow>();

            // 从窗口的 DataContext 获取 ViewModel 实例（避免 Transient DI 创建新实例导致事件订阅错位）
            var wizardViewModel = (SetupWizardViewModel)_wizardWindow.DataContext;
            wizardViewModel.WizardCompleted += OnWizardCompleted;
            wizardViewModel.WizardSkipped += OnWizardSkipped;
            wizardViewModel.RequestMinimizeToTray += OnRequestMinimizeToTray;
            wizardViewModel.BackgroundWaitReady += OnBackgroundWaitReady;
            wizardViewModel.BackgroundWaitCanceled += OnBackgroundWaitCanceled;

            // 向导完成时清理事件订阅（避免重复触发）
            void cleanupEvents()
            {
                wizardViewModel.WizardCompleted -= OnWizardCompleted;
                wizardViewModel.WizardSkipped -= OnWizardSkipped;
                wizardViewModel.RequestMinimizeToTray -= OnRequestMinimizeToTray;
                wizardViewModel.BackgroundWaitReady -= OnBackgroundWaitReady;
                wizardViewModel.BackgroundWaitCanceled -= OnBackgroundWaitCanceled;
            }
            wizardViewModel.WizardCompleted += (s, ev) => cleanupEvents();
            wizardViewModel.WizardSkipped += (s, ev) => cleanupEvents();

            _wizardWindow.Show();

            _logger.LogInformation("配置向导窗口已显示");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "启动配置向导失败，回退到直接启动主界面");
            ShowMainWindow();
        }
    }

    /// <summary>向导完成 — 关闭向导并启动主窗口</summary>
    private void OnWizardCompleted(object? sender, EventArgs e)
    {
        _logger?.LogInformation("配置向导已完成，切换至主界面");

        // 必须在 UI 线程操作窗口
        Dispatcher.Invoke(() =>
        {
            try
            {
                // 关闭向导窗口
                if (_wizardWindow != null && _wizardWindow.IsLoaded)
                {
                    _wizardWindow.Close();
                }
                _wizardWindow = null;

                // 清理托盘图标
                CleanupTrayIcon();

                // 启动主窗口（若尚未启动）
                if (_mainWindow == null)
                {
                    ShowMainWindow();
                }
                else
                {
                    _mainWindow.Show();
                    _mainWindow.WindowState = WindowState.Normal;
                    _mainWindow.Activate();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "向导完成切换失败");
                ShowMainWindow();
            }
        });
    }

    /// <summary>向导被跳过 — 使用默认配置进入主界面</summary>
    private void OnWizardSkipped(object? sender, EventArgs e)
    {
        _logger?.LogInformation("配置向导被用户跳过");

        Dispatcher.Invoke(() =>
        {
            try
            {
                if (_wizardWindow != null && _wizardWindow.IsLoaded)
                {
                    _wizardWindow.Close();
                }
                _wizardWindow = null;
                CleanupTrayIcon();

                if (_mainWindow == null)
                {
                    ShowMainWindow();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "向导跳过切换失败");
                ShowMainWindow();
            }
        });
    }

    /// <summary>请求最小化到系统托盘（后台等待 Everything 索引）</summary>
    private void OnRequestMinimizeToTray(object? sender, EventArgs e)
    {
        _logger?.LogInformation("进入后台等待模式 — 最小化到系统托盘");

        Dispatcher.Invoke(() =>
        {
            try
            {
                // 创建托盘图标
                CreateTrayIcon();

                // 隐藏向导窗口
                _wizardWindow?.Hide();

                _logger?.LogInformation("已隐藏窗口到托盘，等待 Everything 索引完成");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "最小化到托盘失败");
            }
        });
    }

    // ================================================================
    // 托盘图标管理
    // ================================================================

    /// <summary>
    /// 创建系统托盘图标（后台等待模式）。
    /// 使用原生 Shell_NotifyIcon P/Invoke，避免 WinForms 依赖冲突。
    /// </summary>
    private void CreateTrayIcon()
    {
        CleanupTrayIcon();

        _trayIconService = _serviceProvider!.GetRequiredService<TrayIconService>();

        // 托盘图标双击 → 若后台等待已完成则直接转换，否则恢复窗口
        _trayIconService.TrayIconDoubleClick += (s, ev) =>
        {
            _logger?.LogInformation("用户双击托盘图标");
            Dispatcher.Invoke(() =>
            {
                // 尝试完成后台等待转换（若等待已完成则进入主界面，幂等安全）
                CompleteBackgroundWaitAndTransition();
                // 若等待尚未完成（_pending 为 null），仅恢复窗口
                _wizardWindow?.RestoreFromTray();
            });
        };

        // 用户点击气泡通知 → "查看结果" — 完成后台等待转换
        _trayIconService.BalloonClicked += (s, ev) =>
        {
            _logger?.LogInformation("用户点击托盘通知气泡");
            Dispatcher.Invoke(() =>
            {
                CompleteBackgroundWaitAndTransition();
            });
        };

        // 右键菜单"显示主界面"
        _trayIconService.ShowMainWindowRequested += (s, ev) =>
        {
            Dispatcher.Invoke(() =>
            {
                CompleteBackgroundWaitAndTransition();
                _wizardWindow?.RestoreFromTray();
            });
        };

        // 右键菜单"退出"
        _trayIconService.ExitRequested += (s, ev) =>
        {
            _logger?.LogInformation("用户通过托盘菜单退出应用");
            Dispatcher.Invoke(() =>
            {
                CleanupTrayIcon();
                Shutdown(0);
            });
        };

        _trayIconService.Show(
            tooltip: "智能磁盘清理 — 等待 Everything 索引完成…",
            balloonTitle: "智能磁盘清理",
            balloonText: "正在后台等待 Everything 索引完成，完成后将自动进入主界面。");

        _logger?.LogInformation("托盘图标已创建");
    }

    /// <summary>清理托盘图标资源</summary>
    private void CleanupTrayIcon()
    {
        try
        {
            // 停止后台等待完成定时器（防止在清理后触发转换）
            StopBackgroundWaitTimer();

            _trayIconService?.Dispose();
            _trayIconService = null;
            _logger?.LogDebug("托盘图标已清理");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "清理托盘图标异常");
        }
    }

    // ================================================================
    // 后台等待通知编排
    // ================================================================

    /// <summary>
    /// 后台等待完成回调 — ViewModel 后台轮询结束后调用。
    /// 显示完成/超时通知气泡，启动 5 秒后自动进入主界面的计时器。
    /// 用户在 5 秒内点击气泡或双击托盘图标可立即完成转换。
    /// </summary>
    private void OnBackgroundWaitReady(object? sender, BackgroundWaitReadyEventArgs e)
    {
        _logger?.LogInformation(
            "后台等待通知阶段: IndexReady={Ready}, Timeout={Timeout}",
            e.IsIndexReady, e.IsTimeout);

        Dispatcher.Invoke(() =>
        {
            try
            {
                if (_trayIconService == null)
                {
                    _logger?.LogWarning("托盘服务不可用，直接完成后台等待转换");
                    CompleteBackgroundWaitAndTransition();
                    return;
                }

                // 根据等待结果显示不同的气泡通知
                if (e.IsIndexReady)
                {
                    _trayIconService.UpdateTooltip("索引已就绪 — 点击查看结果");
                    _trayIconService.ShowBalloon(
                        "Everything 索引已就绪",
                        "索引构建完成，正在进入主界面…");
                }
                else
                {
                    _trayIconService.UpdateTooltip("索引等待超时 — 点击查看结果");
                    _trayIconService.ShowBalloon(
                        "索引等待超时",
                        "索引未就绪，将以警告模式运行，部分功能受限。");
                }

                // 启动 5 秒自动转换计时器（用户可点击气泡或双击托盘提前触发）
                StartBackgroundWaitTimer();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "显示后台等待通知异常，直接完成转换");
                CompleteBackgroundWaitAndTransition();
            }
        });
    }

    /// <summary>
    /// 后台等待取消回调 — ViewModel 等待被取消/异常时调用。
    /// 清理托盘图标，恢复向导窗口，让用户重新选择。
    /// </summary>
    private void OnBackgroundWaitCanceled(object? sender, EventArgs e)
    {
        _logger?.LogInformation("后台等待被取消，清理托盘并恢复窗口");

        Dispatcher.Invoke(() =>
        {
            try
            {
                StopBackgroundWaitTimer();
                CleanupTrayIcon();
                _wizardWindow?.RestoreFromTray();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "后台等待取消处理异常");
            }
        });
    }

    /// <summary>
    /// 完成后台等待到主界面的转换。
    /// 停止自动转换计时器，调用 ViewModel.CompleteBackgroundWait()，
    /// 该方法会触发 WizardCompleted，由 OnWizardCompleted 完成窗口切换。
    ///
    /// 注意：必须从窗口的 DataContext 获取 ViewModel 实例，
    /// 而非通过 DI 新建（SetupWizardViewModel 注册为 Transient，新建实例会导致事件订阅错位）。
    /// </summary>
    private void CompleteBackgroundWaitAndTransition()
    {
        try
        {
            StopBackgroundWaitTimer();

            // 从向导窗口的 DataContext 获取 ViewModel 实例（与事件订阅的是同一个实例）
            if (_wizardWindow?.DataContext is SetupWizardViewModel wizardVm)
            {
                wizardVm.CompleteBackgroundWait();
                // CompleteBackgroundWait() 触发 WizardCompleted →
                // OnWizardCompleted() → 关闭向导 → 清理托盘 → 显示主窗口
            }
            else
            {
                _logger?.LogWarning("无法获取向导 ViewModel 实例，跳过转换");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "完成后台等待转换异常");
        }
    }

    /// <summary>启动后台等待完成后 5 秒自动进入主界面的计时器</summary>
    private void StartBackgroundWaitTimer()
    {
        StopBackgroundWaitTimer();

        _backgroundWaitCompletionTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(BackgroundWaitAutoProceedSeconds)
        };
        _backgroundWaitCompletionTimer.Tick += (s, ev) =>
        {
            _logger?.LogInformation("后台等待 {Sec}s 自动转换超时", BackgroundWaitAutoProceedSeconds);
            CompleteBackgroundWaitAndTransition();
        };
        _backgroundWaitCompletionTimer.Start();
    }

    /// <summary>停止后台等待自动转换计时器（幂等安全）</summary>
    private void StopBackgroundWaitTimer()
    {
        if (_backgroundWaitCompletionTimer != null)
        {
            _backgroundWaitCompletionTimer.Stop();
            _backgroundWaitCompletionTimer = null;
        }
    }

    // ================================================================
    // 主窗口启动
    // ================================================================

    /// <summary>创建并显示主窗口，同时注册托盘图标消息钩子</summary>
    private void ShowMainWindow()
    {
        try
        {
            // Everything 依赖检测已通过（首次运行）或上次已验证（非首次运行），启动文件变更轮询
            var fileListProvider = _serviceProvider!.GetRequiredService<IFileListProvider>();
            fileListProvider.StartChangePolling();

            _mainWindow = _serviceProvider!.GetRequiredService<MainWindow>();

            // 注册托盘图标消息钩子（TrayIconService 需要窗口过程处理自定义消息）
            _mainWindow.Loaded += (s, e) =>
            {
                var helper = new WindowInteropHelper(_mainWindow);
                nint handle = helper.Handle;
                if (handle != IntPtr.Zero)
                {
                    var source = HwndSource.FromHwnd(handle);
                    source?.AddHook(WndProcHook);
                    _logger?.LogDebug("托盘图标消息钩子已注册, HWND=0x{Handle:X}", handle);
                }
            };

            _mainWindow.Show();

            var appStateService = _serviceProvider.GetRequiredService<IAppStateService>();
            _logger?.LogInformation("主窗口已启动, AppMode={Mode}, IsAdmin={Admin}, IsIndexReady={IndexReady}",
                appStateService.AppMode, appStateService.IsAdmin, appStateService.IsIndexReady);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "主窗口创建失败");
            MessageBox.Show(
                $"主窗口启动失败: {ex.Message}",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <summary>
    /// WPF 窗口过程钩子 — 拦截托盘图标自定义消息。
    /// 将托盘消息转发给 TrayIconService.HandleMessage 处理。
    /// </summary>
    private nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        try
        {
            _trayIconService?.HandleMessage(msg, wParam, lParam, ref handled);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "处理托盘图标消息异常");
        }
        return IntPtr.Zero;
    }

    // ================================================================
    // 日志配置
    // ================================================================

    /// <summary>
    /// 配置 Serilog 日志管道：Debug 输出 + 滚动文件（保留7天）。
    /// </summary>
    private static void ConfigureSerilog()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpaceClear", "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                path: Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    /// <summary>
    /// 构建 DI 容器并注册所有服务、ViewModel、窗口。
    /// 注册顺序：基础设施 → 服务 → ViewModel → 窗口
    /// </summary>
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // ---- 日志 ----
        services.AddLogging(builder =>
        {
            builder.AddSerilog();
        });

        // ---- 配置层（复用 A4 实现的 AddConfigLayer 扩展） ----
        services.AddConfigLayer();

        // ---- 本地化（复用 A6 实现的 AddLocalizationLayer 扩展，依赖 IConfigService） ----
        services.AddLocalizationLayer();

        // ---- 全局状态管理 ----
        services.AddSingleton<IAppStateService, AppStateService>();

        // ---- 数据库路径（%LocalAppData%\SpaceClear\data.db） ----
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpaceClear", "data.db");

        // ---- 数据层（SQLite 真实仓储 + 墓碑缓存） ----
        services.AddDataLayer(dbPath);

        // ---- Everything SDK 适配器（真实 Everything 流式文件提供者） ----
        services.AddEverythingServices();

        // ---- Everything 依赖检测器接口（SetupWizardViewModel 使用） ----
        services.AddSingleton<IEverythingDetector>(sp =>
            sp.GetRequiredService<EverythingDependencyDetector>());

        // ---- 规则引擎层（硬规则 + 启发式规则 + 热加载） ----
        services.AddRulesLayer();

        // ---- 决策引擎层（仲裁逻辑 + 快照生成） ----
        services.AddDecisionLayer();

        // ---- 操作层（操作执行器 + 回滚管理器 + 隔离区管理） ----
        services.AddOperationsLayer();

        // ---- C1 仪表板数据服务（从 SQLite 决策缓存聚合真实统计数据） ----
        services.AddSingleton<IDashboardDataProvider, DashboardDataProvider>();

        // ---- C2: 文件列表与分组视图 ----
        services.AddSingleton<SemanticLabelLocalizer>();
        services.AddSingleton<FileListViewModel>();

        // ---- B7: AI 顾问层（可插拔旁路，不阻塞主流程） ----
        services.AddAiLayer();

        // ---- C5: 模拟运行与报告导出 ----
        // ISimulationService 依赖真实 B1/B2/B3 实现（IFileListProvider/IRuleEngine/IDecisionEngine）
        services.AddSingleton<ISimulationService, SimulationService>();
        services.AddSingleton<IReportExporter, ReportExporter>();

        // ---- C3: 首次启动向导 ----
        services.AddSingleton<TrayIconService>();
        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<SetupWizardWindow>();

        // ---- C4: 设置窗口 ----
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();

        // ---- C6: 历史记录窗口（审计日志 + 删除历史 + 回滚） ----
        services.AddTransient<AuditLogViewModel>();
        services.AddTransient<DeletionHistoryViewModel>();
        services.AddTransient<HistoryWindowViewModel>();
        services.AddTransient<HistoryWindow>();

        // ---- C7: 隔离区管理界面 ----
        services.AddTransient<QuarantineViewModel>();
        services.AddTransient<QuarantineWindow>();

        // ---- ViewModel & 窗口 ----
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();

        var provider = services.BuildServiceProvider();
        ServiceProvider = provider;
        return provider;
    }

    /// <summary>
    /// 检测当前进程是否以管理员权限运行。
    /// 非管理员时设置 AppMode.ReadOnly，后续 UI 层会据此禁用清理按钮并显示警告横幅。
    /// </summary>
    private void DetectAdminRights(IAppStateService appStateService)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            bool isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

            appStateService.IsAdmin = isAdmin;

            if (!isAdmin)
            {
                appStateService.AppMode = AppMode.ReadOnly;
                _logger?.LogWarning("非管理员权限启动，进入只读模式。清理/删除按钮将被禁用。");
            }
            else
            {
                appStateService.AppMode = AppMode.Normal;
                _logger?.LogInformation("管理员权限已确认，所有功能可用");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "管理员权限检测失败，默认启用只读模式以确保安全");
            // 安全默认值：检测失败时假定非管理员
            appStateService.IsAdmin = false;
            appStateService.AppMode = AppMode.ReadOnly;
        }
    }

    /// <summary>
    /// 加载用户配置并应用主题、语言等设置。
    /// 配置加载失败时使用默认值，不阻塞启动流程。
    /// </summary>
    private async Task LoadConfigurationAsync(IAppStateService appStateService)
    {
        try
        {
            var configService = _serviceProvider!.GetRequiredService<IConfigService>();
            await configService.LoadAsync();
            var config = configService.Current;

            _logger?.LogInformation(
                "配置已加载: 语言={Lang}, 主题={Theme}, 用户模式={Mode}, 启动行为={Startup}, 首次运行={IsFirstRun}",
                config.Language, config.Theme, config.UserMode, config.StartupBehavior, config.IsFirstRun);

            // ---- 应用主题 ----
            ApplyTheme(config.Theme);

            // ---- 应用语言 ----
            var localization = _serviceProvider!.GetRequiredService<ILocalizationService>();
            if (config.Language != localization.CurrentLanguage)
            {
                localization.SetLanguage(config.Language);
                _logger?.LogInformation("界面语言切换为: {Lang}", config.Language);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "用户配置加载失败，使用默认配置（浅色主题、简体中文）");
            // 不阻塞启动 — 主题已在 XAML 中默认 Light，本地化默认 zh-CN
        }
    }

    /// <summary>
    /// 根据配置应用主题 ResourceDictionary（静态方法，供 SettingsViewModel 实时切换主题使用）。
    /// </summary>
    public static void ApplyTheme(ThemeType theme)
    {
        try
        {
            var app = Current;
            if (app == null) return;

            bool useDark = theme switch
            {
                ThemeType.Dark => true,
                ThemeType.Light => false,
                ThemeType.FollowSystem => IsWindowsDarkMode(),
                _ => false
            };

            var themeUri = useDark
                ? new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
                : new Uri("Themes/LightTheme.xaml", UriKind.Relative);

            // 移除旧主题资源字典
            var oldTheme = app.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source != null &&
                    (d.Source.OriginalString.Contains("LightTheme") ||
                     d.Source.OriginalString.Contains("DarkTheme")));
            if (oldTheme != null)
            {
                app.Resources.MergedDictionaries.Remove(oldTheme);
            }

            // 添加新主题
            var newTheme = new ResourceDictionary { Source = themeUri };
            app.Resources.MergedDictionaries.Add(newTheme);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"主题加载失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 检测 Windows 系统是否为深色模式（通过注册表读取）。
    /// 若注册表键不存在或读取失败，默认返回 false（浅色）。
    /// </summary>
    private static bool IsWindowsDarkMode()
    {
        try
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("========== 应用程序退出 (Code={Code}) ==========", e.ApplicationExitCode);

        // 停止后台等待定时器
        StopBackgroundWaitTimer();

        // 释放托盘资源
        CleanupTrayIcon();

        // 释放 Everything SDK
        EverythingDependencyDetector.Cleanup();

        // 显式刷新数据层批量写入队列，确保审计日志和删除记录不丢失
        // （虽然 DI 容器的 Dispose 链会触发 BatchWriteQueue.Dispose → final Flush，
        //   但显式调用更健壮：避免容器释放顺序问题导致 flush 失败）
        try
        {
            var auditRepo = _serviceProvider?.GetService<Data.IAuditLogRepository>();
            if (auditRepo != null)
                auditRepo.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

            var deletionRepo = _serviceProvider?.GetService<Data.IDeletionRecordRepository>();
            if (deletionRepo != null)
                deletionRepo.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

            _logger?.LogInformation("数据层批量写入队列已显式刷新");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "退出时刷新数据队列失败（Dispose 链将二次保障）");
        }

        // 释放 DI 容器（Singleton 仓储的 Dispose 会二次调用 BatchWriteQueue.Dispose，
        // 此时队列已空，FlushCoreAsync 检测 batch.Count==0 直接返回，幂等安全）
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        // 刷新并关闭 Serilog
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
