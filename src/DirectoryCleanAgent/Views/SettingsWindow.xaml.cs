using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.ViewModels;

namespace DirectoryCleanAgent.Views;

/// <summary>
/// 设置窗口 — 包含五个标签页的设置对话框。
/// 使用 DI 构造函数注入 ViewModel 和 Logger。
///
/// 特殊处理：
/// - PasswordBox（API Key）不支持 WPF 双向绑定，通过 PasswordChanged 事件手动同步。
/// - 数字输入框通过 PreviewTextInput 限制只能输入数字字符。
/// - 窗口关闭事件用于日志记录。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ILogger<SettingsWindow> _logger;
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(
        SettingsViewModel viewModel,
        ILogger<SettingsWindow> logger)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        try
        {
            InitializeComponent();
            DataContext = _viewModel;

            // 订阅 ViewModel 事件：确定 → DialogResult=true + 关闭
            _viewModel.RequestCloseWithOK += OnRequestCloseWithOK;

            // 订阅 ViewModel 事件：取消 → DialogResult=false + 关闭
            _viewModel.RequestCloseWithCancel += OnRequestCloseWithCancel;

            // 订阅 ViewModel 事件：应用 → 仅日志，窗口保持打开
            _viewModel.SettingsApplied += OnSettingsApplied;

            _logger.LogInformation("SettingsWindow 初始化完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SettingsWindow 初始化失败");
            MessageBox.Show(
                $"设置窗口初始化失败: {ex.Message}",
                "初始化错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            throw;
        }
    }

    // ================================================================
    // 窗口生命周期事件
    // ================================================================

    /// <summary>窗口加载完成后：将已保存的 API Key 填充到 PasswordBox</summary>
    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(_viewModel.EditAIApiKey))
            {
                AIApiKeyBox.Password = _viewModel.EditAIApiKey;
            }
            _logger.LogDebug("SettingsWindow 已加载");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SettingsWindow Loaded 事件异常");
        }
    }

    /// <summary>窗口关闭前记录日志</summary>
    private void SettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            _logger.LogInformation("设置窗口关闭 (DialogResult={Result})", DialogResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SettingsWindow Closing 事件异常");
        }
    }

    // ================================================================
    // ViewModel 事件回调
    // ================================================================

    /// <summary>确定：保存成功，关闭窗口（DialogResult=true）</summary>
    private void OnRequestCloseWithOK(object? sender, EventArgs e)
    {
        try
        {
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭窗口（OK）时异常");
        }
    }

    /// <summary>取消：放弃修改，关闭窗口（DialogResult=false）</summary>
    private void OnRequestCloseWithCancel(object? sender, EventArgs e)
    {
        try
        {
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "关闭窗口（Cancel）时异常");
        }
    }

    /// <summary>应用：配置已保存，窗口保持打开</summary>
    private void OnSettingsApplied(object? sender, EventArgs e)
    {
        try
        {
            _logger.LogInformation("设置已应用，窗口保持打开");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置应用事件回调异常");
        }
    }

    // ================================================================
    // PasswordBox 事件处理（API Key 不可直接绑定的变通方案）
    // ================================================================

    /// <summary>
    /// PasswordBox 使用 SecurePassword，不支持标准 Binding。
    /// 在每次密码变更时手动同步到 ViewModel。
    /// </summary>
    private void AIApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.EditAIApiKey = AIApiKeyBox.Password;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步 API Key 到 ViewModel 失败");
        }
    }

    // ================================================================
    // 数字输入限制
    // ================================================================

    /// <summary>
    /// 限制 TextBox 只能输入数字字符。
    /// 用于隔离区大小、保留天数、每日限额等数值输入框。
    /// </summary>
    private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // 仅允许数字字符通过
        e.Handled = !e.Text.All(char.IsDigit);
    }
}
