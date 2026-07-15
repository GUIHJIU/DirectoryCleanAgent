using System.Windows;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.ViewModels;

namespace DirectoryCleanAgent;

/// <summary>
/// 主窗口 — 应用程序主界面的容器。
/// 通过构造函数注入 MainViewModel，所有 UI 交互通过数据绑定驱动。
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly MainViewModel _viewModel;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;

    /// <summary>
    /// 构造函数：接收 DI 注入的 MainViewModel 和 ILogger。
    /// </summary>
    public MainWindow(MainViewModel viewModel, ILogger<MainWindow> logger)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        try
        {
            InitializeComponent();
            DataContext = _viewModel;
            Closed += OnMainWindowClosed;
            _logger.LogInformation("MainWindow 初始化完成，DataContext 已绑定");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MainWindow 初始化失败");
            MessageBox.Show(
                $"主窗口初始化失败: {ex.Message}\n\n详细错误已写入日志文件。",
                "启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            throw;
        }
    }

    /// <summary>
    /// 窗口加载完成后初始化 ViewModel，并创建系统托盘 NotifyIcon 以供 AI Toast 通知使用。
    /// </summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 创建 WinForms NotifyIcon 实例并注册到 App 静态字段，
            // 使 MainViewModel.ShowAiAnalysisToast 的 App.ShowBalloonTip 调用生效
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = "智能磁盘清理"
            };
            App.SetNotifyIcon(_notifyIcon);
            _logger.LogDebug("NotifyIcon 已创建并注册到 App");

            _logger.LogMethodEntry("MainWindow 加载完成，开始初始化 ViewModel");
            await _viewModel.InitializeAsync();
            _logger.LogMethodExit("MainWindow 启动流程结束");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MainWindow Loaded 事件处理异常");
            MessageBox.Show(
                $"初始化失败: {ex.Message}",
                "启动异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 窗口关闭时释放 NotifyIcon 资源。
    /// </summary>
    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
            _logger.LogDebug("NotifyIcon 已释放");
        }
    }
}
