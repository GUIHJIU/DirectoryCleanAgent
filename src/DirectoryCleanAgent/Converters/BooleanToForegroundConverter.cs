using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// 布尔值 → 前景色转换器（用于回滚状态文本）。
/// true → 绿色（可回滚），false → 红色（不可回滚）。
/// </summary>
public class BooleanToForegroundConverter : IValueConverter
{
    public static readonly BooleanToForegroundConverter Instance = new();

    private static readonly SolidColorBrush TrueBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush FalseBrush = new(Color.FromRgb(0xC6, 0x28, 0x28));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is true ? TrueBrush : FalseBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
