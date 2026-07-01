using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Models;

/// <summary>
/// 仪表板单个分类卡片的数据模型。
/// 每个卡片展示一个 FinalAction 分类的文件总大小和项数。
/// </summary>
public class DashboardItem
{
    /// <summary>分类（AutoDelete / SuggestDelete / ManualReview / Protected）</summary>
    public FinalAction Category { get; init; }

    /// <summary>卡片标题（如"安全可自动清理"）</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>卡片图标文本</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>该分类文件总大小（字节）</summary>
    public long SizeBytes { get; init; }

    /// <summary>该分类文件总项数</summary>
    public int ItemCount { get; init; }

    /// <summary>格式化后的大小字符串（用于显示）</summary>
    public string FormattedSize => FormatSize(SizeBytes);

    /// <summary>格式化后的项数字符串（如"113项"）</summary>
    public string FormattedCount => $"{ItemCount:N0} 项";

    /// <summary>卡片是否处于选中/高亮状态</summary>
    public bool IsSelected { get; set; }

    /// <summary>
    /// 将字节大小格式化为人类可读字符串。
    /// </summary>
    private static string FormatSize(long bytes)
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
