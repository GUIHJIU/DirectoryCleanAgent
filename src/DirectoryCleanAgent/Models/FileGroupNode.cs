using System.Collections.ObjectModel;
using DirectoryCleanAgent.ViewModels.Base;

namespace DirectoryCleanAgent.Models;

/// <summary>
/// 分组树节点模型 — 支持 TreeView 双向绑定的层级节点。
/// 每个节点代表一个文件分组（路径/时间/大小/类型/语义标签）。
/// 叶子节点包含文件引用列表（FileCacheKeys），非叶子节点可含子节点。
/// </summary>
public class FileGroupNode : ViewModelBase
{
    private string _label = string.Empty;
    private string _icon = "📁";
    private int _itemCount;
    private long _totalSizeBytes;
    private bool _isExpanded;
    private bool _isSelected;
    private int _depth;

    /// <summary>分组显示名称（如"系统临时文件"、"C:\Windows"、"今天"）</summary>
    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    /// <summary>分组图标（Emoji 字符）</summary>
    public string Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    /// <summary>该分组下的文件总数（含子节点）</summary>
    public int ItemCount
    {
        get => _itemCount;
        set
        {
            if (SetProperty(ref _itemCount, value))
            {
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    /// <summary>该分组下的文件总大小（字节）</summary>
    public long TotalSizeBytes
    {
        get => _totalSizeBytes;
        set
        {
            if (SetProperty(ref _totalSizeBytes, value))
            {
                OnPropertyChanged(nameof(SizeText));
            }
        }
    }

    /// <summary>格式化后的文件总大小文本（如 "1.5 GB"）</summary>
    public string SizeText => FormatSizeBytes(TotalSizeBytes);

    /// <summary>格式化后的文件数量文本（如 "45 项"）</summary>
    public string CountText => $"{ItemCount} 项";

    /// <summary>TreeView 展开/折叠状态</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>TreeView 选中状态</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>子节点集合（二级分组）</summary>
    public ObservableCollection<FileGroupNode> Children { get; set; } = new();

    /// <summary>父节点引用（用于 TreeView 向上导航）</summary>
    public FileGroupNode? Parent { get; set; }

    /// <summary>树层级深度：0=根节点, 1=一级分组, 2=二级分组</summary>
    public int Depth
    {
        get => _depth;
        set => SetProperty(ref _depth, value);
    }

    /// <summary>语义分类标签（仅语义标签分组模式下有意义）</summary>
    public string? SemanticCategory { get; set; }

    /// <summary>该分组关联的文件缓存键列表（FilePath），用于选中分组后过滤 DataGrid</summary>
    public IReadOnlyList<string> FileCacheKeys { get; set; } = Array.Empty<string>();

    /// <summary>是否为叶子节点</summary>
    public bool IsLeaf => Children.Count == 0;

    /// <summary>分组对应的 FinalAction（叶子节点可能有明确的操作建议）</summary>
    public string? GroupFinalActionLabel { get; set; }

    /// <summary>
    /// 格式化字节数为可读大小字符串（内联实现，避免跨层依赖 FileSizeConverter）
    /// </summary>
    private static string FormatSizeBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unitIndex = 0;
        double size = bytes;

        while (size >= 1024.0 && unitIndex < units.Length - 1)
        {
            size /= 1024.0;
            unitIndex++;
        }

        return size < 10
            ? $"{size:F1} {units[unitIndex]}"
            : $"{size:F0} {units[unitIndex]}";
    }
}
