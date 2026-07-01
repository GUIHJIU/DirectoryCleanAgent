using System.Windows.Controls;

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
}
