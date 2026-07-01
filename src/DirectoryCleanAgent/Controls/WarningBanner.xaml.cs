using System.Windows.Controls;

namespace DirectoryCleanAgent.Controls;

/// <summary>
/// 警告横幅用户控件。
/// 显示黄色背景的警告信息，用于非管理员只读模式和索引未完成提示。
/// 通过绑定 MainViewModel.WarningMessage 控制显示/隐藏和文本内容。
/// </summary>
public partial class WarningBanner : UserControl
{
    public WarningBanner()
    {
        InitializeComponent();
    }
}
