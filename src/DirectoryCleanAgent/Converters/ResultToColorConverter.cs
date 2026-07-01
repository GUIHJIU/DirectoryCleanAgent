using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// 审计日志操作结果文本 → 颜色转换器。
/// SUCCESS → 绿色, FAILED → 红色, CANCELLED → 橙色, 其他 → 默认灰。
/// </summary>
public class ResultToColorConverter : IValueConverter
{
    public static readonly ResultToColorConverter Instance = new();

    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush FailedBrush = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush CancelledBrush = new(Color.FromRgb(0xE6, 0x51, 0x00));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(0x33, 0x33, 0x33));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString()?.ToUpperInvariant() switch
        {
            "SUCCESS" => SuccessBrush,
            "FAILED" => FailedBrush,
            "CANCELLED" => CancelledBrush,
            "PARTIAL" => CancelledBrush,
            _ => DefaultBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
