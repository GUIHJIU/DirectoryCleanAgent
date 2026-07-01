using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.ViewModels;

namespace DirectoryCleanAgent.Views;

/// <summary>
/// 审计日志视图 — UserControl 代码后置。
/// 仅负责 Loaded 事件触发 ViewModel 初始数据加载。
/// </summary>
public partial class AuditLogView : UserControl
{
    private readonly ILogger<AuditLogView> _logger;
    private AuditLogViewModel? _vm;

    public AuditLogView()
    {
        InitializeComponent();

        _logger = App.ServiceProvider!.GetRequiredService<ILogger<AuditLogView>>();
        _logger.LogMethodEntry("AuditLogView 构造完成");
    }

    /// <summary>
    /// 视图加载完成后触发 ViewModel 初始数据加载。
    /// </summary>
    private async void AuditLogView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            if (_vm != null) return; // 避免重复加载

            _vm = DataContext as AuditLogViewModel;
            if (_vm == null)
            {
                _logger.LogWarning("AuditLogView.DataContext 不是 AuditLogViewModel");
                return;
            }

            _logger.LogInformation("AuditLogView 加载完成，触发初始数据加载");
            await _vm.LoadInitialDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AuditLogView 初始加载失败");
        }
    }
}
