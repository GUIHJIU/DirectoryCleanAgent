using System.Globalization;
using System.Windows.Data;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// FinalAction 枚举 → 本地化展示文本转换器。
/// 用于文件列表"操作建议"列的文本展示。
/// AutoDelete → "可自动清理", SuggestDelete → "建议清理",
/// ManualReview → "需确认", Protected → "受保护"
/// </summary>
public class FinalActionToDisplayConverter : IValueConverter
{
    /// <summary>
    /// 将 FinalAction 枚举值转换为中文展示文本。
    /// 支持 FinalAction 枚举、int 值（0-3）。
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            FinalAction action = value switch
            {
                FinalAction a => a,
                int i when Enum.IsDefined(typeof(FinalAction), i) => (FinalAction)i,
                _ => throw new ArgumentException($"无法将值 '{value}' (类型: {value?.GetType().Name ?? "null"}) 转换为 FinalAction")
            };

            return action switch
            {
                FinalAction.AutoDelete => "可自动清理",
                FinalAction.SuggestDelete => "建议清理",
                FinalAction.ManualReview => "需确认",
                FinalAction.Protected => "受保护",
                _ => "未知"
            };
        }
        catch
        {
            return "未知";
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("FinalActionToDisplayConverter 不支持反向转换");
    }
}
