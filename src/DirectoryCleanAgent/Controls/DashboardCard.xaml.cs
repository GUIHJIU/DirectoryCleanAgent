using System.Windows.Controls;
using System.Windows.Input;
using DirectoryCleanAgent.Models;

namespace DirectoryCleanAgent.Controls;

/// <summary>
/// 仪表板分类卡片用户控件。
/// 显示单个 FinalAction 分类的文件总大小和项数，支持点击交互。
/// </summary>
public partial class DashboardCard : UserControl
{
    public DashboardCard()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 卡片点击事件：切换选中状态，并更新视觉反馈。
    /// 后续 C2 阶段可扩展为筛选文件列表。
    /// </summary>
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (DataContext is DashboardItem item)
            {
                item.IsSelected = !item.IsSelected;
                // 点击卡片后触发筛选逻辑（C2 阶段实现文件列表联动）
            }
        }
        catch (Exception ex)
        {
            // 卡片点击不应对用户造成功能性中断，仅记录日志
            System.Diagnostics.Debug.WriteLine($"DashboardCard 点击异常: {ex.Message}");
        }
    }
}
