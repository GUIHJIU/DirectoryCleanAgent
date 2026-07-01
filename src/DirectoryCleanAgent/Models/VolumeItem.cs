using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.Models;

/// <summary>
/// 磁盘卷复选框项模型，用于"扫描与清理"标签页的卷选择列表。
/// 继承 ViewModelBase 以支持 IsIncluded 的双向绑定和属性变更通知。
/// </summary>
public class VolumeItem : ViewModelBase
{
    /// <summary>盘符，如 "C:"（不含尾部反斜杠）</summary>
    public string DriveLetter { get; init; } = string.Empty;

    /// <summary>显示名称，如 "C: (本地磁盘) - NTFS - 238 GB"</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>文件系统格式（仅 NTFS 卷显示）</summary>
    public string Format { get; init; } = string.Empty;

    private bool _isIncluded;

    /// <summary>用户是否勾选此卷进行扫描（双向绑定到 CheckBox.IsChecked）</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }
}
