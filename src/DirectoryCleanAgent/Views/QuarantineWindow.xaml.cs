using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.ViewModels;

namespace DirectoryCleanAgent.Views;

/// <summary>
/// 隔离区管理窗口 — C7 阶段核心界面。
///
/// 提供隔离区文件的完整管理功能：查看文件列表、容量统计、
/// 多选批量恢复和永久删除。
///
/// 使用 DI 构造函数注入 ViewModel 和 Logger，
/// 遵循 SettingsWindow 的代码后置模式：
/// - Loaded 事件触发初始数据加载
/// - ViewModel 的 RequestClose 事件驱动窗口关闭
/// - Closing 事件记录日志
/// - 所有逻辑在 ViewModel 中处理
/// </summary>
public partial class QuarantineWindow : Window
{
    private readonly ILogger<QuarantineWindow> _logger;
    private readonly QuarantineViewModel _viewModel;

    public QuarantineWindow(
        QuarantineViewModel viewModel,
        ILogger<QuarantineWindow> logger)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        try
        {
            InitializeComponent();
            DataContext = _viewModel;

            // 订阅 ViewModel 事件：关闭窗口
            _viewModel.RequestClose += OnRequestClose;

            _logger.LogInformation("QuarantineWindow 初始化完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuarantineWindow 初始化失败");
            MessageBox.Show(
                $"隔离区管理窗口初始化失败: {ex.Message}",
                "初始化错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            throw;
        }
    }

    // ================================================================
    // 窗口生命周期事件
    // ================================================================

    /// <summary>
    /// 窗口加载完成后：异步触发初始数据加载。
    /// 使用 fire-and-forget 模式启动异步操作，异常由 ViewModel 内部处理。
    /// </summary>
    private void QuarantineWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogDebug("QuarantineWindow 已加载，触发初始数据加载");
            // 异步加载隔离区数据（fire-and-forget，异常在 ViewModel 中处理）
            _ = _viewModel.LoadDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuarantineWindow Loaded 事件异常");
        }
    }

    /// <summary>
    /// 窗口关闭前记录日志和当前状态。
    /// 如果正在执行操作，确认是否要关闭并取消操作。
    /// </summary>
    private void QuarantineWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            // 如果正在执行操作，提示用户确认
            if (_viewModel.IsOperating)
            {
                var result = MessageBox.Show(
                    "当前仍有操作正在执行，关闭窗口将取消操作。\n确定要关闭吗？",
                    "确认关闭",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (result == MessageBoxResult.No)
                {
                    e.Cancel = true; // 阻止关闭
                    return;
                }
            }

            _logger.LogInformation("隔离区管理窗口关闭 (文件数={FileCount})",
                _viewModel.QuarantineFiles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuarantineWindow Closing 事件异常");
        }
    }

    // ================================================================
    // ViewModel 事件回调
    // ================================================================

    /// <summary>
    /// ViewModel 请求关闭窗口（用户点击关闭按钮）。
    /// </summary>
    private void OnRequestClose(object? sender, EventArgs e)
    {
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭窗口时异常");
        }
    }
}
