using System.Windows;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.ViewModels;

namespace DirectoryCleanAgent.Views;

/// <summary>
/// 历史记录窗口 — 包含审计日志和删除历史两个 Tab 页。
/// 通过 DI 注入容器 ViewModel，作为模态窗口打开（Owner=MainWindow, ShowDialog）。
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly ILogger<HistoryWindow> _logger;

    /// <summary>
    /// DI 构造函数 — 注入容器 ViewModel 并设为 DataContext。
    /// </summary>
    public HistoryWindow(HistoryWindowViewModel viewModel, ILogger<HistoryWindow> logger)
    {
        InitializeComponent();

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        _logger.LogMethodEntry("HistoryWindow 构造完成");
    }

    /// <summary>
    /// 窗口加载完成事件。
    /// </summary>
    private void HistoryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogInformation("HistoryWindow 已加载");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HistoryWindow 加载事件处理失败");
        }
    }

    /// <summary>
    /// 关闭按钮 — 关闭模态窗口（设置 DialogResult = true）。
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogInformation("用户关闭历史记录窗口");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭历史记录窗口失败");
        }
    }
}
