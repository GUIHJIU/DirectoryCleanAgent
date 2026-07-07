using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DirectoryCleanAgent.Converters;

/// <summary>
/// 整数计数到 Visibility 的转换器。
/// count == 0 → Visible（常用于显示"空列表"警告）；
/// count > 0 → Collapsed。
/// 设置 ConverterParameter="Inverse" 时反转：count > 0 → Visible, count == 0 → Collapsed。
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int count = value is int i ? i : 0;
        bool inverse = parameter is string s && s.Equals("Inverse", StringComparison.OrdinalIgnoreCase);
        bool visible = inverse ? count > 0 : count == 0;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("CountToVisibilityConverter 不支持 ConvertBack");
    }
}
