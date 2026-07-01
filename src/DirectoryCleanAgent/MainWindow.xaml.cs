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
    /// 窗口加载完成后初始化 ViewModel（加载仪表板数据、设置初始状态）。
    /// </summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
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
}
