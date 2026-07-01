using DirectoryCleanAgent.Core.DTOs;
using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.Models;

/// <summary>
/// 隔离区文件列表行模型 — 包装 QuarantineFileEntry 以支持 DataGrid CheckBox 双向绑定。
///
/// QuarantineFileEntry 是 readonly struct（值语义），无法直接绑定 DataGridCheckBoxColumn
/// 的 IsChecked 属性。QuarantineFileItem 作为可变包装类（ViewModelBase 派生），
/// 通过 SetProperty 实现属性变更通知，使 DataGrid 多选功能正常工作。
///
/// 委托属性：OriginalFileName、FileSizeText 等直接透传 Entry 的对应值。
/// </summary>
public class QuarantineFileItem : ViewModelBase
{
    /// <summary>底层不可变的隔离区文件条目数据</summary>
    public QuarantineFileEntry Entry { get; }

    private bool _isSelected;
    /// <summary>是否被选中（支持 DataGridCheckBoxColumn 双向绑定）</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>委托：原始文件名</summary>
    public string OriginalFileName => Entry.OriginalFileName;

    /// <summary>委托：人类可读文件大小</summary>
    public string FileSizeText => Entry.FileSizeText;

    /// <summary>委托：隔离时间文本</summary>
    public string QuarantineTimestampText => Entry.QuarantineTimestampText;

    /// <summary>委托：SHA256 前缀</summary>
    public string Sha256Prefix => Entry.Sha256Prefix;

    /// <summary>委托：是否过期</summary>
    public bool IsExpired => Entry.IsExpired;

    /// <summary>委托：完整隔离区文件路径</summary>
    public string FullPath => Entry.FullPath;

    /// <summary>委托：文件大小字节数</summary>
    public long FileSizeBytes => Entry.FileSizeBytes;

    /// <summary>
    /// 过期状态显示文本，用于 DataGrid 中展示"已过期"或"正常"。
    /// </summary>
    public string ExpiredStatusText => IsExpired ? "已过期" : "正常";

    public QuarantineFileItem(QuarantineFileEntry entry)
    {
        Entry = entry;
    }
}
