using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DirectoryCleanAgent.Core.Enums;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// FinalAction 到卡片背景色的转换器。
/// AutoDelete → 绿色系, SuggestDelete → 蓝色系, ManualReview → 橙色系, Protected → 灰色系。
/// </summary>
public class FinalActionToColorConverter : IValueConverter
{
    // 各分类对应的卡片背景色（浅色主题）
    private static readonly SolidColorBrush AutoDeleteBrush = new(Color.FromRgb(232, 245, 233));    // 浅绿
    private static readonly SolidColorBrush SuggestDeleteBrush = new(Color.FromRgb(227, 242, 253)); // 浅蓝
    private static readonly SolidColorBrush ManualReviewBrush = new(Color.FromRgb(255, 243, 224));  // 浅橙
    private static readonly SolidColorBrush ProtectedBrush = new(Color.FromRgb(245, 245, 245));     // 浅灰
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(250, 250, 250));

    // 边框色
    private static readonly SolidColorBrush AutoDeleteBorderBrush = new(Color.FromRgb(129, 199, 132));   // 绿
    private static readonly SolidColorBrush SuggestDeleteBorderBrush = new(Color.FromRgb(100, 181, 246)); // 蓝
    private static readonly SolidColorBrush ManualReviewBorderBrush = new(Color.FromRgb(255, 183, 77));   // 橙
    private static readonly SolidColorBrush ProtectedBorderBrush = new(Color.FromRgb(189, 189, 189));     // 灰
    private static readonly SolidColorBrush DefaultBorderBrush = new(Color.FromRgb(224, 224, 224));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is not FinalAction action)
                return DefaultBrush;

            // parameter="Border" 时返回边框色，否则返回背景色
            bool isBorder = parameter is string s && s.Equals("Border", StringComparison.OrdinalIgnoreCase);

            return action switch
            {
                FinalAction.AutoDelete => isBorder ? AutoDeleteBorderBrush : AutoDeleteBrush,
                FinalAction.SuggestDelete => isBorder ? SuggestDeleteBorderBrush : SuggestDeleteBrush,
                FinalAction.ManualReview => isBorder ? ManualReviewBorderBrush : ManualReviewBrush,
                FinalAction.Protected => isBorder ? ProtectedBorderBrush : ProtectedBrush,
                _ => isBorder ? DefaultBorderBrush : DefaultBrush
            };
        }
        catch (Exception)
        {
            return DefaultBrush;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("FinalActionToColorConverter 不支持反向转换");
    }
}
