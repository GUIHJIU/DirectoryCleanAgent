using System.Globalization;
using System.Windows.Data;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// 语义分类枚举值或字符串 → Emoji 图标转换器。
/// 用于分组树节点图标和文件列表语义标签列的图标展示。
/// 映射关系对应设计文档 3.3 节定义的 8 个语义标签。
/// </summary>
public class SemanticCategoryToIconConverter : IValueConverter
{
    private static readonly Dictionary<SemanticCategory, string> IconMap = new()
    {
        [SemanticCategory.SystemTempFiles] = "🗑️",
        [SemanticCategory.AppCache] = "🧹",
        [SemanticCategory.OldDownloads] = "📥",
        [SemanticCategory.RecycleBin] = "♻️",
        [SemanticCategory.LogsAndDumps] = "📋",
        [SemanticCategory.OldUserFiles] = "📁",
        [SemanticCategory.Protected] = "🛡️",
        [SemanticCategory.Uncategorized] = "❓",
    };

    /// <summary>
    /// 将 SemanticCategory 枚举值或字符串转换为对应的 Emoji 图标。
    /// 支持 SemanticCategory 枚举、字符串（枚举名称或中英文标签文本）、int 值。
    /// 无法识别时返回默认文件图标 "📄"。
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            return value switch
            {
                SemanticCategory cat => IconMap.TryGetValue(cat, out var icon) ? icon : "📄",
                string s => ParseString(s),
                int i when Enum.IsDefined(typeof(SemanticCategory), i) =>
                    IconMap.TryGetValue((SemanticCategory)i, out var intIcon) ? intIcon : "📄",
                _ => "📄"
            };
        }
        catch
        {
            return "📄";
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("SemanticCategoryToIconConverter 不支持反向转换");
    }

    /// <summary>
    /// 从字符串解析语义分类并返回对应图标。
    /// 先尝试枚举名称解析，再尝试中英文标签文本匹配。
    /// </summary>
    private static string ParseString(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "📄";

        // 尝试按枚举名称解析（如 "SystemTempFiles"）
        if (Enum.TryParse<SemanticCategory>(text, ignoreCase: true, out var cat))
        {
            return IconMap.TryGetValue(cat, out var icon) ? icon : "📄";
        }

        // 按中英文标签文本匹配
        foreach (var kv in IconMap)
        {
            // 通过已知映射表反向匹配（避免依赖 SemanticLabelLocalizer 的循环引用）
            if (IsLabelMatch(text, kv.Key))
                return kv.Value;
        }

        return "📄";
    }

    /// <summary>
    /// 判断文本是否与指定语义分类的中文或英文标签匹配。
    /// </summary>
    private static bool IsLabelMatch(string text, SemanticCategory category)
    {
        return category switch
        {
            SemanticCategory.SystemTempFiles =>
                text is "系统临时文件" or "System Temporary Files",
            SemanticCategory.AppCache =>
                text is "应用缓存" or "Application Cache",
            SemanticCategory.OldDownloads =>
                text is "下载旧文件" or "Old Downloads",
            SemanticCategory.RecycleBin =>
                text is "回收站" or "Recycle Bin",
            SemanticCategory.LogsAndDumps =>
                text is "日志/转储" or "Logs / Dumps",
            SemanticCategory.OldUserFiles =>
                text is "用户旧文件" or "Old User Files",
            SemanticCategory.Protected =>
                text is "保护内容" or "Protected Content",
            SemanticCategory.Uncategorized =>
                text is "未分类" or "Uncategorized",
            _ => false
        };
    }
}
