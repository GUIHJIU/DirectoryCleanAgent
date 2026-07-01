using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DirectoryCleanAgent.Core.Logging;
using DirectoryCleanAgent.ViewModels;

namespace DirectoryCleanAgent.Views;

/// <summary>
/// 删除历史与回滚视图 — UserControl 代码后置。
/// 仅负责 Loaded 事件触发 ViewModel 初始数据加载。
/// </summary>
public partial class DeletionHistoryView : UserControl
{
    private readonly ILogger<DeletionHistoryView> _logger;
    private DeletionHistoryViewModel? _vm;

    public DeletionHistoryView()
    {
        InitializeComponent();

        _logger = App.ServiceProvider!.GetRequiredService<ILogger<DeletionHistoryView>>();
        _logger.LogMethodEntry("DeletionHistoryView 构造完成");
    }

    /// <summary>
    /// 视图加载完成后触发 ViewModel 初始数据加载。
    /// </summary>
    private async void DeletionHistoryView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            if (_vm != null) return; // 避免重复加载

            _vm = DataContext as DeletionHistoryViewModel;
            if (_vm == null)
            {
                _logger.LogWarning("DeletionHistoryView.DataContext 不是 DeletionHistoryViewModel");
                return;
            }

            _logger.LogInformation("DeletionHistoryView 加载完成，触发初始数据加载");
            await _vm.LoadInitialDataAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeletionHistoryView 初始加载失败");
        }
    }
}
