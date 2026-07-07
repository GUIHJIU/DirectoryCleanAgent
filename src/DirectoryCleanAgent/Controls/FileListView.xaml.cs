using System.Windows.Controls;
using DirectoryCleanAgent.ViewModels;

namespace DirectoryCleanAgent.Controls;

/// <summary>
/// 文件列表与分组视图用户控件。
/// ViewModel 通过父容器的 DataContext 属性自动继承，无需手动赋值。
/// 代码后置仅负责 InitializeComponent，所有逻辑在 FileListViewModel 中处理。
/// </summary>
public partial class FileListView : UserControl
{
    public FileListView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 拦截 DataGrid 列头排序事件，阻止 WPF 默认内存排序，
    /// 将 SortMemberPath 作为逻辑键转发给 ViewModel 处理排序下推。
    /// </summary>
    private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // 阻止 WPF 默认排序（全部委托 ViewModel 处理）
        e.Handled = true;

        var column = e.Column;
        var sortKey = column.SortMemberPath;
        if (string.IsNullOrEmpty(sortKey)) return;

        if (DataContext is FileListViewModel vm)
        {
            vm.SortByColumnCommand.Execute(sortKey);
        }
    }
}
